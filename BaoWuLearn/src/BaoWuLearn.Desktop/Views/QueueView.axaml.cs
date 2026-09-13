using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BaoWuLearn.Desktop.ViewModels;

namespace BaoWuLearn.Desktop.Views;

/// <summary>
/// 学习队列页视图。
///
/// 「我的已选课程」用的是官方 TreeDataGrid（树形表格），不再是"平铺列表 + └ 制表符假装层级"。
/// 列是**代码构造**的 —— TreeDataGrid 的列选择器要求写 C# 表达式（编译期类型安全），
/// 所以没法在 XAML 里声明；XAML 只提供单元格长相（资源键模板，列构造时按键引用）。
/// </summary>
public partial class QueueView : UserControl
{
    private QueueViewModel? _boundVm;
    private HierarchicalTreeDataGridSource<MyCourseRow>? _source;

    /// <summary>正在做"子行晚到"补偿 —— 期间由补展开触发的 RowExpanding 要直接忽略。</summary>
    private bool _compensating;

    // ── 挂机列表自动滚动 ──────────────────────────────────────
    //
    // 用户报的两个现象其实是同一套状态：
    //   · 列表比可视区高时，当前在挂那一行滚出去了也不会自己回来（他说"这个 12 是我拖到这才显示的"）；
    //   · 手动拖去看别的行之后，停手一会儿应该自动滚回当前行。
    // 做法与日志页的"钉底跟随"同源：一个跟随开关 + 一个空闲计时器。

    /// <summary>是否跟随当前在挂行。开着的时候列表会自动滚到它。</summary>
    private bool _followCurrent = true;

    /// <summary>上一次已经滚过去的当前行下标。当前行没变就不折腾，免得跟用户抢滚动条。</summary>
    private int _followedIndex = -1;

    /// <summary>用户停手多久之后自动滚回当前行。</summary>
    private static readonly TimeSpan ResumeFollowDelay = TimeSpan.FromSeconds(8);

    /// <summary>程序自己刚设置过 Offset 的时刻 —— 用来把"自己滚的"和"用户滚的"分开。</summary>
    private DateTimeOffset _programmaticScrollAt = DateTimeOffset.MinValue;

    /// <summary>自己滚动的宽限期：这段时间内收到的滚动事件一律当作自己触发的。</summary>
    private static readonly TimeSpan ProgrammaticGrace = TimeSpan.FromMilliseconds(400);

    /// <summary>手动滚动后的恢复计时器。</summary>
    private DispatcherTimer? _resumeFollowTimer;

    /// <summary>每秒对一次表：当前行变了、或用户已经停手，就把当前行滚回可视区。</summary>
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public QueueView()
    {
        InitializeComponent();
        SetupQueueAutoScroll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindSource();
    }

    private void BindSource()
    {
        if (DataContext is not QueueViewModel vm) return;
        if (ReferenceEquals(_boundVm, vm)) return;   // 同一个 VM 反复挂接，别重建整棵树

        _boundVm = vm;
        _source?.Dispose();

        // 列表整体重建后，之前记的"已经滚到第几行"就作废了（行对象全换了一批），
        // 不清掉的话下一次对表会以为"当前行没变"而放弃滚动。
        vm.QueueItems.CollectionChanged += (_, _) => _followedIndex = -1;

        _source = new HierarchicalTreeDataGridSource<MyCourseRow>(vm.VisibleCourses)
        {
            Columns =
            {
                // 「选择」：原生复选框列，直接读写 IsSelected，比自绘模板可靠。
                new CheckBoxColumn<MyCourseRow>(
                    "选择",
                    x => x.IsSelected,
                    (x, v) => x.IsSelected = v,
                    width: new GridLength(48)),

                // 「名称」列自带展开箭头 —— 合集行的子课程挂在它自己的 Children 上。
                new HierarchicalExpanderColumn<MyCourseRow>(
                    new TemplateColumn<MyCourseRow>(
                        "课程 / 班级名称",
                        "QueueTitleCell",
                        width: new GridLength(1, GridUnitType.Star),
                        options: new TemplateColumnOptions<MyCourseRow>
                        {
                            MinWidth = new GridLength(240),
                        }),
                    // 子行集合是行对象自己的 ObservableCollection（实例不变，预取到货往里 Add 即可）；
                    // 箭头是否显示只看"是不是班级行"，与子行是否已预取无关。
                    childSelector: x => x.CanExpand ? x.Children : null,
                    hasChildrenSelector: x => x.CanExpand),

                new TextColumn<MyCourseRow, string>("分类", x => x.Category,
                    width: new GridLength(150)),
                new TextColumn<MyCourseRow, string>("学时", x => x.HoursText,
                    width: new GridLength(64)),
                new TextColumn<MyCourseRow, string>("得分", x => x.ScoreText,
                    width: new GridLength(90)),
                new TextColumn<MyCourseRow, string>("状态", x => x.StatusText,
                    width: new GridLength(78)),

                // 「操作」：课程行的「加入 / 移出队列」开关（班级行的展开交给名称列箭头）。
                new TemplateColumn<MyCourseRow>("操作", "QueueActionCell",
                    width: new GridLength(110)),
            },
        };

        // 展开一个还没预取到货的合集时兜底再拉一次（正常情况下预取早已完成、这里空转）。
        _source.RowExpanding += (_, args) =>
        {
            // ★ 只在"展开那一刻子行还没到货"时接管：这一次展开必然铺不出子行，
            //   等数据回来补一次（见下面的 LoadChildrenAsync）。
            //   已经拉齐的行不动 —— 补展开自身会再触发 RowExpanding，无条件接管会递归打转。
            if (!_compensating && args.Row.Model.Children.Count == 0)
                _ = LoadChildrenAsync(args.Row.Model);
        };

        CourseTree.Source = _source;
    }

