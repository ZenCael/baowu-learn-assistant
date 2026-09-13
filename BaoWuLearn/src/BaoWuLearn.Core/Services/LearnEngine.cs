using System.Text.Json;
using BaoWuLearn.Core.Behavior;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;

namespace BaoWuLearn.Core.Services;

/// <summary>引擎运行状态。</summary>
public enum EngineState
{
    Idle,
    Running,
    Paused,
    Stopping,
    Stopped,
}

/// <summary>
/// 结算接口的业务应答。
/// <paramref name="Accepted"/> = 平台是否受理了这次结算任务；
/// <paramref name="Message"/> 在受理时是异步任务 id，在被拒时是平台的拒绝原因。
/// </summary>
public sealed record SettleOutcome(bool Accepted, string? Message);

/// <summary>引擎对外广播的实时快照（供界面绑定）。</summary>
public sealed record LearnSnapshot(
    EngineState State,
    string? CourseName,
    string? WareName,
    double PlayedSeconds,
    double TargetSeconds,
    int CurrentIndex,
    int TotalCourses,
    int CompletedCourses,
    DateTimeOffset? NextHeartbeatAt,
    // 当前在挂课程的身份。界面必须按身份匹配列表行，不能按索引 ——
    // 用户随时可能从队列里移出课程，索引整体前移会让"当前在挂"标到别的行上。
    string? CourseNo = null,
    string? OlClassNo = null,
    // 课程级学时（单位秒）。已学 = 各课件在服务端的 maxPlayTime 之和 + 本次挂课的净增量，
    // 与平台的账本口径一致 —— 所以界面拿它按秒插值推进，也不会跟最终成绩单对不上。
    // 之所以要单独带这一对：平台的「已完成学时」只有在结算后异步刷新，
    // 挂课途中那个数字纹丝不动，用户看到的就是"挂了半天没反应"。
    double CoursePlayedSeconds = 0,
    double CourseTargetSeconds = 0,
    // 课程内课件进度：总数 = 课件目录里的课件个数，序号 = 当前课件在大纲里的原始位置（1 起）。
    // 序号按大纲顺序而不是挂机顺序 —— 挂机是按"剩多少补多少"排序的，会跳来跳去，
    // 用户问的是"这门课第几个视频"，跟网页端目录对得上才有意义。
    int WareIndex = 0,
    int WareTotal = 0,
    // ── 结算可观测性（v1.0.19）────────────────────────────
    // 「结算」是学时/得分能不能涨的唯一开关，但它此前完全活在引擎内部：
    // 用户看不到发了几次、什么时候发的、平台受没受理、成绩有没有跟着刷新 ——
    // 原话就是「我也不知道做了结算没有，得分有没有刷新」。
    // 这四项把结算链路原样摊到界面上，界面据此显示「已提交结算 N 次 · 最近 hh:mm:ss」。
    int SettleCount = 0,
    DateTimeOffset? LastSettleAt = null,
    bool? LastSettleAccepted = null,
    // 平台成绩真正发生变化（回读到新值）的时刻。为空 = 提交过但还没看到落库。
    DateTimeOffset? ScoreRefreshedAt = null,
    // ── 模拟暂停窗口（v1.0.21）────────────────────────────
    // 引擎偶尔会插入一次 5–30 秒的"人离开了"停顿，这段**一秒都不计入学时**。
    // 界面是按墙钟插值推进进度的，不把这段告诉它，它就会照推不误 ——
    // 暂停结束的快照一到，显示值被拽回去，用户看到的就是「进度条回退」。
    // 有值且在未来 = 此刻正在暂停，界面必须停表。
    DateTimeOffset? PausingUntil = null,
    // ── 登录过期（v1.0.25）────────────────────────────────
    // 非 null = 检出过 token 过期、引擎已自动暂停。界面据此亮红色横幅、
    // 引导用户重新登录（登录成功后引擎继续，队列和进度都在）。
    DateTimeOffset? TokenExpiredAt = null)
{
    public double Progress => TargetSeconds <= 0 ? 0 : Math.Clamp(PlayedSeconds / TargetSeconds, 0, 1);

    /// <summary>判断某一行是不是当前正在挂的那门课。</summary>
    public bool Matches(string? courseNo, string? olClassNo)
        => !string.IsNullOrEmpty(CourseNo)
           && string.Equals(CourseNo, courseNo, StringComparison.Ordinal)
           && string.Equals(OlClassNo, olClassNo ?? "", StringComparison.Ordinal);
}

/// <summary>
/// 挂课引擎。
///
/// 与网页端播放器的等价关系：网页端靠 video 元素每秒推进播放进度、每 60 秒发一次心跳；
/// 本引擎用「真实时间推进 + 同构 payload」复刻同一套时序，并额外叠加
/// <see cref="Humanize"/> 的拟人化抖动，使请求分布落在真人区间内。
///
/// 关键纪律：
///  - 学时只按真实流逝时间上报，绝不虚报、不超前
///  - 单账号串行，不并发多课
///  - 所有网络失败都记录并降速重试，不静默吞掉
/// </summary>
public sealed class LearnEngine
{
    private readonly ApiClient _api;
    private readonly CourseService _courses;

    private readonly List<CourseItem> _queue = new();
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private readonly ManualResetEventSlim _resume = new(true);

    private int _currentIndex = -1;
    private int _completed;
    private string? _courseName;
    private string? _courseNo;
    private string? _olClassNo;
    private string? _wareName;
    private CourseItem? _currentCourse;
    /// <summary>当前课件在大纲里的原始序号（1 起，0 = 尚未开始播某个课件）。</summary>
    private int _wareIndex;
    /// <summary>当前课程的课件总数（来自课件目录，与网页端目录个数一致）。</summary>
    private int _wareTotal;
    private double _played;
    private double _target;
    private DateTimeOffset? _nextBeat;

    /// <summary>
    /// 模拟暂停的结束时刻（不在暂停中则为 null）。
    ///
    /// 暂停期间引擎一秒都不计入学时，但界面是按墙钟插值的 —— 必须让界面知道"现在停着"，
    /// 否则它会照推，暂停一结束显示值就被快照拽回去（"进度条回退"）。
    /// </summary>
    private DateTimeOffset? _pausingUntil;

    // ── 课程级学时账本 ────────────────────────────────────
    /// <summary>各课件在服务端的 maxPlayTime 之和 —— 课程开始时的学时基线。</summary>
    private double _courseBase;

    /// <summary>本次挂课中已经挂完的课件贡献的净增量。</summary>
    private double _sessionGain;

    /// <summary>当前课件进入时的起始播放位置，用来算它贡献了多少增量。</summary>
    private double _wareStart;

    /// <summary>课程要求学时（秒），界面用它显示"已学 / 要求"。</summary>
    private double _courseTarget;

    // ── 结算节奏 ──────────────────────────────────────────
    /// <summary>
    /// 向平台补发「进度结算」的周期。
    ///
    /// ★ 平台账本是两段式的（2026-09-11 实测确认）：
    ///   1) 心跳只更新**每个课件的最远播放位置** maxPlayTime —— 成绩单纹丝不动；
    ///   2) 只有调用结算接口，平台才会把 maxPlayTime 汇总进成绩单的 CE002（学习时长）与 learnScore，
    ///      且**不必等视频播完**：挂到一半发一次，已播时长立刻入账。
    ///
    /// 实测数据（习近平外交思想，courseNo 70035）：
    ///   · 播到 ware2 = 06:56 时成绩单还是上一次结算的旧值 18.75 分；
    ///   · 手动补发一次结算，40 秒后成绩单变成 25.63 分、得分 17.98 → 24.58，
    ///     与各课件 maxPlayTime 之和（26 分）吻合。
    ///
    /// 网页端播放器只在 video 的 ended 事件里结算一次，所以长视频下"分数长时间不动"
    /// 是平台本身的行为；本引擎按固定周期补发，让分数在挂课途中持续推进。
    /// 周期取 3 分钟：既能让用户看到变化，又不至于把平台的结算任务打得太密。
    /// </summary>
    public static readonly TimeSpan SettlementInterval = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 「收尾跳」的等待下限（秒）。
    ///
    /// 距目标只剩零点几秒时不至于发出间隔近乎 0 的碎心跳；最多多等这么久，
    /// 相比原来空转一整个 58–63 秒的周期，已经是数量级的改善。
    /// </summary>
    public const double MinTailWaitSeconds = 2;

    /// <summary>
    /// 计算下一次心跳应该等待多久。
    ///
    /// 正常情况就是一个完整的拟人化心跳周期；只有「距课件目标已不足一个周期」时，
    /// 才把等待缩到剩余时间（下限 <see cref="MinTailWaitSeconds"/>）。
    ///
    /// 动机（v1.0.18）：原来是"等满一个周期 → 推进进度 → 才判断播完没"，
    /// 于是视频进度早就满了，界面还要空转最多一分钟才切下一个课件 ——
    /// 用户看到的就是「进度条已显示 19m05s/19m05s，播放时长还在涨，过了一会才切」。
    /// </summary>
    public static TimeSpan NextBeatInterval(
        double playedSeconds, double targetSeconds, TimeSpan normalInterval)
    {
        var remain = targetSeconds - playedSeconds;
        if (remain <= 0) return normalInterval;                      // 已播满：交给播满判断收尾
        if (remain >= normalInterval.TotalSeconds) return normalInterval;

        return TimeSpan.FromSeconds(Math.Max(remain, MinTailWaitSeconds));
    }

    /// <summary>上一次向平台提交结算的时刻（换课时重置）。</summary>
    private DateTimeOffset _lastSettleAt = DateTimeOffset.MinValue;

    /// <summary>本次挂课累计向平台提交过多少次结算（换课时重置）。</summary>
    private int _settleCount;

    /// <summary>最近一次结算平台的业务应答：true = 受理，false = 被拒，null = 还没提交过。</summary>
    private bool? _lastSettleAccepted;

    /// <summary>平台成绩真正回读到新值的时刻（换课时重置）。</summary>
    private DateTimeOffset? _scoreRefreshedAt;

    /// <summary>上一次记进日志的成绩观测（得分 / 学习时长），用于"只在变化时记一行"。</summary>
    private (double Score, double Duration)? _lastScoreObservation;

    /// <summary>
    /// 结算后「盯成绩」的后台轮询。
    ///
    /// 结算接口是异步任务：受理后平台要几十秒才把 maxPlayTime 汇总进成绩单。
    /// 只在结算那一刻回读（旧行为）等于没读 —— 拿回来的必然是旧值，
    /// 界面上就成了「明明发了结算，得分却一动不动」。
    /// 这里改成结算后按 <see cref="ScoreProbeDelays"/> 逐档回读，读到变化为止。
    /// 它有自己的 CTS，**不随主循环取消** —— 队列跑完（甚至用户按了停止）之后
    /// 那一次结算同样要被跟到底，否则最后一门课的分数会永远停在旧值。
    /// </summary>
    private CancellationTokenSource? _scoreProbeCts;

