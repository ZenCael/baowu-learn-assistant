using System.Collections.ObjectModel;
using Avalonia.Threading;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 课程页：按平台真实菜单组织课程浏览。
///
/// 网页端 <c>/#/learnManagement/:centerCode</c> 下的菜单：
///   网络自学 → 公开课程、学习专区
///   集中培训 → 我的网络专题班、我的培训班
/// （考试中心 / Global Talk / 学习社群 / 课程共创 / 学习助手 不含课程，故不收录。）
///
/// 两类菜单的数据口径不同，与平台前端保持一致：
///  - 公开课程 / 学习专区：全平台可浏览目录（ignoreCenter=true 跨站点）
///  - 我的网络专题班 / 我的培训班：**本人**已加入的班级（归档接口），
///    行内可展开出班内课程再挑要挂的课
///
/// 「加入已选课程」平台没有独立按钮 —— 点进课程开始学习就会自动进入已选列表，
/// 对应接口是 learnRecord/initLearnRecord，这里复刻同一调用。
/// </summary>
public partial class CoursesViewModel : ViewModelBase
{
    private readonly CourseService _courses;
    private readonly UserCenterService _userCenter;
    private readonly LearnEngine _engine;
    private readonly Action<string> _log;

    /// <summary>当前登录工号，"我的…"两个菜单与加入已选都要用。</summary>
    private string _stuCode = "";

    /// <summary>归档查询在用的工号（登录后可能被个人信息接口校正，供主窗口比对）。</summary>
    public string StuCode => _stuCode;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "选择左侧菜单开始浏览";
    [ObservableProperty] private CourseMenu? _selectedMenu;
    [ObservableProperty] private bool _hideFinished;

    /// <summary>学习中心代码。网页端路由 /learnManagement/C001 里的那一段。</summary>
    [ObservableProperty] private string _centerCode = "C001";

    // ── 学习中心下拉 ──────────────────────────────────────
    //
    // 原先这里是个手输编码的输入框（C001 / C007 这种），能用但很难用：
    // 用户记得住站点的中文名，记不住中心编码 C007。平台自己有站点下拉
    // （selectCurrentStuHomeCourseAuthCenterList），中文名与编码都给了，照抄过来。

    /// <summary>可进入的学习中心（登录预热时拉取，按平台 sort 升序）。</summary>
    public ObservableCollection<CenterOption> Centers { get; } = new();

    [ObservableProperty] private CenterOption? _selectedCenter;

    /// <summary>中心列表是否真的来自平台（拉取失败时界面要说明这是兜底状态）。</summary>
    [ObservableProperty] private bool _centersLoaded;

    /// <summary>中心列表拉取失败的说明（成功时清空）。</summary>
    [ObservableProperty] private string _centersError = "";

    /// <summary>下拉框的 ToolTip：正常说清来源，失败就说清"现在是兜底状态"。</summary>
    public string CentersHint => CentersLoaded
        ? "选择学习中心（列表来自平台，选中即切换并重取）"
        : $"⚠ 平台中心列表未加载（{CentersError}），当前仅可按 {CenterCode} 查询";

    partial void OnCentersLoadedChanged(bool value) => OnPropertyChanged(nameof(CentersHint));
    partial void OnCentersErrorChanged(string value) => OnPropertyChanged(nameof(CentersHint));

    /// <summary>
    /// 选中心 = 换数据源：归 1 页、作废索引与上一次的平台检索结果，然后立刻重取。
    /// 只改编码不重取的话，用户看到的还是上一个中心的课 —— 比报错更糟。
    /// </summary>
    partial void OnSelectedCenterChanged(CenterOption? value)
    {
        if (value is null || value.CenterCode == CenterCode) return;

        CenterCode = value.CenterCode;    // 触发 OnCenterCodeChanged → InvalidateIndex
        Page = 1;
        _ = LoadAsync();
    }

    /// <summary>
    /// 拉取学习中心列表并选中当前中心（默认 C001）。
    ///
    /// ★ 失败也要让下拉框有的可显示：兜底塞一条"当前编码"，并在界面上写明列表没拉到。
    ///   否则用户面对一个空下拉框，连"本来能查"这件事都看不出来。
    /// </summary>
    public async Task LoadCentersAsync()
    {
        try
        {
            var list = await _courses.GetCentersAsync();
            if (list.Count == 0) throw new InvalidOperationException("平台返回了空的中心列表");

            Centers.Clear();
            foreach (var c in list) Centers.Add(c);

            CentersLoaded = true;
            CentersError = "";
            SelectedCenter = PickDefaultCenter(list, CenterCode);
            _log($"✓ 学习中心列表就绪：{list.Count} 个（当前 {SelectedCenter?.Display ?? CenterCode}）");
        }
        catch (Exception ex)
        {
            CentersLoaded = false;
            CentersError = ex.Message;
            _log($"⚠ 学习中心列表获取失败：{ex.Message}（下拉框暂只显示当前中心 {CenterCode}）");

            if (Centers.Count == 0)
            {
                var fallback = new CenterOption { CenterCode = CenterCode, CenterName = "（中心列表未加载）" };
                Centers.Add(fallback);
                SelectedCenter = fallback;
            }
        }
    }

    /// <summary>
    /// 默认选中哪一个中心（纯函数，便于 <c>--selftest</c> 离线验算）：
    /// 编码对得上就沿用它，对不上就取平台排在最前的那个（列表已按 sort 排好）。
    /// </summary>
    public static CenterOption? PickDefaultCenter(IReadOnlyList<CenterOption> list, string currentCode)
    {
        if (list.Count == 0) return null;
        foreach (var c in list)
            if (string.Equals(c.CenterCode, currentCode, StringComparison.OrdinalIgnoreCase)) return c;
        return list[0];
    }

    // ── 分页（只对"浏览型"菜单有效） ───────────────────────
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _pageSize = 20;

    /// <summary>当前菜单是否支持分页（"我的…"是归档接口，一次取全）。</summary>
    public bool SupportsPaging => SelectedMenu?.Kind is CourseMenuKind.PublicCourse or CourseMenuKind.StudyZone;

