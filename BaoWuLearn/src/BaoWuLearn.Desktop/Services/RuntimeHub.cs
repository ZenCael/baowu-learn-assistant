using BaoWuLearn.Core.Services;

namespace BaoWuLearn.Desktop.Services;

/// <summary>
/// 多账号运行时池：管 <see cref="AccountRuntime"/> 的生老病死，
/// 顺带把「队列按账号存档」的持久化挂在引擎的 QueueChanged 上。
///
/// 纪律：
///  - 挂后台 = 只换 Active 视图，运行时留在池里继续挂（引擎、保活、零点预警都不动）；
///  - 移除 = 存档 → 停引擎 → 释放，仅此一条路会杀运行时；
///  - 批量启动一律走 <see cref="StartAllDelays"/> 排期错峰，绝不同一秒点火 ——
///    N 台引擎同秒起跳是多人挂机唯一的强机器特征。
/// </summary>
public sealed class RuntimeHub
{
    private readonly List<AccountRuntime> _list = new();
    private readonly Action<AccountRuntime>? _onNewRuntime;

    /// <summary>队列存档（本来就是按工号分仓的，池化后零改动）。</summary>
    public QueueStore QueueStore { get; } = new();

    public IReadOnlyList<AccountRuntime> All => _list;

    /// <summary>当前「查看」的账号；null = 停在登录页（池里可能仍有账号在挂）。</summary>
    public AccountRuntime? Active { get; private set; }

    /// <summary>池内容或 Active 有任何变化（界面全量重同步的扳机）。</summary>
    public event Action? Changed;

    /// <param name="onNewRuntime">新运行时入池时的接线回调（日志前缀、过期转发等）。</param>
    public RuntimeHub(Action<AccountRuntime>? onNewRuntime = null)
        => _onNewRuntime = onNewRuntime;

    public AccountRuntime? Find(string? userNo)
        => string.IsNullOrEmpty(userNo)
            ? null
            : _list.FirstOrDefault(r => string.Equals(r.UserNo, userNo, StringComparison.Ordinal));

    /// <summary>
    /// 造一个已登录运行时并置为 Active。token 从登录页的共享 ApiClient 复制进来 ——
    /// 之后这个账号所有的请求都走自己那份带自己 token 的出口，与其他账号零共享。
    /// </summary>
    public AccountRuntime Create(string userNo, string? displayName, string? token)
    {
        var rt = new AccountRuntime(userNo, displayName) { Api = { Token = token } };

        // 过期检出：ApiClient 全局扫响应体（token 过期藏在 200 里）→ 标运行时 → 转告界面
        rt.Api.TokenExpired += _ => rt.MarkExpired();
        // 队列一动就按该账号存档
        rt.Engine.QueueChanged += () => PersistQueue(rt);

        _list.Add(rt);
        _onNewRuntime?.Invoke(rt);
        SetActive(rt);
        return rt;
    }

    /// <summary>把 Active 换成指定运行时（null = 回登录页）。变了才广播。</summary>
    public void SetActive(AccountRuntime? rt)
    {
        if (ReferenceEquals(Active, rt)) return;
        Active = rt;
        Changed?.Invoke();
    }

    /// <summary>按当前引擎队列写该账号的存档（抑制标志位开着就跳过）。</summary>
    public void PersistQueue(AccountRuntime rt)
    {
        if (rt.SuspendQueuePersist) return;
        if (string.IsNullOrWhiteSpace(rt.UserNo)) return;
        QueueStore.Save(rt.UserNo, rt.Engine.Queue);
    }

    /// <summary>
    /// 恢复该账号上次的挂课队列（存档里是上次退出那一刻的成绩快照，
    /// 随后由正常的成绩回读刷新）。返回恢复的门数。
    /// </summary>
    public int RestoreQueue(AccountRuntime rt)
    {
        if (string.IsNullOrWhiteSpace(rt.UserNo)) return 0;
        var saved = QueueStore.Load(rt.UserNo);
        if (saved.Count == 0) return 0;

        rt.SuspendQueuePersist = true;
        try
        {
            rt.Engine.ClearQueue();
            rt.Engine.Enqueue(saved);
        }
        finally
        {
            rt.SuspendQueuePersist = false;
        }
        return saved.Count;
    }

    /// <summary>
    /// 移出并释放一个账号：先存档（防止停队清空把存档一起抹掉），再停引擎释放网络。
    /// 若移除的是 Active，自动挑一个接替（优先没过期、其次在挂的）。
    /// </summary>
    public void Remove(AccountRuntime rt)
    {
        PersistQueue(rt);
        _list.Remove(rt);
        if (ReferenceEquals(Active, rt))
            Active = PickNext(rt);
        rt.Dispose();
        Changed?.Invoke();
    }

    private AccountRuntime? PickNext(AccountRuntime? exclude = null)
        => _list.FirstOrDefault(r => !ReferenceEquals(r, exclude)
                                     && !r.SessionExpired
                                     && r.Engine.State == EngineState.Running)
           ?? _list.FirstOrDefault(r => !ReferenceEquals(r, exclude) && !r.SessionExpired)
           ?? _list.FirstOrDefault(r => !ReferenceEquals(r, exclude));

    /// <summary>退出收尾用：只发停止信号不等待（等待在调用方并行做）。</summary>
    public void StopAll()
    {
        foreach (var rt in _list)
        {
            try { rt.Engine.Stop(); } catch { /* 收尾阶段吞一切 */ }
        }
    }

    /// <summary>
    /// 全池空闲判定 —— 自动更新的安装门。Paused 也算闲：那是过期自动暂停，
    /// 装完新版重登本来就还要人来点，不存在"打断挂机"。
    /// </summary>
    public bool AllIdle
        => _list.All(r => r.Engine.State is EngineState.Idle
                                       or EngineState.Stopped
                                       or EngineState.Paused);

    /// <summary>有没有可以「全部开始」的对象（没过期、引擎闲着、队列非空）。</summary>
    public bool HasStartable
        => _list.Any(r => !r.SessionExpired
                          && r.Engine.State is EngineState.Idle or EngineState.Stopped
                          && r.Engine.Queue.Count > 0);

    /// <summary>
    /// 「全部开始」的错峰排期（纯函数，供 <c>--selftest</c> 离线验算）：
    /// 返回 n 个**累计**等待时刻，首个 8~20 秒，其后逐个再随机加 55~240 秒。
    /// 保证严格递增 —— 引擎心跳相位从各自 Start 时刻起跑，错峰启动就永远不会同秒跳。
    /// </summary>
    public static IReadOnlyList<TimeSpan> StartAllDelays(int n, Random? rng = null)
    {
        var r = rng ?? new Random();
        var delays = new List<TimeSpan>(n);
        if (n <= 0) return delays;

        var acc = TimeSpan.FromSeconds(8 + r.Next(0, 13));
        for (var i = 0; i < n; i++)
        {
            delays.Add(acc);
            acc += TimeSpan.FromSeconds(55 + r.Next(0, 186));
        }
        return delays;
    }
}