    /// <summary>
    /// 结算后回读成绩的时间点（秒，相对提交时刻）。
    ///
    /// 实测平台约 40 秒落库（2026-09-11 手动结算验证），所以前三档都压在 60 秒内，
    /// 第四档兜到 100 秒；一旦读到变化就提前收工，不会白等满一轮。
    /// </summary>
    public static readonly double[] ScoreProbeDelays = { 10, 20, 30, 40 };

    /// <summary>距上次结算是否已经够一个周期（到点就该补发一次）。</summary>
    public static bool NeedsPeriodicSettlement(TimeSpan sinceLastSettle)
        => sinceLastSettle >= SettlementInterval;

    /// <summary>
    /// 课件开局时要不要先补一次结算。
    ///
    /// 服务端已经记了历史进度（resumeFrom &gt; 0）却从没结算过时，成绩单会是 0 或旧值 ——
    /// 用户上一轮挂的时长明明在，分数却不动，这就是"挂了半天分数还是 0"的来源。
    /// 开局先补一次，把历史进度立刻落进成绩单。
    /// </summary>
    public static bool NeedsEntrySettlement(double resumeFromSeconds)
        => resumeFromSeconds > 0;

    /// <summary>
    /// 「补学时」最多跑几跳（v1.0.36）。
    ///
    /// 位置到顶而平台学时没到顶的缺口 = Σ(心跳间隔 − 60) ≈ 课件时长 × 5%，
    /// 900 秒的课件最多差 45 秒 —— 一跳（≤ 60s）就能补平。
    /// 留 6 跳是给历史版本（"收尾硬顶到目标"、"失败的那跳也推账本"）攒下的大缺口留余量。
    /// </summary>
    public const int MaxTopUpBeats = 6;

    /// <summary>
    /// 课件位置已到顶、但这门课还该继续补学时吗（v1.0.36）。
    ///
    /// 平台维护**两本账**：课件的 <c>maxPlayTime</c>（最远播放位置）和成绩单的累计学时
    /// （每跳最多计入 60 秒）。旧版把两者当成一本账 —— <see cref="WareProgress"/> 的老注释
    /// 就写着"各课件 maxPlayTime 之和 == CE002 的 finishValue"，于是位置一到顶就整门课跳过。
    ///
    /// 2026-09-13 实测证伪了这个等式：零余量 PDF 专区三门课的位置都在 15:00，
    /// 成绩单却只有 14分44 / 14分52 / 14分45 秒，得分卡在 98~99 分不再涨 ——
    /// 位置满 ≠ 学时满。零余量课（分数线 100 + 视频占分 100%，要求 100% 时长）
    /// 没有任何余量去吸收这点差，于是永远过不了。
    /// </summary>
    /// <param name="courseNeedsTopUp">
    /// 这门课本身该不该补 —— 由 <see cref="CourseNeedsTopUp"/> 给出（未达标 + 零余量）。
    /// 它是一道额外的门：单纯"没读到成绩"也会让达标判断为假，
    /// 没有这道门，普通课会被拖去白补 6 跳。
    /// </param>
    public static bool NeedsTopUp(double resumeFromSeconds, double wareTargetSeconds, bool courseNeedsTopUp)
        => courseNeedsTopUp && resumeFromSeconds >= wareTargetSeconds - 1;

    /// <summary>
    /// 这门课要不要走补学时通道：**未达标 且 是零余量课**。
    ///
    /// 只有零余量课可能出现"位置满、学时差一截"的错位 —— 它要求 100% 时长，
    /// 没有余量去吸收平台单跳上限（60 秒）吃掉的那点秒数。
    /// 普通课按分数线打折（60 分 ÷ 80% 权重 = 只挂 75% 时长），位置满时时学早就超标了。
    /// 加"零余量"这一层是为了杜绝误伤：成绩回读偶发拿不到值时 <see cref="ShouldSkipCourse"/>
    /// 也是 false，没有这层门，普通课同样会被拖去补 6 跳（白等十分钟）。
    /// </summary>
    private static bool CourseNeedsTopUp(CourseItem course)
        => !ShouldSkipCourse(course)
           && LearnPolicy.IsZeroMargin(course.PassScore, course.DurationScoreWeight);

    public LearnEngine(ApiClient api, CourseService courses)
    {
        _api = api;
        _courses = courses;
        // 任何接口的响应体里出现「token 过期」，引擎都必须立刻停下 ——
        // v1.0.24 之前挂机会在过期状态下空转两个多小时（HTTP 200 让心跳"看起来"正常）。
        _api.TokenExpired += OnTokenExpired;
    }

    /// <summary>登录过期被检出的时刻。非 null = 引擎因过期自动暂停，等重新登录。</summary>
    private DateTimeOffset? _tokenExpiredAt;

    /// <summary>
    /// token 过期的统一处置：立刻自动暂停。
    ///
    /// 为什么是暂停而不是停止：暂停保留了当前课件的进度状态（_played / 基线账本），
    /// 重新登录后按「恢复」就能接着挂，已挂到的学时一分钟都不浪费；
    /// 停止则要把整个课程重新走一遍开局流程。
    /// 恢复动作由界面引导（重新登录成功后自动续挂），引擎自己绝不带着过期 token 重发请求。
    /// </summary>
    private void OnTokenExpired(string reason)
    {
        _tokenExpiredAt = DateTimeOffset.Now;

        if (State is EngineState.Running or EngineState.Paused)
        {
            Emit("⛔ 登录已过期，挂机已自动暂停 —— 已挂到的进度保留，重新登录后将继续");
            Emit(DateTimeOffset.Now.Hour < 1
                ? "ℹ 提示：平台会话按自然日失效（2026-09-13 两次实测：跨 0 点必在 0 点整被切断，"
                  + "与登录时刻无关、心跳也拦不住）—— 重新登录后请留意 0 点后再登一次"
                : "ℹ 提示：同账号在其他设备/浏览器登录会把本会话顶掉；"
                  + "若非人为，多半是跨过了 0 点（实测平台会话按自然日失效，活不过 0 点）");
            _resume.Reset();          // 挂起心跳循环，与手动暂停同一路径
            SetState(EngineState.Paused);
        }
        else
        {
            Emit($"⛔ 登录已过期（{reason}），请重新登录");
        }

        Publish();
    }

    /// <summary>日志广播。</summary>
    public event Action<string>? Log;

    /// <summary>状态快照广播。</summary>
    public event Action<LearnSnapshot>? Snapshot;

    /// <summary>
    /// 队列内容发生变化（加入 / 移出 / 调序 / 清空）时触发。
    ///
    /// 供界面层把队列持久化到本地 —— 队列原来只活在内存里，程序一关就没了，
    /// 每次重开都要重新挑课加一遍。
    /// </summary>
    public event Action? QueueChanged;

    private void NotifyQueueChanged()
    {
        try
        {
            QueueChanged?.Invoke();
        }
        catch
        {
            // 持久化失败绝不能影响挂课本身
        }
    }

    public EngineState State { get; private set; } = EngineState.Idle;

    public IReadOnlyList<CourseItem> Queue
    {
        get { lock (_sync) return _queue.ToList(); }
    }

    // ── 队列操作 ──────────────────────────────────────────

    public void Enqueue(IEnumerable<CourseItem> courses)
    {
        lock (_sync)
        {
            foreach (var c in courses)
                // 去重键用 courseNo + olClassNo：班内课程的 Guid 经常是空的，
                // 用 Guid 比较会让同一门课被重复加进队列。
                if (!_queue.Any(q => q.CourseNo == c.CourseNo && q.OlClassNo == c.OlClassNo))
                    _queue.Add(c);
        }
        Publish();
        NotifyQueueChanged();
    }

    public void Remove(CourseItem course)
    {
        lock (_sync) _queue.RemoveAll(q => q.Guid == course.Guid && q.CourseNo == course.CourseNo);
        Publish();
        NotifyQueueChanged();
    }

    /// <summary>
    /// 某门课是否还在挂机队列里。
    /// 用户在挂机过程中随时可能把它移出，运行循环要据此立即收尾，而不是继续空转。
    /// </summary>
    private bool IsStillQueued(CourseItem course)
    {
        lock (_sync)
            return _queue.Any(q => q.CourseNo == course.CourseNo && q.OlClassNo == course.OlClassNo);
    }

    /// <summary>队列判重 / 去重用的键。</summary>
    private static string QueueKey(CourseItem c) => c.CourseNo + "@" + c.OlClassNo;

    /// <summary>
    /// 对当前课程补一次结算，把已经挂到的进度落进平台成绩单。
    ///
    /// 心跳只更新"最远播放位置"，成绩单（finishInfo 的 CE002）要等结算任务跑完才会刷新。
    /// 这里刻意不传 CancellationToken —— 停止流程里 ct 已经取消，
    /// 带着它发请求会立刻被打断，等于没提交。
    /// </summary>
    private async Task TrySettleCurrentAsync()
    {
        var course = _currentCourse;
        if (course is null || string.IsNullOrEmpty(course.CourseNo)) return;

        // 走和主循环同一条结算路径：同样的业务结果解析、同样的结算后盯成绩 ——
        // 停止是用户最常做的一个动作，恰恰最需要看到"这次到底入没入账"。
        await SettleProgressAsync(course, "停止结算", CancellationToken.None);
    }

    /// <summary>
    /// 手动「保存进度」：不等心跳周期，立刻把当前课程已挂到的进度结算进平台成绩单。
    ///
    /// 运行中 / 暂停中都可调用（暂停只是心跳循环挂起，HTTP 请求照发）；
    /// 没有正在挂的课程时只发一行日志、什么都不做 —— 没开始挂课就无进度可保存。
    /// 刻意不传 CancellationToken：用户点这个按钮就是要它落地，取消令牌打断了等于白点。
    /// </summary>
    public async Task SaveProgressNowAsync()
    {
        var course = _currentCourse;
        if (course is null || string.IsNullOrEmpty(course.CourseNo))
        {
            Emit("ℹ 当前没有正在挂的课程，无进度可保存");
            return;
        }

        await SettleProgressAsync(course, "手动保存进度", CancellationToken.None);
    }