    public int TotalPages => Total <= 0 ? 1 : (int)Math.Ceiling(Total / (double)PageSize);
    public string PageText => $"第 {Page} / {TotalPages} 页";
    public string TotalText => Total <= 0 ? "—" : $"共 {Total} 条";
    public bool CanPrev => SupportsPaging && Page > 1 && !IsBusy;
    public bool CanNext => SupportsPaging && Page < TotalPages && !IsBusy;

    partial void OnSelectedMenuChanged(CourseMenu? value)
    {
        OnPropertyChanged(nameof(SupportsPaging));
        BumpPaging();
        InvalidateIndex();          // 换了菜单，上一份索引 / 上一份平台结果都不再代表这个列表
        if (value is null) return;
        Page = 1;
        // ★ 这里**不再**额外 ArmSearch：LoadAsync 自己就认关键字（有关键字且这页签走 ES
        //   就顺手把检索做了）。再补一次防抖只会让慢网络下同一关键字请求两遍。
        _ = LoadAsync();
    }

    /// <summary>换了学习中心：作废索引与上一次的平台检索结果（键里含中心代码）。</summary>
    partial void OnCenterCodeChanged(string value)
    {
        InvalidateIndex();
        OnPropertyChanged(nameof(CentersHint));   // 兜底文案里带着当前编码
    }

    /// <summary>
    /// 丢掉手上所有"比当前页更大"的检索结果，并中止正在进行的索引任务。
    /// 换菜单 / 换学习中心 / 点「查询」都走这里 —— 平台检索结果同样只代表
    /// "当时那个页签 + 当时那个关键字"，留着就会被当成新条件的结果用。
    /// </summary>
    private void InvalidateIndex()
    {
        _indexCts?.Cancel();
        _indexCts = null;
        _fullIndex = null;
        _fullIndexKey = "";
        _indexError = null;
        IndexedCount = 0;
        IndexTotal = 0;
        IsIndexing = false;
        _esAppliedKeyword = "";
        _esFailedKeyword = "";
        BumpSearchScope();
    }

    partial void OnTotalChanged(int value) => BumpPaging();
    partial void OnPageChanged(int value) => BumpPaging();
    partial void OnIsBusyChanged(bool value) => BumpPaging();

