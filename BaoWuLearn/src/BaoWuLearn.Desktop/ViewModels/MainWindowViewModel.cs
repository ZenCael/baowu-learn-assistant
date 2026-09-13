using System.Reflection;
using Avalonia.Threading;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Desktop.Models;
using BaoWuLearn.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel：持有全部服务与子页面，负责登录态切换与导航。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly AuthService _auth;
    private readonly CourseService _courses;
    private readonly UserCenterService _userCenter;
    private readonly LearnEngine _engine;
    private readonly SettingsService _settings;
    private readonly AccountStore _accounts;
    private readonly QueueStore _queueStore;

    /// <summary>当前登录账号，用作队列存档的键（按账号分开存，换账号不会串课）。</summary>
    private string? _currentUserNo;

    /// <summary>
    /// 抑制队列回写。
    /// 登录时"清掉上个账号的队列再装载本账号的队列"、登出时"清空队列"这几个动作
    /// 都会触发队列变更事件，若不拦一下，就会把刚存好的存档当场覆盖成空。
    /// </summary>
    private bool _suspendQueuePersist;

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private string _activePage = "dashboard";
    [ObservableProperty] private string _userDisplay = "未登录";
    [ObservableProperty] private string _engineBadge = "空闲";
    [ObservableProperty] private string _currentPageTitle = "总览";

    /// <summary>
    /// 登录过期横幅。平台把过期表达在 HTTP 200 响应体里（v1.0.24 之前完全不可见，
    /// 挂机空转两个多小时），现在由 ApiClient 全局检出 → 引擎自动暂停 → 这里亮横幅。
    /// </summary>
    [ObservableProperty] private bool _sessionExpired;

    /// <summary>过期时引擎是否在挂机（决定重新登录后要不要自动续挂）。</summary>
    private bool _wasRunningOnExpiry;

    /// <summary>重新登录成功后自动继续挂机（由过期横幅的「重新登录」置位）。</summary>
    private bool _autoStartAfterRelogin;

    /// <summary>本次登录时刻（保活日志里显示已登录时长用）。</summary>
    private DateTimeOffset _loginAt = DateTimeOffset.Now;

    /// <summary>
    /// 会话保活定时器：登录后每 15 分钟带 token 轻探一次个人信息接口。
    ///
    /// ★ 目的**不是续命**（曾以为是"活跃滑动续期"，2026-09-13 更正）：
    ///   平台会话按**自然日**失效、跨不过 0 点，60 秒一次的心跳也拦不住
    ///   （两个跨 0 点样本：23:37:18 登录与 23:47:59 登录，都在 0 点整被切断）。
    ///   保活的真实价值只剩两条：①第一时间发现失效（触发横幅 + 引擎自动暂停）
    ///   ②顺带采样平台的"累计学习时长"。
    /// ★ 刻意**不**调 <see cref="ApiEndpoints.RefreshToken"/>：该端点在当前网页前端
    ///   是死代码（enableRefreshToken:!1，全站零调用），正常流量里它的调用量为零；
    ///   审计记录里若出现唯一账号规律性调用，等于自报"非标准客户端"——
    ///   指纹风险与未验证的收益不对称。
    /// </summary>
    private readonly DispatcherTimer _keepaliveTimer;

    /// <summary>检出会话过期的本地时刻（保活日志里"已过期多久"用它算）。</summary>
    private DateTimeOffset? _expiredAt;

    /// <summary>
    /// 跨零点预警定时器：平台会话按**自然日**失效（2026-09-13 实测两例，见
    /// <see cref="_keepaliveTimer"/> 注释），在 23:45 提醒一次 —— 好让用户有机会
    /// 在 0 点后重新登录，否则整夜挂机必被切断。23:45 之后才登录的，登录当场就提醒。
    /// 只写日志、不发任何请求：零指纹成本。
    /// </summary>
    private readonly DispatcherTimer _midnightWarnTimer;

    public LogsViewModel Logs { get; }
    public LoginViewModel Login { get; }
    public DashboardViewModel Dashboard { get; }
    public CoursesViewModel Courses { get; }
    public QueueViewModel Queue { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>
    /// 侧边栏副标题 —— 显示版本号。
    /// 版本号由 publish.sh 的 <c>-p:Version</c> 注入程序集属性，这里运行时读回来，
    /// 不再写死 —— 用户报问题时"左下角显示什么版本"就是最直接的排查线索。
    /// </summary>
    public string VersionText { get; } = ResolveVersionText();

    private static string ResolveVersionText()
    {
        var raw = typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(raw)) return "开发版";
        // 有源码修订号时形如 "1.0.11+abc123"，只取语义化部分
        var plus = raw.IndexOf('+');
        return "v" + (plus > 0 ? raw[..plus] : raw);
    }

    public MainWindowViewModel()
    {
        // ── 服务装配 ──────────────────────────────────────
        _api = new ApiClient();
        _auth = new AuthService(_api);
        _courses = new CourseService(_api);
        _userCenter = new UserCenterService(_api);
        _engine = new LearnEngine(_api, _courses);
        _settings = new SettingsService();
        _accounts = new AccountStore();
        _queueStore = new QueueStore();

        // ── 日志汇聚 ──────────────────────────────────────
        Logs = new LogsViewModel();
        _api.Log += text => Logs.AppendAuto(text);
        _engine.Log += text => Logs.AppendAuto(text);

        // 登录过期是后台线程检出的，切回 UI 线程再动绑定属性
        _api.TokenExpired += _ => Dispatcher.UIThread.Post(ShowSessionExpired);

        // ── 会话保活 ──────────────────────────────────────
        _keepaliveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _keepaliveTimer.Tick += async (_, _) => await KeepaliveAsync();

        // 跨零点预警：Interval 由 ArmMidnightWarning 按"距 23:45 还有多久"算出来
        _midnightWarnTimer = new DispatcherTimer();
        _midnightWarnTimer.Tick += (_, _) =>
        {
            _midnightWarnTimer.Stop();   // 一次性：提醒过就不再响
            WarnAboutMidnight();
        };

        // ── 子页面 ────────────────────────────────────────
        Login = new LoginViewModel(_auth, _accounts, OnLoginSuccess, text => Logs.AppendAuto(text));
        Dashboard = new DashboardViewModel(_engine, _userCenter, text => Logs.AppendAuto(text));
        Queue = new QueueViewModel(_engine, _courses, _userCenter, text => Logs.AppendAuto(text));
        Courses = new CoursesViewModel(_courses, _userCenter, _engine, Notify);
        Settings = new SettingsViewModel(_settings, _accounts, _auth, text => Logs.AppendAuto(text));

        _engine.Snapshot += OnEngineSnapshot;
        _engine.QueueChanged += OnQueueChanged;

        // 设置页清掉已存密码后，登录页下拉里的"已保存密码"提示要立刻跟着变
        Settings.AccountsChanged += Login.ReloadAccounts;

        // 上次登录的账号与密码由 LoginViewModel 自己预填（账号存档里就有）

        CurrentPage = Dashboard;
        Logs.Append($"宝武学习助手 {VersionText} 已启动（纯 API 模式，不加载平台页面）", LogLevel.Success);
        _ = Login.RefreshCaptchaAsync();
    }

    /// <summary>
    /// 引擎快照来自后台线程，必须切回 UI 线程再改绑定属性，
    /// 否则会触发 Avalonia 的跨线程访问异常。
    /// </summary>
    private void OnEngineSnapshot(LearnSnapshot s)
    {
        var badge = s.State switch
        {
            EngineState.Running => "运行中",
            EngineState.Paused => "已暂停",
            EngineState.Stopped => "已停止",
            EngineState.Stopping => "正在停止",
            _ => "空闲",
        };

        if (Dispatcher.UIThread.CheckAccess()) EngineBadge = badge;
        else Dispatcher.UIThread.Post(() => EngineBadge = badge);
    }

    /// <summary>
    /// 检出到登录过期：亮横幅 + 记住"当时是否在挂机"。
    /// 引擎的自动暂停由引擎自己完成（见 LearnEngine.OnTokenExpired），这里只管界面。
    /// </summary>
    private void ShowSessionExpired()
    {
        if (!IsLoggedIn) return;

        _wasRunningOnExpiry = _engine.State is EngineState.Running or EngineState.Paused;
        _expiredAt = DateTimeOffset.Now;
        SessionExpired = true;
        Logs.Append("⛔ 登录已过期，挂机已自动暂停（已挂进度保留）。"
                    + "点顶部横幅的「重新登录」续上会话", LogLevel.Error);
    }

    /// <summary>
    /// 过期横幅上的「重新登录」：复用退出登录的完整流程
    ///（停引擎 → 队列按账号存档 → 切回登录页），
    /// 并记住"过期时在挂机"——重新登录成功后自动把队列拉起来接着挂。
    /// </summary>
    [RelayCommand]
    private void ReLoginAfterExpiry()
    {
        SessionExpired = false;
        _autoStartAfterRelogin = _wasRunningOnExpiry;
        _wasRunningOnExpiry = false;

        if (_autoStartAfterRelogin)
            Logs.Append("ℹ 队列已存档：重新登录后将自动继续挂机", LogLevel.Warn);

        Logout();
    }

    /// <summary>会话保活：轻探一次个人信息接口，顺带把会话状态写进日志。</summary>
    private async Task KeepaliveAsync()
    {
        if (!IsLoggedIn) return;

        try
        {
            var p = await _userCenter.GetProfileAsync();

            // ★ "没抛异常" ≠ "会话还活着"。平台用 HTTP 200 + isSuccess:false 表达过期，
            //   而 GetProfileAsync 取不到 data 时是**静默返回 null**（不抛异常）——
            //   于是会话早就死了，这里还每 15 分钟打印一次"保活正常"。
            //   2026-09-13 日志里 [00:02:59] 那条就是这么来的假象。判据：那一行没有
            //   "平台累计学习时长"后缀（LearnTimeText 非空），说明 p 是 null。
            if (p is null)
            {
                Logs.AppendAuto(SessionExpired
                    ? "⛔ 会话保活失败：登录仍处于过期状态"
                      + (_expiredAt is { } at
                          ? $"（已过期 {(int)(DateTimeOffset.Now - at).TotalMinutes} 分钟）"
                          : "")
                      + "，平台一直拒绝本会话 —— 点顶部横幅「重新登录」才能续上"
                    : "⚠ 会话保活拿不到个人信息（平台既没报过期、也没给 data）—— 本次不处理，下次再探");
                return;
            }

            // 带上平台侧的「累计学习时长」：这个字段疑似不是实时落库（2026-09-12 用户观察到
            // 学时 15 与累计 4h22m 对不上），每 15 分钟采样一次，日志里就能看出它到底是
            // 实时更新、T+1 批量还是别的口径。顺带把总览页的数字也保持新鲜。
            Logs.AppendAuto($"🔒 会话保活正常（已登录 {(int)(DateTimeOffset.Now - _loginAt).TotalMinutes} 分钟"
                            + $"，平台累计学习时长 {p.LearnTimeText}）"
                            + (SessionExpired
                                ? "；★ 平台重新接受了本会话，但界面仍标着「已过期」—— 挂机不会自动恢复，到挂机页点「开始」即可续挂"
                                : ""));
            Dashboard.UpdateProfile(p);
        }
        catch (Exception ex)
        {
            // 保活失败不弹横幅 —— 若真是过期，响应体检出会统一走横幅路径
            Logs.AppendAuto($"⚠ 会话保活请求失败：{ex.Message}");
        }
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        if (value)
        {
            _keepaliveTimer.Start();
        }
        else
        {
            _keepaliveTimer.Stop();
            _midnightWarnTimer.Stop();   // 退出登录就别再提醒了
        }
    }

    // ── 跨零点预警 ────────────────────────────────────────

    /// <summary>
    /// 按"平台会话跨不过 0 点"这条实测规则，安排一次跨零点预警。
    /// 已在 23:45 之后的会话（只剩十几分钟寿命）登录当场就提醒。
    /// </summary>
    private void ArmMidnightWarning()
    {
        if (MidnightWarningDelay(DateTimeOffset.Now) is not { } delay)
        {
            WarnAboutMidnight();   // 已经过了 23:45，没什么可等的
            return;
        }

        _midnightWarnTimer.Interval = delay;
        _midnightWarnTimer.Start();
    }

    /// <summary>
    /// 跨零点预警的决策（纯函数，供 <c>--selftest</c> 离线验算）：
    /// 返回 null = 登录当场就该提醒（已经过了今天 23:45）；
    /// 否则返回距今天 23:45 还要等多久。
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
    /// 提醒：本会话活不过 0 点。刻意把"怎么办"写全 —— 重新登录要人工输图形验证码
    /// （不 OCR、不绕过），被切断时挂机自动暂停、学时全保留，重登后自动接着挂。
    /// </summary>
    private void WarnAboutMidnight()
    {
        var remain = (int)(NextMidnight(DateTimeOffset.Now) - DateTimeOffset.Now).TotalMinutes;
        Logs.Append(
            $"⏰ 本次会话活不过 0 点：实测平台会话按自然日失效（两个跨 0 点的样本都在 0 点整被切断，"
            + "与登录时刻无关，60 秒一次的心跳也拦不住）。"
            + $"本次登录 {_loginAt:HH:mm}，距 0 点还有 {remain} 分钟。"
            + "要整夜挂机，请在 0 点后重新登录一次（登录要人工输一次图形验证码）；"
            + "被切断时挂机自动暂停、已挂学时全部保留，重新登录后自动接着挂。",
            LogLevel.Warn);
    }

    /// <summary>全局异常兜底的回调：把崩溃信息也写进运行日志页。</summary>
    public void ReportCrash(string message)
        => Logs.Append(message + "（详见崩溃日志）", LogLevel.Error);

    // ── 导航 ──────────────────────────────────────────────

    public bool IsDashboardPage => ActivePage == "dashboard";
    public bool IsCoursesPage => ActivePage == "courses";
    public bool IsQueuePage => ActivePage == "queue";
    public bool IsLogsPage => ActivePage == "logs";
    public bool IsSettingsPage => ActivePage == "settings";

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashboardPage));
        OnPropertyChanged(nameof(IsCoursesPage));
        OnPropertyChanged(nameof(IsQueuePage));
        OnPropertyChanged(nameof(IsLogsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        page ??= "dashboard";
        ActivePage = page;
        CurrentPage = page switch
        {
            "courses" => Courses,
            "queue" => Queue,
            "logs" => Logs,
            "settings" => Settings,
            _ => Dashboard,
        };
        CurrentPageTitle = page switch
        {
            "courses" => "课程",
            "queue" => "学习队列",
            "logs" => "运行日志",
            "settings" => "设置",
            _ => "总览",
        };

        if (page == "queue") Queue.OnNavigatedTo();
        // 账号信息在登录页可能改过，进设置页时刷新一下概况
        if (page == "settings") Settings.RefreshAccountSummary();
    }

    [RelayCommand]
    private void Logout()
    {
        _engine.Stop();

        // 先把队列按当前账号存好：登出会清空引擎队列，
        // 不先存档的话这次清空就会把该账号的队列存档一起抹掉。
        if (!string.IsNullOrEmpty(_currentUserNo))
            _queueStore.Save(_currentUserNo, _engine.Queue);

        _suspendQueuePersist = true;
        try
        {
            _engine.ClearQueue();
        }
        finally
        {
            _suspendQueuePersist = false;
        }

        _currentUserNo = null;
        _api.Token = null;
        IsLoggedIn = false;
        SessionExpired = false;
        UserDisplay = "未登录";
        Login.Reset();
        Logs.Append("已退出登录", LogLevel.Warn);
        _ = Login.RefreshCaptchaAsync();
    }

    // ── 挂课队列持久化 ────────────────────────────────────

    /// <summary>队列内容变化时写回存档（登录后每个账号各存一份）。</summary>
    private void OnQueueChanged()
    {
        if (_suspendQueuePersist) return;
        if (string.IsNullOrEmpty(_currentUserNo)) return;

        _queueStore.Save(_currentUserNo, _engine.Queue);
    }

    /// <summary>
    /// 恢复该账号上次的挂课队列。
    ///
    /// 存档里的学时 / 成绩是上次退出时的快照，先原样展示，随后由正常的成绩回读刷新 ——
    /// 总比让用户每次都到课程页重新挑一遍、重新排序强。
    /// </summary>
    private void RestoreQueue(string? userNo)
    {
        if (string.IsNullOrEmpty(userNo)) return;

        // 清掉上一个账号遗留的队列（此时正处于"已存档"状态，不能再写回）
        _suspendQueuePersist = true;
        try
        {
            _engine.ClearQueue();
        }
        finally
        {
            _suspendQueuePersist = false;
        }

        var saved = _queueStore.Load(userNo);
        if (saved.Count == 0) return;

        _suspendQueuePersist = true;
        try
        {
            _engine.Enqueue(saved);
        }
        finally
        {
            _suspendQueuePersist = false;
        }

        Logs.Append($"✓ 已恢复上次的挂课队列：{saved.Count} 门课程", LogLevel.Success);
        Queue.Refresh();

        // 存档里是上次退出那一刻的成绩快照，异步刷成服务端最新值
        _ = Queue.RefreshQueueScoresAsync();
    }

    // ── 登录成功 ──────────────────────────────────────────

    private void OnLoginSuccess(LoginResult result)
    {
        IsLoggedIn = true;
        SessionExpired = false;
        UserDisplay = result.RealName ?? result.UserName ?? "已登录";
        Dashboard.SetUser(result.RealName ?? result.UserName, result.UserNo);
        Queue.SetUser(result.UserNo);
        Courses.SetUser(result.UserNo);
        _currentUserNo = result.UserNo;
        _loginAt = DateTimeOffset.Now;
        _expiredAt = null;
        Logs.Append($"✓ 登录成功：{UserDisplay}", LogLevel.Success);

        // 把 token 的有效期摊开讲清楚：能解析就报死线，解析不了说明是 opaque token，
        // 只能靠「过期自动暂停 + 重新登录」兜底 —— 两种情况用户都不用猜。
        if (AuthService.TryGetTokenExpiry(_api.Token) is { } exp)
        {
            var remain = exp - DateTimeOffset.UtcNow;
            Logs.Append(remain > TimeSpan.Zero
                ? $"🔒 token 有效期至 {exp.ToLocalTime():MM-dd HH:mm}（约 {(int)remain.TotalMinutes} 分钟）"
                : "🔒 token 疑似已过期（exp 早于当前时间），请注意校准系统时钟",
                remain > TimeSpan.Zero ? LogLevel.Info : LogLevel.Warn);
        }
        else
        {
            // 平台发的是 opaque token（服务端说了算），本地读不出死线。
            // 但 2026-09-13 两次跨 0 点实测把规律钉死了：会话按自然日失效，活不过 0 点。
            Logs.AppendAuto("🔒 token 非 JWT，无法本地读取有效期"
                            + "；★ 实测平台会话按自然日失效（跨 0 点必断，与登录时刻无关）"
                            + $"—— 本次登录 {_loginAt:HH:mm}，预计 {NextMidnight(_loginAt):MM-dd HH:mm} 前后被切断");
        }

        // 跨零点预警：登录本身就晚于 23:45 的当场提醒，否则挂到 23:45 再提醒
        ArmMidnightWarning();

        Navigate("dashboard");

        // 队列按账号分开存，登录后先把本账号上次的队列装回来
        RestoreQueue(result.UserNo);

        // 过期后重新登录的场景：自动把挂机续上（引擎会按服务端 maxPlayTime 接着算，
        // 不会从零重挂；已过分数线的课会被达标筛查跳过）。
        if (_autoStartAfterRelogin)
        {
            _autoStartAfterRelogin = false;
            if (_engine.Queue.Count > 0)
            {
                _engine.Start();
                Logs.Append("▶ 已自动恢复挂机（过期前的队列已续上）", LogLevel.Success);
            }
        }

        // ★ 工号校正：登录响应里的 userNo/loginName 不保证等于归档查询要的 stuCode
        //（用手机号/别名登录的账号两者就不同 —— 传错的话"我的已选"四个分类会全部
        //  静默查空，界面就是一张空表，看不出任何错误）。以个人信息接口为准。
        _ = CorrectStuCodeAsync(result.UserNo);

        // 登录后先把课程页的首个菜单（公开课程）拉一页，
        // 对应网页端"登录后先浏览"的拟人化要求
        Courses.WarmUp();
    }

    private async Task CorrectStuCodeAsync(string? fallback)
    {
        try
        {
            var profile = await _userCenter.GetProfileAsync();
            var real = profile?.StuCode;
            if (string.IsNullOrWhiteSpace(real) || real == (fallback ?? "")) return;

            Logs.Append($"工号校正：{fallback} → {real}（归档查询以个人信息接口为准）", LogLevel.Warn);
            Queue.SetUser(real);
            Courses.SetUser(real);
            Dashboard.SetUser(profile!.StuName, real);
        }
        catch (Exception ex)
        {
            Logs.Append($"⚠ 个人信息获取失败，继续用登录工号：{ex.Message}", LogLevel.Warn);
        }
    }

    private void Notify(string message)
    {
        if (message.StartsWith("请先")) Logs.Append(message, LogLevel.Warn);
        else Logs.AppendAuto(message);
    }

    /// <summary>退出流程是否已经开始（窗口 Closing 只应该拦一次）。</summary>
    private int _shutdownStarted;

    /// <summary>窗口关闭时调用；返回 true 表示"这是我第一次处理关闭，请先拦下，我去收尾"。</summary>
    public bool TryBeginShutdown() => Interlocked.Exchange(ref _shutdownStarted, 1) == 0;

    /// <summary>
    /// 退出收尾：摘掉界面回调 → 停引擎 → 等它把最后一次结算发出去 → 释放网络。
    ///
    /// 必须是异步的。以前这里是
    /// <c>WaitForStopAsync(3s).GetAwaiter().GetResult()</c> —— 在 UI 线程上死等，
    /// 而同一时刻引擎还在推快照、挂机秒表还在每秒 tick，全都排在这个被占住的
    /// UI 线程后面。结果就是挂课中点关闭，程序直接卡死（Mac 上必现）。
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            // 先摘界面回调：不再往 UI 线程堆活儿，收尾才跑得完
            Queue.DetachEngine();
            Dashboard.DetachEngine();
            _engine.Snapshot -= OnEngineSnapshot;

            _engine.Stop();
            await _engine.WaitForStopAsync(TimeSpan.FromSeconds(3));
            _api.Dispose();
        }
        catch
        {
            // 退出阶段忽略一切异常
        }
    }

    /// <summary>
    /// 兜底退出路径（Cmd+Q、自检结束等没经过窗口关闭流程的情形）：
    /// 只摘回调、发停止信号，**不做任何等待**。
    ///
    /// 刻意不等：退出阶段阻塞 UI 线程正是之前卡死的原因。
    /// 代价是这条路径上最后一次结算可能来不及发出；正常点关闭按钮走的是
    /// <see cref="ShutdownAsync"/>，会完整收尾。
    /// </summary>
    public void AbortForShutdown()
    {
        try
        {
            Queue.DetachEngine();
            Dashboard.DetachEngine();
            _engine.Snapshot -= OnEngineSnapshot;
            _engine.Stop();
        }
        catch
        {
            // 退出阶段忽略一切异常
        }
    }
}
