using System.Reflection;
using Avalonia.Threading;
using BaoWuLearn.Core.Download;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Desktop.Models;
using BaoWuLearn.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel：多账号运行时池的组合根。
///
/// v1.0.42 起，「一套服务 + 一台引擎」变成「一池运行时」（<see cref="RuntimeHub"/>）：
/// 每个登录账号自带 ApiClient/token/引擎/保活/零点预警，互不共享；
/// 侧栏「挂后台·切换账号」只换查看对象，后台引擎照挂。
/// 登录页仍用共享的 <c>_api</c> 过验证码 —— token 拿到后立刻复制进运行时，
/// 共享出口随即清空（此后它只当匿名登录通道用）。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>登录页专用网关（验证码 + 登录请求），不持业务 token。</summary>
    private readonly ApiClient _api;
    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private readonly AccountStore _accounts;
    private readonly RuntimeHub _hub;

    /// <summary>未登录时的占位运行时：让子页面在未登录态也有非空引擎可绑。</summary>
    private readonly AccountRuntime _cold;

    /// <summary>下载引擎（v1.0.44）：全局单份 —— 任务表跨账号共享，预览通道现取当前激活账号的会话。</summary>
    private readonly DownloadService _downloads;

    /// <summary>子页面当前绑定的运行时 + 其快照订阅句柄（重建时负责摘钩）。</summary>
    private AccountRuntime? _pagesRt;
    private Action<LearnSnapshot>? _pagesSnapshotHandler;

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private string _activePage = "dashboard";
    [ObservableProperty] private string _userDisplay = "未登录";
    [ObservableProperty] private string _engineBadge = "空闲";
    [ObservableProperty] private string _currentPageTitle = "总览";

    /// <summary>
    /// 登录过期横幅（当前查看账号的）。平台把过期表达在 HTTP 200 响应体里，
    /// 由 ApiClient 全局检出 → 引擎自动暂停 → 运行时标记 → 这里亮横幅。
    /// </summary>
    [ObservableProperty] private bool _sessionExpired;

    // ── 子页面：随「当前查看的运行时」重建（v1.0.42）──────
    [ObservableProperty] private DashboardViewModel _dashboard = null!;
    [ObservableProperty] private QueueViewModel _queue = null!;
    [ObservableProperty] private CoursesViewModel _courses = null!;
    [ObservableProperty] private DownloadViewModel _download = null!;

    public LogsViewModel Logs { get; }
    public LoginViewModel Login { get; }
    public FleetViewModel Fleet { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>运行时池（自检与总览页共用同一实例）。</summary>
    public RuntimeHub Hub => _hub;

    /// <summary>启动时供 App 读取的外观设置（皮肤 / 密度）。</summary>
    public AppSettings CurrentSettings => Settings.CurrentSettings;

    /// <summary>停在登录页但池里仍有账号 → 登录页显示「返回会话」。</summary>
    public bool CanReturnToPool => !IsLoggedIn && _hub.All.Count > 0;

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
        _settings = new SettingsService();
        _accounts = new AccountStore();
        _hub = new RuntimeHub(onNewRuntime: WireRuntime);
        _cold = new AccountRuntime("", "未登录");

        // ── 下载引擎（v1.0.44）────────────────────────────
        // 预览通道的鉴权现取当前激活账号（闭包里取，不是构造时快照）；
        // 任务表与设置同目录（%APPDATA%/BaoWuLearn/download.json）。
        _downloads = new DownloadService(
            () => _hub.Active?.Api,
            () => _settings.Load().ResolvedDownloadRoot,
            Path.Combine(Path.GetDirectoryName(_settings.ConfigPath) ?? ".", "download.json"));
        _downloads.ReviveInterrupted();

        // ── 日志汇聚 ──────────────────────────────────────
        Logs = new LogsViewModel();
        _api.Log += Logs.AppendAuto;

        // ── 子页面 ────────────────────────────────────────
        Fleet = new FleetViewModel(_hub, new FleetActions(
            OpenRuntime, StartRuntime, StopRuntime, ReloginRuntime, LogoutRuntime,
            StartAllRuntimesAsync, BeginAddAccount));
        Login = new LoginViewModel(_auth, _accounts, OnLoginSuccess, Logs.AppendAuto);
        Login.OnReturnPool = ResumePool;
        RebindPages(_cold);

        Settings = new SettingsViewModel(
            _settings, _accounts, _auth, Logs.AppendAuto,
            checkUpdate: () => RunUpdateCheckAsync(manual: true));

        // 登录过期是后台线程检出的，切回 UI 线程再动绑定属性（按运行时接线见 WireRuntime）

        // 设置页清掉已存密码后，登录页下拉里的"已保存密码"提示要立刻跟着变
        Settings.AccountsChanged += Login.ReloadAccounts;
        _hub.Changed += OnHubChanged;

        CurrentPage = Dashboard;
        Logs.Append($"宝武学习助手 {VersionText} 已启动（纯 API 模式，不加载平台页面；支持多账号并行挂机）",
            LogLevel.Success);
        _ = Login.RefreshCaptchaAsync();

        // ── 自动更新（v1.0.41）─────────────────────────────
        InitUpdateChannel();
    }

    // ── 运行时接线 / 池事件 ────────────────────────────────

    /// <summary>新运行时入池：日志加账号前缀、过期与保活回读转给界面。</summary>
    private void WireRuntime(AccountRuntime rt)
    {
        rt.Log += (text, level) => Logs.Append(Tag(rt) + text, level);
        rt.ExpiredDetected += r => Dispatcher.UIThread.Post(() => OnRuntimeExpired(r));
        rt.ProfileRefreshed += p =>
        {
            // 只有"这页正显示着它"才刷总览的学习时长卡
            if (ReferenceEquals(_pagesRt, rt))
                Dispatcher.UIThread.Post(() => Dashboard.UpdateProfile(p));
        };
    }

    /// <summary>
    /// 下载页「更改保存目录…」的持久化通道：写回设置文件，
    /// DownloadService 每次入队都重新解析根目录，改完下一单立即生效。
    /// </summary>
    private void SaveDownloadRoot(string? path, Action<string> log)
    {
        try
        {
            var s = _settings.Load();
            s.DownloadDirectory = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            _settings.Save(s);
            log($"✓ 课件下载目录已改为：{s.ResolvedDownloadRoot}");
        }
        catch (Exception ex)
        {
            log("✖ 下载目录保存失败：" + ex.Message);
        }
    }

    /// <summary>多账号时的日志前缀；池里只有一个账号就不啰嗦。</summary>
    private string Tag(AccountRuntime rt)
        => rt.UserNo.Length == 0 || _hub.All.Count <= 1 ? "" : $"[{rt.DisplayName}] ";

    private void OnHubChanged()
    {
        Login.SetPoolInfo(_hub.All.Count);
        OnPropertyChanged(nameof(CanReturnToPool));

        if (_hub.Active is { } act && IsLoggedIn)
        {
            RebindPages(act);
            SyncActiveUi();
        }
    }

    private void SyncActiveUi()
    {
        if (_hub.Active is not { } act) return;
        SessionExpired = act.SessionExpired;
        EngineBadge = BadgeOf(act.Engine.State);
        UserDisplay = act.DisplayName;
    }

    /// <summary>
    /// 子页面跟着「当前查看的运行时」重建：Dashboard/Queue/Courses 的构造注入
    /// 换成这个账号自己那套服务，先摘旧引擎的钩（DetachEngine + 快照退订）。
    /// 课程页的搜索状态随之丢弃 —— 换账号本就换语境，正好。
    /// </summary>
    private void RebindPages(AccountRuntime rt)
    {
        if (ReferenceEquals(_pagesRt, rt)) return;

        Queue?.DetachEngine();
        Dashboard?.DetachEngine();
        Download?.Detach();
        if (_pagesRt is { } old && _pagesSnapshotHandler is { } h)
            old.Engine.Snapshot -= h;

        var log = new Action<string>(t => Logs.AppendAuto(Tag(rt) + t));
        Dashboard = new DashboardViewModel(rt.Engine, rt.UserCenter, log);
        Queue = new QueueViewModel(rt.Engine, rt.Courses, rt.UserCenter, log);
        Courses = new CoursesViewModel(rt.Courses, rt.UserCenter, rt.Engine, m => NotifyOn(rt, m));
        Download = new DownloadViewModel(rt.Engine, rt.Courses, _downloads, log,
            () => _settings.Load().ResolvedDownloadRoot,
            p => SaveDownloadRoot(p, log));
        Download.Refresh();
        Dashboard.SetUser(rt.UserNo.Length == 0 ? null : rt.DisplayName,
                          rt.UserNo.Length == 0 ? null : rt.StuCode);
        Queue.SetUser(rt.StuCode);
        Courses.SetUser(rt.StuCode);

        var handler = new Action<LearnSnapshot>(s =>
        {
            // 后台账号的快照不进顶栏徽章 —— 徽章说的是"当前看的是哪个"
            if (ReferenceEquals(_hub.Active, rt)) OnEngineSnapshot(s);
        });
        rt.Engine.Snapshot += handler;
        _pagesSnapshotHandler = handler;
        _pagesRt = rt;
    }

    private static string BadgeOf(EngineState s) => s switch
    {
        EngineState.Running => "运行中",
        EngineState.Paused => "已暂停",
        EngineState.Stopped => "已停止",
        EngineState.Stopping => "正在停止",
        _ => "空闲",
    };

    /// <summary>引擎快照来自后台线程，必须切回 UI 线程再改绑定属性。</summary>
    private void OnEngineSnapshot(LearnSnapshot s)
    {
        var badge = BadgeOf(s.State);
        if (Dispatcher.UIThread.CheckAccess()) EngineBadge = badge;
        else Dispatcher.UIThread.Post(() => EngineBadge = badge);
    }

    /// <summary>某账号检出过期：当前查看的亮横幅，后台的只写日志（总览页行会变红）。</summary>
    private void OnRuntimeExpired(AccountRuntime rt)
    {
        if (ReferenceEquals(_hub.Active, rt))
        {
            SessionExpired = true;
            EngineBadge = "已暂停";
        }
        Logs.Append(Tag(rt) + "⛔ 登录已过期，该账号挂机已自动暂停（已挂进度保留）。"
                    + "点顶部横幅或到多挂机页「重新登录」续上会话", LogLevel.Error);
    }

    // ── 登录成功（新登录 / 重新登录两条路）──────────────────

    private void OnLoginSuccess(LoginResult result)
    {
        var userNo = result.UserNo ?? "";
        IsLoggedIn = true;

        var rt = _hub.Find(userNo);
        if (rt is null)
        {
            rt = _hub.Create(userNo, result.RealName ?? result.UserName ?? userNo, _api.Token);
            rt.StartWatchers();
            LogTokenLine(rt);
            Logs.Append(Tag(rt) + $"✓ 登录成功：{rt.DisplayName}", LogLevel.Success);

            // 队列按账号分开存，新入池先把该账号上次的队列装回来
            var n = _hub.RestoreQueue(rt);
            if (n > 0)
            {
                Logs.Append(Tag(rt) + $"✓ 已恢复上次的挂课队列：{n} 门课程", LogLevel.Success);
                Queue.Refresh();
                _ = Queue.RefreshQueueScoresAsync();
            }

            Navigate("dashboard");
            _ = CorrectStuCodeAsync(rt, userNo);

            // 登录后先把课程页的首个菜单（公开课程）拉一页 —— 对应网页端"登录后先浏览"的拟人化要求
            Courses.WarmUp();
        }
        else
        {
            var resume = rt.AutoStartAfterRelogin;
            rt.ApplyRelogin(_api.Token);
            rt.StartWatchers();
            _hub.SetActive(rt);
            Logs.Append(Tag(rt) + "✓ 重新登录成功，该账号会话已续上", LogLevel.Success);
            SyncActiveUi();
            if (resume && rt.Engine.Queue.Count > 0)
            {
                rt.Engine.Start();
                Logs.Append(Tag(rt) + "▶ 已自动恢复挂机（过期前的队列已续上）", LogLevel.Success);
            }
            Navigate("dashboard");
            _ = CorrectStuCodeAsync(rt, userNo);
            Courses.WarmUp();
        }

        // 共享出口只当登录通道用：token 已复制进运行时，这边立刻清空
        _api.Token = null;
    }

    /// <summary>把 token 的有效期摊开讲清楚（原 v1.0.25 逻辑，按运行时各报各的）。</summary>
    private void LogTokenLine(AccountRuntime rt)
    {
        if (AuthService.TryGetTokenExpiry(rt.Api.Token) is { } exp)
        {
            var remain = exp - DateTimeOffset.UtcNow;
            Logs.Append(Tag(rt) + (remain > TimeSpan.Zero
                    ? $"🔒 token 有效期至 {exp.ToLocalTime():MM-dd HH:mm}（约 {(int)remain.TotalMinutes} 分钟）"
                    : "🔒 token 疑似已过期（exp 早于当前时间），请注意校准系统时钟"),
                remain > TimeSpan.Zero ? LogLevel.Info : LogLevel.Warn);
        }
        else
        {
            // 平台发的是 opaque token（服务端说了算）。实测规律（2026-09-13 两例）：
            // 会话按自然日失效，活不过 0 点。
            Logs.AppendAuto(Tag(rt) + "🔒 token 非 JWT，无法本地读取有效期"
                            + "；★ 实测平台会话按自然日失效（跨 0 点必断，与登录时刻无关）"
                            + $"—— 本次登录 {rt.LoginAt:HH:mm}，预计 {AccountRuntime.NextMidnight(rt.LoginAt):MM-dd HH:mm} 前后被切断");
        }
    }

    private async Task CorrectStuCodeAsync(AccountRuntime rt, string? fallback)
    {
        try
        {
            var profile = await rt.UserCenter.GetProfileAsync();
            var real = profile?.StuCode;

            if (!string.IsNullOrWhiteSpace(profile?.StuName))
                rt.DisplayName = profile.StuName;

            if (string.IsNullOrWhiteSpace(real) || real == (fallback ?? "")) return;

            Logs.Append(Tag(rt) + $"工号校正：{fallback} → {real}（归档查询以个人信息接口为准）",
                LogLevel.Warn);
            rt.StuCode = real;
            if (ReferenceEquals(_pagesRt, rt))
            {
                Queue.SetUser(real);
                Courses.SetUser(real);
                Dashboard.SetUser(profile!.StuName, real);
            }
        }
        catch (Exception ex)
        {
            Logs.Append(Tag(rt) + $"⚠ 个人信息获取失败，继续用登录工号：{ex.Message}", LogLevel.Warn);
        }
    }

    // ── 多账号池操作 ──────────────────────────────────────

    /// <summary>总览页「打开」：切 Active + 页面重建 + 跳总览。</summary>
    private void OpenRuntime(AccountRuntime rt)
    {
        _hub.SetActive(rt);
        RebindPages(rt);
        SyncActiveUi();
        Navigate("dashboard");
    }

    private void StartRuntime(AccountRuntime rt)
    {
        if (rt.Engine.Queue.Count == 0)
        {
            Logs.Append(Tag(rt) + "⚠ 该账号队列为空：先「打开」到课程页挑课", LogLevel.Warn);
            return;
        }
        rt.Engine.Start();
        Logs.Append(Tag(rt) + "▶ 开始挂机", LogLevel.Success);
    }

    private void StopRuntime(AccountRuntime rt)
    {
        rt.Engine.Stop();
        Logs.Append(Tag(rt) + "■ 已停止（队列与进度保留）", LogLevel.Warn);
    }

    /// <summary>过期行的「重新登录」：留着整个池去登录页，且只顶这一个账号的会话。</summary>
    private void ReloginRuntime(AccountRuntime rt)
    {
        rt.AutoStartAfterRelogin = rt.WasRunningOnExpiry;
        if (rt.AutoStartAfterRelogin)
            Logs.Append(Tag(rt) + "ℹ 队列已存档：重新登录后将自动继续挂机", LogLevel.Warn);

        _hub.SetActive(null);
        IsLoggedIn = false;
        SessionExpired = false;
        Login.Reset();
        Login.PrefillFor(rt.UserNo);
        _ = Login.RefreshCaptchaAsync();
        Logs.Append($"↪ 去重新登录 {rt.DisplayName}（池中其他账号的挂机不受影响）", LogLevel.Info);
    }

    /// <summary>顶部过期横幅的「重新登录」。</summary>
    [RelayCommand]
    private void ReLoginAfterExpiry()
    {
        if (_hub.Active is { } rt) ReloginRuntime(rt);
    }

    /// <summary>侧栏「挂后台·切换账号」：当前账号连引擎一起留在池里跑，回登录页。</summary>
    [RelayCommand]
    private void SwitchAccount()
    {
        if (_hub.Active is { } rt) SwitchAccountCore(rt);
    }

    private void BeginAddAccount()
    {
        if (_hub.Active is { } rt) SwitchAccountCore(rt);
    }

    private void SwitchAccountCore(AccountRuntime rt)
    {
        _hub.PersistQueue(rt);
        _hub.SetActive(null);
        IsLoggedIn = false;
        SessionExpired = false;
        Logs.Append($"↪ {rt.DisplayName} 已挂后台 —— 会话、引擎、保活照常运转（池中 {Hub.All.Count} 个账号）",
            LogLevel.Info);
        Login.Reset();
        _ = Login.RefreshCaptchaAsync();
    }

    /// <summary>登录页「返回会话」：优先回到正在挂的那个账号。</summary>
    private void ResumePool()
    {
        var pick = _hub.All.FirstOrDefault(r => !r.SessionExpired
                   && r.Engine.State == EngineState.Running)
               ?? _hub.All.FirstOrDefault(r => !r.SessionExpired)
               ?? _hub.All.FirstOrDefault();
        if (pick is null) return;

        IsLoggedIn = true;
        _hub.SetActive(pick);
        RebindPages(pick);
        SyncActiveUi();
        Navigate("dashboard");
        Logs.Append($"↩ 已回到 {pick.DisplayName} 的会话", LogLevel.Info);
    }

    /// <summary>
    /// 「全部开始」：给所有「没过期 + 空闲 + 有队列」的账号排错峰启动。
    /// 排期来自 <see cref="RuntimeHub.StartAllDelays"/>；到点再核一遍状态
    /// （用户可能已经手动开过），过期了就跳过并说明。
    /// </summary>
    private async Task StartAllRuntimesAsync()
    {
        var cands = _hub.All
            .Where(r => !r.SessionExpired
                        && r.Engine.State is EngineState.Idle or EngineState.Stopped
                        && r.Engine.Queue.Count > 0)
            .ToList();
        if (cands.Count == 0)
        {
            Logs.Append("全部开始：没有可启动的账号（都过期、在挂、或队列为空）", LogLevel.Warn);
            return;
        }

        var delays = RuntimeHub.StartAllDelays(cands.Count);
        Logs.Append($"▶ 全部开始：{cands.Count} 个账号错峰启动 —— 首个约 {(int)delays[0].TotalSeconds} 秒后，"
                    + "其后逐个随机间隔 1~4 分钟（心跳相位不对齐，是多人挂机唯一的机器特征防线）",
            LogLevel.Info);

        var prev = TimeSpan.Zero;
        foreach (var (rt, delay) in cands.Zip(delays))
        {
            await Task.Delay(delay - prev);
            prev = delay;
            var captured = rt;
            Dispatcher.UIThread.Post(() =>
            {
                if (captured.SessionExpired)
                {
                    Logs.Append(Tag(captured) + "⏭ 跳过启动：该账号已过期，等重登", LogLevel.Warn);
                    return;
                }
                if (captured.Engine.State is EngineState.Idle or EngineState.Stopped
                    && captured.Engine.Queue.Count > 0)
                {
                    captured.Engine.Start();
                    Logs.Append(Tag(captured) + "▶ 已按计划启动", LogLevel.Success);
                }
            });
        }
    }

    // ── 退出登录（当前账号移出池；池空了才回登录页）────────

    [RelayCommand]
    private void Logout()
    {
        if (_hub.Active is { } rt) LogoutRuntime(rt);
        else ShowLoginScreen();
    }

    private void LogoutRuntime(AccountRuntime rt)
    {
        Logs.Append($"已退出登录：{rt.DisplayName}", LogLevel.Warn);
        _hub.Remove(rt);   // 内部先存档再停引擎再释放；Active 自动挑接替者

        if (_hub.Active is not null)
        {
            // 池里还有别的账号：OnHubChanged 已把它们扶正并重建页面
            Logs.Append($"↩ 池里还有 {_hub.All.Count} 个账号（挂机与保活照常），当前查看：{_hub.Active.DisplayName}",
                LogLevel.Info);
            return;
        }

        ShowLoginScreen();
    }

    private void ShowLoginScreen()
    {
        IsLoggedIn = false;
        SessionExpired = false;
        UserDisplay = "未登录";
        EngineBadge = "空闲";
        Login.Reset();
        _ = Login.RefreshCaptchaAsync();
    }

    private void NotifyOn(AccountRuntime rt, string message)
    {
        if (message.StartsWith("请先")) Logs.Append(Tag(rt) + message, LogLevel.Warn);
        else Logs.AppendAuto(Tag(rt) + message);
    }

    // ── 跨零点/纯函数转发（保持 v1.0.34 自检与外部引用不破）──

    /// <summary>今天 24:00 的本地时刻（转发至 AccountRuntime，自检与旧调用不变）。</summary>
    public static DateTimeOffset NextMidnight(DateTimeOffset now) => AccountRuntime.NextMidnight(now);

    /// <summary>跨零点预警决策（转发至 AccountRuntime）。</summary>
    public static TimeSpan? MidnightWarningDelay(DateTimeOffset now)
        => AccountRuntime.MidnightWarningDelay(now);

    /// <summary>全局异常兜底的回调：把崩溃信息也写进运行日志页。</summary>
    public void ReportCrash(string message)
        => Logs.Append(message + "（详见崩溃日志）", LogLevel.Error);

    // ── 导航 ──────────────────────────────────────────────

    public bool IsDashboardPage => ActivePage == "dashboard";
    public bool IsFleetPage => ActivePage == "fleet";
    public bool IsCoursesPage => ActivePage == "courses";
    public bool IsQueuePage => ActivePage == "queue";
    public bool IsDownloadPage => ActivePage == "download";
    public bool IsLogsPage => ActivePage == "logs";
    public bool IsSettingsPage => ActivePage == "settings";

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashboardPage));
        OnPropertyChanged(nameof(IsFleetPage));
        OnPropertyChanged(nameof(IsCoursesPage));
        OnPropertyChanged(nameof(IsQueuePage));
        OnPropertyChanged(nameof(IsDownloadPage));
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
            "fleet" => Fleet,
            "courses" => Courses,
            "queue" => Queue,
            "download" => Download,
            "logs" => Logs,
            "settings" => Settings,
            _ => Dashboard,
        };
        CurrentPageTitle = page switch
        {
            "fleet" => "多挂机总览",
            "courses" => "课程",
            "queue" => "学习队列",
            "download" => "课件下载",
            "logs" => "运行日志",
            "settings" => "设置",
            _ => "总览",
        };

        if (page == "queue") Queue.OnNavigatedTo();
        if (page == "download") Download.Refresh();
        // 账号信息在登录页可能改过，进设置页时刷新一下概况
        if (page == "settings") Settings.RefreshAccountSummary();
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        // 池里每个运行时自带保活计时（登录即挂、在池就跳），
        // 登录态切换只管界面，不再启停任何计时器。
        Login.SetPoolInfo(_hub.All.Count);
        OnPropertyChanged(nameof(CanReturnToPool));
    }

    // ── 退出收尾 ──────────────────────────────────────────

    /// <summary>退出流程是否已经开始（窗口 Closing 只应该拦一次）。</summary>
    private int _shutdownStarted;

    /// <summary>窗口关闭时调用；返回 true 表示"这是我第一次处理关闭，请先拦下，我去收尾"。</summary>
    public bool TryBeginShutdown() => Interlocked.Exchange(ref _shutdownStarted, 1) == 0;

    /// <summary>
    /// 退出收尾：摘界面回调 → 停**池里所有**引擎 → 并行等各自把最后一次结算发出去
    /// → 释放网络。刻意不串行等（N 个账号 × 3 秒会把关窗拖成"卡死"的观感）。
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            Fleet.Detach();
            Queue?.DetachEngine();
            Dashboard?.DetachEngine();
            if (_pagesRt is { } p && _pagesSnapshotHandler is { } h) p.Engine.Snapshot -= h;

            _hub.StopAll();
            var runtimes = Hub.All.ToList();
            await Task.WhenAll(runtimes.Select(rt => rt.Engine.WaitForStopAsync(TimeSpan.FromSeconds(3))));
            foreach (var rt in runtimes) rt.Dispose();
            _cold.Dispose();
            _api.Dispose();
        }
        catch
        {
            // 退出阶段忽略一切异常
        }
    }

    /// <summary>
    /// 兜底退出路径（Cmd+Q、自检结束等没经过窗口关闭流程的情形）：
    /// 只摘回调、发停止信号，**不做任何等待**（阻塞 UI 线程正是之前卡死的原因）。
    /// </summary>
    public void AbortForShutdown()
    {
        try
        {
            Fleet.Detach();
            Queue?.DetachEngine();
            Dashboard?.DetachEngine();
            if (_pagesRt is { } p && _pagesSnapshotHandler is { } h) p.Engine.Snapshot -= h;
            _hub.StopAll();
        }
        catch
        {
            // 退出阶段忽略一切异常
        }
    }
}