    private void BumpPaging()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
    }

    partial void OnHideFinishedChanged(bool value) => RebuildVisible();

    /// <summary>
    /// 搜索关键字。
    ///
    /// ★ 检索源**跟着当前页签走**（v1.0.39）：
    ///   公开课程 / 学习专区 → 平台自带的 ES 全文检索（一次请求一页、带真 total，
    ///     命中范围含标题/标签/简介，翻页照常）；
    ///   我的网络专题班 / 我的培训班 → 本地过滤（平台 ES 不收这两类，
    ///     一律问 ES 会得到"搜不到"的错误结论）。
    ///
    /// 与「只看未完成」叠加生效；本地过滤那条路上，合集名不匹配但**已加载**的子课程
    /// 命中时也整个保留 —— 用户记得的是"那个合集里有门课叫 xx"。
    ///
    /// 演进过程留个记录：
    ///   ≤v1.0.37 只过滤当前这一页（用户原话「这个查询就是在下方列表当前显示的范围内查询，
    ///     意义不大」）→ v1.0.38 客户端把整个目录翻页建索引 →
    ///   v1.0.39 发现平台本来就有检索接口（用户原话「这个翻页拿数据的方法好蠢，
    ///     有没有可能，平台有检索接口」），两个浏览型页签改走服务端检索，
    ///     翻页索引降级为"平台检索失败时的备胎"。
    /// </summary>
    [ObservableProperty] private string _searchText = "";

    partial void OnSearchTextChanged(string value)
    {
        _esAppliedKeyword = "";               // 关键字一改，手上这批行就不再代表新关键字
        RebuildVisible();                     // 先用手头这一页的结果给个即时反馈
        BumpSearchScope();
        ArmSearch();
        if (string.IsNullOrWhiteSpace(value)) _indexCts?.Cancel();   // 关键字清空 → 别再翻了
        UpdateSearchStatus();
    }

    // ── 平台检索（ES）─────────────────────────────────────
    //
    // v1.0.38 的"全库搜索"是客户端把目录一页页翻回来建索引（FetchAllPagesAsync）。
    // 能用了，但确实蠢：35 页 686 条要先全拉回来，才能回答"有没有一门课叫 xx"。
    // 平台其实自带 ES 全文检索（网页端那个大搜索框），v1.0.39 两个浏览型页签改走它 ——
    // 一次请求一页、带真 total，翻页照常工作。
    //
    // ★ 收录范围实测（详见 Core/Http/ApiEndpoints.cs 的注释）：
    //   ES 只有 course / zone 两个与本页面对口的库（公开课程 / 学习专区），
    //   专题班 ZE1 与培训班 TCE 压根不在索引里 —— 那两个页签维持本地过滤 + 翻页索引。
    //   所以检索源必须**跟着当前页签选**，不能一律问 ES（一律问会得到"搜不到"的错误结论）。

    /// <summary>当前页签是否该走平台 ES 检索（纯函数，便于 <c>--selftest</c> 离线验算）。</summary>
    public static bool UsesEsSearch(CourseMenuKind? kind, string? keyword)
        => !string.IsNullOrWhiteSpace(keyword)
           && kind is CourseMenuKind.PublicCourse or CourseMenuKind.StudyZone;

    /// <summary>当前显示的行是不是"平台检索给回来的那批"（决定还要不要再按标题本地过滤）。</summary>
    private bool IsEsResultsActive
        => UsesEsSearch(SelectedMenu?.Kind, SearchText)
           && _esAppliedKeyword == (SearchText ?? "").Trim();

    /// <summary>已生效的平台检索关键字（与 SearchText 去空格后相等 → 界面显示的就是平台结果）。</summary>
    private string _esAppliedKeyword = "";

    /// <summary>平台检索失败的那个关键字（失败时界面明说，并退回翻页索引）。</summary>
    private string _esFailedKeyword = "";

    /// <summary>
    /// 有关键字、但这次的检索结果还没到手时补一次防抖。
    ///
    /// ★ 光在 <see cref="OnSearchTextChanged"/> 里挂防抖是不够的：
    ///   带着关键字**换菜单**（或点「查询」）时关键字没变、那个钩子不会响，
    ///   结果就再也没人取 —— 界面会永远停在"检索中…"。
    ///
    /// 两种待办都从这里出门：
    ///   ① 浏览型页签 → 向平台发一次 ES 检索（Page 归 1）
    ///   ② 平台检索已经失败过的关键字 → 退回 v1.0.38 那套翻页建索引
    /// </summary>
    private void ArmSearch()
    {
        _searchDebounce.Stop();
        if (ShouldRequestEsSearch(SelectedMenu?.Kind, SearchText, _esAppliedKeyword, _esFailedKeyword)
            || ShouldBuildIndex(SearchText, SupportsPaging, IsIndexReady))
            _searchDebounce.Start();
    }

    /// <summary>
    /// 该不该再向平台发一次检索（纯函数，便于 <c>--selftest</c> 离线验算）：
    /// 该走 ES 的页签 + 有关键字 + 这个关键字还没出过结果、也没失败过。
    /// 后两个条件缺一不可 —— 少了就会出现"每敲一个字就打一次平台"或
    /// "失败后每次防抖都重试、把网关打个来回"。
    /// </summary>
    public static bool ShouldRequestEsSearch(
        CourseMenuKind? kind, string? keyword, string appliedKeyword, string failedKeyword)
    {
        var kw = keyword?.Trim() ?? "";
        if (!UsesEsSearch(kind, kw)) return false;
        return kw != appliedKeyword && kw != failedKeyword;
    }

    /// <summary>
    /// 一行是否匹配搜索关键字（纯函数，便于 <c>--selftest</c> 离线验算）。
    /// 关键字为空视为全匹配。
    /// </summary>
    public static bool MatchesFilter(MyCourseRow row, string? keyword)
    {
        var kw = keyword?.Trim();
        if (string.IsNullOrEmpty(kw)) return true;
        if (row.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;

        // 合集行：名字不匹配，但已加载的子课程里有命中的也保留 ——
        // 不然用户记得"那个合集里有门课叫 xx"，却因为合集名不匹配整个被滤掉了。
        foreach (var child in row.Children)
            if (child.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>清空搜索框。</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ── 全库搜索 ──────────────────────────────────────────
    //
    // 分页目录一页 20 条，"只在当前页里搜"等于没搜。这里维护一份**整库行**，
    // 关键字非空且索引就绪时，匹配就在它上面做（见 PickSearchSource）。

    /// <summary>全库索引（浏览型菜单才有；"我的…"是归档接口，本来就一次取全）。</summary>
    private List<MyCourseRow>? _fullIndex;

    /// <summary>索引对应的（菜单 · 学习中心）键 —— 换菜单 / 换中心即作废重建。</summary>
    private string _fullIndexKey = "";

    /// <summary>索引失败原因。非空时界面明说"只在本页里匹配"，不让用户以为搜的是全库。</summary>
    private string? _indexError;

    private CancellationTokenSource? _indexCts;

    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private int _indexedCount;
    [ObservableProperty] private int _indexTotal;

    /// <summary>搜索防抖：敲字时不立刻翻页，停手 400ms 再开始建索引。</summary>
    private readonly DispatcherTimer _searchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>索引对应的键（菜单类型 + 学习中心）。</summary>
    private string IndexKey => $"{(int?)SelectedMenu?.Kind}|{CenterCode}";

    /// <summary>是否处于"全库搜索"状态（有关键字）。</summary>
    public bool IsFullSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// 全库索引是否已就绪。
    /// 「我的网络专题班 / 我的培训班」走归档接口、一次取全 → 天然就绪，不需要额外翻页。
    /// </summary>
    public bool IsIndexReady
        => !SupportsPaging || (_fullIndex is not null && _fullIndexKey == IndexKey);

    /// <summary>
    /// 翻页控件是否显示。
    /// 平台检索是**服务端分页**（带真 total），页码照样有意义 → 继续显示；
    /// 只有退回 v1.0.38 那套翻页索引时结果来自整库、页码没有意义 → 让位给命中统计。
    /// </summary>
    public bool PagingVisible => SupportsPaging && !IsIndexSearchActive;

    /// <summary>
    /// 是否处于"用整库索引在搜"的状态（平台检索没接手时才成立）。
    /// ★ 界面拿它当"命中统计那一格"的显隐判据 —— 必须与 <see cref="PagingVisible"/> 互斥，
    ///   两个控件占的是同一格，同时可见就叠在一起（v1.0.22 那类"布局重叠"的老坑）。
    /// </summary>
    public bool IsIndexSearchActive => IsFullSearchActive && !IsEsResultsActive;

    /// <summary>
    /// 搜索范围提示（占翻页控件那一格）。
    ///
    /// ★ 三种口径必须分清楚说，别让用户以为都在搜同一件事：
    ///   平台检索命中 / 正在向平台检索 / 索引命中（并写明是退回方案）。
    /// </summary>
    public string SearchScopeText
    {
        get
        {
            if (!IsFullSearchActive) return "";

            if (IsEsResultsActive)
                return $"平台检索命中 {Total} 门 · 本页 {VisibleRows.Count} 条（匹配标题/标签/简介）";

            if (_esFailedKeyword.Length > 0)
                return "⚠ 平台检索不可用，已改用整库索引";

            if (_indexError is { } err) return $"⚠ 仅在本页匹配（全库索引失败：{err}）";

            if (IsIndexing)
                return IndexTotal > 0
                    ? $"正在建立全库索引 {IndexedCount}/{IndexTotal} 门…"
                    : $"正在建立全库索引（已取 {IndexedCount} 门）…";

            if (!IsIndexReady) return SupportsPaging ? "正在向平台检索…" : "检索中…";

            var scope = SupportsPaging ? $"共 {_fullIndex?.Count ?? 0} 门" : $"共 {AllRows.Count} 门";
            return $"全库命中 {VisibleRows.Count} 门 / {scope}";
        }
    }

    /// <summary>
    /// 搜索该在哪个集合上做（纯函数，便于 <c>--selftest</c> 离线验算）：
    /// 有关键字且全库索引已就绪 → 在**整库**上找；否则退回当前页。
    /// </summary>
    public static IReadOnlyList<MyCourseRow> PickSearchSource(
        string? keyword, IReadOnlyList<MyCourseRow> pageRows, IReadOnlyList<MyCourseRow>? fullIndex)
        => !string.IsNullOrWhiteSpace(keyword) && fullIndex is not null ? fullIndex : pageRows;

    /// <summary>
    /// 现在要不要去建全库索引（纯函数，便于自检）：
    /// 有关键字、当前菜单是分页目录、索引还没就绪 —— 三条都满足才翻页。
    /// </summary>
    public static bool ShouldBuildIndex(string? keyword, bool supportsPaging, bool indexReady)
        => !string.IsNullOrWhiteSpace(keyword) && supportsPaging && !indexReady;

    /// <summary>
    /// 自检专用：直接塞一份"全库索引"，跳过网络。
    /// 生产路径只有 <see cref="EnsureFullIndexAsync"/> 会填它 —— 自检没登录，不能真去翻页。
    /// </summary>
    internal void SetFullIndexForTest(IReadOnlyList<MyCourseRow> rows)
    {
        _fullIndex = rows.ToList();
        _fullIndexKey = IndexKey;
        _indexError = null;
        RebuildVisible();
    }

    /// <summary>自检专用：清掉全库索引，回到"只在当前页里找"的状态。</summary>
    internal void ClearFullIndexForTest()
    {
        _fullIndex = null;
        _fullIndexKey = "";
        _indexError = null;
        RebuildVisible();
    }

    /// <summary>
    /// 自检专用： pretend 平台检索已就返回了这个关键字的结果（自检没登录，不能真去问平台）。
    /// 用它来验"翻页控件"与"整库索引统计"两格的互斥关系 —— 两者同占一格，
    /// 判据不互斥就会叠出重影（v1.0.22 那类布局坑）。
    /// </summary>
    internal void SetEsResultsKeywordForTest(string? keyword)
    {
        _esAppliedKeyword = keyword ?? "";
        RebuildVisible();
        BumpSearchScope();
    }

    private void BumpSearchScope()
    {
        OnPropertyChanged(nameof(IsFullSearchActive));
        OnPropertyChanged(nameof(IsIndexReady));
        OnPropertyChanged(nameof(PagingVisible));
        OnPropertyChanged(nameof(IsIndexSearchActive));     // 与 PagingVisible 互斥，必须一起通知
        OnPropertyChanged(nameof(SearchScopeText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(CanNext));
    }

    /// <summary>把搜索相关的进度写进状态栏（只在搜索态接管，别抢正常加载的文案）。</summary>
    private void UpdateSearchStatus()
    {
        if (!IsFullSearchActive) return;
        if (IsBusy) return;         // 正在取数时别抢 LoadAsync 写的状态栏文案

        if (IsEsResultsActive) StatusText = SearchScopeText;
        else if (_esFailedKeyword.Length > 0) StatusText = SearchScopeText;
        else if (_indexError is not null) StatusText = SearchScopeText;
        else if (IsIndexing) StatusText = $"正在建立「{SelectedMenu?.Title}」整库索引，搜索将覆盖全库…";
        else if (IsIndexReady) StatusText = SearchScopeText;
        else StatusText = $"正在向平台检索「{(SearchText ?? "").Trim()}」…";
    }

    /// <summary>左侧菜单树（与平台菜单一致）。</summary>
    public ObservableCollection<CourseMenu> Menus { get; } = new()
    {
        new("网络自学", "公开课程", CourseMenuKind.PublicCourse),
        new("网络自学", "学习专区", CourseMenuKind.StudyZone),
        new("集中培训", "我的网络专题班", CourseMenuKind.MyOnlineTopic),
        new("集中培训", "我的培训班", CourseMenuKind.MyTrainClass),
    };

    /// <summary>当前菜单取到的全部行（不含展开出来的子行）。</summary>
    public ObservableCollection<MyCourseRow> AllRows { get; } = new();

    /// <summary>界面实际显示的行（树网格的**顶层**行，受「只看未完成」影响）。</summary>
    public ObservableCollection<MyCourseRow> VisibleRows { get; } = new();

    /// <summary>
    /// 合集展开的加载器。学习队列页用的是同一个类 —— 两页的子行口径必须一致，
    /// 否则同一门课在两边显示的门数/成绩会不一样。
    /// </summary>
    private readonly ClassChildrenLoader _children;

    public CoursesViewModel(
        CourseService courses,
        UserCenterService userCenter,
        LearnEngine engine,
        Action<string> log)
    {
        _courses = courses;
        _userCenter = userCenter;
        _engine = engine;
        _log = log;

        // 展开失败必须看得见：只在日志里留一行，用户看到的就是"点了箭头没反应"。
        // 加载器可能从后台线程回报，所以切回 UI 线程再改 StatusText。
        _children = new ClassChildrenLoader(courses,
            msg =>
            {
                _log(msg);
                Dispatcher.UIThread.Post(() => StatusText = msg);
            },
            IsInEngineQueue);

        // 搜索防抖到点 → 该问平台的问平台（RunSearchAsync），该建索引的建索引（EnsureFullIndexAsync）。
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            if (ShouldRequestEsSearch(SelectedMenu?.Kind, SearchText, _esAppliedKeyword, _esFailedKeyword))
                _ = RunSearchAsync();
            else if (ShouldBuildIndex(SearchText, SupportsPaging, IsIndexReady))
                _ = EnsureFullIndexAsync();
        };
    }

    /// <summary>登录成功后由主窗口调用，记录工号。</summary>
    public void SetUser(string? stuCode) => _stuCode = stuCode ?? "";

    /// <summary>登录后预热：拉学习中心下拉 + 选中首个菜单（公开课程）并拉第一页。</summary>
    public void WarmUp()
    {
        _ = LoadCentersAsync();     // 下拉框的中文名要尽早备齐，别等用户点开才发现是空的

        if (SelectedMenu is null)
        {
            // 赋值会触发 OnSelectedMenuChanged → LoadAsync
            SelectedMenu = Menus[0];
            return;
        }

        Page = 1;
        _ = LoadAsync();
    }

    // ── 取数 ──────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy || SelectedMenu is null) return;

        IsBusy = true;
        var kw = (SearchText ?? "").Trim();
        var useEs = UsesEsSearch(SelectedMenu.Kind, kw);
        StatusText = useEs
            ? $"正在向平台检索「{kw}」（{SelectedMenu.Title}）…"
            : $"正在查询「{SelectedMenu.Title}」…";
        try
        {
            var rows = useEs
                ? await LoadEsSearchAsync(SelectedMenu.Kind, kw)
                : SelectedMenu.Kind switch
                {
                    CourseMenuKind.PublicCourse => await LoadBrowseCatalog(
                        ct => _courses.GetPublicCoursePageAsync(CenterCode, Page, PageSize, ct: ct)),
                    CourseMenuKind.StudyZone => await LoadBrowseCatalog(
                        ct => _courses.GetZonePageAsync(CenterCode, Page, PageSize, ct: ct)),
                    CourseMenuKind.MyOnlineTopic => await LoadMyClasses("ZE1"),
                    CourseMenuKind.MyTrainClass => await LoadMyClasses("TCE"),
                    _ => new List<MyCourseRow>(),
                };

            _children.Reset();
            AllRows.Clear();
            foreach (var r in rows) AllRows.Add(r);

            RebuildVisible();

            StatusText = rows.Count == 0
                ? useEs
                    ? $"平台没有检索到与「{kw}」相关的{SelectedMenu.Title}"
                    : $"「{SelectedMenu.Title}」没有查询到内容"
                : useEs
                    ? $"平台检索「{kw}」{TotalText}，本页 {rows.Count} 条"
                    : $"「{SelectedMenu.Title}」{TotalText}，本页 {rows.Count} 条";
            if (!useEs) _log($"✓ 「{SelectedMenu.Title}」加载完成：{rows.Count} 条");

            // 后台补齐「视频占分」权重（挂课要挂多久才到分数线就由它决定）。
            // 有会话缓存的课程不重复发请求；权重到货一行升级一行。
            _ = CourseListOps.PrefetchWeightsAsync(Walk(AllRows).ToList(), _courses);
        }
        catch (Exception ex)
        {
            StatusText = "查询失败：" + ex.Message;
            _log("✖ 课程查询失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 防抖到点后发起一次平台检索（回到第 1 页 —— 换了关键字，还停在第 7 页没有意义）。
    /// 与「上一批结果」的唯一差别就是 Page 归 1，其余走 <see cref="LoadAsync"/> 同一条路，
    /// 免得两套取数逻辑各写一遍、迟早出现"直接搜索能出、翻页后再搜就出不来"。
    /// </summary>
    private async Task RunSearchAsync()
    {
        if (IsBusy)
        {
            // 正忙着（多半是上一次翻页或加载还没落地）→ 稍后再试一次，别把这次检索吞掉
            _searchDebounce.Start();
            return;
        }

        Page = 1;
        await LoadAsync();
        ArmSearch();        // 平台检索失败时，这里把"退回整库索引"排上队
    }

    /// <summary>
    /// 向平台 ES 检索一页。<paramref name="kw"/> 必须是去空格后的非空关键字。
    ///
    /// ★ 失败**不能**只弹个错误就完事：用户敲了关键字就是要找课。
    ///   这里失败后先把手头这一页交回去（界面至少还有内容），同时记下失败的关键字，
    ///   由 <see cref="ArmSearch"/> 把 v1.0.38 那套整库索引排上队当备胎 ——
    ///   索引就绪后搜索自动升级回"搜整库"，但界面上会明说是退回了备胎方案。
    /// </summary>
    private async Task<List<MyCourseRow>> LoadEsSearchAsync(CourseMenuKind kind, string kw)
    {
        var lib = kind == CourseMenuKind.StudyZone
            ? ApiEndpoints.EsLibZone
            : ApiEndpoints.EsLibCourse;

        try
        {
            var res = await _courses.SearchCatalogAsync(lib, kw, Page, PageSize);

            Total = res.Total;
            _esAppliedKeyword = kw;
            _esFailedKeyword = "";

            _log(res.Total == 0
                ? $"平台检索「{kw}」没有命中（{SelectedMenu?.Title}）"
                : $"✓ 平台检索「{kw}」命中 {res.Total} 门（{SelectedMenu?.Title}），本页 {res.Items.Count} 条");

            return MapCatalogRows(res.Items, kind, SelectedMenu?.Title ?? "");
        }
        catch (Exception ex)
        {
            _esFailedKeyword = kw;
            _log($"⚠ 平台检索「{kw}」失败：{ex.Message} → 改用整库索引（较慢，但同样覆盖全库）");

            // 先把当前这一页拿回来，别让列表空着；整库索引由防抖那条路补上
            var fallback = await LoadBrowseCatalogByKind(kind);
            ArmSearch();
            return fallback;
        }
    }

    /// <summary>浏览型菜单按类型取当前页（平台检索失败时的备胎取数）。</summary>
    private Task<List<MyCourseRow>> LoadBrowseCatalogByKind(CourseMenuKind kind)
        => kind == CourseMenuKind.PublicCourse
            ? LoadBrowseCatalog(ct => _courses.GetPublicCoursePageAsync(CenterCode, Page, PageSize, ct: ct))
            : LoadBrowseCatalog(ct => _courses.GetZonePageAsync(CenterCode, Page, PageSize, ct: ct));

    /// <summary>浏览型菜单（分页目录）。</summary>
    private async Task<List<MyCourseRow>> LoadBrowseCatalog(Func<CancellationToken, Task<PagedResult<CourseItem>>> fetch)
    {
        var result = await fetch(default);

        // 平台有时不回 total，用当前页条数兜底，避免分页控件显示成 0
        Total = result.Total > 0 ? result.Total : (Page == 1 ? result.Items.Count : Total);

        var label = SelectedMenu?.Title ?? "";
        return MapCatalogRows(result.Items, SelectedMenu?.Kind, label);
    }

    /// <summary>
    /// 浏览型目录记录 → 展示行。
    ///
    /// 目录接口不回 olClassType（实测为 null），按菜单补上 —— 展开班内课程
    /// （getOnlineClassCourseSortPage）与挂课链路都要用这个字段。
    ///
    /// ★ 当前页与全库索引**共用**这一份映射：两处各写一套，迟早出现
    ///   「同一门课在第 1 页能挂、搜索出来后挂不了」这种只在某一页复现的怪事。
    /// </summary>
    private static List<MyCourseRow> MapCatalogRows(
        IEnumerable<CourseItem> items, CourseMenuKind? kind, string label)
    {
        var rows = new List<MyCourseRow>();
        foreach (var c in items)
        {
            if (kind == CourseMenuKind.StudyZone && string.IsNullOrEmpty(c.OlClassType))
                c.OlClassType = "ZE0";
            else if (kind == CourseMenuKind.PublicCourse && string.IsNullOrEmpty(c.OlClassType))
                c.OlClassType = "OCE";

            rows.Add(MyCourseRow.FromCourse(c, label, nested: false));
        }
        return rows;
    }

    /// <summary>"我的…"菜单（归档接口，一次取全）。</summary>
    private async Task<List<MyCourseRow>> LoadMyClasses(string olClassType)
    {
        if (string.IsNullOrWhiteSpace(_stuCode))
            throw new InvalidOperationException("尚未登录，无法获取本人的班级列表");

        var load = await _userCenter.GetMyClassesAsync(_stuCode, olClassType);
        if (load.Errors.Count > 0)
            _log($"⚠ 「{SelectedMenu?.Title}」查询失败：{load.Errors[0]}");
        Total = load.Items.Count;

        return load.Items.Select(i => new MyCourseRow(i)).ToList();
    }

    // ── 全库索引 ──────────────────────────────────────────

    /// <summary>
    /// 把当前菜单的整个目录拉齐，建成"全库索引"。
    ///
    /// 只在**有关键字**时触发（见 <see cref="OnSearchTextChanged"/> 的防抖），同一菜单只建一次；
    /// 换菜单 / 换学习中心 / 点「查询」都会把它作废重建。
    /// 平台把 size 截断、或压根不回 total 都不怕 —— 翻页策略在 CourseService.FetchAllPagesAsync 里，
    /// 那边按实际返回条数自适应。
    /// </summary>
    private async Task EnsureFullIndexAsync()
    {
        if (SelectedMenu is null || !SupportsPaging) return;
        if (_fullIndex is not null && _fullIndexKey == IndexKey) return;

        var menu = SelectedMenu;
        var key = IndexKey;

        // ★ 上一轮哪怕还在跑也直接作废（不 return 干等）：
        //   "正在收尾的旧任务"会把新一次重建挡在门外，用户看到的就一直是"全库检索中…"。
        //   作废后旧任务的结果会被丢弃（见下面对 cts 的归属判断），不会写进索引。
        _indexCts?.Cancel();
        var cts = new CancellationTokenSource();
        _indexCts = cts;

        IsIndexing = true;
        _indexError = null;
        IndexedCount = 0;
        IndexTotal = Total;
        BumpSearchScope();
        UpdateSearchStatus();

        _log($"↻ 建立「{menu.Title}」全库索引：{TotalText}，按页取回全部课程（搜索将覆盖整库）");

        try
        {
            Task<List<CourseItem>> FetchAll(int size, CancellationToken token)
                => CourseService.FetchAllPagesAsync(
                    FetchCatalogPage(menu, CenterCode),
                    preferredSize: size,
                    onProgress: (got, total) => Dispatcher.UIThread.Post(() =>
                    {
                        IndexedCount = got;
                        if (total > 0) IndexTotal = total;
                        BumpSearchScope();
                        UpdateSearchStatus();
                    }),
                    ct: token);

            List<CourseItem> courses;
            try
            {
                courses = await FetchAll(CourseService.IndexPageSize, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 一次要 500 条被网关顶回来（不该发生，但真发生也别让"搜全库"静默降级成"搜本页"）。
                // 退回每页 20 —— 那是这个网关各处都在用的常规页大小，稳。
                _log($"⚠ 全库索引：一次取 {CourseService.IndexPageSize} 条被拒（{ex.Message}），改用每页 20 条重试");
                IndexedCount = 0;
                courses = await FetchAll(20, cts.Token);
            }

            if (cts.IsCancellationRequested) return;   // 换菜单 / 清空了关键字：这次结果作废

            _fullIndex = BuildIndexRows(courses, menu);
            _fullIndexKey = key;
            _log($"✓ 全库索引就绪：「{menu.Title}」{_fullIndex.Count} 门（搜索已覆盖整库）");

            RebuildVisible();
            UpdateSearchStatus();

            // 命中行里靠前的那几门顺手补「视频占分」。限量 30：一次搜索动辄几百个命中，
            // 全查等于为敲两个字打出几百个 finishInfo 请求。
            _ = CourseListOps.PrefetchWeightsAsync(VisibleRows.Take(30).ToList(), _courses);
        }
        catch (OperationCanceledException)
        {
            // 用户换了菜单或清空关键字 —— 正常，不必打扰
        }
        catch (Exception ex)
        {
            _indexError = ex.Message;
            _log($"✖ 全库索引建立失败：{ex.Message}（搜索退回当前页）");
            RebuildVisible();
            UpdateSearchStatus();
        }
        finally
        {
            // ★ 只有还是"我这次"在跑才复位：否则会把后一次（换菜单后立刻重搜）的进行态掐掉。
            if (ReferenceEquals(_indexCts, cts))
            {
                IsIndexing = false;
                _indexCts = null;
                BumpSearchScope();
                UpdateSearchStatus();
            }
            cts.Dispose();
        }
    }

    /// <summary>当前菜单对应的"取第 N 页"委托。</summary>
    private Func<int, int, CancellationToken, Task<PagedResult<CourseItem>>> FetchCatalogPage(
        CourseMenu menu, string centerCode)
        => menu.Kind switch
        {
            CourseMenuKind.PublicCourse => (page, size, ct) =>
                _courses.GetPublicCoursePageAsync(centerCode, page, size, ct: ct),
            _ => (page, size, ct) => _courses.GetZonePageAsync(centerCode, page, size, ct: ct),
        };

    /// <summary>
    /// 整库课程对象 → 行。**尽量复用已有行实例**：
    /// 当前页那批行可能已经被勾选、或已展开出子行，重建一份新的会让用户眼睁睁看着勾选消失。
    /// </summary>
    private List<MyCourseRow> BuildIndexRows(IReadOnlyList<CourseItem> courses, CourseMenu menu)
    {
        var mapped = MapCatalogRows(courses, menu.Kind, menu.Title);

        var byKey = new Dictionary<string, MyCourseRow>(StringComparer.Ordinal);
        foreach (var r in AllRows) byKey[r.Key] = r;

        var rows = new List<MyCourseRow>(mapped.Count);
        foreach (var fresh in mapped)
        {
            var row = byKey.TryGetValue(fresh.Key, out var existed) ? existed : fresh;
            byKey[fresh.Key] = row;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// 按「只看未完成」重建**顶层**行（TreeDataGrid 的数据源）。
    ///
    /// 班内课程不再插进这个平铺列表里 —— 它们挂在各行自己的 Children 上，
    /// 由树网格展开时按需拉取。旧版是"平铺列表 + 手写展开按钮 + └ 制表符假装层级"，
    /// 展开状态一刷新就丢，表头列宽也和真实布局对不上。
    /// </summary>
    private void RebuildVisible()
    {
        VisibleRows.Clear();

        // ★ 平台检索的结果**不再本地二次过滤**：ES 已经按关键字筛过（还带自己的分页 total），
        //   这里再按"标题含关键字"收一道，会出现"共 407 条、这一页却是空的"这种看着像 bug 的页面
        //   —— ES 是全文检索，命中可能在标签/简介里。要收成"只看标题"是另一个需求，别偷偷做。
        var localKeyword = IsEsResultsActive ? "" : SearchText;

        // 有关键字且全库索引就绪 → 在整库上匹配；否则就是当前这一页（见 PickSearchSource）。
        foreach (var row in PickSearchSource(localKeyword, AllRows, _fullIndex))
        {
            if (HideFinished && IsFinished(row)) continue;
            if (!MatchesFilter(row, localKeyword)) continue;

            VisibleRows.Add(row);
            row.InQueue = IsInEngineQueue(row.Item.CourseNo, row.Item.OlClassNo);
        }

        BumpSearchScope();   // 命中数变了，提示那一格跟着变
    }

    /// <summary>深度遍历行（含各合集已拉取到的子课程）。</summary>
    private static IEnumerable<MyCourseRow> Walk(IEnumerable<MyCourseRow> rows)
    {
        foreach (var r in rows)
        {
            yield return r;
            foreach (var c in Walk(r.Children)) yield return c;
        }
    }

    /// <summary>界面当前看得见的行（含已展开合集的子行）—— 勾选、入队、查学时都按它算。</summary>
    private List<MyCourseRow> WalkVisible() => Walk(VisibleRows).ToList();

    private bool IsFinished(MyCourseRow row)
    {
        if (row.SourceCourse is { } c) return c.IsFinished;

        // 班级级：平台用 finishStatus == "2" 表示已完成
        return row.Item.IsClassLevel && row.Item.FinishStatus == "2";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Page = 1;
        InvalidateIndex();          // 「查询」= 向平台重新取数，索引与上一次的平台结果都重来
        await LoadAsync();          // 有关键字时 LoadAsync 自己会走平台检索，不必再补一次防抖
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (!CanPrev) return;
        Page--;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanNext) return;
        Page++;
        await LoadAsync();
    }

    // ── 展开班级（树网格的子行数据源）──────────────────────

    /// <summary>
    /// 树网格展开某合集时调用（视图里挂的是 RowExpanding）。
    ///
    /// 旧版这里是一个手写的「展开 / 收起」按钮，把子行插进平铺列表 ——
    /// 现在展开箭头由 TreeDataGrid 提供，这里只负责把子行数据备齐。
    /// </summary>
    public async Task EnsureChildrenAsync(MyCourseRow row)
    {
        await _children.EnsureAsync(row);

        // 明细到货后把「共 N 门 · 已完成 x」这类汇总同步到状态栏，
        // 免得用户点开箭头看不到任何反馈。
        if (row.ChildStatsReady)
        {
            StatusText = $"「{row.Title}」{row.DetailText}";
            _log($"✓ 展开「{row.Title}」：{row.DetailText}");
        }
    }

    // ── 行内加入挂机队列 ──────────────────────────────────

    /// <summary>
    /// 行操作列的「加入队列 / 移出队列」开关。
    ///
    /// 平台本身没有"加入已选"这种显式操作 —— 点进课程开始学习就自动进已选列表，
    /// 所以原来的「加入已选」按钮（initLearnRecord）没有存在意义，已移除。
    /// 现在行按钮直接把这门课加进挂机队列；挂课一开始，平台那边自然把它记进已选。
    /// </summary>
    [RelayCommand]
    private async Task ToggleQueueAsync(MyCourseRow? row)
    {
        if (row is null || !row.CanToggleQueue || IsBusy) return;

        // 已在队列 → 移出
        if (IsInEngineQueue(row.Item.CourseNo, row.Item.OlClassNo))
        {
            var target = _engine.Queue.FirstOrDefault(c =>
                c.CourseNo == row.Item.CourseNo && c.OlClassNo == row.Item.OlClassNo);
            if (target is not null)
            {
                _engine.Remove(target);
                _log($"已移出挂机队列：{row.Title}");
            }
            SyncQueueFlags();
            return;
        }

        // 不在队列 → 加入
        IsBusy = true;
        row.ActionText = "加入中…";
        try
        {
            var items = await CourseListOps.ResolveQueueItemsAsync(
                new[] { row }, _courses, msg => StatusText = msg);

            if (items.Count == 0)
            {
                row.ActionText = "加入队列";
                StatusText = "这门课暂时加不进队列（可能取不到课件列表）";
                _log($"⚠ 「{row.Title}」解析后无可挂内容");
                return;
            }

            _engine.Enqueue(items);
            _log($"✓ 已加入挂机队列：{row.Title}（去「学习队列」页开始挂课）");
            StatusText = $"已把「{row.Title}」加入挂机队列";

            await _courses.EnrichWithScoreAsync(_engine.Queue).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "加入挂机队列失败：" + ex.Message;
            _log("✖ 加入挂机队列失败：" + ex.Message);
        }
        finally
        {
            row.ActionText = "加入队列";
            IsBusy = false;
            SyncQueueFlags();
        }
    }

    /// <summary>
    /// 只把「已在队列」标记刷到每一行（含已展开的子行），**不动列表本身**。
    ///
    /// 这里不能图省事调 <see cref="RebuildVisible"/>：那会把 VisibleRows 清空重建，
    /// 树网格的展开状态跟着一起丢 —— 用户点个「加入队列」，展开的合集就自己收起来了。
    /// </summary>
    private void SyncQueueFlags()
    {
        foreach (var row in AllKnownRows())
            row.InQueue = IsInEngineQueue(row.Item.CourseNo, row.Item.OlClassNo);
    }

    /// <summary>
    /// 界面上可能出现的所有行实例（当前页 + 全库索引），按引用去重 ——
    /// 索引是复用了当前页那些行对象的，不去重会重复写一遍。
    ///
    /// ★ 全库搜索时显示的行来自索引，只刷 AllRows 会漏掉它们：
    ///   用户搜索后点「加入队列」，那一行的按钮文案不会变回「移出队列」。
    /// </summary>
    private IEnumerable<MyCourseRow> AllKnownRows()
    {
        var seen = new HashSet<MyCourseRow>();
        foreach (var r in Walk(AllRows)) if (seen.Add(r)) yield return r;

        if (_fullIndex is null) yield break;
        foreach (var r in Walk(_fullIndex)) if (seen.Add(r)) yield return r;
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var all = WalkVisible();
        var target = all.Any(r => !r.IsSelected);
        foreach (var r in all.Where(r => r.CanCheck)) r.IsSelected = target;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in WalkVisible()) r.IsSelected = false;
    }

    /// <summary>
    /// 把勾选的内容加入挂机队列（之后去「学习队列」页开始挂课）。
    /// 勾班级（合集）时会自动展开，把班内课程一并加入 —— 合集的复选框就是干这个用的。
    /// </summary>
    [RelayCommand]
    private async Task AddSelectedToQueueAsync()
    {
        var picked = WalkVisible().Where(r => r.IsSelected && r.CanCheck).ToList();
        if (picked.Count == 0)
        {
            StatusText = "请先勾选要挂机的课程或班级（勾班级会把班内课程一起加入）";
            return;
        }

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

            var classCount = picked.Count(r => r.IsClassLevel);
            StatusText = classCount > 0
                ? $"已把 {items.Count} 门课程加入挂机队列（含 {classCount} 个合集展开出来的课程）"
                : $"已把 {items.Count} 门课程加入挂机队列（去「学习队列」页开始挂课）";
            _log($"✓ 已加入挂机队列：{items.Count} 门");
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

    /// <summary>查询勾选课程的学时完成情况。</summary>
    [RelayCommand]
    private async Task QuerySelectedScoresAsync()
    {
        if (IsBusy) return;

        var rows = WalkVisible().Where(r => r.IsSelected && r.CanCheck).ToList();
        if (rows.Count == 0)
        {
            StatusText = "请先勾选要查询学时的课程";
            return;
        }

        IsBusy = true;
        StatusText = $"正在查询 {rows.Count} 门课程的学时…";
        try
        {
            await _courses.EnrichWithScoreAsync(rows.Select(r => r.ToCourseItem()));
            foreach (var r in rows) r.Bump();
            StatusText = $"学时查询完成（{rows.Count} 门）";
            _log($"✓ 学时查询完成：{rows.Count} 门");
        }
        catch (Exception ex)
        {
            StatusText = "学时查询失败：" + ex.Message;
            _log("✖ 学时查询失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsInEngineQueue(string? courseNo, string? olClassNo)
        => _engine.Queue.Any(q => q.CourseNo == (courseNo ?? "") && q.OlClassNo == (olClassNo ?? ""));
}

/// <summary>课程页左侧菜单项的取数口径。</summary>
public enum CourseMenuKind
{
    PublicCourse,
    StudyZone,
    MyOnlineTopic,
    MyTrainClass,
}

/// <summary>左侧菜单项（分组 + 标题 + 取数口径）。</summary>
public sealed class CourseMenu
{
    public CourseMenu(string group, string title, CourseMenuKind kind)
    {
        Group = group;
        Title = title;
        Kind = kind;
    }

    public string Group { get; }
    public string Title { get; }
    public CourseMenuKind Kind { get; }

    public string Display => $"{Group} · {Title}";
}
