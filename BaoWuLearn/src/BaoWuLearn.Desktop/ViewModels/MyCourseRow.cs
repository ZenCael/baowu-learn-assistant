using System.Collections.ObjectModel;
using BaoWuLearn.Core.Behavior;
using BaoWuLearn.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// "我的已选课程"行。
///
/// 平台把已选课程分成两种粒度：
///  - 课程级（公开课）：直接带 courseNo，可以立刻挂课
///  - 班级级（学习专区 / 网络专题班 / 培训班）：只有 olClassNo，
///    需要先展开成班内的课程列表，再挑要挂的课
/// 界面据此把两种粒度区分显示，避免"勾了却挂不了"。
/// </summary>
public partial class MyCourseRow : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    /// <summary>是否已经进了挂机队列。</summary>
    [ObservableProperty] private bool _inQueue;

    /// <summary>展开出来的课程数量。</summary>
    [ObservableProperty] private int _expandedCount;

    // 展开后按班内课程 learnStatus 汇总出来的门数（详见 DetailText 上方注释）。
    [ObservableProperty] private int _expandedDone;
    [ObservableProperty] private int _expandedDoing;
    [ObservableProperty] private int _expandedNotStarted;

    /// <summary>是否已经加入"我的已选课程"。</summary>
    [ObservableProperty] private bool _joined;

    /// <summary>「加入已选」按钮的实时状态文案。</summary>
    [ObservableProperty] private string _actionText = "加入已选";

    /// <summary>按钮是否可点（正在提交时置灰，避免重复提交）。</summary>
    [ObservableProperty] private bool _canAction = true;

    /// <summary>该行是合集的子行（树网格里挂在父行下面那一层）。</summary>
    [ObservableProperty] private bool _isNested;

    /// <summary>
    /// 班级行的子课程（树网格的 Children）。集合实例始终同一个 ——
    /// 预取到货时往里 Add，TreeDataGrid 的层级视图会自动跟着刷新，
    /// 不需要重建整棵树。
    /// </summary>
    public ObservableCollection<MyCourseRow> Children { get; } = new();

    /// <summary>
    /// 子课程是否已从班内课程接口拉齐（拉齐后收起态也能显示真实汇总）。
    /// </summary>
    [ObservableProperty] private bool _childStatsReady;

    partial void OnChildStatsReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(StatusText));   // 明细到货后班级行状态改由子行汇总
        OnPropertyChanged(nameof(ChildrenReady));
    }

    /// <summary>子课程已拉齐（ResolveQueueItems 走缓存子行走这条路）。</summary>
    public bool ChildrenReady => ChildStatsReady && Children.Count > 0;

    /// <summary>这一行（或其所属班级）当前正在挂课 —— 名称列右侧显示「正在挂课」角标。</summary>
    [ObservableProperty] private bool _isCurrent;

    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(IsCurrentOrQueue));
    public bool IsCurrentOrQueue => IsCurrent || InQueue;

    /// <summary>父班级的定位键，用于把子行与它的父行对上号。</summary>
    [ObservableProperty] private string _parentKey = "";

    /// <summary>班内课程行携带的完整课程对象（挂课要用它，不能只有展示字段）。</summary>
    public CourseItem? SourceCourse { get; private set; }

    public MyCourseItem Item { get; }

    /// <summary>从平台记录构造（可能是班级级，也可能是课程级的公开课）。</summary>
    public MyCourseRow(MyCourseItem item) => Item = item;

    /// <summary>
    /// 从课程对象构造一行（用于"展开班级"得到的班内课程，
    /// 以及课程页里直接列出的公开课 / 学习专区课程）。
    /// </summary>
    public static MyCourseRow FromCourse(
        CourseItem course, string category, bool nested, string parentKey = "")
    {
        // 课程页目录行（newestOnlineClassPage 等）对专区/专题班/培训班这类"班级"记录
        // 不回 courseName（为 null），只回 olClassName；ToCourse 把 olClassName 塞进了 CourseName。
        // 而 MyCourseItem.IsClassLevel 只认 OlClassName 非空 —— 不补这一下，
        // 专区行会被当成"课程级行"：没有复选框、没有展开按钮，整行死掉（用户实测踩过）。
        var isClassRow = string.IsNullOrEmpty(course.CourseNo) && !string.IsNullOrEmpty(course.OlClassNo);
        var row = new MyCourseRow(new MyCourseItem
        {
            CenterCode = course.CenterCode,
            CenterName = course.CenterName,
            OlClassNo = course.OlClassNo,
            OlClassType = course.OlClassType,
            CourseNo = course.CourseNo,
            CourseName = isClassRow ? "" : course.CourseName,
            OlClassName = isClassRow ? course.CourseName : "",
            Guid = course.Guid,
            Category = category,
            CourseHours = course.CourseHours,
            LearnStatus = course.LearnStatus,
        })
        {
            SourceCourse = course,
            IsNested = nested,
            ParentKey = parentKey,
        };

        return row;
    }

    public string Title => Item.Title;
    public string Category => Item.Category;

    /// <summary>
    /// 状态文案。
    ///
    /// ★ 班级行（myClassPage）**不返回任何状态字段**，直接透传会是一列空白。
    ///   这里按已知信息推导：
    ///     - 班内明细已拉齐 → 用子行汇总（全部完成=已完成，有进度=进行中）
    ///     - 否则用平台给的「已学门数」learnNum（>0 即已开始）
    /// </summary>
    public string StatusText
    {
        get
        {
            if (IsClassLevel)
            {
                if (ChildStatsReady && ExpandedCount > 0)
                {
                    if (ExpandedDone == ExpandedCount) return "已完成";
                    return ExpandedDone + ExpandedDoing > 0 ? "进行中" : "未开始";
                }

                if (Item.CourseNum is not null)
                    return (Item.LearnedCourseNum ?? 0) > 0 ? "进行中" : "未开始";
            }

            return Item.StatusText;
        }
    }

    /// <summary>
    /// 得分。班内课程列表不返回成绩，展开后会为「已加入学习」的那几门单独补查
    /// （见 CourseListOps.FinishExpandAsync），查到就用查到的值。
    ///
    /// ⚠ 平台对"还没产生成绩"的课程返回的是 0（实测：进行中的课 learnScore=0），
    ///   直接显示成「0 分」会让人以为考砸了，这里按「—」处理。
    /// </summary>
    public string ScoreText
    {
        get
        {
            if (IsNested && SourceCourse?.LearnScore is { } nestedScore)
                return nestedScore <= 0 && Item.LearnStatus != "2"
                    ? "—"
                    : nestedScore.ToString("0.##");
            return Item.ScoreText;
        }
    }

    public string HoursText => Item.HoursText;
    public string LearnSourceText => Item.LearnSourceText;
    public string MustTeachText => Item.MustTeachText;

    /// <summary>
    /// 视频时长占总得分的百分比（班内课程对象优先，裸已选记录兜底）。
    /// null = 还没查到（<see cref="CourseListOps.PrefetchWeightsAsync"/> 会后台补齐），界面不显示。
    /// </summary>
    public double? DurationScoreWeight
        => SourceCourse?.DurationScoreWeight ?? Item.DurationScoreWeight;

    /// <summary>「视频占分 80%」文案；权重未知时为 null（界面整段藏起）。</summary>
    public string? WeightText
        => DurationScoreWeight is { } w and > 0 ? $"视频占分 {w:0.##}%" : null;

    /// <summary>班级级 = 需要展开；课程级 = 可直接挂。</summary>
    public bool IsClassLevel => Item.IsClassLevel && !IsNested;

    /// <summary>
    /// 界面是否显示复选框。
    /// 课程级勾选后直接入队；班级级（合集）勾选后会自动展开，把班内课程一起入队 ——
    /// 一个班级下面动辄几十门课，逐门展开再勾太费事。
    ///
    /// ⚠ 这里不能直接用 Item.CanLearn 判断：CanLearn 要求「同时有 courseNo 与 olClassNo」，
    ///   而班级行天生只有 olClassNo、没有 courseNo，用它判断会让班级行的复选框永远不显示
    ///   （这正是「课程合集前面没有复选框」的根因）。
    /// </summary>
    public bool CanCheck => Item.CanLearn
        || (Item.IsClassLevel && !string.IsNullOrEmpty(Item.OlClassNo));

    /// <summary>课程级行：勾选后可直接入队，也才能"加入已选"。</summary>
    public bool IsDirectCourse => !IsClassLevel && Item.CanLearn;

    /// <summary>"加入已选"只对课程级行有意义（班级本身就是一条已选记录）。</summary>
    public bool CanJoin => IsDirectCourse;

    /// <summary>
    /// 只有班级级行显示"展开"。
    /// 同样不能用 Item.CanLearn 判断 —— 班级行天生没有 courseNo，否则展开按钮永远出不来。
    /// </summary>
    public bool CanExpand => IsClassLevel && !string.IsNullOrEmpty(Item.OlClassNo);

    /// <summary>
    /// 操作列是否显示「加入队列 / 移出队列」按钮。
    /// 班级行走"展开"那条路（展开后单独勾班内课程），其余能挂的行直接给一个入队开关，
    /// 免得操作列只剩一个没有任何含义的「—」。
    /// </summary>
    public bool CanToggleQueue => !IsClassLevel && Item.CanLearn;

    /// <summary>操作列那个队列开关的文案。</summary>
    public string QueueActionText => InQueue ? "移出队列" : "加入队列";

    partial void OnInQueueChanged(bool value)
    {
        OnPropertyChanged(nameof(QueueActionText));
        OnPropertyChanged(nameof(IsCurrentOrQueue));
    }

    /// <summary>
    /// 标题下方的浅色说明行。
    ///
    /// ★ 班级行（合集）这里以前写「已完成 X 门 · 未完成 Y 门」，用户实测反馈
    ///   「已完成 1 门 · 未完成 1 门」却展开出 21 门课程，数字自相矛盾。
    ///   用真实账号逐班核对原始报文（见 缺陷修复说明-v1.0.8.md）后确认：
    ///     平台这两个字段**不是合集课程总数**，而是「你已开始学习的课程里，已完成 / 进行中的门数」。
    ///     实测 5 个专区，两数恰好等于班内 learnStatus≠0 的课程数
    ///     （learnStatus 0↔未开始 / 1↔进行中 / 2↔已完成），逐一吻合。
    ///     例：宝武人讲AI学习专区 = 已完成 1 门 + 进行中 1 门，而该专区实际有 21 门课程。
    ///   所以改为：班内明细还没拉到时只报「已完成 / 进行中」（不再谎称总数）；
    ///             班内明细一到位（预取或展开），收起状态下也直接显示真实汇总 ——
    ///             数字与展开后的子行同源，任何时候都不会互相矛盾。
    /// </summary>
    public string DetailText
    {
        get
        {
            var baseText = BuildDetailText();
            // 视频占分权重到了就追加一段（合集行没有统一权重，保持原样不追加）
            return WeightText is { } weight ? $"{baseText} · {weight}" : baseText;
        }
    }

    private string BuildDetailText()
    {
        {
            if (IsClassLevel)
            {
                if (ChildStatsReady && ExpandedCount > 0)
                {
                    var parts = new List<string> { $"{ExpandedCount} 门" };
                    if (ExpandedDone > 0) parts.Add($"已完成 {ExpandedDone}");
                    if (ExpandedDoing > 0) parts.Add($"进行中 {ExpandedDoing}");
                    if (ExpandedNotStarted > 0) parts.Add($"未开始 {ExpandedNotStarted}");
                    return string.Join(" · ", parts);
                }

                // 未展开：班内明细还没到，说平台自己给的口径。
                // ★ v1.0.16：合集行改由 student/myClassPage 提供，平台直接给
                //   courseNum（合集课程总数）与 learnNum（本人已学门数）—— 权威且与
                //   展开后的真实门数一致，优先用它（旧接口的"已完成/进行中门数"
                //   新接口不返回，留作兜底）。
                if (Item.CourseNum is { } total)
                {
                    var learned = Item.LearnedCourseNum ?? 0;
                    return learned > 0
                        ? $"共 {total} 门 · 已学 {learned} 门"
                        : $"共 {total} 门 · 尚未开始学习";
                }

                var done = Item.FinishCourseNumber ?? 0;
                var doing = Item.NotFinishCourseNumber ?? 0;
                var head = new List<string>();
                if (done > 0) head.Add($"已完成 {done} 门");
                if (doing > 0) head.Add($"进行中 {doing} 门");
                return head.Count > 0 ? string.Join(" · ", head) : "尚未开始学习";
            }

            // 班内课程：班内课程列表只给 learnStatus，用它显示真实状态。
            if (IsNested)
            {
                var st = Item.StatusText;
                var learned = SourceCourse?.CompletedText;
                if (!string.IsNullOrWhiteSpace(st) && !string.IsNullOrWhiteSpace(learned))
                    return $"{st} · 已学 {learned}";
                if (!string.IsNullOrWhiteSpace(st)) return st;
                return "尚未开始学习";
            }

            return Item.TotalLearnTimeSeconds > 0 ? "累计已学 " + Item.LearnTimeText : "尚未开始学习";
        }
    }

    /// <summary>
    /// 展开完成后，用班内课程的真实状态汇总父行。
    /// 这样父行副标题与子行「状态」列出自同一份数据，不会再出现互相矛盾的数字。
    /// </summary>
    public void ApplyChildStats(IReadOnlyList<MyCourseRow> children)
    {
        ExpandedCount = children.Count;
        ExpandedDone = children.Count(c => c.SourceCourse?.LearnStatus == "2");
        ExpandedDoing = children.Count(c => c.SourceCourse?.LearnStatus == "1");
        ExpandedNotStarted = children.Count - ExpandedDone - ExpandedDoing;
        ChildStatsReady = true;   // 收起状态起显示真实汇总（不再退回"只报已开始"的保守口径）
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>标识一行（加入队列去重用）。</summary>
    public string Key => !string.IsNullOrEmpty(Item.CourseNo)
        ? Item.CourseNo + "@" + Item.OlClassNo
        : Item.OlClassNo;

    /// <summary>取用于挂课的课程对象。</summary>
    public CourseItem ToCourseItem() => SourceCourse ?? new CourseItem
    {
        CourseName = Item.CourseName,
        Guid = Item.Guid,
        CenterCode = Item.CenterCode,
        CenterName = Item.CenterName,
        CourseNo = Item.CourseNo,
        OlClassNo = Item.OlClassNo,
        OlClassType = Item.OlClassType,
    };

    /// <summary>让界面重新读取全部计算属性。</summary>
    public void Bump()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(HoursText));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(IsDirectCourse));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(CanToggleQueue));
        OnPropertyChanged(nameof(QueueActionText));
    }
}

