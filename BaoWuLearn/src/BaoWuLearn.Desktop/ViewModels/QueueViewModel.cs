using System.Collections.ObjectModel;
using Avalonia.Threading;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 学习队列页 —— 也是挂课页。
///
/// 上半部分是「我的已选课程」，数据与网页端 userCenter 一致
/// （公开课 / 学习专区 / 网络专题班 / 培训班），勾选后加入挂机队列；
/// 下半部分是「挂机操作区」，开始挂课后浮出，显示当前在挂课程、
/// 挂机列表、视频时长、得分、分数线。
///
/// 关键点：挂机跑在引擎自己的后台任务里，界面上的任何操作
/// （收起面板、切页、改筛选）都不会影响它。
/// </summary>
public partial class QueueViewModel : ViewModelBase
{
    private readonly LearnEngine _engine;
    private readonly CourseService _courses;
    private readonly UserCenterService _userCenter;
    private readonly Action<string> _log;

    /// <summary>当前登录工号，查"我的已选课程"要用。</summary>
    private string _stuCode = "";

    /// <summary>归档查询在用的工号（登录后可能被个人信息接口校正，供主窗口比对）。</summary>
    public string StuCode => _stuCode;

    /// <summary>最后一次引擎快照。列表重建时用它恢复"当前在挂"的高亮。</summary>
    private LearnSnapshot? _lastSnapshot;

    /// <summary>
    /// 课件级与课程级两个本地时钟（口径见 <see cref="LiveProgressClock"/>）。
    ///
    /// ★ 为什么是两个而不是一个：左侧「课件进度」算的是**当前视频**的账（换个视频就归零重来），
    ///   挂机列表行内那条算的是**整门课**的账（一直往前爬）。共用一个时钟会互相打架。
    ///
    /// ★ 它们与总览页用的是同一个类、同一套纪律 —— 两个页面显示同一个数字，不能各算各的。
    /// </summary>
    private readonly LiveProgressClock _wareClock = new();
    private readonly LiveProgressClock _courseClock = new();

    /// <summary>
    /// 挂机列表里「当前在挂」的那一行 —— 它的时长文本吃实时账本（见 QueueRow.LiveDurationText）。
    /// 非当前行的 LiveDurationText 保持 null，显示平台回读值。
    /// </summary>
    private QueueRow? _liveRow;

    /// <summary>
    /// 「下次心跳」倒计时的秒表。
    /// 引擎只在每次心跳前后推一次快照，两次心跳之间界面收不到任何更新 ——
    /// 不自己每秒刷新的话，倒计时会一直停在发车时那个数字（就是用户看到的"永远是 59 秒"）。
    /// </summary>
    private readonly DispatcherTimer _beatTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // ── 已选课程 ──────────────────────────────────────────
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "登录后自动加载已选课程";
    [ObservableProperty] private string _yearText = $"{DateTime.Now.Year} 年度";

    /// <summary>false = 本年度，true = 上一年度。</summary>
    [ObservableProperty] private bool _isLastYear;

    /// <summary>分类下拉的当前选项。</summary>
    [ObservableProperty] private string _selectedCategory = AllCategories;

    public const string AllCategories = "全部分类";

    public ObservableCollection<string> CategoryOptions { get; } = new() { AllCategories };

    /// <summary>平台返回的全部已选课程。</summary>
    public ObservableCollection<MyCourseRow> MyCourses { get; } = new();

    /// <summary>界面实际显示的行（含展开出来的班内课程）。</summary>
    public ObservableCollection<MyCourseRow> VisibleCourses { get; } = new();

    partial void OnSelectedCategoryChanged(string value) => RebuildVisible();

    partial void OnIsLastYearChanged(bool value)
    {
        YearText = (value ? DateTime.Now.Year - 1 : DateTime.Now.Year) + " 年度";
        _ = LoadMyCoursesAsync();
    }

    // ── 挂机操作区 ────────────────────────────────────────
    /// <summary>挂机操作区是否展开。收起后挂机照常运行。</summary>
    [ObservableProperty] private bool _isConsoleOpen;

    [ObservableProperty] private string _stateText = "空闲";
    [ObservableProperty] private string _currentCourse = "—";
    [ObservableProperty] private string _currentWare = "—";
    /// <summary>"当前课程共几个视频、正在播第几个"的提示文案（空串 = 暂无数据，不占位）。</summary>
    [ObservableProperty] private string _wareProgressText = "";
    [ObservableProperty] private string _currentDurationText = "—";
    [ObservableProperty] private string _currentScoreText = "—";
    [ObservableProperty] private string _currentPassScoreText = "—";
    /// <summary>
    /// 「得分」卡片下方的结算台账文案。
    ///
    /// 结算与得分刷新原先只发生在引擎内部，界面上完全看不见 ——
    /// 用户原话是「我也不知道做了结算没有」。这一行把"提交了几次、什么时候、
    /// 平台受没受理、成绩有没有跟着刷新"直接摊出来。
    /// </summary>
    [ObservableProperty] private string _currentSettleText = "尚未提交结算";
    /// <summary>
    /// 「得分」卡片里的视频占分提示（"视频占分 80%"）。
    ///
    /// ★ 平台得分 = 已学时长 ÷ 要求时长 × 视频占分权重，各课权重不同（实测 70/80 都有）
    ///   —— 同样挂 30 分钟，权重 80 的课拿 41 分、权重 70 的课拿 36 分。
    ///   不把权重亮出来，用户没法判断"这门课还要挂多久才到分数线"。
    /// </summary>
    [ObservableProperty] private string _currentWeightText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "0m00s / 0m00s";

