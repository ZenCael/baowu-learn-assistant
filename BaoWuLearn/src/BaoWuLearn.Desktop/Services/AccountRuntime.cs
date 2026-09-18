using Avalonia.Threading;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Desktop.Models;

namespace BaoWuLearn.Desktop.Services;

/// <summary>
/// 一个已登录账号的完整运行时：独立 ApiClient（token 挂在实例上）、
/// 独立 CourseService / UserCenterService、独立挂课引擎，外加各自一份
/// 会话保活与跨零点预警计时器。
///
/// 多账号挂机 = 一池这样的运行时（见 <see cref="RuntimeHub"/>）。
/// 平台按账号发 token、按账号校验会话，账号之间零共享；
/// 心跳相位从各自的登录时刻起跑、58~63 秒拟人抖动，天然错峰 ——
/// 这是多人挂机唯一要防的机器特征，所以批量启动必须走
/// <see cref="RuntimeHub.StartAllDelays"/> 排期，不允许同一秒点火。
/// </summary>
public sealed class AccountRuntime : IDisposable
{
    /// <summary>登录响应里的工号，同时是队列存档的键（校正不改键，避免存档分裂）。</summary>
    public string UserNo { get; }

    /// <summary>个人信息接口核出来的 stuCode（归档查询用它才准），未校正前等于 UserNo。</summary>
    public string StuCode { get; set; }

    /// <summary>界面上显示的名字（stuName 优先，拿不到用登录名）。</summary>
    public string DisplayName { get; set; }

    public ApiClient Api { get; }
    public AuthService Auth { get; }
    public CourseService Courses { get; }
    public UserCenterService UserCenter { get; }
    public LearnEngine Engine { get; }

    /// <summary>引擎最近一次快照（总览页每 5 秒拉一次这坨来刷行卡，不订阅行事件）。</summary>
    public LearnSnapshot? LastSnapshot { get; private set; }

    /// <summary>本次（重新）登录时刻，保活日志的「已登录 N 分钟」用它算。</summary>
    public DateTimeOffset LoginAt { get; private set; } = DateTimeOffset.Now;

    /// <summary>检出过期的本地时刻（保活日志「已过期 N 分钟」用它算）。</summary>
    public DateTimeOffset? ExpiredAt { get; private set; }

    /// <summary>会话处于过期状态（重新登录成功会清掉）。</summary>
    public bool SessionExpired { get; private set; }

    /// <summary>过期那一刻是否在挂机 —— 决定重新登录成功后要不要自动续挂。</summary>
    public bool WasRunningOnExpiry { get; private set; }

    /// <summary>重新登录成功后自动续挂（由过期横幅/总览页的重登入口置位）。</summary>
    public bool AutoStartAfterRelogin { get; set; }

    /// <summary>抑制队列回写（运行时内部清装队列时不往存档里泼空）。</summary>
    public bool SuspendQueuePersist { get; set; }

    public Action<string, LogLevel>? Log { get; set; }

    /// <summary>检出会话过期（后台线程发出，订阅方自己切 UI 线程）。</summary>
    public event Action<AccountRuntime>? ExpiredDetected;

    /// <summary>保活顺带拉回了个人信息（总览页的学习时长卡靠它保鲜）。</summary>
    public event Action<StudentProfile>? ProfileRefreshed;

    private DispatcherTimer? _keepaliveTimer;
    private DispatcherTimer? _midnightWarnTimer;

    public AccountRuntime(string userNo, string? displayName)
    {
        UserNo = userNo;
        StuCode = userNo;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? userNo : displayName!;

        Api = new ApiClient();
        Auth = new AuthService(Api);
        Courses = new CourseService(Api);
        UserCenter = new UserCenterService(Api);
        Engine = new LearnEngine(Api, Courses);
        Engine.Snapshot += s => LastSnapshot = s;
    }

    // ── 过期处理 ──────────────────────────────────────────

    /// <summary>
    /// 标记会话过期。引擎的自动暂停由 LearnEngine 自己完成，
    /// 这里只记账：过期标记、过期时刻、「过期时是否在挂」。
    /// </summary>
    public void MarkExpired()
    {
        if (SessionExpired) return;
        WasRunningOnExpiry = Engine.State is EngineState.Running or EngineState.Paused;
        ExpiredAt = DateTimeOffset.Now;
        SessionExpired = true;
        ExpiredDetected?.Invoke(this);
    }

    /// <summary>
    /// 重新登录成功：把新 token 装进本运行时的 ApiClient，清过期标记、刷新登录时刻。
    /// ★ 平台单会话约束是按账号的 —— 这一步只顶掉这个账号的旧会话，池里别人不受影响。
    /// </summary>
    public void ApplyRelogin(string? token)
    {
        Api.Token = token;
        Api.ResetTokenLatch();
        SessionExpired = false;
        ExpiredAt = null;
        WasRunningOnExpiry = false;
        LoginAt = DateTimeOffset.Now;
    }

    // ── 会话保活 / 跨零点预警（v1.0.42 起按运行时各持一份）──