    /// <summary>
    /// 把当前课程已挂到的进度结算进平台成绩单，并刷新"上次结算时刻"。
    ///
    /// 结算接口返回的是异步任务的 taskId，平台要几十秒才把结果写进成绩单 ——
    /// 所以「提交」和「入账」是两件事：这里读响应体确认平台**受理**了没有
    ///（HTTP 200 不等于受理），再挂一个后台轮询把入账跟到底（见 <see cref="ProbeScoreAsync"/>）。
    /// 结算失败只记日志、不打断挂课。
    /// </summary>
    /// <param name="probe">
    /// 是否挂"落库盯梢"。默认挂；「开局结算」传 false —— 它只是把服务端已有的历史进度
    /// 补报一次，平台很可能早就把这批进度结算过了，此时盯"成绩有没有变化"必然落空，
    /// 白白给出一条"可点刷新复核"的误导警告（白天日志 19:13:56 那条）。
    /// </param>
    private async Task SettleProgressAsync(
        CourseItem course, string what, CancellationToken ct, bool probe = true)
    {
        if (string.IsNullOrEmpty(course.CourseNo)) return;

        bool accepted;
        string? detail;

        try
        {
            using var doc = await _api.PostRawAsync(ApiEndpoints.SaveComputeAfterVideoPlayed,
                new Dictionary<string, object?>
                {
                    ["classNo"] = course.OlClassNo,
                    ["courseNo"] = course.CourseNo,
                },
                ct: ct);

            var outcome = ReadSettleOutcome(doc?.RootElement);
            accepted = outcome.Accepted;
            detail = outcome.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 网络/超时等失败不打断挂课，但**必须**说出来 ——
            // 静默失败的结算正是"挂了半天分数不动"最容易被漏掉的一条线索。
            Emit($"✖ 结算提交失败（{what}，classNo={course.OlClassNo} courseNo={course.CourseNo}）：{ex.Message}");
            _lastSettleAt = DateTimeOffset.UtcNow;
            _lastSettleAccepted = false;
            _settleCount++;
            Publish();
            return;
        }
        finally
        {
            // 无论成功与否都推进时刻，避免结算接口持续失败时每个心跳都重试、把请求打爆
            _lastSettleAt = DateTimeOffset.UtcNow;
        }

        _settleCount++;
        _lastSettleAccepted = accepted;

        if (accepted)
        {
            var taskHint = string.IsNullOrEmpty(detail) ? "" : $"，taskId={detail}";
            // 把结算报文的身份也写进日志：classNo / courseNo 传错（尤其是班级号为空的归档课）
            // 时平台照样返回 200，但一分钟都不入账 —— 事后对着日志一眼就能看出来。
            Emit($"→ 结算已受理（{what}，第 {_settleCount} 次，"
                 + $"classNo={course.OlClassNo} courseNo={course.CourseNo}{taskHint}），"
                 + "正在盯平台落库…");
            // 受理 ≠ 入账。异步任务要几十秒才写进成绩单，这里挂一个后台轮询把它跟到底。
            // （开局结算不挂，见方法注释里的 probe 参数说明。）
            if (probe) ScheduleScoreProbe(course);
        }
        else
        {
            Emit($"⚠ 结算被平台拒绝（{what}，第 {_settleCount} 次）"
                 + (string.IsNullOrEmpty(detail) ? "" : $"：{detail}"));
            Publish();
        }
    }

    /// <summary>
    /// 结算之后"盯落库"：按平台的落库节奏（<see cref="ScoreProbeDelays"/> 秒）轮询成绩，
    /// 一读到变化就提前收工。
    ///
    /// 补学时流程专用（v1.0.36）。它先把心跳与结算提交上去，**再原地等平台把账算完** ——
    /// 一次性回读拿到的必然是结算前的旧快照，会让人误判成"补了没效果"。
    /// 后台那条 <see cref="ScheduleScoreProbe"/> 不方便在这里用：它不随主循环、
    /// 补学时却必须"这一跳确认完才决定要不要再补下一跳"。
    /// </summary>
    private async Task ProbeScoreUntilSettledAsync(CourseItem course, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        foreach (var atSeconds in ScoreProbeDelays)
        {
            var wait = atSeconds - (DateTimeOffset.UtcNow - started).TotalSeconds;
            if (wait > 0) await DelayAsync(TimeSpan.FromSeconds(wait), ct);

            await _courses.EnrichWithScoreAsync(course, ct);
            ObserveScore(course);
            Publish();

            if (ShouldSkipCourse(course)) return;
        }
    }

    /// <summary>
    /// 解析结算接口的**业务结果**。
    ///
    /// 只看 HTTP 200 是不够的：平台把"任务是否受理"表达在响应体的 isSuccess / message 里，
    /// HTTP 层几乎永远是 200 —— 这正是「发了结算但分数不涨、谁也不知道成没成」的根源。
    /// 抽成纯静态函数是为了能在 <c>--selftest</c> 里离线验算各种响应形态。
    /// </summary>
    public static SettleOutcome ReadSettleOutcome(JsonElement? root)
    {
        if (root is not { } r || r.ValueKind != JsonValueKind.Object)
            return new SettleOutcome(true, null);

        foreach (var flag in new[] { "isSuccess", "success" })
        {
            if (r.TryGetProperty(flag, out var v) && v.ValueKind == JsonValueKind.False)
                return new SettleOutcome(false, FirstString(r, "message", "msg", "errorMsg"));
        }

        // data 在结算接口里放的是异步任务 id（字符串），拿它当"任务已受理"的凭证记进日志。
        var taskId = FirstString(r, "taskId");
        if (r.TryGetProperty("data", out var d))
        {
            if (d.ValueKind == JsonValueKind.String) taskId = d.GetString();
            else if (d.ValueKind == JsonValueKind.Object) taskId ??= FirstString(d, "taskId", "id");
        }

        return new SettleOutcome(true, taskId);
    }