    /// <summary>
    /// 整门课程的进度（0~1）—— 挂机列表行内那条进度条用的就是它。
    ///
    /// ★ 它和左侧面板的「课件进度」**不是一个东西**：左侧是当前视频播到哪了，
    ///   这条是这门课所有视频合计挂到哪了。课程有三个视频时，左边每换一个视频
    ///   就归零重来，这条只认整门课的总账，一直往前爬 —— 这正是它存在的理由。
    /// </summary>
    [ObservableProperty] private double _courseProgress;
    [ObservableProperty] private string _counters = "0 / 0";
    [ObservableProperty] private string _nextBeatText = "—";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    /// <summary>引擎正在做停止收尾（等停止结算落库）：这一小段时间按钮全部置灰。</summary>
    [ObservableProperty] private bool _isStopping;

    /// <summary>引擎当前状态（每次快照同步）。控制条按钮的可用性全部由它推导。</summary>
    private EngineState _engineState = EngineState.Idle;

    /// <summary>
    /// 控制条四个按钮的可用性。
    ///
    /// 口径（与 <see cref="ControlBarStates"/> 一致）：
    ///   · 空闲/已停止 → 只有「开始挂课」可用（有队列才有得开始）；
    ///   · 运行/暂停中 → 暂停、停止、保存进度可用，开始挂课置灰（已经在挂了，不能重复开始）；
    ///   · 停止中     → 全部置灰（正在收尾，点了也没有意义）。
    /// </summary>
    public bool CanStartHang => ControlBarStates(_engineState, HasQueue).Start;
    public bool CanPause => ControlBarStates(_engineState, HasQueue).Pause;
    public bool CanStop => ControlBarStates(_engineState, HasQueue).Stop;
    public bool CanSaveProgress => ControlBarStates(_engineState, HasQueue).Save;

    /// <summary>四个控制按钮在给定引擎状态下是否可点（纯函数，便于自检逐状态验算）。</summary>
    public static (bool Start, bool Pause, bool Stop, bool Save) ControlBarStates(
        EngineState state, bool hasQueue)
        => state switch
        {
            EngineState.Running => (false, true, true, true),
            EngineState.Paused => (false, true, true, true),
            EngineState.Stopping => (false, false, false, false),
            // 空闲与已停止：没开始（或已收工）就没有暂停/停止/保存这一说 —— 用户原话
            _ => (hasQueue, false, false, false),
        };