    /// <summary>
    /// 启动本账号的看门计时（15 分钟保活 + 一次性零点预警，并刷新零点提醒）。
    /// 幂等：重复调用只重启计时器。
    /// </summary>
    public void StartWatchers()
    {
        _keepaliveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _keepaliveTimer.Tick -= OnKeepaliveTick;
        _keepaliveTimer.Tick += OnKeepaliveTick;
        _keepaliveTimer.Start();

        ArmMidnightWarning();
    }

    private async void OnKeepaliveTick(object? sender, EventArgs e)
    {
        try { await KeepaliveAsync(); }
        catch { /* 计时器回调里绝不许抛出 */ }
    }

    public void StopWatchers()
    {
        _keepaliveTimer?.Stop();
        _midnightWarnTimer?.Stop();
    }

    /// <summary>会话保活：轻探一次个人信息接口，顺带把会话状态写进日志。</summary>
    private async Task KeepaliveAsync()
    {
        try
        {
            var p = await UserCenter.GetProfileAsync();

            // ★ "没抛异常" ≠ "会话还活着"。平台用 HTTP 200 + isSuccess:false 表达过期，
            //   而 GetProfileAsync 取不到 data 时是**静默返回 null**。判据沿用 v1.0.34：
            //   正常分支必带「平台累计学习时长」后缀，没后缀 = p 是 null = 这次其实失败了。
            if (p is null)
            {
                Log?.Invoke(SessionExpired
                    ? "⛔ 会话保活失败：登录仍处于过期状态"
                      + (ExpiredAt is { } at
                          ? $"（已过期 {(int)(DateTimeOffset.Now - at).TotalMinutes} 分钟）"
                          : "")
                      + "，平台一直拒绝本会话 —— 重新登录才能续上"
                    : "⚠ 会话保活拿不到个人信息（平台既没报过期、也没给 data）—— 本次不处理，下次再探",
                    LogLevel.Warn);
                return;
            }

            Log?.Invoke($"🔒 会话保活正常（已登录 {(int)(DateTimeOffset.Now - LoginAt).TotalMinutes} 分钟"
                        + $"，平台累计学习时长 {p.LearnTimeText}）"
                        + (SessionExpired
                            ? "；★ 平台重新接受了本会话，但界面仍标着「已过期」—— 挂机不会自动恢复，点「开始」即可续挂"
                            : ""),
                        LogLevel.Info);
            ProfileRefreshed?.Invoke(p);
        }
        catch (Exception ex)
        {
            // 保活失败不弹横幅 —— 若真是过期，响应体检出会统一走横幅路径
            Log?.Invoke($"⚠ 会话保活请求失败：{ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 按"平台会话跨不过 0 点"这条实测规则，安排一次跨零点预警。
    /// 已在 23:45 之后的会话（只剩十几分钟寿命）当场就提醒。
    /// </summary>
    public void ArmMidnightWarning()
    {
        if (MidnightWarningDelay(DateTimeOffset.Now) is not { } delay)
        {
            WarnAboutMidnight();   // 已经过了 23:45，没什么可等的
            return;
        }

        _midnightWarnTimer ??= new DispatcherTimer();
        _midnightWarnTimer.Tick -= OnMidnightWarnTick;
        _midnightWarnTimer.Tick += OnMidnightWarnTick;
        _midnightWarnTimer.Interval = delay;
        _midnightWarnTimer.Start();
    }

    private void OnMidnightWarnTick(object? sender, EventArgs e)
    {
        _midnightWarnTimer?.Stop();   // 一次性：提醒过就不再响
        WarnAboutMidnight();
    }

    /// <summary>
    /// 跨零点预警的决策（纯函数，供 <c>--selftest</c> 离线验算）：
    /// 返回 null = 当场就该提醒（已经过了今天 23:45）；否则返回还要等多久。
    /// </summary>
    public static TimeSpan? MidnightWarningDelay(DateTimeOffset now)
    {
        var warnAt = NextMidnight(now).AddMinutes(-15);   // 今天的 23:45
        return now >= warnAt ? null : warnAt - now;
    }

    /// <summary>今天 24:00（即明天 0 点）的本地时刻。公开静态：自检要离线验算跨日边界。</summary>
    public static DateTimeOffset NextMidnight(DateTimeOffset now)
        => new DateTimeOffset(
               new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Unspecified), now.Offset)
           .AddDays(1);

    /// <summary>
    /// 提醒：本账号的会话活不过 0 点。刻意把"怎么办"写全 —— 重新登录要人工输图形
    /// 验证码（不 OCR、不绕过），被切断时挂机自动暂停、学时全保留，重登后自动接着挂。
    /// </summary>
    private void WarnAboutMidnight()
    {
        var remain = (int)(NextMidnight(DateTimeOffset.Now) - DateTimeOffset.Now).TotalMinutes;
        Log?.Invoke(
            $"⏰ 本次会话活不过 0 点：平台会话按自然日失效（实测与登录时刻无关，心跳拦不住）。"
            + $"本次登录 {LoginAt:HH:mm}，距 0 点还有 {remain} 分钟。"
            + "要整夜挂机，请在 0 点后重新登录（人工过一次图形验证码）；"
            + "被切断时挂机自动暂停、已挂学时全部保留，重新登录后自动接着挂。",
            LogLevel.Warn);
    }

    public void Dispose()
    {
        StopWatchers();
        try { Engine.Stop(); } catch { /* 释放阶段吞一切 */ }
        Api.Dispose();
    }
}