    private async Task LoadChildrenAsync(MyCourseRow row)
    {
        if (_boundVm is not { } vm) return;

        await vm.EnsureChildrenAsync(row);

        // 拉回来一门都没有（或加载失败）就别补了
        if (row.Children.Count == 0) return;

        // ★ 重入哨兵：补展开会同步再触发一次 RowExpanding，
        //   没有它就会「补 → 展开 → 补 → …」无限递归。
        _compensating = true;
        try
        {
            TreeGridExpansion.ReExpand(_source, vm.VisibleCourses, row);
        }
        finally
        {
            _compensating = false;
        }
    }

    // ── 挂机列表自动滚动 ──────────────────────────────────────

    /// <summary>
    /// 接上挂机列表的自动滚动。
    ///
    /// 用"每秒对一次表"而不是订阅每一行的 IsCurrent：挂机列表会被整体重建
    ///（<c>QueueViewModel.Refresh</c> 走的是 <c>Clear()</c> + <c>Add()</c>），
    /// 订阅就得跟着每一次重建逐个挂钩子、逐个摘钩子，漏一次就永远不跟随了。
    /// 每秒扫一遍一行数据比这个便宜得多，而且不会漏。
    /// </summary>
    private void SetupQueueAutoScroll()
    {
        QueueScroll.ScrollChanged += OnQueueScrollChanged;
        _followTimer.Tick += (_, _) => FollowCurrentRow();
        _followTimer.Start();
    }

    private void OnQueueScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 程序自己刚滚的（或布局变化引起的微调）→ 不算用户操作。
        // 少了这道闸，我们自己每滚一次都会被当成"用户接管"，跟随就此永久关闭。
        if (DateTimeOffset.Now - _programmaticScrollAt < ProgrammaticGrace) return;

        // 用户接管：先关掉跟随，起个空闲计时器 —— 停手之后自己滚回当前行。
        _followCurrent = false;

        _resumeFollowTimer ??= CreateResumeFollowTimer();
        _resumeFollowTimer.Stop();
        _resumeFollowTimer.Start();
    }

    private DispatcherTimer CreateResumeFollowTimer()
    {
        var timer = new DispatcherTimer { Interval = ResumeFollowDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _followCurrent = true;
            _followedIndex = -1;      // 强制下一句真的滚一次（当前行下标没变也不管）
            FollowCurrentRow();
        };
        return timer;
    }

    /// <summary>
    /// 把当前在挂的那一行滚进可视区。
    ///
    /// 三种情况直接返回：没在跟随（用户正在自己看）、没在挂课（没有要跟的行）、
    /// 当前行没变（该滚的上一秒已经滚过了，反复设置 Offset 只会跟用户抢滚动条）。
    /// </summary>
    private void FollowCurrentRow()
    {
        if (!_followCurrent) return;
        if (DataContext is not QueueViewModel vm) return;

        var index = -1;
        for (var i = 0; i < vm.QueueItems.Count; i++)
        {
            if (!vm.QueueItems[i].IsCurrent) continue;
            index = i;
            break;
        }
        if (index < 0) return;
        if (index == _followedIndex) return;

        if (RowContainer(vm.QueueItems[index]) is not { } container) return;
        if (container.TranslatePoint(new Point(0, 0), QueueList) is not { } top) return;

        var viewport = QueueScroll.Viewport.Height;
        var extent = QueueScroll.Extent.Height;
        if (viewport <= 0 || extent <= 0) return;

        // 目标是"这一行尽量落在可视区中间"：贴着上沿或下沿的话前后文都看不见，
        // 看着就像列表卡住了。滚不动的位置（首尾几行）由 Clamp 兜住。
        var rowHeight = container.Bounds.Height;
        var target = top.Y - Math.Max(0, (viewport - rowHeight) / 2);
        target = Math.Clamp(target, 0, Math.Max(0, extent - viewport));

        _followedIndex = index;   // 拿到容器了才记账；容器还没铺出来时留到下一秒重试
        if (Math.Abs(QueueScroll.Offset.Y - target) < 1) return;

        _programmaticScrollAt = DateTimeOffset.Now;
        QueueScroll.Offset = new Vector(QueueScroll.Offset.X, target);
    }

    /// <summary>
    /// 找某一行在视觉树里的容器。
    ///
    /// 用视觉树查找而不是 <c>ContainerFromIndex</c>：列表刚重建（Clear + Add）时容器
    /// 可能还没铺出来，这里返回 null 让调用方"跳过这一秒、下一秒再来"，
    /// 而不会因为拿到一个错的容器把位置算歪。
    /// 行的最外层就是那个 <c>Border.row.queuerow</c>，深度优先遍历会最先遇到它。
    /// </summary>
    private Control? RowContainer(QueueRow row)
        => QueueList.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, row));
}