    /// <summary>
    /// 状态一变就让操作区的按钮重算可用性。
    /// 队列页与总览页都会调它，所以「清除已合格课程」的可用性也挂在这里 ——
    /// 队列内容或课程成绩一变（心跳回读会改 LearnScore），这个按钮的亮灭就得跟着重算。
    /// </summary>
    private void NotifyControlButtons()
    {
        OnPropertyChanged(nameof(CanStartHang));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSaveProgress));
        OnPropertyChanged(nameof(HasPassedInQueue));
    }

    /// <summary>挂机列表（= 引擎队列）。</summary>
    public ObservableCollection<QueueRow> QueueItems { get; } = new();

    /// <summary>引擎状态徽标文案（页面上方常显，不依赖操作区是否展开）。</summary>
    public string ConsoleHint => IsConsoleOpen ? "收起面板（挂机继续）" : "显示挂机面板";

    partial void OnIsConsoleOpenChanged(bool value) => OnPropertyChanged(nameof(ConsoleHint));

    /// <summary>暂停/继续按钮的动态文案。</summary>
    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonText));

    /// <summary>队列里是否有课程。空队列时「开始挂课」直接置灰。</summary>
    public bool HasQueue => _engine.Queue.Count > 0;

    /// <summary>
    /// 队列里有没有「清除已合格课程」真能清掉的东西 —— 没有就把按钮置灰。
    ///
    /// 口径与 <see cref="ClearPassed"/> 完全同源（同一个 <see cref="LearnEngine.ShouldClearPassed"/>），
    /// 所以不会出现"按钮亮着、点了却说没得清"。正在挂的那门不算（引擎也留着它）。
    /// </summary>
    public bool HasPassedInQueue
        => _engine.Queue.Any(c => LearnEngine.ShouldClearPassed(c, IsCurrentRunning(c)));

    /// <summary>这门课此刻是不是"正在挂"（引擎在跑，且当前课就是它）。</summary>
    private bool IsCurrentRunning(CourseItem c)
        => _engineState is EngineState.Running or EngineState.Paused
           && _lastSnapshot is { } s && s.Matches(c.CourseNo, c.OlClassNo);

    public QueueViewModel(
        LearnEngine engine,
        CourseService courses,
        UserCenterService userCenter,
        Action<string> log)
    {
        _engine = engine;
        _courses = courses;
        _userCenter = userCenter;
        _log = log;
        _children = new ClassChildrenLoader(courses, _log, IsInEngineQueue);

        _engine.Snapshot += OnSnapshot;
        _beatTimer.Tick += (_, _) => UpdateLiveTick();
        Refresh();
    }

    /// <summary>登录成功后由主窗口调用：记录工号并自动拉取已选课程。</summary>
    public void SetUser(string? stuCode)
    {
        _stuCode = stuCode ?? "";
        IsLastYear = false;
        _ = LoadMyCoursesAsync();
    }

    /// <summary>切到本页时调用：刷新挂机列表并重新拉一次平台的已选课程。</summary>
    public void OnNavigatedTo()
    {
        Refresh();
        _ = LoadMyCoursesAsync();
    }

    // ── 已选课程加载 ──────────────────────────────────────

    [RelayCommand]
    private async Task LoadMyCoursesAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(_stuCode))
        {
            StatusText = "尚未登录，无法获取已选课程";
            return;
        }

        IsBusy = true;
        StatusText = "正在获取我的已选课程…";
        try
        {
            var load = await _userCenter.GetMyCoursesAsync(_stuCode, IsLastYear ? 1 : 0);
            var list = load.Items;

            MyCourses.Clear();
            foreach (var item in list)
                MyCourses.Add(new MyCourseRow(item) { InQueue = IsInEngineQueue(item.CourseNo, item.OlClassNo) });

            // 列表换了一批行，旧的预取记录作废
            _children.Reset();

            // 分类下拉按实际出现过的分类重建，避免出现空分类
            var cats = list.Select(x => x.Category)
                           .Where(x => !string.IsNullOrWhiteSpace(x))
                           .Distinct()
                           .OrderBy(x => x)
                           .ToList();

            var keep = CategoryOptions.Contains(SelectedCategory) ? SelectedCategory : AllCategories;
            CategoryOptions.Clear();
            CategoryOptions.Add(AllCategories);
            foreach (var c in cats) CategoryOptions.Add(c);
            SelectedCategory = CategoryOptions.Contains(keep) ? keep : AllCategories;

            RebuildVisible();

            // ★ 失败必须可见：以前单个分类查询失败被静默吞掉，界面就是"无声的空白"，
            //   用户分不清"真的没课"还是"查询失败"。
            if (load.Errors.Count > 0)
            {
                foreach (var err in load.Errors) _log($"⚠ 已选课程查询失败：{err}");
                StatusText = load.Items.Count > 0
                    ? $"已加载 {load.Items.Count} 条（{load.Errors.Count} 个分类查询失败，详见运行日志）"
                    : "已选课程查询失败：" + load.Errors[0];
            }
            else
            {
                StatusText = MyCourses.Count == 0
                    ? $"{YearText}暂无已选课程"
                    : $"已加载 {MyCourses.Count} 条已选记录（{cats.Count} 个分类）";
                _log($"✓ 已选课程加载完成：{MyCourses.Count} 条");
            }

            // 后台把所有合集的班内明细拉齐：收起状态下也能看到"共几门、完成几门"。
            // 刻意不 await —— 主列表先出来，明细到货一行升级一行，不挡登录后的首屏。
            _ = PrefetchAllClassChildrenAsync();

            // 后台补齐「视频占分」权重（挂课要挂多久才到分数线就由它决定）。
            // 有会话缓存的课程不会重复发请求，失败按不显示处理。
            _ = CourseListOps.PrefetchWeightsAsync(Walk(MyCourses).ToList(), _courses);
        }
        catch (Exception ex)
        {
            StatusText = "已选课程加载失败：" + ex.Message;
            _log("✖ 已选课程加载失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>刷新：重新拉平台数据 + 刷新学时 + 刷新挂机列表。</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await LoadMyCoursesAsync();
        var loadOk = StatusText.StartsWith("已加载") || StatusText.Contains("暂无已选课程");

        // 队列里的课程对象也要重查一次学时，否则「视频时长 / 得分 / 分数线」
        // 还是入队那一刻的旧值。
        await _courses.EnrichWithScoreAsync(_engine.Queue).ConfigureAwait(true);
        Refresh();

        // 上面 LoadMyCoursesAsync 报过失败的话，别用一句"已刷新"把它盖掉。
        StatusText = loadOk
            ? "已刷新，学时为平台当前记录值"
            : StatusText;
    }

    /// <summary>切到上一年度（会触发重新拉取已选课程）。</summary>
    [RelayCommand]
    private void ShowLastYear() => IsLastYear = true;

    /// <summary>切回本年度（会触发重新拉取已选课程）。</summary>
    [RelayCommand]
    private void ShowCurrentYear() => IsLastYear = false;

    /// <summary>
    /// 树网格的顶层行（按分类过滤后的班级行 / 公开课行）。
    /// 班内课程不再是"插进平铺列表的子行"，而是挂在各行自己的 Children 里，由 TreeDataGrid 展开。
    /// </summary>
    private void RebuildVisible()
    {
        VisibleCourses.Clear();
        foreach (var row in MyCourses)
        {
            if (SelectedCategory != AllCategories && row.Category != SelectedCategory) continue;
            VisibleCourses.Add(row);
        }
    }

    // ── 班内明细预取（树网格的子行数据源）──────────────────
    /// <summary>
    /// 合集展开的加载器：并发闸、按班去重、写回 Children、补查成绩都在里面
    /// （见 <see cref="ClassChildrenLoader"/>）。课程页用的是同一套 ——
    /// 两页的子行口径必须一致，否则同一门课在两边显示的数字会不一样。
    /// </summary>
    private readonly ClassChildrenLoader _children;

    /// <summary>
    /// 确保某班级行的子课程已拉齐（树网格展开、预取、勾选合集入队都走这一条路）。
    /// 拉齐后：子行挂进行自身的 Children、收起态副标题升级为真实汇总。
    /// </summary>
    public Task EnsureChildrenAsync(MyCourseRow row) => _children.EnsureAsync(row);

    /// <summary>已选列表加载完成后，后台把所有合集的班内明细一次性拉齐（收起态也能看汇总）。</summary>
    private async Task PrefetchAllClassChildrenAsync()
    {
        var targets = MyCourses.Where(r => r.CanExpand && !r.ChildStatsReady).ToList();
        if (targets.Count == 0) return;
        await Task.WhenAll(targets.Select(EnsureChildrenAsync));
    }

    private bool IsInEngineQueue(string? courseNo, string? olClassNo)
        => _engine.Queue.Any(q => q.CourseNo == (courseNo ?? "") && q.OlClassNo == (olClassNo ?? ""));

    // ── 勾选与入队 ────────────────────────────────────────

    /// <summary>深度遍历树行（含每个合集已预取到的子课程）。</summary>
    private static IEnumerable<MyCourseRow> Walk(IEnumerable<MyCourseRow> rows)
    {
        foreach (var r in rows)
        {
            yield return r;
            foreach (var c in Walk(r.Children)) yield return c;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Walk(MyCourses).Where(r => r.CanCheck)) row.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Walk(MyCourses)) row.IsSelected = false;
    }

    /// <summary>
    /// 把勾选的内容加入挂机队列。
    /// 勾班级（学习专区 / 网络专题班 / 培训班）时会自动展开，把班内课程一并加入。
    /// </summary>
    [RelayCommand]
    private async Task AddSelectedAsync()
    {
        var picked = Walk(MyCourses).Where(r => r.IsSelected && r.CanCheck).ToList();
        if (picked.Count == 0)
        {
            StatusText = "请先勾选要挂课的课程或班级（勾班级会把班内课程一起加入）";
            return;
        }

        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var items = await CourseListOps.ResolveQueueItemsAsync(
                picked, _courses, msg => StatusText = msg);

            if (items.Count == 0)
            {
                StatusText = "勾选的内容里没有可以挂的课程";
                _log("⚠ 勾选内容解析后为空，未加入任何课程");
                return;
            }

            _engine.Enqueue(items);

            foreach (var r in picked)
            {
                r.InQueue = true;
                r.IsSelected = false;
            }

            Refresh();
            StatusText = $"已加入 {items.Count} 门课程到挂机队列";
            _log($"✓ 已加入挂机队列：{items.Count} 门");

            // 入队后立刻补一次学时/分数线，让列表一进来就有数
            await _courses.EnrichWithScoreAsync(_engine.Queue, ct: default).ConfigureAwait(true);
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = "加入挂机队列失败：" + ex.Message;
            _log("✖ 加入挂机队列失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把所有已选且能挂的课程一次性加入队列。</summary>
    [RelayCommand]
    private async Task AddAllAsync()
    {
        foreach (var row in Walk(MyCourses).Where(r => r.CanCheck)) row.IsSelected = true;
        await AddSelectedAsync();
    }

    /// <summary>
    /// 列表操作列那个「加入队列 / 移出队列」开关。
    /// 课程级行直接在行上切换，不必"先勾选、再点右上角大按钮"。
    /// </summary>
    [RelayCommand]
    private async Task ToggleQueueAsync(MyCourseRow? row)
    {
        if (row is null || IsBusy) return;

        // 已在队列 → 移出
        if (IsInEngineQueue(row.Item.CourseNo, row.Item.OlClassNo))
        {
            var target = _engine.Queue.FirstOrDefault(c =>
                c.CourseNo == row.Item.CourseNo && c.OlClassNo == row.Item.OlClassNo);
            if (target is not null)
            {
                _engine.Remove(target);
                _log($"已移出挂机队列：{row.Item.Title}");
            }
            Refresh();
            return;
        }

        // 不在队列 → 加入（走与"加入挂机队列"同一套解析，班级会自动展开）
        IsBusy = true;
        try
        {
            var items = await CourseListOps.ResolveQueueItemsAsync(
                new[] { row }, _courses, msg => StatusText = msg);

            if (items.Count == 0)
            {
                StatusText = "这门课暂时加不进队列（可能取不到课件列表）";
                return;
            }

            _engine.Enqueue(items);
            _log($"✓ 已加入挂机队列：{row.Item.Title}");

            await _courses.EnrichWithScoreAsync(_engine.Queue, ct: default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "加入挂机队列失败：" + ex.Message;
            _log("✖ 加入挂机队列失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    // ── 挂机控制 ──────────────────────────────────────────

    [RelayCommand]
    private void StartHang()
    {
        // 已经在挂（或正在收尾）就不再响应：按钮置灰是第一道，这里是第二道
        if (!CanStartHang)
        {
            StatusText = _engineState switch
            {
                EngineState.Running or EngineState.Paused => "挂课正在进行中，无需重复开始",
                EngineState.Stopping => "正在停止，请稍候",
                _ => "挂机队列是空的，请先在列表里勾选课程并加入队列",
            };
            return;
        }

        IsConsoleOpen = true;
        _engine.Start();
        Refresh();
    }

    [RelayCommand]
    private void ToggleConsole() => IsConsoleOpen = !IsConsoleOpen;

    [RelayCommand]
    private void PauseToggle()
    {
        if (!CanPause) return;
        if (IsPaused) _engine.Resume();
        else _engine.Pause();
    }

    [RelayCommand]
    private void StopHang()
    {
        if (!CanStop) return;
        _engine.Stop();
        Refresh();

        // 停止流程自带一次「停止结算」（引擎在收尾时补发），这里说清楚，
        // 免得用户停完立刻看列表以为"又没涨"。
        StatusText = "已停止挂课，正在把当前进度结算给平台（成绩异步落库，约几十秒后点「刷新」可见）";
    }

    /// <summary>「保存进度」按钮：不等心跳周期，立刻把当前课程已挂到的进度结算给平台。</summary>
    [RelayCommand]
    private async Task SaveProgressAsync()
    {
        if (!CanSaveProgress || IsSavingProgress) return;

        IsSavingProgress = true;
        StatusText = "正在保存进度（提交结算）…";
        try
        {
            await _engine.SaveProgressNowAsync();
            StatusText = "已提交保存进度，平台受理后几十秒内落库（见「得分」卡片下方的结算台账）";
        }
        catch (Exception ex)
        {
            StatusText = "保存进度失败：" + ex.Message;
            _log("✖ 手动保存进度失败：" + ex.Message);
        }
        finally
        {
            IsSavingProgress = false;
        }
    }

    /// <summary>「保存进度」正在提交中：按钮临时置灰，防连点重复提交结算。</summary>
    [ObservableProperty] private bool _isSavingProgress;

    [RelayCommand]
    private void RemoveFromQueue(QueueRow? row)
    {
        if (row is null) return;
        _engine.Remove(row.Course);
        Refresh();
        _log($"已移出挂机队列：{row.Course.CourseName}");
    }

    [RelayCommand]
    private void MoveUp(QueueRow? row)
    {
        if (row is null) return;
        _engine.MoveUp(row.Course);
        Refresh();
    }

    [RelayCommand]
    private void ClearQueue()
    {
        _engine.ClearQueue();
        Refresh();
        _log("挂机队列已清空");
    }

    /// <summary>
    /// 「清除已合格课程」：把已经挂满 / 已达分数线的课从队列里摘掉，
    /// 留下还没挂完的（含正在挂的那门，见 <see cref="LearnEngine.ShouldClearPassed"/>）。
    /// </summary>
    [RelayCommand]
    private void ClearPassed()
    {
        var n = _engine.ClearPassed();
        Refresh();

        // 清 0 门也要给个回话：按钮点了什么都不发生，用户只会怀疑是不是坏了
        _log(n > 0
            ? $"已清除 {n} 门已合格课程"
            : "队列里没有已合格的课程可清（正在挂的那门不算）");
    }

    // ── 队列与快照 ────────────────────────────────────────

    /// <summary>
    /// 退出程序前把界面侧的订阅与秒表全部摘掉。
    ///
    /// 少了这一步，关窗口时 UI 线程正忙着做收尾（等引擎停、补结算），
    /// 而引擎仍在推快照、秒表仍在每秒 tick —— 两边都往同一个 UI 线程上堆活儿，
    /// 谁也别想跑完。Mac 上表现就是"挂课中点关闭，程序直接卡死"。
    /// </summary>
    public void DetachEngine()
    {
        _beatTimer.Stop();
        _engine.Snapshot -= OnSnapshot;
    }

    /// <summary>按引擎队列重建挂机列表，并把界面上的"已在队列"标记同步回去。</summary>
    public void Refresh()
    {
        var queue = _engine.Queue;

        // 重建列表时按身份恢复"当前在挂"的高亮，否则每次刷新都会闪一下丢状态
        var snap = _lastSnapshot;
        var running = snap is { State: EngineState.Running or EngineState.Paused };

        QueueItems.Clear();
        for (var i = 0; i < queue.Count; i++)
        {
            QueueItems.Add(new QueueRow(i + 1, queue[i])
            {
                IsDone = queue[i].IsFinished,
                IsCurrent = running && snap!.Matches(queue[i].CourseNo, queue[i].OlClassNo),
            });
        }

        // 列表重建后旧行对象已全部作废，"当前在挂"的实时时长行要重新对上号，
        // 否则接下来每秒的账本推送写不进新行，列表里的时长又会冻住。
        _liveRow = null;
        if (running && snap is { } s && s.CourseTargetSeconds > 0)
        {
            var live = QueueItems.FirstOrDefault(r => s.Matches(r.Course.CourseNo, r.Course.OlClassNo));
            if (live is not null)
            {
                live.LiveDurationText = CurrentDurationText;
                _liveRow = live;
            }
        }

        OnPropertyChanged(nameof(HasQueue));
        // 空闲态下「开始挂课」是否可点由 HasQueue 决定，队列一变就得重算
        NotifyControlButtons();

        // 含合集子行 —— 直接挂进队列的班内课程也要同步「已在队列」角标
        foreach (var row in Walk(MyCourses))
            row.InQueue = IsInEngineQueue(row.Item.CourseNo, row.Item.OlClassNo);
    }

    /// <summary>
    /// 恢复队列后调用：把存档里的旧成绩刷成服务端最新值。
    ///
    /// 存档里的学时/得分是上次退出那一刻的快照，直接摆着会让人以为"挂了半天没动"。
    /// 刷新失败也不影响挂课 —— 真正挂课的过程中还会持续回读。
    /// </summary>
    public async Task RefreshQueueScoresAsync()
    {
        if (_engine.Queue.Count == 0) return;
        try
        {
            await _courses.EnrichWithScoreAsync(_engine.Queue, ct: default).ConfigureAwait(true);
            Refresh();
        }
        catch
        {
            // 静默：用户进队列页时还会再刷一次
        }
    }

    private void OnSnapshot(LearnSnapshot s)
    {
        void Apply()
        {
            StateText = s.State switch
            {
                EngineState.Running => "运行中",
                EngineState.Paused => "已暂停",
                EngineState.Stopping => "停止中",
                EngineState.Stopped => "已停止",
                _ => "空闲",
            };

            CurrentCourse = string.IsNullOrEmpty(s.CourseName) ? "—" : s.CourseName;
            CurrentWare = string.IsNullOrEmpty(s.WareName) ? "—" : s.WareName;
            WareProgressText = FormatWareProgress(s.WareIndex, s.WareTotal);

            // 快照入钟。归属键一变（换课件 / 换课程）时钟会自己把进度清零 ——
            // 少了这一步，新课件会卡在旧课件那个高位上不动。
            var now = DateTimeOffset.Now;
            _wareClock.Accept($"{s.CourseNo}@{s.OlClassNo}#{s.WareIndex}", s.PlayedSeconds, now);
            if (_courseClock.Accept($"{s.CourseNo}@{s.OlClassNo}", s.CoursePlayedSeconds, now))
                CourseProgress = 0;   // 换了课，行内那条课程进度条也从头爬

            PublishWareDisplay(s, now);
            Counters = $"{s.CompletedCourses} / {s.TotalCourses}";
            _engineState = s.State;
            IsRunning = s.State == EngineState.Running;
            IsPaused = s.State == EngineState.Paused;
            IsStopping = s.State == EngineState.Stopping;
            NotifyControlButtons();

            NextBeatText = FormatBeatCountdown(s.NextHeartbeatAt, s.State, DateTimeOffset.Now);

            // 引擎在跑就把倒计时秒表打开（每秒往前推），一停就收工。
            // 少了这一步，两次心跳之间界面收不到任何更新，倒计时会永远停在发车时那个数字。
            if (s.State == EngineState.Running)
            {
                if (!_beatTimer.IsEnabled) _beatTimer.Start();
            }
            else
            {
                _beatTimer.Stop();
            }

            // 高亮当前在挂的那一行。必须按「课程身份」匹配而不是索引 ——
            // 用户随时可能把课程移出队列，索引会整体前移，按索引会把"在挂"标到别的行上
            // （这正是「删掉一门再去挂另一门时，当前在挂显示不对」的根因）。
            var running = s.State is EngineState.Running or EngineState.Paused;
            _liveRow = null;
            for (var i = 0; i < QueueItems.Count; i++)
            {
                var row = QueueItems[i];
                row.IsCurrent = running && s.Matches(row.Course.CourseNo, row.Course.OlClassNo);
                row.IsDone = row.Course.IsFinished;
                // 非当前行的实时时长作废，退回平台回读值；当前行记下来，
                // 由 ApplyCourseDisplay 每秒把实时账本写进去（与面板同源同钟）。
                if (row.IsCurrent) _liveRow = row;
                else row.LiveDurationText = null;
                row.Bump();
            }

            // 「我的已选课程」树里也标出正在挂的那门（名称列右侧角标）。
            // 同样按课程身份匹配：Matches 要求 courseNo 非空，合集行天然不会误标。
            foreach (var row in Walk(MyCourses))
                row.IsCurrent = running && s.Matches(row.Item.CourseNo, row.Item.OlClassNo);

            // 时长 / 得分 / 分数线直接从引擎队列里的课程对象取，不依赖界面行是否存在 ——
            // 即使那一行刚被移出列表，操作区也能立即显示正确的数据。
            var cur = _engine.Queue.FirstOrDefault(c => s.Matches(c.CourseNo, c.OlClassNo));
            CurrentScoreText = cur?.LearnScore is { } sc ? sc.ToString("0.##") : "—";
            CurrentPassScoreText = cur?.PassScore is { } ps ? ps.ToString("0.##") : "—";
            CurrentWeightText = cur?.DurationScoreWeight is { } w and > 0
                ? $"视频占分 {w:0.##}%"
                : "";
            CurrentSettleText = FormatSettleLedger(
                s.SettleCount, s.LastSettleAt, s.LastSettleAccepted, s.ScoreRefreshedAt);

            // 引擎每次心跳回读的成绩，顺手镜像进「我的已选」树 ——
            // 已选行的得分列来自归档快照，否则它只在手动"刷新已选课程"时才动，
            // 用户盯着树里的分数看就会觉得"得分比课程进度慢一拍"。
            if (cur?.LearnScore is { } liveScore)
            {
                foreach (var row in Walk(MyCourses))
                {
                    if (row.Item.CourseNo != cur.CourseNo || row.Item.OlClassNo != cur.OlClassNo) continue;
                    if (Math.Abs((row.Item.CourseScore ?? 0) - liveScore) < 0.005) continue;
                    row.Item.CourseScore = liveScore;
                    if (row.SourceCourse is { } sc2) sc2.LearnScore = liveScore;
                    row.Bump();
                }
            }

            // 课程学时改用引擎的实时账本（基线 = 各课件在服务端的 maxPlayTime 之和，与平台口径一致）。
            // 不再用课程对象上那个平台学时字段 —— 它只有结算之后才会异步刷新，
            // 挂课途中纹丝不动，用户看到的就是"挂了半天这个数字没动"。
            if (s.CourseTargetSeconds > 0) PublishCourseDisplay(s, now);
            else CurrentDurationText = cur?.DurationText ?? "—";

            _lastSnapshot = s;
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>
    /// 每秒跑一次：推「下次心跳」倒计时，并把时长与进度条按本地流逝的时间往前补。
    /// 引擎停了就自动停表。
    /// </summary>
    private void UpdateLiveTick()
    {
        var s = _lastSnapshot;
        if (s is null)
        {
            _beatTimer.Stop();
            NextBeatText = "—";
            return;
        }

        NextBeatText = FormatBeatCountdown(s.NextHeartbeatAt, s.State, DateTimeOffset.Now);

        if (s.State != EngineState.Running)
        {
            _beatTimer.Stop();
            return;
        }

        var now = DateTimeOffset.Now;

        // ★ 按秒插值。
        //   引擎只在心跳（约 60 秒）前后推一次快照，两次之间界面收不到任何更新，
        //   进度条和两个时长会整段冻住 —— 用户的原话是"感官不好，我想要还是按秒更新"。
        //   模拟暂停期间"停表"由时钟内部处理（锚点顶到当前时刻、返回值原地不动），
        //   这里不必再分支 —— 分支就等于又多一处口径。
        PublishWareDisplay(s, now);

        if (s.CourseTargetSeconds > 0) PublishCourseDisplay(s, now);
    }

    /// <summary>
    /// 把结算台账拼成一行给用户看的人话（纯函数，便于 <c>--selftest</c> 离线验算）。
    ///
    /// 三种状态必须区分清楚，否则等于没显示：
    ///   · 提交了、成绩还没动 → 「等平台落库…」（平台是异步任务，几十秒很正常）
    ///   · 提交了、平台明确拒绝 → 「平台未受理」
    ///   · 成绩真的变了 → 「得分已刷新 hh:mm:ss」
    /// </summary>
    public static string FormatSettleLedger(
        int settleCount, DateTimeOffset? lastSettleAt,
        bool? lastSettleAccepted, DateTimeOffset? scoreRefreshedAt)
    {
        if (settleCount <= 0) return "尚未提交结算";

        var when = lastSettleAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
        var tail = scoreRefreshedAt is { } t
            ? $"得分已刷新 {t.ToLocalTime():HH:mm:ss}"
            : lastSettleAccepted == false ? "平台未受理" : "等平台落库…";

        return $"已结算 {settleCount} 次 · 最近 {when} · {tail}";
    }

    /// <summary>
    /// 已迁至 <see cref="LiveProgressClock"/>（与总览页共用同一份实现）。
    ///
    /// 保留这三个转发方法只是为了不动既有调用点与 <c>--selftest</c> 断言；
    /// **实现只有一处** —— 两个页面各写一套是这类显示最经典的翻车方式。
    /// </summary>
    public static double InterpolatePlayed(double snapshotPlayed, double target, double elapsedSeconds)
        => LiveProgressClock.InterpolatePlayed(snapshotPlayed, target, elapsedSeconds);

    /// <inheritdoc cref="LiveProgressClock.AdvanceDisplay"/>
    public static double AdvanceDisplay(double shownBefore, double candidate)
        => LiveProgressClock.AdvanceDisplay(shownBefore, candidate);

    /// <inheritdoc cref="LiveProgressClock.IsPausing"/>
    public static bool IsPausing(DateTimeOffset? pausingUntil, DateTimeOffset now)
        => LiveProgressClock.IsPausing(pausingUntil, now);

    /// <summary>
    /// 把课件进度写到界面。
    ///
    /// 单调、封顶、暂停停表全部交给 <see cref="LiveProgressClock"/> ——
    /// 快照到达与每秒 tick 都走这一条路，两个入口一个口径。
    /// </summary>
    private void PublishWareDisplay(LearnSnapshot s, DateTimeOffset now)
    {
        var shown = _wareClock.Advance(s.TargetSeconds, s.PausingUntil, now);

        ProgressText = $"{Format(shown)} / {Format(s.TargetSeconds)}";
        Progress = s.TargetSeconds > 0 ? Math.Clamp(shown / s.TargetSeconds, 0, 1) : 0;
    }

    /// <summary>
    /// 把课程级已学时长写到界面，并同步挂机列表当前行的实时时长。
    ///
    /// 「换课件 / 换课程就清零」由时钟的归属键负责（见 <see cref="LiveProgressClock.Accept"/>）。
    /// </summary>
    private void PublishCourseDisplay(LearnSnapshot s, DateTimeOffset now)
    {
        var shown = _courseClock.Advance(s.CourseTargetSeconds, s.PausingUntil, now);

        CurrentDurationText = $"{Format(shown)} / {Format(s.CourseTargetSeconds)}";
        // 行内进度条吃的整门课进度与时长同源（同一个单调 floor），天然不会回退
        CourseProgress = s.CourseTargetSeconds > 0 ? Math.Clamp(shown / s.CourseTargetSeconds, 0, 1) : 0;

        // ★ 挂机列表当前行的「时长」与面板这里写的是**同一个字符串**：
        //   心跳快照与每秒插值都从这一条路走，面板涨一秒、列表就涨一秒，
        //   不再出现「面板在动、列表死着」的两个时钟。
        _liveRow?.LiveDurationText = CurrentDurationText;
    }

    /// <summary>
    /// 「当前课程共几个视频、正在播第几个」的文案。
    ///
    /// 抽成纯函数是为了能在 <c>--selftest</c> 里直接验算；
    /// 序号为 0（课件还没发车）时显示"准备开始"，总数未知时返回空串让提示整个藏起来。
    /// </summary>
    public static string FormatWareProgress(int wareIndex, int wareTotal)
    {
        if (wareTotal <= 0) return "";
        return wareIndex <= 0
            ? $"共 {wareTotal} 个视频 · 准备开始"
            : $"共 {wareTotal} 个视频 · 正在播第 {wareIndex} 个";
    }

    /// <summary>
    /// 「下次心跳」的倒计时文案。
    ///
    /// 抽成纯函数是为了能在 <c>--selftest</c> 里直接验算：
    /// 这个数字必须随"当前时刻"递减，而它以前只在引擎推快照时算一次，
    /// 两次心跳之间界面完全不动 —— 用户看到的就是"永远停在 59 秒"。
    /// </summary>
    public static string FormatBeatCountdown(DateTimeOffset? next, EngineState state, DateTimeOffset now)
    {
        if (state != EngineState.Running || next is not { } t) return "—";
        return $"约 {Math.Max(0, (int)(t - now).TotalSeconds)} 秒后";
    }

    private static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h{ts.Minutes:00}m"
            : $"{ts.Minutes}m{ts.Seconds:00}s";
    }
}