    /// <summary>按顺序取第一个存在且非空的字符串字段。</summary>
    private static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (Str(el, name) is { Length: > 0 } s) return s;
        return null;
    }

    /// <summary>
    /// 结算后在后台按 <see cref="ScoreProbeDelays"/> 回读成绩，读到**这门课的成绩**涨了即收工。
    ///
    /// 这个循环**脱离主流程独立跑**：主循环照旧按节奏切课件、发心跳，一秒都不耽误，
    /// 用户看到的是"课件照切、分数在后台自己跳上来"。
    ///
    /// ★ 基线取「发起结算那一刻这门课的成绩」（v1.0.31 修）。
    ///   原来拿全局的 <c>_scoreVersion</c> 当判据，它被两条无关路径破坏：
    ///     ① 换课时会被重置为 0 —— 上一门课没跑完的探针带着旧版本号，永远等不到"更大"；
    ///     ② 只有心跳回读（<see cref="ObserveScore"/>）会推进它，**探针自己的回读反而不算数**。
    ///   于是「✓ 成绩已刷新」只会在心跳恰好先发现时命中（并发竞态），
    ///   一换课或一停引擎，探针就必然误报"成绩没有变化"。
    ///   白天日志 14:20:38 / 19:13:56 / 20:15:25 三条警告全部来自这里。
    /// </summary>
    private void ScheduleScoreProbe(CourseItem course)
    {
        // 基线＝结算发起时这门课的成绩。探针只关心"它涨了没有"。
        var baseline = (Score: course.LearnScore, Duration: course.CompletedDuration);
        var cts = new CancellationTokenSource();
        // 上一轮的盯成绩还没跑完就换成这一轮：以最新一次结算的基线为准。
        Interlocked.Exchange(ref _scoreProbeCts, cts)?.Cancel();
        _ = Task.Run(() => ProbeScoreAsync(course, baseline, cts.Token));
    }

    /// <summary>成绩相对基线是否已经变了（得分与学习时长任一变化即算）。</summary>
    public static bool ScoreMovedSince(
        (double? Score, double? Duration) baseline, CourseItem course)
        => !SameNumber(baseline.Score, course.LearnScore)
           || !SameNumber(baseline.Duration, course.CompletedDuration);

    private async Task ProbeScoreAsync(
        CourseItem course,
        (double? Score, double? Duration) baseline,
        CancellationToken ct)
    {
        try
        {
            await ProbeScoreCoreAsync(course, baseline, ct);
        }
        catch (Exception ex)
        {
            // 这是后台任务：异常逃出去只会变成一个没人看的 Task 异常，
            // 而"盯成绩"本身失败了恰恰是最需要留痕的事。
            Emit($"⚠ 结算后盯成绩出错：{ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task ProbeScoreCoreAsync(
        CourseItem course,
        (double? Score, double? Duration) baseline,
        CancellationToken ct)
    {
        var elapsed = 0.0;

        foreach (var gap in ScoreProbeDelays)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(gap), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            elapsed += gap;

            // 心跳那边的回读已经先看到变化（并且日志里已经报过"平台成绩已更新"）——
            // 那就不必再发一次请求、也不必再报一遍，静默收工即可。
            if (ScoreMovedSince(baseline, course)) return;

            // 刻意用 CancellationToken.None：引擎停掉/用户停课都不该打断这次回读，
            // 否则"停止后最后一次结算"永远等不到落库反馈。
            await _courses.EnrichWithScoreAsync(course, ct: CancellationToken.None);
            Publish();

            // 这一跳是探针自己回读到的。先**静默**把观测基准推到新值，
            // 免得心跳路径随后把同一跳又打印一遍；再由探针给出带"落库耗时"的确认行。
            if (ScoreMovedSince(baseline, course))
            {
                ObserveScore(course, silent: true);
                Emit($"✓ 成绩已刷新（结算后 {elapsed:0} 秒落库）："
                     + $"得分 {FormatNum(course.LearnScore)}，"
                     + $"学习时长 {FormatNum(course.CompletedDuration)} 分钟");
                Publish();
                return;
            }
        }

        Emit($"⚠ 结算已提交但 {elapsed:0} 秒内成绩没有变化"
             + $"（得分 {FormatNum(course.LearnScore)}，学习时长 {FormatNum(course.CompletedDuration)} 分钟）——"
             + "平台可能还在排队；若一直不动，说明这次结算没被入账，可点「刷新已选课程」复核");
        Publish();
    }

    /// <summary>
    /// 记一次成绩观测。
    ///
    /// 心跳回读与结算后轮询共用这一个基准：只有相对上次**真的有变化**时才返回 true。
    /// 这样日志里就是一条干净的"成绩时间线"，不会出现"心跳记一次、轮询又记一次"的重复行。
    /// </summary>
    /// <param name="silent">
    /// true = 只推进观测基准、不打日志。结算探针自己回读拿到新值时用它"吃掉"这一跳，
    /// 免得心跳路径随后把同一跳再打印一遍（探针那边会给出带"落库耗时"的确认行）。
    /// </param>
    private bool ObserveScore(CourseItem course, bool silent = false)
    {
        var now = (Score: course.LearnScore ?? 0, Duration: course.CompletedDuration ?? 0);

        if (_lastScoreObservation is { } prev
            && SameNumber(prev.Score, now.Score)
            && SameNumber(prev.Duration, now.Duration))
            return false;

        var from = _lastScoreObservation;
        _lastScoreObservation = now;
        _scoreRefreshedAt = DateTimeOffset.Now;

        if (!silent)
        {
            Emit(from is { } f
                ? $"✓ 平台成绩已更新：得分 {FormatNum(f.Score)} → {FormatNum(now.Score)}，"
                  + $"学习时长 {FormatNum(f.Duration)} → {FormatNum(now.Duration)} 分钟"
                : $"✓ 平台成绩：得分 {FormatNum(now.Score)}，学习时长 {FormatNum(now.Duration)} 分钟"
                  + "（后续每次结算都会回读对比）");
        }
        return true;
    }

    private static bool SameNumber(double? a, double? b)
        => Math.Abs((a ?? 0) - (b ?? 0)) < 0.005;

    private static string FormatNum(double? v) => v is { } n ? n.ToString("0.##") : "—";


    public void ClearQueue()
    {
        lock (_sync) _queue.Clear();
        Publish();
        NotifyQueueChanged();
    }

    /// <summary>
    /// 「清除已合格课程」要不要删掉这一门（v1.0.37）。
    ///
    /// 判据**复用 <see cref="ShouldSkipCourse"/>** —— 那是引擎里唯一的"这门课过没过"口径，
    /// 界面上成绩胶囊、状态列的「已及格」也都出自它。这里再写一套"分数≥分数线"的话，
    /// 迟早出现"列表说已及格、按钮却不清它"这种自相矛盾。
    ///
    /// 唯一的附加条件：**正在挂的那门不动**。删它会让运行循环立刻收尾
    /// （<see cref="IsStillQueued"/> 返回 false），而它要么马上被自动切走、
    /// 要么本来就在正常推进 —— 用户点这个按钮的意图是"清掉积压的旧账"，不是"打断当前"。
    /// </summary>
    public static bool ShouldClearPassed(CourseItem course, bool isCurrentRunning)
        => !isCurrentRunning && ShouldSkipCourse(course);

    /// <summary>
    /// 清掉队列里已经合格的课程（挂满 / 已达分数线），返回实际清掉的条数。
    ///
    /// 与「清空队列」的区别：清空是无差别全删（含正在挂的那门，会中断当前），
    /// 这个是"收拾残局"——只清那些引擎本来也会一路跳过的课，
    /// 队列里还没挂完的课一门不动。
    /// </summary>
    public int ClearPassed()
    {
        var running = State is EngineState.Running or EngineState.Paused;
        var currentKey = running && _currentCourse is { } cur ? QueueKey(cur) : null;

        int removed;
        lock (_sync)
        {
            removed = _queue.RemoveAll(c =>
                ShouldClearPassed(c, currentKey is not null && QueueKey(c) == currentKey));
        }

        if (removed > 0)
        {
            Publish();
            NotifyQueueChanged();   // 队列存档同步，重启后不会把清掉的课又搬回来
        }

        return removed;
    }

    /// <summary>把队列中某一项上移一位（对应"优先级调整"）。</summary>
    public void MoveUp(CourseItem course)
    {
        lock (_sync)
        {
            var i = _queue.FindIndex(q => q.CourseNo == course.CourseNo && q.Guid == course.Guid);
            if (i > 0) (_queue[i - 1], _queue[i]) = (_queue[i], _queue[i - 1]);
        }
        Publish();
        NotifyQueueChanged();
    }

    // ── 运行控制 ──────────────────────────────────────────

    public void Start()
    {
        if (State is EngineState.Running or EngineState.Paused) return;
        if (Queue.Count == 0)
        {
            Emit("⚠ 队列为空，先到「课程」页加入课程");
            return;
        }

        _cts = new CancellationTokenSource();
        _resume.Set();
        _completed = 0;
        _currentIndex = -1;
        _tokenExpiredAt = null;   // 新一轮启动视为全新会话（重新登录后的自动续挂走这里）
        _runner = Task.Run(() => RunAsync(_cts.Token));
        Emit("▶ 引擎已启动");
    }

    public void Pause()
    {
        if (State != EngineState.Running) return;
        _resume.Reset();
        Emit("⏸ 已暂停（当前心跳循环挂起）");
        SetState(EngineState.Paused);
    }

    public void Resume()
    {
        if (State != EngineState.Paused) return;
        // 过期暂停必须先重新登录（调用方负责换好 token）再恢复；
        // 恢复即视为会话已续上，撤掉界面的过期横幅。
        if (_tokenExpiredAt is { } expired && DateTimeOffset.Now - expired < TimeSpan.FromMinutes(1))
            Emit("▶ 会话已续上，从暂停处继续挂机");
        _tokenExpiredAt = null;
        _resume.Set();
        Emit("▶ 已恢复");
        SetState(EngineState.Running);
    }

    public void Stop()
    {
        if (_cts is null) return;
        Emit("⏹ 正在停止…");
        SetState(EngineState.Stopping);
        _resume.Set();          // 唤醒被暂停阻塞的循环
        _cts.Cancel();
    }

    /// <summary>等待引擎完全停止（用于退出程序时优雅收尾）。</summary>
    public async Task WaitForStopAsync(TimeSpan timeout)
    {
        if (_runner is null) return;
        try
        {
            await Task.WhenAny(_runner, Task.Delay(timeout));
        }
        catch
        {
            // 忽略
        }
    }

    // ── 主循环 ────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        SetState(EngineState.Running);

        // 开场先"浏览"片刻，避免"登录即挂课"的机械开篇
        try
        {
            await Task.Delay(Humanize.BrowsingDelay(), ct);
        }
        catch (OperationCanceledException)
        {
            Finish();
            return;
        }

        // 刻意不拍快照：用户随时可能往队列里加课、删课或调序。
        // 引擎每轮都从「当前队列里第一个尚未处理过的课」取，才能跟上这些操作 ——
        // 否则挂课途中新加入的课程永远不会被挂，界面上看就是"换了课程还在挂旧的"。
        var processed = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(ct);

                CourseItem? course;
                int index;
                lock (_sync)
                {
                    index = _queue.FindIndex(c => !processed.Contains(QueueKey(c)));
                    course = index >= 0 ? _queue[index] : null;
                }

                if (course is null) break;   // 队列里没有待处理的新课程了

                processed.Add(QueueKey(course));

                _currentIndex = index;
                _courseName = course.CourseName;
                _currentCourse = course;
                // 换课先清课件进度：上一门课的"共 N 个视频"不能带到这一门上
                _wareIndex = 0;
                _wareTotal = 0;
                // 身份随课程切换同步更新，界面据此把"当前在挂"标到正确的行上
                _courseNo = course.CourseNo;
                _olClassNo = course.OlClassNo;

                if (ShouldSkipCourse(course))
                {
                    Emit(course.IsFinished
                        ? $"✓ {course.CourseName} 学时已挂满，跳过"
                        : $"✓ {course.CourseName} 已达分数线（{course.LearnScore:0.##}），跳过");
                    _completed++;
                    Publish();
                    continue;
                }

                await RunCourseAsync(course, ct);
                _completed++;
                Publish();
            }

            Emit($"🎉 队列全部处理完毕：完成 {_completed} / {processed.Count} 门");
        }
        catch (OperationCanceledException)
        {
            Emit("⏹ 引擎已停止");
            // 停止前把已经挂到的进度落库。
            // 平台的成绩单只有"结算"才会刷新（心跳只更新最远播放位置），
            // 少这一步，中途停止就等于白挂。
            await TrySettleCurrentAsync();
        }
        catch (Exception ex)
        {
            Emit($"✖ 引擎异常终止：{ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>
    /// 这门课还要不要挂（＝这门课算不算过关）。
    ///
    /// 挂满策略只看"时长够没够"；及格策略下分数已经够分数线也算过关 ——
    /// 有些课的要求时长比课件总时长还长（挂到底也满不了），只认时长就会永远挂不完。
    ///
    /// ★ 这是引擎里**唯一**的"这门课过没过"判据，三处必须共用它：
    ///   ① 课程开头 —— 已过关的课直接跳过；
    ///   ② 课件遍历循环 —— 中途达标就停止切剩余课件（v1.0.31 补，见 RunCourseAsync）；
    ///   ③ 课程收尾日志 —— 决定报"已完成"还是"仍未达标"（v1.0.31 从 course.IsFinished 改过来）。
    ///   曾经 ③ 用的是 course.IsFinished（要求 100% 挂满），及格模式下与 ①② 自相矛盾，
    ///   日志里就出现「✓ 已够分数线」10 秒后紧跟「⚠ 结算后仍未达标，可稍后重跑」。
    ///
    /// 公开为 public 是为了让 <c>--selftest</c> 能离线验算这套口径。
    /// </summary>
    public static bool ShouldSkipCourse(CourseItem course)
    {
        if (course.IsFinished) return true;
        if (LearnPolicy.Current != CompletionPolicy.PassScore) return false;

        return course.LearnScore is { } score
               && score >= LearnPolicy.NormalizePassScore(course.PassScore);
    }

    private async Task RunCourseAsync(CourseItem course, CancellationToken ct)
    {
        Emit($"── 开始课程：{course.CourseName}");

        // 成绩观测基准按课重置（必须赶在下面这次回读之前），
        // 否则这门课的第一次回读会被当成"上一门课的变化"而漏记。
        _lastScoreObservation = null;

        // 补齐学时数据（用于判断完成目标）
        await _courses.EnrichWithScoreAsync(course, ct);
        // 把开课时的成绩先记进日志：挂课前后各一行，涨没涨一眼可见
        ObserveScore(course);

        var detail = await _courses.GetCourseDetailAsync(course, ct);
        if (detail is null || detail.Wares.Count == 0)
        {
            Emit($"⚠ {course.CourseName}：未取到课件列表，跳过该课程");
            return;
        }

        // 目标时长：优先课程要求学时，缺失则累加课件时长；
        // 再按设置里的完成策略折算 —— 「及格就跳」只需要挂到分数线对应的比例。
        // 比例的分母是**视频占分权重**（CE002 percentage）：得分 = 时长比 × 权重，
        // 权重 80 的课 60 分要挂到 75% 时长，不是想当然的 60%。
        var fullTargetSeconds = NormalizeDurationSeconds(course.RequiredDuration);
        var courseTargetSeconds = LearnPolicy.TargetSeconds(
            fullTargetSeconds, course.PassScore, course.DurationScoreWeight);
        var weightNote = course.DurationScoreWeight is { } weightValue and > 0
            ? $"视频占分 {weightValue:0.##}%"
            : "视频占分未知（按 /100 估算）";
        if (fullTargetSeconds is { } fullTarget && courseTargetSeconds is { } reduced && reduced < fullTarget)
        {
            Emit($"ℹ 及格就跳：{weightNote}，课程目标 {FormatClock(fullTarget)} → {FormatClock(reduced)}"
                 + $"（分数线 {LearnPolicy.NormalizePassScore(course.PassScore):0.##}）");
        }
        else if (fullTargetSeconds is { } zeroFull && LearnPolicy.IsZeroMargin(course.PassScore, course.DurationScoreWeight))
        {
            // 零余量课（实测于 2026 年零余量 PDF 专区：分数线 100 分、
            // 视频占分 100%）：得分 = 已学 ÷ 要求 × 100%，要 100 分就得挂满 100% 时长，
            // 一秒余量都没有。日志里必须说清楚 —— 否则用户看到"挂了 15 分钟还没到 100"
            // 只会以为程序偷懒，实际上差的是平台单跳上限吃掉的那十几秒。
            Emit($"ℹ 本课零余量：{weightNote}"
                 + $"、分数线 {LearnPolicy.NormalizePassScore(course.PassScore):0.##}"
                 + $"，必须挂满完整时长 {FormatClock(zeroFull)} 才算过 ——"
                 + "平台单跳最多计 60s 学时，账本已按同一口径对齐");
        }
        var wareCount = detail.Wares.Count;
        _wareTotal = wareCount;
        _wareIndex = 0;   // 课件还没开始播，界面先显示"准备开始"

        // ★ 一次性拿到整门课所有课件的服务端进度。
        //   之前是等逐个课件进 RunWareAsync 时才查，几十个课件的课程要串行等好几轮网络往返，
        //   界面上看就是"一直在已挂过的课件里打转，迟迟不开始正式挂课"。
        var progressMap = await PrefetchProgressAsync(course, detail.Wares, ct);
        ct.ThrowIfCancellationRequested();

        // 课程级学时账本。基线取「各课件在服务端的 maxPlayTime 之和」——
        // 这正是平台的学时口径（实测各课件 maxPlayTime 之和 == 成绩单 CE002 的 finishValue），
        // 所以界面拿它按秒往前推，不会跟最后落库的成绩对不上。
        _courseBase = progressMap.Values.Sum(p => p?.MaxPlaySeconds ?? 0);
        // 界面「视频时长（已学 / 要求）」的分母用**完整要求学时**，不是"及格就跳"折算后的目标。
        // 平台成绩单的 CE002 就是拿已学比完整要求（实测 63分27秒 / 73 分钟），
        // 之前拿折算后的 43m48s 当分母，归档里已挂过 53 分钟的课一进队列就显示
        // 「54m04s / 43m48s」—— 已学超过要求，用户只会以为数字坏了。
        _courseTarget = fullTargetSeconds ?? detail.Wares.Sum(w => w.DurationSeconds ?? 0);
        _sessionGain = 0;
        _wareStart = 0;
        _played = 0;
        _target = 0;
        // 结算节奏按课重置：新课的头一次"期间结算"从此刻起算 3 分钟
        _lastSettleAt = DateTimeOffset.UtcNow;
        // 结算台账也按课重置，界面上的"已提交结算 N 次 / 得分已刷新"才是指当前这门课的
        _settleCount = 0;
        _lastSettleAccepted = null;
        _scoreRefreshedAt = null;
        Publish();

        // 排课顺序：先剔掉已经挂满的，剩下的按「已播放位置从远到近」排 ——
        // 快挂满的优先补完，成绩单能最快看到变化。OrderByDescending 是稳定排序，
        // 全都没进度时仍保持课件原本的顺序。
        var allWares = detail.Wares
            .Select(w => (
                Ware: w,
                Target: TargetSecondsFor(w, courseTargetSeconds, wareCount),
                Progress: progressMap.GetValueOrDefault(WareKey(w))))
            .ToList();

        var pending = allWares
            .Where(x => !IsWareFull(x.Progress, x.Target))
            .OrderByDescending(x => x.Progress?.MaxPlaySeconds ?? 0)
            .ToList();

        var fullCount = allWares.Count - pending.Count;
        if (fullCount > 0)
            Emit($"⏭ 已有 {fullCount} 个课件挂满，跳过（无需逐个等待）");

        if (pending.Count == 0)
        {
            if (!CourseNeedsTopUp(course))
            {
                Emit($"✓ 「{course.CourseName}」的课件均已挂满，无需再挂");
            }
            else
            {
                // ★★ 位置挂满 ≠ 平台学时挂满（v1.0.36）。
                //
                //   平台的两本账：课件 maxPlayTime（最远播放位置）与成绩单累计学时
                //   （每跳最多计入 60 秒）。旧版 <<WareProgress>> 的老注释把两者当成一本账
                //   （"各课件 maxPlayTime 之和 == CE002 的 finishValue"），位置一到顶就
                //   认定"这门课没得挂了"→ 直接结算收工。
                //
                //   2026-09-13 用户实测当场证伪：零余量 PDF 专区三门课的位置都在
                //   15:00，成绩单只有 14分44 / 14分52 / 14分45 秒 → 得分 98.22 / 99.11 /
                //   98.33，而它要求 100 分，于是永远卡住。
                //
                //   现在改为：位置满但课程没过关，挑一个课件走"补学时"通道
                //   （RunWareAsync 的 topUp 分支）—— 位置不再前进，只把平台学时账本补平。
                var topUpWare = allWares.OrderByDescending(x => x.Target).First();
                Emit($"⚠ 「{course.CourseName}」课件位置已挂满，但平台得分 {FormatNum(course.LearnScore)}"
                     + $" 未到分数线 {LearnPolicy.NormalizePassScore(course.PassScore):0.##}"
                     + $"（平台已计入 {course.CompletedText ?? "—"}）——"
                     + "位置满 ≠ 学时满，改走补学时通道");
                pending.Add(topUpWare);
            }
        }

        var playedWares = 0;
        foreach (var (ware, target, progress) in pending)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct);

            // ★ 课程级达标检查（v1.0.31）：及格策略下分数够了就整门课收工。
            //
            //   之前只有 RunWareAsync 的心跳循环里判 ShouldSkipCourse，那处的 break 只跳出
            //   「当前这一个课件」。外层这个 foreach 没有课程级检查，于是接着切下一个课件，
            //   1 分钟后又在心跳循环里判定"已够分数线"→ 又 break → 又发一次结算 → 再切……
            //   白天日志实测：某长视频课 18:54:57 够线后连切 15 个课件，17 分钟才真正收工。
            //
            //   平台侧看到的是「8~43 分钟的视频只播了 1 分钟就触发"播放完成结算"」，
            //   而且是每分钟一次 —— 这比 UA / TLS 指纹更能坐实"不是真人"，
            //   与撤下 refresh 探测是同一条纪律：不做没有正常人会做的调用。
            if (ShouldSkipCourse(course))
            {
                Emit($"⏹ 「{course.CourseName}」已达标，不再切换剩余课件");
                break;
            }

            // 中途被移出队列就不必再往下挂了
            if (!IsStillQueued(course))
            {
                Emit($"⏹ 「{course.CourseName}」已被移出挂机队列，跳过剩余课件");
                return;
            }

            // 课件之间的短暂停顿（真人切课件也要几秒）。
            // 只在"确实挂过上一节"之后才停：被跳过的课件之间不必等，
            // 否则几十个已挂满的课件光等待就要好几分钟。
            if (playedWares > 0)
                await DelayAsync(TimeSpan.FromSeconds(3 + Random.Shared.NextDouble() * 5), ct);

            var outcome = await RunWareAsync(
                course, ware, target, progress, detail.Wares.IndexOf(ware) + 1, ct);
            playedWares++;

            // 因课程达标而中断：立刻停，别把这个课件的"完成结算"再叠加上去。
            if (outcome == WareOutcome.PassScoreReached) break;
        }

        // 课程级结算：触发成绩计算，再回读一次完成度。
        // 平台的成绩计算是异步任务（接口返回 taskId），所以结算后要留出一点时间，
        // 否则紧接着回读拿到的还是旧快照，界面上看就是"结算了但数字没变"。
        Emit($"→ 课程结算：{course.CourseName}");
        try
        {
            await _courses.SaveCourseDetailAsync(course, ct);

            // 课程级结算同样是异步任务，也要挂上盯成绩 ——
            // 原来这里只回读两次、各隔 5 秒，而平台要几十秒才落库，
            // 于是「最后一门课跑完，得分永远停在旧值」。
            // 现在改成后台轮询：跑完队列、甚至用户按了停止，它都继续跟到底。
            _settleCount++;
            _lastSettleAt = DateTimeOffset.UtcNow;
            _lastSettleAccepted = true;
            ScheduleScoreProbe(course);

            // 平台的成绩计算是异步任务，落库有几十秒延迟（实测，见 ScoreProbeDelays）。
            // 按 10 / 20 秒两档回读，最多等 30 秒；判据用 ShouldSkipCourse（与引擎执行口径一致）——
            // 原来用 course.IsFinished，它是"100% 挂满"，及格模式下永远为 false，这两次回读必然跑满、白等 10 秒。
            // ★ 顺带修掉"抢跑"：原来是固定 2×5 秒，平台还没算完就下结论，日志里会出现
            //   「刚结算完就报仍未达标」的假警报（2026-09-13 PDF 专区三门课全部如此）。
            foreach (var waitSeconds in new[] { 10, 20 })
            {
                if (ShouldSkipCourse(course)) break;
                await DelayAsync(TimeSpan.FromSeconds(waitSeconds), ct);
                await _courses.EnrichWithScoreAsync(course, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Emit($"⚠ 课程结算失败：{ex.Message}");
        }

        await _courses.EnrichWithScoreAsync(course, ct);

        // ★ 判据必须与引擎实际执行的口径一致（v1.0.31）。
        //   引擎跑的是 ShouldSkipCourse（及格策略只看"得分 ≥ 分数线"，挂满策略才看时长），
        //   这里原来用 course.IsFinished —— 该属性要求「已完成 ≥ 要求」，即 100% 挂满。
        //   于是及格模式下必定自相矛盾，白天日志里三门课全部是：
        //     [14:18:57] ✓ 「习近平法治思想专题辅导报告」已够分数线（62.57），提前结束该课
        //     [14:19:08] ⚠ 习近平法治思想专题辅导报告 结算后仍未达标（72分57秒 / 115分钟），可稍后重跑
        //   实测那门课已完成 63.4% 时长、得分 63.43 ≥ 60，早就达标了。
        //   用户照提示去"稍后重跑"，等于白挂几个小时。
        // ★ 判据必须与引擎实际执行的口径一致（v1.0.31）再叠一层"是谁没到位"的区分（v1.0.35）：
        //   我们已经把课件挂满了（本地账本 ≥ 课程目标），平台却没给到分数线 ——
        //   那是平台侧的事（还在算 / 这一轮没入账），说"可稍后重跑"会让人以为程序偷懒。
        //   零余量的专区课最典型：14分44秒 / 15分钟 差的就是平台单跳上限吃掉的十几秒。
        var playedAll = _courseTarget > 0 && _courseBase + _sessionGain >= _courseTarget - 1;
        Emit(ShouldSkipCourse(course)
            ? $"✓ {course.CourseName} 已完成（{course.DurationText}）"
            : playedAll
                ? $"⚠ {course.CourseName} 课件已全部挂满（{course.DurationText}），"
                  + $"但平台得分 {FormatNum(course.LearnScore)} 未到分数线 "
                  + $"{LearnPolicy.NormalizePassScore(course.PassScore):0.##} ——"
                  + "平台结算可能还在排队，稍后可在课程页复核；若一直不涨再重跑"
                : $"⚠ {course.CourseName} 结算后仍未达标（{course.DurationText}），可稍后重跑");
    }

    /// <summary>课件这一轮的结束原因。</summary>
    private enum WareOutcome
    {
        /// <summary>正常播满（或够线中断以外的情形），可以继续下一个课件。</summary>
        Played,

        /// <summary>及格策略下分数已达标，整门课应当立刻收工。</summary>
        PassScoreReached,
    }

    private async Task<WareOutcome> RunWareAsync(
        CourseItem course,
        WareItem ware,
        double target,
        WareProgress? progress,
        int wareNo,
        CancellationToken ct)
    {
        var outcome = WareOutcome.Played;
        _wareName = ware.WareName;
        _target = target;
        _wareIndex = wareNo;   // 按大纲顺序报"正在播第几个"，与网页端目录编号一致

        // ★ 关键：平台按「每个课件的最远播放位置 maxPlayTime」累计学时。
        //   若不论服务端已记到哪里都从 00:00:00 重播，curPlayTime 永远超不过历史最远位置，
        //   心跳虽然返回 {"status":"success"}，学时却一分钟都不涨 —— 这正是"挂课无效"的根因。
        //   实测：14 个课件的 maxPlayTime 之和 = 7731s = 128.85 分钟，
        //         与 finishInfo CE002 的 finishValue(128.52 分钟) 吻合。
        //   进度已由 RunCourseAsync 一次性批量预取（含"已挂满则不发车"的过滤），此处直接取用。
        var resumeFrom = progress?.MaxPlaySeconds ?? 0;

        // ★ 补学时模式（v1.0.36）：位置已到顶、课程却还没过关。
        //   平台的两本账（位置 / 学时）正是在这里分了岔 —— 补学时期间位置原地不动，
        //   只把平台学时账本一截一截补齐（取证见 NeedsTopUp）。
        var topUp = NeedsTopUp(resumeFrom, target, CourseNeedsTopUp(course));

        // 记下这个课件的起点，课程级账本要靠它算"本课件贡献了多少增量"
        _wareStart = resumeFrom;
        _played = resumeFrom;
        Publish();

        if (topUp)
            Emit($"↻ 「{ware.WareName}」服务端位置已到 {progress!.MaxPlayTime}（目标 {FormatClock(target)}）、"
                 + $"平台学时 {course.CompletedText ?? "—"} —— 位置不再前进，只补学时");
        else if (resumeFrom > 0)
            Emit($"↻ 「{ware.WareName}」从服务端进度 {progress!.MaxPlayTime} 续播（目标 {FormatClock(target)}）");

        // 开局先补一次结算：服务端已有历史进度但这个课件从没结算过时，
        // 成绩单会停在 0/旧值 —— 用户看到的就是"时长明明在、分数还是 0"。
        // 补一次就能立刻把历史进度落进成绩单（平台不用等视频播完也算数）。
        if (NeedsEntrySettlement(resumeFrom))
        {
            await SettleProgressAsync(course, "开局结算", ct, probe: false);
            Emit($"→ 已把「{ware.WareName}」的既有进度提交结算");
        }

        var pageId = Humanize.NewPageId();

        // 1) 初始化学习记录（与网页端 CourseStudy 页同一调用与同一字段）
        //    平台只发 courseNo / olClassNo / pageId，返回第一个课件的 cataNo / wareCode / 上次播放位置。
        var initPayload = new Dictionary<string, object?>
        {
            ["courseNo"] = course.CourseNo,
            ["olClassNo"] = course.OlClassNo,
            ["pageId"] = pageId,
        };

        string? resumeWareCode = null;
        using (var initDoc = await SafePostDocAsync(ApiEndpoints.InitLearnRecord, initPayload, "初始化学习记录", ct))
        {
            var d = initDoc is null ? null : ApiResponseReader.Data(initDoc.RootElement);
            if (d is { } dd)
            {
                resumeWareCode = Str(dd, "wareCode");
                if (string.IsNullOrEmpty(ware.CataNo)) ware.CataNo = Str(dd, "cataNo");
            }
        }

        // 平台会从上次播放的位置续播；这里同步一下显示（不改变真实计时）
        if (!string.IsNullOrEmpty(resumeWareCode) && resumeWareCode != ware.WareId)
            Emit($"ℹ 服务端记录的续播课件为 {resumeWareCode}，本次按队列顺序从「{ware.WareName}」开始");

        // 2) 记录"开始播放"（operateType 1=播放 2=暂停 3=结束 4=拖动，videoStatus 由 operateType 映射）
        var optPayload = BuildOptPayload(course, ware, "1", _played);
        await SafePostAsync(ApiEndpoints.ListenVideoOptRecord, optPayload, "记录播放开始", ct);

        Emit($"▶ 学习中：{course.CourseName} / {ware.WareName}（目标 {FormatClock(target)}）");

        // 3) 心跳主循环
        var lastTick = DateTimeOffset.UtcNow;
        var beatCount = 0;
        // 补学时专门的计数：跑了多少跳、一共往平台学时账本里补了多少秒
        //（这些秒数不进 _played —— 位置已经封顶，硬加只会凭空多报位置）
        var topUpBeats = 0;
        var topUpCredited = 0;

        // 关键帧打点：网页端在播放越过每个打点时上报一次（markeTimePoint 原样回传）。
        var markerPoints = ParseMarkers(ware.MarkTimePoint);
        var reportedMarkers = new HashSet<int>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct);

            // 用户把这门课移出队列 → 立刻停挂，切下一门（不再空转心跳）
            if (!IsStillQueued(course))
            {
                Emit($"⏹ 「{course.CourseName}」已被移出挂机队列，立即结束该课");
                break;
            }

            // ★ 收尾精准等待（v1.0.18）：距目标已不足一个完整心跳周期时，只等到「刚好播满」为止，
            //   而不是硬等满 58–63 秒的整个周期。
            //
            //   原来是"等满一个周期 → 推进 → 才判断播完没"，于是进度条早就满了，
            //   界面却还要空转最多一分钟，用户看到的就是
            //   「视频进度已 19m05s/19m05s，播放时长还在涨，过了一会才切到下一个视频」。
            var normalBeat = Humanize.HeartbeatInterval();
            // 补学时模式不走近尾声等待：位置本来就在末尾，把周期缩短只会把请求打密、
            // 每一跳还得重新等平台的落库确认，得不偿失。
            var interval = topUp ? normalBeat : NextBeatInterval(_played, target, normalBeat);
            var isFinalBeat = !topUp && interval < normalBeat;

            _nextBeat = DateTimeOffset.UtcNow + interval;
            Publish();

            await DelayAsync(interval, ct);

            // 用户按了暂停：这段挂起的时间一秒都不该计入。
            // 原来 elapsed 会把整个暂停时长算进来（挂起两小时 = 报 7200s 学时），
            // 属于典型的虚报；现在按"是否真的等过"重置计时基点。
            if (await WaitIfPausedAsync(ct)) lastTick = DateTimeOffset.UtcNow;

            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - lastTick).TotalSeconds;
            lastTick = now;

            // 偶发"暂停再续播"，模拟人离开一下。
            // 收尾那一跳跳过：都已经在切课件了，再插一次随机暂停只会让用户干等。
            if (!isFinalBeat && Humanize.ShouldPause())
            {
                var pause = Humanize.PauseDuration();
                Emit($"⏸ 模拟暂停 {pause.TotalSeconds:0}s（不计入学时）");
                await SafePostAsync(ApiEndpoints.ListenVideoOptRecord,
                    BuildOptPayload(course, ware, "2", _played), "记录暂停", ct);

                // ★ 先跟界面打招呼：引擎这段一秒都不计入学时，而界面是按墙钟插值的。
                //   不告诉它，它就会照推不误，暂停结束的快照一到显示值被拽回去 ——
                //   用户看到的就是「进度条回退」。置上窗口 + 立刻推一份快照，界面随即停表。
                _pausingUntil = DateTimeOffset.UtcNow + pause;
                Publish();

                await DelayAsync(pause, ct);

                _pausingUntil = null;
                Publish();

                lastTick = DateTimeOffset.UtcNow;
                await SafePostAsync(ApiEndpoints.ListenVideoOptRecord,
                    BuildOptPayload(course, ware, "1", _played), "记录续播", ct);
            }

            var learnSeconds = Humanize.LearnSeconds(elapsed);

            // ★★ 账本按「平台会计口径」前进（v1.0.35，本项目最重要的一条平台事实）。
            //
            //   心跳间隔是拟人化的 58–63 秒，但平台对**单次心跳最多只计入 60 秒**，
            //   超出部分直接丢掉（见 Humanize.MaxCreditPerBeatSeconds 的取证）。
            //   本地账本原先按未截断的学时前进，天生比平台"多"1~2%：
            //   普通课程按分数线打折（60 分 ÷ 80% 权重 = 只挂 75% 时长）看不出来，
            //   但「分数线 100 + 视频占分 100%」的 PDF 专区课要求 100% 时长、零余量，
            //   于是账本跑到 900 秒、平台只认 884 秒 —— 挂到 98 分就"完成"并切课，
            //   永远到不了 100 分（用户 2026-09-13 实测三门课全部如此）。
            var credited = Math.Min(learnSeconds, Humanize.MaxCreditPerBeatSeconds);

            // 进度不得超过课件总时长：剩余不足一跳时只补到剩余量（向下取整），绝不多报位置。
            // （旧版这里是"收尾跳直接顶到目标"，属于在本地凭空补秒 —— 平台不认，
            //   零余量课程正是死在这一句上。）
            // ★ 补学时模式例外：remain 已经是 0，照这段算会把 credited 压成 1 秒
            //   （进而在下一句"已播满"判断里立刻收工）—— 补学时正是要绕开它。
            if (!topUp)
            {
                var remain = target - _played;
                if (remain < credited) credited = Math.Max(1, (int)Math.Floor(remain));
            }

            // 补学时模式：位置就报课件末尾 —— 不越界（平台可能校验 curPlayTime ≤ 时长），
            // 也不会再前进，平台只看这一跳的 learnRealTime 往学时账本里累加。
            var curPlayTime = topUp
                ? Humanize.FormatPlayTime(target)
                : Humanize.FormatPlayTime(_played + credited + Humanize.ProgressJitter());

            // ★ isBlur 恒为 "0"（2026-09-12 实测定案）：平台对 isBlur=1 的心跳**整条拒绝**
            //   （响应 status=blur「无效心跳：页面失去焦点」），这一跳的学时一分不记。
            //   旧版按 3.5% 概率模拟失焦，纯属白丢学时（实测 75 分钟丢 2 跳 ≈ 2 分钟）。
            //   恒不失焦 = 用户把页面停在前台不动，是完全正常的用户行为，无指纹风险。
            var isBlur = "0";

            // 与网页端同构的心跳报文。
            // 注意 learnTime = learnRealTime × videoSpeed，且 videoSpeed 是字符串 "1.0"。
            // learnRealTime 用**平台口径**的秒数（≤ 单跳上限），见上面的 credited。
            var payload = new Dictionary<string, object?>
            {
                ["cataNo"] = ware.CataNo ?? "",
                ["classCourseCenterCode"] = course.CenterCode,
                ["courseNo"] = course.CourseNo,
                ["curPlayTime"] = curPlayTime,
                ["isBlur"] = isBlur,
                ["learnRealTime"] = credited,
                ["learnTime"] = Math.Round(credited * 1.0, 5),
                ["olClassNo"] = course.OlClassNo,
                ["pageId"] = pageId,
                ["status"] = "1",
                ["videoSpeed"] = "1.0",
                ["wareId"] = ware.WareId,
                ["wareType"] = ware.WareType,
            };

            beatCount++;
            // 补学时的"第几跳"按**尝试次数**算：失败的那跳不推进学时，但周期确实用掉了，
            // 不按尝试次数计数就会在持续失败时无限循环。
            if (topUp) topUpBeats++;
            var ok = await SendHeartbeatAsync(ApiEndpoints.SaveLearnHertRecord, payload, ct);

            // ★ 只有被平台受理的这一跳才推进账本（v1.0.35）。
            //   旧版无论受理与否都先加进去，夜里几次网络抖动就凭空多出几分钟"学时"，
            //   账本跑到头、平台却没记 —— 对零余量的专区课同样是致命的。
            //   失败时账本原地不动，下个周期自然重来（这一跳的秒数不再补记，绝不虚报）。
            if (ok)
            {
                // 补学时只推进"学时账本"，绝不推进位置 —— 位置硬加只会凭空多报播放位置
                if (topUp) topUpCredited += credited;
                else _played += credited;
            }

            var beatNote = learnSeconds > credited
                ? $"（+{credited}s；本跳间隔 {learnSeconds}s 超出平台单跳上限 {Humanize.MaxCreditPerBeatSeconds}s，多出的 {learnSeconds - credited}s 不计学时）"
                : $"（+{credited}s）";
            Emit(ok
                ? topUp
                    ? $"♥ 心跳 #{beatCount}（补学时 {topUpBeats}/{MaxTopUpBeats}）"
                      + $"  {curPlayTime}/{FormatClock(target)}{beatNote}"
                    : $"♥ 心跳 #{beatCount}  {curPlayTime}/{FormatClock(target)}{beatNote}"
                : $"✖ 心跳 #{beatCount} 失败，这一跳不记入学时，下个周期重来");

            Publish();

            // ★ 播满即刻收尾（v1.0.18）：后面紧接着的打点上报、成绩回读对这个已经播完的课件
            //   已无意义，早点跳出循环，把"播满 → 结算 → 切下一个"之间的空转压到最小。
            //   结算不会漏 —— 跳出后紧跟着的结束流程里就有一次结算。
            // 补学时模式下 _played 恒等于 target（位置早就满了），这一条不能生效 ——
            // 否则第一跳就会把补学时刚开的头掐掉。
            if (!topUp && _played >= target)
            {
                // ★ 零余量课的第二条入口（v1.0.36）：位置是**这一趟**才挂满的。
                //
                //   课 2「新版《…》的宣贯与解读」就是这种：进场时位置 898/900（差 1 秒，
                //   够不上 IsWareFull 的容差），于是老老实实续播、几秒后挂满 ——
                //   可平台学时只记到 14分52秒，得分 99.33 < 100，问题与课 1/3 一模一样。
                //   只堵住"进场就满"那条路是不够的，这里必须也转成补学时。
                //
                //   判据与入场时同一套（位置满 + 课程未达标）。普通课不会误入：
                //   它们有余量，早在心跳循环里因达标 break 了，走不到这一句。
                if (NeedsTopUp(_played, target, CourseNeedsTopUp(course)))
                {
                    topUp = true;
                    topUpBeats = 0;
                    Emit($"ℹ 「{ware.WareName}」位置已挂满（{FormatClock(_played)}），但课程得分 "
                         + $"{FormatNum(course.LearnScore)} 未到分数线 "
                         + $"{LearnPolicy.NormalizePassScore(course.PassScore):0.##}"
                         + " —— 位置满 ≠ 学时满，原地补学时");
                    continue;
                }

                Emit($"✓ 课件完成：{ware.WareName}（{FormatClock(_played)}），正在切换下一个");
                break;
            }

            // 关键帧打点上报：越过一个打点就报一次（与网页端 video timeupdate 行为一致）
            foreach (var mk in markerPoints)
            {
                if (mk > _played || reportedMarkers.Contains(mk)) continue;
                reportedMarkers.Add(mk);

                await SafePostAsync(ApiEndpoints.ListenVideoMarkProgress, new Dictionary<string, object?>
                {
                    ["cataNo"] = ware.CataNo ?? "",
                    ["courseNo"] = course.CourseNo,
                    ["curPlayTime"] = Humanize.FormatPlayTime(mk),
                    ["markeTimePoint"] = Humanize.FormatPlayTime(mk),
                    ["olClassNo"] = course.OlClassNo,
                    ["pageId"] = pageId,
                    ["wareId"] = ware.WareId,
                    ["wareType"] = ware.WareType,
                }, $"打点 {Humanize.FormatPlayTime(mk)}", ct);
            }

            // ★ 补学时模式（v1.0.36）：每一跳都必须「结算 + 等平台落库确认」。
            //
            //   心跳只写平台的位置/原始学时账，**把学时汇总进成绩单的只有结算**
            //   （旧结论：只发心跳分数纹丝不动）。补学时本来就只有几跳，
            //   不等落库就不知道自己补够了没有 —— 少补一秒，零余量课照样到不了 100 分。
            if (topUp)
            {
                await SettleProgressAsync(course, "补学时结算", ct);
                await ProbeScoreUntilSettledAsync(course, ct);
                Publish();

                if (ShouldSkipCourse(course))
                {
                    Emit($"✓ 「{course.CourseName}」补学时后已达标（得分 {course.LearnScore:0.##}，"
                         + $"本课件累计补 {topUpCredited}s）");
                    outcome = WareOutcome.PassScoreReached;
                    break;
                }

                if (topUpBeats >= MaxTopUpBeats)
                {
                    Emit($"⚠ 补学时已跑满 {MaxTopUpBeats} 跳（本课件累计 +{topUpCredited}s），平台得分 "
                         + $"{FormatNum(course.LearnScore)} 仍未到分数线 "
                         + $"{LearnPolicy.NormalizePassScore(course.PassScore):0.##}"
                         + " —— 位置到顶后平台可能不再计入超出的学时，先收工");
                    break;
                }

                continue;
            }

            // 长视频也要让「得分」动起来：按固定节奏补发结算。
            // 只发心跳的话，平台成绩单要等视频播完那一刻才刷新一次，中间几十分钟纹丝不动。
            if (NeedsPeriodicSettlement(DateTimeOffset.UtcNow - _lastSettleAt))
            {
                await SettleProgressAsync(course, "期间结算", ct);
                Emit($"→ 已提交一次进度结算（每 {SettlementInterval.TotalMinutes:0} 分钟一次）");
            }

            // 每次心跳后回读成绩并推送快照：平台成绩在结算后异步落库，回读越勤，
            // 界面「得分」跟得越紧（原来每 5 次心跳才回读一次，用户看到分数五分钟不动）。
            // 回读失败不影响挂课（内部已容错）；及格模式下若已达标则提前收工。
            await _courses.EnrichWithScoreAsync(course, ct);
            // 心跳回读也要进"成绩时间线"：平台落库有时会晚于结算后的轮询窗口，
            // 那一跳只有靠心跳回读才抓得到；没有这行，日志里就会出现"结算了但看不到刷新"。
            ObserveScore(course);
            Publish();
            if (ShouldSkipCourse(course))
            {
                Emit(course.IsFinished
                    ? $"✓ 服务端已判定「{course.CourseName}」学时达标，提前结束该课"
                    : $"✓ 「{course.CourseName}」已够分数线（{course.LearnScore:0.##}），提前结束该课");
                outcome = WareOutcome.PassScoreReached;
                break;
            }
        }

        // 这个课件挂完了，把它贡献的净增量并进课程级账本。
        // 并完必须把 _wareStart 顶到 _played —— 否则 Publish 里那段
        // 「当前课件增量」会把同一段时间重复累加，课程级数字会虚高。
        _sessionGain += Math.Max(0, _played - _wareStart);
        _wareStart = _played;
        Publish();

        // 4) 记录"结束播放"
        await SafePostAsync(ApiEndpoints.ListenVideoOptRecord,
            BuildOptPayload(course, ware, "3", _played), "记录播放结束", ct);

        // 5) 关键：通知平台结算。
        //    网页端在 video 的 ended 事件里调 saveComputeTask4AfterVideoPlayed，
        //    由平台后台任务把播放进度汇总进 finishInfo 的 CE002。
        //    只发心跳不调结算的话，maxPlayTime 会涨，但"已学分钟数"一动不动。
        //
        //    ★ 但「够线中断」这条路径不发（v1.0.31）：这个接口的语义是
        //      「视频播放完成后结算」，而这条路径上只播了不到一个心跳周期（约 1 分钟）。
        //      平台侧看到的就成了「43 分钟的视频 1 分钟就播完」——比 UA / TLS 更硬的机器特征。
        //      课程既然已经达标，这一分钟的增量也无需入账；紧接着的课程级结算会把
        //      整门课的进度照常汇总。
        if (outcome == WareOutcome.PassScoreReached)
        {
            Emit($"⏭ 「{ware.WareName}」因课程已达标收工，不发「视频完成结算」");
        }
        else
        {
            await SettleProgressAsync(course, "视频完成结算", ct);
        }

        return outcome;
    }

    // ── 工具方法 ──────────────────────────────────────────

    // ── 课件进度预取 ──────────────────────────────────────

    /// <summary>课件定位键（cataNo 可能为空，wareId 才是主键）。</summary>
    private static string WareKey(WareItem w) => (w.CataNo ?? "") + "@" + w.WareId;

    /// <summary>单个课件的目标时长：优先课件自身时长；否则把课程目标均摊到各课件。</summary>
    private static double TargetSecondsFor(WareItem ware, double? courseTargetSeconds, int wareCount)
    {
        var target = ware.DurationSeconds ?? (courseTargetSeconds is { } t ? t / Math.Max(1, wareCount) : 0);
        return target > 0 ? target : 30 * 60;   // 兜底 30 分钟
    }

    /// <summary>服务端记录的最远播放位置是否已达到该课件的目标时长。</summary>
    private static bool IsWareFull(WareProgress? p, double target)
        => p is not null && p.MaxPlaySeconds >= target - 1;

    /// <summary>
    /// 一次性并发拉取整门课所有课件的服务端进度。
    ///
    /// 逐个串行查的话，课件多的课程要等好几轮网络往返才开始挂课 ——
    /// 用户看到的就是"程序在已挂过的课件里一个个试、很久才真正进入挂课"。
    /// 并发度限制在 6：够快，又不至于把平台请求打爆。
    /// </summary>
    private async Task<Dictionary<string, WareProgress?>> PrefetchProgressAsync(
        CourseItem course, IReadOnlyList<WareItem> wares, CancellationToken ct)
    {
        var map = new Dictionary<string, WareProgress?>(StringComparer.Ordinal);
        if (wares.Count == 0) return map;

        using var gate = new SemaphoreSlim(6);
        var tasks = wares.Select(async w =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var p = await _courses.GetWareProgressAsync(course, w, ct).ConfigureAwait(false);
                return (Key: WareKey(w), Progress: p);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        foreach (var r in await Task.WhenAll(tasks).ConfigureAwait(false))
            map[r.Key] = r.Progress;

        return map;
    }

    /// <summary>带容错的 POST：失败只记日志，不打断流程。</summary>
    private async Task<bool> SafePostAsync(string url, object payload, string what, CancellationToken ct)
    {
        try
        {
            await _api.PostRawAsync(url, payload, ct: ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 这里承诺的是"失败只记日志、不打断流程"，所以必须捕获全部异常类型；
            // 只捕 ApiException 会让其它异常穿透，直接中断整门课的后续步骤。
            Emit($"⚠ {what}失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 带容错的 POST，并把响应解析为 JSON 文档（失败返回 null，只记日志、不打断流程）。
    /// 用于 initLearnRecord 这类「需要读返回值」的调用。
    /// </summary>
    private async Task<JsonDocument?> SafePostDocAsync(string url, object payload, string what, CancellationToken ct)
    {
        try
        {
            return await _api.PostRawAsync(url, payload, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Emit($"⚠ {what}失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 发送学时心跳，并检查服务端返回的**业务状态**。
    ///
    /// 网页端在 <c>data.status !== "success"</c> 时会暂停播放并提示错误；
    /// 这里同样不能只看 HTTP 200 —— 服务端把"心跳过密""会话失效"等原因都表达在
    /// data.status 里，只看 HTTP 码会把失败当成功写进日志，界面上就成了
    /// "一切正常、学时却不动"。
    /// </summary>
    private async Task<bool> SendHeartbeatAsync(string url, object payload, CancellationToken ct)
    {
        try
        {
            using var doc = await _api.PostRawAsync(url, payload, ct: ct);
            if (doc is null) return true;

            var data = ApiResponseReader.Data(doc.RootElement);
            var status = data is { } d ? Str(d, "status") : null;

            // 没有 status 字段时按成功处理（不同课件类型的响应结构略有差异）
            if (string.IsNullOrEmpty(status) || status == "success") return true;

            var desc = data is { } dd ? Str(dd, "heartDesc") : null;
            Emit($"⚠ 心跳被服务端拒绝：status={status}{(string.IsNullOrEmpty(desc) ? "" : $"（{desc}）")}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Emit($"⚠ 学时心跳失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 构造「播放操作记录」报文（listenVideoOptRecord），字段与网页端播放器逐一对齐：
    /// cataNo / classCourseCenterCode / courseNo / olClassNo / operateType /
    /// videoBeginTime / videoSpeed / videoStatus / wareId / wareType。
    /// operateType：1=播放 2=暂停 3=结束 4=拖动 5=页面隐藏。
    /// </summary>
    private static Dictionary<string, object?> BuildOptPayload(
        CourseItem course, WareItem ware, string operateType, double playedSeconds)
    {
        return new Dictionary<string, object?>
        {
            ["cataNo"] = ware.CataNo ?? "",
            ["classCourseCenterCode"] = course.CenterCode,
            ["courseNo"] = course.CourseNo,
            ["olClassNo"] = course.OlClassNo,
            ["operateType"] = operateType,
            ["videoBeginTime"] = Humanize.FormatPlayTime(playedSeconds),
            ["videoSpeed"] = "1.0",
            ["videoStatus"] = MapVideoStatus(operateType),
            ["wareId"] = ware.WareId,
            ["wareType"] = ware.WareType,
        };
    }

    /// <summary>与网页端播放器 C() 完全一致的 operateType → videoStatus 映射。</summary>
    private static string MapVideoStatus(string operateType) => operateType switch
    {
        "1" => "1",   // 播放中
        "2" => "2",   // 已暂停
        "3" => "0",   // 播放结束
        "4" => "1",   // 拖动（拖动后仍处于播放态）
        "5" => "1",   // 页面隐藏
        _ => "2",
    };

    /// <summary>
    /// 解析课件关键帧串（网页端 markeTimePoint：逗号分隔的 HH:MM:SS / MM:SS / 纯秒），
    /// 返回升序秒数列表。网页端在播放越过的每个关键帧上报一次。
    /// </summary>
    private static List<int> ParseMarkers(string? raw)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':');
            int seconds;

            if (parts.Length == 3
                && int.TryParse(parts[0], out var h)
                && int.TryParse(parts[1], out var m)
                && int.TryParse(parts[2], out var s))
            {
                seconds = h * 3600 + m * 60 + s;
            }
            else if (parts.Length == 2
                     && int.TryParse(parts[0], out var m2)
                     && int.TryParse(parts[1], out var s2))
            {
                seconds = m2 * 60 + s2;
            }
            else if (int.TryParse(token, out var plain))
            {
                seconds = plain;
            }
            else
            {
                continue;
            }

            if (seconds > 0) list.Add(seconds);
        }

        list.Sort();
        return list;
    }

    /// <summary>读取 JSON 对象上的字符串字段。</summary>
    private static string? Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>可取消的延时（供暂停/停止响应）。</summary>
    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// 暂停时挂起循环，恢复后继续。
    /// 返回 true 表示**真的挂起过**（用于让调用方重置计时基点 —— 暂停的时长不能算学时）。
    /// </summary>
    private async Task<bool> WaitIfPausedAsync(CancellationToken ct)
    {
        if (_resume.IsSet) return false;
        await Task.Run(() => _resume.Wait(ct), ct);
        return true;
    }

    private static Dictionary<string, object?> Merge(
        Dictionary<string, object?> baseDict,
        Dictionary<string, object?> overrides)
    {
        var merged = new Dictionary<string, object?>(baseDict);
        foreach (var kv in overrides) merged[kv.Key] = kv.Value;
        return merged;
    }

    /// <summary>把接口返回的学时值折算为秒（兼容分钟/秒两种口径）。</summary>
    private static double? NormalizeDurationSeconds(double? value)
    {
        if (value is not { } v || v <= 0) return null;
        // 约定：数值小于 600 视为分钟（一节课最多几百分钟），否则视为秒
        return v < 600 ? v * 60 : v;
    }

    private static string FormatClock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h{ts.Minutes:00}m"
            : $"{ts.Minutes}m{ts.Seconds:00}s";
    }

    private void Finish()
    {
        State = EngineState.Stopped;
        _nextBeat = null;
        _wareName = null;
        _wareIndex = 0;
        _wareTotal = 0;
        _cts?.Dispose();
        _cts = null;
        _runner = null;
        Publish();
        Emit("⏹ 引擎已停止");
    }

    private void SetState(EngineState state)
    {
        State = state;
        Publish();
    }

    private void Publish()
    {
        // 课程级已学 = 基线 + 已挂完课件的增量 + 当前课件正在推进的那部分。
        // 最后一项不能省：少了它，课程级数字要等一整个课件挂完（可能几十分钟）才跳一次。
        var coursePlayed = _courseBase + _sessionGain;
        if (_played > _wareStart) coursePlayed += _played - _wareStart;

        Snapshot?.Invoke(new LearnSnapshot(
            State, _courseName, _wareName, _played, _target,
            _currentIndex, Queue.Count, _completed, _nextBeat,
            _courseNo, _olClassNo, coursePlayed, _courseTarget,
            _wareIndex, _wareTotal,
            _settleCount,
            // 从没结算过就不要把 DateTimeOffset.MinValue 抛给界面（会显示成 0001-01-01）
            _settleCount > 0 ? _lastSettleAt : null,
            _lastSettleAccepted,
            _scoreRefreshedAt,
            // 只上报"还在未来"的暂停窗口：已经过去的暂停不该继续让界面停表
            _pausingUntil is { } pauseUntil && pauseUntil > DateTimeOffset.UtcNow ? pauseUntil : null,
            _tokenExpiredAt));
    }

    private void Emit(string message) => Log?.Invoke(message);
}