/// <summary>
/// 挂机列表行：引擎队列里的一门课，附带"当前在挂 / 已完成"的实时状态、
/// 视频时长、得分、分数线。
/// </summary>
public partial class QueueRow : ObservableObject
{
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isDone;

    /// <summary>
    /// 当前在挂行的**实时时长**文本（引擎课程账本，界面每秒推进）。
    ///
    /// ★ 为什么要单独有这一位：Course.DurationText 吃的是平台回读值（CE002），
    ///   只在结算落库后才动 —— 挂课途中纹丝不动，用户看到的就是
    ///   「面板上的时长在按秒涨，列表里的时长死着」，同一门课两个数还对不上
    ///   （面板是引擎实时账本，列表是上一次结算的旧值）。
    ///   现在当前在挂的行由界面秒表把这一位置成与面板**同一个字符串**，
    ///   同源同钟；非当前行保持 null，退回平台回读值。
    /// </summary>
    [ObservableProperty] private string? _liveDurationText;

    partial void OnLiveDurationTextChanged(string? value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(SubtitleText));
    }

    /// <summary>队列序号，从 1 开始。</summary>
    public int Index { get; }

    public CourseItem Course { get; }

    public QueueRow(int index, CourseItem course)
    {
        Index = index;
        Course = course;
    }

    public string IndexText => Index.ToString("00");
    public string Title => Course.CourseName;

    /// <summary>
    /// 视频时长（已学 / 要求）。
    /// 当前在挂的行返回实时账本（见 <see cref="LiveDurationText"/>），其余行走平台回读值。
    /// </summary>
    public string DurationText => LiveDurationText ?? Course.DurationText;

    /// <summary>
    /// 「视频占分 80%」文案 —— 视频时长占总得分的百分比，各课不同（实测 70 / 80 都有），
    /// 直接决定"挂多久才到分数线"。权重未知（还没查过 finishInfo）时为 null，界面不显示。
    /// </summary>
    public string? WeightText
        => Course.DurationScoreWeight is { } w and > 0 ? $"视频占分 {w:0.##}%" : null;

    /// <summary>
    /// 行副标题的**完整字段串**（时长 · 得分 · 分数线 · 视频占分）。
    ///
    /// ★ 它已经不再直接当行内文字渲染了（见 <see cref="ScoreChipText"/> 的说明），
    ///   现在的用途是那一行的**悬停提示**：界面上为了省宽度把字段名全去掉了，
    ///   鼠标停在上面就能看到带字段名的完整表述，信息一项不丢，
    ///   也不必为了"可读性"再把它们塞回那两百多像素里。
    /// </summary>
    public string SubtitleText
    {
        get
        {
            var text = $"时长 {DurationText} · 得分 {ScoreText} · 分数线 {PassScoreText}";
            return WeightText is { } weight ? $"{text} · {weight}" : text;
        }
    }

    /// <summary>视频占分权重是否已知（未知时那一小段整体不显示）。</summary>
    public bool HasWeight => Course.DurationScoreWeight is { } w and > 0;

    /// <summary>
    /// 行内「成绩胶囊」的文案：`已得分/分数线`（分数线未知就只显示得分）。
    ///
    /// ★ 为什么把成绩收成一个小胶囊，而不是继续拼在副标题那一行里：
    ///   那一列只有两百多像素，光是「时长 · 得分 · 分数线 · 视频占分」四个**字段名**
    ///   就吃掉近一半宽度 —— 四个字段全写上必然折行，而折行更难看（用户反馈原文）。
    ///   字段名其实一个都不需要：位置固定、顺序固定，读第二眼就知道哪个是哪个。
    ///   去掉字段名、成绩收进彩色胶囊，信息零丢失而宽度省近一半。
    ///
    /// 平台还没回读到分数时是 `—/60` —— 跟原来那行「得分 — · 分数线 60」同一个意思。
    /// </summary>
    public string ScoreChipText => Course.PassScore is { } pass
        ? $"{ScoreText}/{pass:0.##}"
        : ScoreText;

    /// <summary>得分。</summary>
    public string ScoreText => Course.LearnScore is { } s ? s.ToString("0.##") : "—";

    /// <summary>分数线。</summary>
    public string PassScoreText => Course.PassScore is { } s ? s.ToString("0.##") : "—";

    /// <summary>
    /// 状态文案：在挂 / 已完成 / 已及格 / 等待。
    ///
    /// ★ 「已及格」这一档是必须的：分数已经过了分数线的课会被引擎直接跳过，
    ///   再把它写成一个待办的「等待」，用户只会以为队列卡住了没往下走。
    /// </summary>
    public string StatusText => IsCurrent ? "在挂"
        : IsDone ? "已完成"
        : IsPassed ? "已及格"
        : "等待";

    /// <summary>
    /// 分数已经过了分数线 —— 这门课不会再挂了。
    ///
    /// ★ 口径必须与引擎 <c>LearnEngine.ShouldSkipCourse</c> **完全一致**：
    ///   只有「及格就跳」策略下分数够了才算过关；「挂满才跳」策略下分数再高，
    ///   只要时长没挂够，这门课照样要接着挂 —— 那种情况下写「已及格」就是骗人。
    /// </summary>
    public bool IsPassed => !IsDone
        && LearnPolicy.Current == CompletionPolicy.PassScore
        && Course.LearnScore is { } score
        && score >= LearnPolicy.NormalizePassScore(Course.PassScore);

    public bool IsWaiting => !IsCurrent && !IsDone && !IsPassed;

    /// <summary>
    /// 这门课有结果了（挂满 / 已过线）—— 状态列走成功色。
    /// 「等待」是灰的、成果是绿的、正在挂是强调蓝，一列扫过去不用读字就知道谁是哪一档。
    /// </summary>
    public bool IsSuccess => IsDone || IsPassed;

    /// <summary>平台学时字段（课程卡片的"学时"），可空。</summary>
    public string HoursText => Course.HoursText;

    partial void OnIsCurrentChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsPassed));
        OnPropertyChanged(nameof(IsSuccess));
    }

    partial void OnIsDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsPassed));
        OnPropertyChanged(nameof(IsSuccess));
    }

    /// <summary>成绩/时长被刷新后，让界面重读这些属性。</summary>
    public void Bump()
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(PassScoreText));
        OnPropertyChanged(nameof(HoursText));
        OnPropertyChanged(nameof(SubtitleText));   // 悬停提示用的完整字段串重新拼一次
        OnPropertyChanged(nameof(ScoreChipText));  // 行内成绩胶囊（得分/分数线）
        OnPropertyChanged(nameof(HasWeight));      // 权重可能这一轮才查到
        // 分数变了，「已及格 / 等待」的判定也可能跟着变（心跳回读会走这里）
        OnPropertyChanged(nameof(IsPassed));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsWaiting));
    }
}
