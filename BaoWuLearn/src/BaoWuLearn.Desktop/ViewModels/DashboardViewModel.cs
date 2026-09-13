using Avalonia.Threading;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 总览页：个人学时看板 + 个人信息 + 挂课状态。
///
/// 数据来源与网页端 https://learn.baowugroup.com/#/userCenter 完全一致：
///  - 顶部三个学时数字 → queryArchivesStudyDurationStatistics
///  - 姓名/工号/组织/岗位/累计学习时长 → queryStudentDetails
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly LearnEngine _engine;
    private readonly UserCenterService _userCenter;
    private readonly Action<string> _log;

    /// <summary>
    /// 课件进度的本地时钟（口径见 <see cref="LiveProgressClock"/>，与队列页是同一个类）。
    ///
    /// 引擎约 60 秒才推一次快照，两次之间界面收不到任何更新 —— 没有它，
    /// 「课件进度」这个计时器会整段冻着（用户原话："那个计时器也不是按秒跳动的"）。
    /// </summary>
    private readonly LiveProgressClock _wareClock = new();

    /// <summary>最后一次引擎快照。每秒走表时要拿它取目标时长与暂停窗口。</summary>
    private LearnSnapshot? _lastSnapshot;

    /// <summary>
    /// 每秒推一次的秒表。没在挂课时 tick 直接返回，开销可忽略；
    /// 挂课中则由它把计时器按秒往前推。与队列页的秒表同频、同口径。
    /// </summary>
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>当前登录工号，学时/个人信息查询都要用。</summary>
    private string _stuCode = "";

    // ── 个人信息 ──────────────────────────────────────────
    [ObservableProperty] private string _userName = "—";
    [ObservableProperty] private string _userNo = "—";
    [ObservableProperty] private string _orgName = "—";
    [ObservableProperty] private string _postName = "—";
    [ObservableProperty] private string _totalLearnTimeText = "—";

    // ── 学时看板 ──────────────────────────────────────────
    [ObservableProperty] private double _totalHours;
    [ObservableProperty] private double _onlineSelfHours;
    [ObservableProperty] private double _centralizedHours;

    [ObservableProperty] private string _totalHoursText = "0";
    [ObservableProperty] private string _onlineSelfHoursText = "0";
    [ObservableProperty] private string _centralizedHoursText = "0";

    /// <summary>网络自学的细分说明（公开课 + 学习专区）。</summary>
    [ObservableProperty] private string _onlineDetailText = "公开课 — · 学习专区 —";

    /// <summary>集中培训的细分说明（网络专题班 + 培训班 + 面授班）。</summary>
    [ObservableProperty] private string _centralizedDetailText = "网络专题班 — · 培训班 — · 面授班 —";

    [ObservableProperty] private bool _isLoadingHours;
    [ObservableProperty] private string _hoursStatus = "登录后自动加载";
    [ObservableProperty] private string _yearText = $"{DateTime.Now.Year} 年度";

    /// <summary>false = 本年度，true = 上一年度。对应网页端的年度切换。</summary>
    [ObservableProperty] private bool _isLastYear;

    /// <summary>网络自学占本人在校总学时的比例（进度条用）。</summary>
    public double OnlineRatio => TotalHours <= 0 ? 0 : Math.Clamp(OnlineSelfHours / TotalHours, 0, 1);

    /// <summary>集中培训占本人总学时的比例。</summary>
    public double CentralizedRatio => TotalHours <= 0 ? 0 : Math.Clamp(CentralizedHours / TotalHours, 0, 1);

    partial void OnTotalHoursChanged(double value)
    {
        OnPropertyChanged(nameof(OnlineRatio));
        OnPropertyChanged(nameof(CentralizedRatio));
    }

    partial void OnOnlineSelfHoursChanged(double value) => OnPropertyChanged(nameof(OnlineRatio));
    partial void OnCentralizedHoursChanged(double value) => OnPropertyChanged(nameof(CentralizedRatio));

    partial void OnIsLastYearChanged(bool value)
    {
        YearText = (value ? DateTime.Now.Year - 1 : DateTime.Now.Year) + " 年度";
        _ = LoadHoursAsync();
    }

    // ── 挂课状态 ──────────────────────────────────────────
    // 注意：属性不能命名为 EngineState，否则会遮蔽同名枚举类型
    [ObservableProperty] private string _engineStateText = "空闲";
    [ObservableProperty] private string _currentCourse = "—";
    [ObservableProperty] private string _currentWare = "—";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "0m00s / 0m00s";
    [ObservableProperty] private int _queueTotal;
    [ObservableProperty] private int _queueDone;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _todayLearned = "0m";

    /// <summary>队列进度文案（"已完成 / 总数"）。</summary>
    public string QueueSummary => $"{QueueDone} / {QueueTotal}";

    partial void OnQueueDoneChanged(int value) => OnPropertyChanged(nameof(QueueSummary));
    partial void OnQueueTotalChanged(int value) => OnPropertyChanged(nameof(QueueSummary));

    public DashboardViewModel(LearnEngine engine, UserCenterService userCenter, Action<string> log)
    {
        _engine = engine;
        _userCenter = userCenter;
        _log = log;
        _engine.Snapshot += OnSnapshot;
        _tickTimer.Tick += (_, _) => TickLive();
        _tickTimer.Start();
    }

    /// <summary>退出前摘掉快照订阅并停表，避免关闭过程中还有任务往 UI 线程上堆（详见 QueueViewModel.DetachEngine）。</summary>
    public void DetachEngine()
    {
        _engine.Snapshot -= OnSnapshot;
        _tickTimer.Stop();
    }

    /// <summary>登录成功后由主窗口调用：记录工号并自动拉取学时与个人信息。</summary>
    public void SetUser(string? userName, string? userNo)
    {
        UserName = string.IsNullOrWhiteSpace(userName) ? "—" : userName;
        UserNo = string.IsNullOrWhiteSpace(userNo) ? "—" : userNo;
        _stuCode = userNo ?? "";
        IsLastYear = false;
        _ = LoadAllAsync();
    }

    /// <summary>把个人信息与学时一起拉一遍。</summary>
    public async Task LoadAllAsync()
    {
        await Task.WhenAll(LoadProfileAsync(), LoadHoursAsync());
    }

    private async Task LoadProfileAsync()
    {
        try
        {
            UpdateProfile(await _userCenter.GetProfileAsync());
        }
        catch (Exception ex)
        {
            _log("✖ 个人信息加载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 用最新个人信息刷新界面。
    ///
    /// ★ v1.0.30 教训：「累计学习时长」来自 queryStudentDetails，旧版只在登录时拉一次，
    /// 而「刷新学时」按钮只刷顶部的学时统计 —— 用户会看到「学时 15、累计时长 4 小时」
    /// 对不上的假象。现在刷新学时、15 分钟保活都会顺带把这里刷一遍。
    /// </summary>
    public void UpdateProfile(StudentProfile? p)
    {
        if (p is null) return;

        if (!string.IsNullOrWhiteSpace(p.StuName)) UserName = p.StuName;
        if (!string.IsNullOrWhiteSpace(p.StuCode)) UserNo = p.StuCode;
        OrgName = string.IsNullOrWhiteSpace(p.OrgName) ? "—" : p.OrgShortName;
        PostName = string.IsNullOrWhiteSpace(p.PostName) ? "—" : p.PostName;
        TotalLearnTimeText = p.LearnTimeText;

        if (string.IsNullOrWhiteSpace(_stuCode)) _stuCode = p.StuCode;
    }

    /// <summary>拉取学时统计。公开给界面刷新按钮与登录流程调用。</summary>
    [RelayCommand]
    private async Task LoadHoursAsync()
    {
        if (IsLoadingHours) return;
        if (string.IsNullOrWhiteSpace(_stuCode))
        {
            HoursStatus = "尚未登录，无法获取学时";
            return;
        }

        IsLoadingHours = true;
        HoursStatus = "正在获取学时…";

        // 「累计学习时长」走的是另一个接口（queryStudentDetails），一起刷，
        // 否则它永远停在登录时刻（v1.0.30 修复）。
        await LoadProfileAsync();

        try
        {
            var h = await _userCenter.GetStudyHoursAsync(_stuCode, IsLastYear ? 1 : 0);
            if (h is null)
            {
                HoursStatus = "未获取到学时数据";
                return;
            }

            TotalHours = h.TotalGetHours;
            OnlineSelfHours = h.OnlineSelfStudyHours;
            CentralizedHours = h.CentralizedTrainingHours;

            TotalHoursText = Fmt(h.TotalGetHours);
            OnlineSelfHoursText = Fmt(h.OnlineSelfStudyHours);
            CentralizedHoursText = Fmt(h.CentralizedTrainingHours);

            OnlineDetailText = $"公开课 {Fmt(h.PublicCourseHours)} · 学习专区 {Fmt(h.StudyZoneHours)}";
            CentralizedDetailText =
                $"网络专题班 {Fmt(h.OnlineTopicHours)} · 培训班 {Fmt(h.TrainCourseHours)} · 面授班 {Fmt(h.FaceClassHours)}";

            HoursStatus = h.IsEmpty
                ? $"{h.Year} 年度暂无学时记录"
                : $"统计区间 {h.Year}-01-01 ~ {h.Year}-12-31";
            _log($"✓ 学时已更新：总 {TotalHoursText}（网络自学 {OnlineSelfHoursText} / 集中培训 {CentralizedHoursText}）");
        }
        catch (Exception ex)
        {
            HoursStatus = "学时获取失败：" + ex.Message;
            _log("✖ 学时获取失败：" + ex.Message);
        }
        finally
        {
            IsLoadingHours = false;
        }
    }

    /// <summary>切到上一年度（会触发重新拉取学时）。</summary>
    [RelayCommand]
    private void ShowLastYear() => IsLastYear = true;

    /// <summary>切回本年度（会触发重新拉取学时）。</summary>
    [RelayCommand]
    private void ShowCurrentYear() => IsLastYear = false;

    private void OnSnapshot(LearnSnapshot s)
    {
        void Apply()
        {
            EngineStateText = s.State switch
            {
                EngineState.Running => "运行中",
                EngineState.Paused => "已暂停",
                EngineState.Stopping => "停止中",
                EngineState.Stopped => "已停止",
                _ => "空闲",
            };

            CurrentCourse = string.IsNullOrEmpty(s.CourseName) ? "—" : s.CourseName;
            CurrentWare = string.IsNullOrEmpty(s.WareName) ? "—" : s.WareName;
            // 快照入钟：归属键一变（换课件）时钟会自己把进度清零，再写进界面。
            var now = DateTimeOffset.Now;
            _wareClock.Accept($"{s.CourseNo}@{s.OlClassNo}#{s.WareIndex}", s.PlayedSeconds, now);
            PublishProgress(s, now);

            QueueTotal = s.TotalCourses;
            QueueDone = s.CompletedCourses;
            IsRunning = s.State == EngineState.Running;
            _lastSnapshot = s;
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>
    /// 把课件进度写到界面。
    ///
    /// 单调 / 封顶 / 暂停停表全部交给 <see cref="LiveProgressClock"/> ——
    /// 快照到达与每秒 tick 共用这一条路，两个入口一个口径。
    /// </summary>
    private void PublishProgress(LearnSnapshot s, DateTimeOffset now)
    {
        var shown = _wareClock.Advance(s.TargetSeconds, s.PausingUntil, now);

        Progress = s.TargetSeconds > 0 ? Math.Clamp(shown / s.TargetSeconds, 0, 1) : 0;
        ProgressText = $"{Format(shown)} / {Format(s.TargetSeconds)}";
        TodayLearned = Format(shown);
    }

    /// <summary>
    /// 每秒推一次：让「课件进度」这个计时器跟着墙钟走。
    ///
    /// ★ 引擎只在心跳（约 60 秒）前后推一次快照，旧版这个计时器只在那一刻刷新，
    ///   两次之间整段冻着 —— 用户的原话是"那个计时器也不是按秒跳动的"。
    ///   现在和队列页用同一个 <see cref="LiveProgressClock"/>，口径只有一处。
    /// </summary>
    private void TickLive()
    {
        var s = _lastSnapshot;
        if (s is null || s.State != EngineState.Running) return;
        PublishProgress(s, DateTimeOffset.Now);
    }

    [RelayCommand]
    private void Start()
    {
        _engine.Start();
        _log("从总览页启动引擎");
    }

    [RelayCommand]
    private void Pause()
    {
        if (IsRunning) _engine.Pause();
        else _engine.Resume();
    }

    [RelayCommand]
    private void Stop() => _engine.Stop();

    /// <summary>学时数值展示：整数不带小数点。</summary>
    private static string Fmt(double v) => v == Math.Floor(v) ? ((long)v).ToString() : v.ToString("0.##");

    private static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h{ts.Minutes:00}m"
            : $"{ts.Minutes}m{ts.Seconds:00}s";
    }
}
