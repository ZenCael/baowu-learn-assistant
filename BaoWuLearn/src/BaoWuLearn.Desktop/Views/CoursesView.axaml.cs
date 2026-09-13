using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using BaoWuLearn.Desktop.ViewModels;

namespace BaoWuLearn.Desktop.Views;

/// <summary>
/// 课程页视图。
///
/// 列表用官方 TreeDataGrid（树形表格），与「学习队列」页同一套做法 ——
/// 合集行（学习专区 / 专题班 / 培训班）带原生展开箭头，子课程是真正的树节点，
/// 不再是"平铺列表 + └ 制表符假装层级"（那样展开状态一刷新就丢）。
///
/// 列是**代码构造**的：TreeDataGrid 的列选择器要求写 C# 表达式（编译期类型安全），
/// 所以没法在 XAML 里声明；XAML 只提供单元格长相（资源键模板，列构造时按键引用）。
/// </summary>
public partial class CoursesView : UserControl
{
    private CoursesViewModel? _boundVm;
    private HierarchicalTreeDataGridSource<MyCourseRow>? _source;

    /// <summary>正在做"子行晚到"补偿 —— 期间由补展开触发的 RowExpanding 要直接忽略。</summary>
    private bool _compensating;

    public CoursesView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindSource();
    }

    private void BindSource()
    {
        if (DataContext is not CoursesViewModel vm) return;
        if (ReferenceEquals(_boundVm, vm)) return;   // 同一个 VM 反复挂接，别重建整棵树

        _boundVm = vm;
        _source?.Dispose();

        _source = new HierarchicalTreeDataGridSource<MyCourseRow>(vm.VisibleRows)
        {
            Columns =
            {
                // 「选择」：原生复选框列，直接读写 IsSelected，比自绘模板可靠。
                new CheckBoxColumn<MyCourseRow>(
                    "选择",
                    x => x.IsSelected,
                    (x, v) => x.IsSelected = v,
                    width: new GridLength(48)),

                // 「名称」列自带展开箭头 —— 合集的子课程挂在它自己的 Children 上。
                new HierarchicalExpanderColumn<MyCourseRow>(
                    new TemplateColumn<MyCourseRow>(
                        "课程 / 班级名称",
                        "CourseTitleCell",
                        width: new GridLength(1, GridUnitType.Star),
                        options: new TemplateColumnOptions<MyCourseRow>
                        {
                            MinWidth = new GridLength(260),
                        }),
                    // 子行集合是行对象自己的 ObservableCollection（实例不变，加载到货往里 Add 即可）；
                    // 箭头是否显示只看"是不是合集行"，与子行是否已拉取无关。
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

                // 「操作」：课程行的「加入 / 移出队列」开关（合集行的展开交给名称列箭头）。
                new TemplateColumn<MyCourseRow>("操作", "CourseActionCell",
                    width: new GridLength(110)),
            },
        };

        // 展开合集时才去拉班内课程（浏览目录一页 20 个合集，进页就全拉会把平台请求打爆）。
        // 已经拉齐的行会直接返回，不会重复请求。
        _source.RowExpanding += (_, args) =>
        {
            // ★ 只在"展开那一刻子行还没到货"时接管 —— 课程页常态是点开才去拉，
            //   这一次展开必然铺不出子行，等数据回来补一次（见下面的 LoadChildrenAsync）。
            //   已经拉齐的行不动：补展开自身会再触发 RowExpanding，无条件接管会递归打转。
            if (!_compensating && args.Row.Model.Children.Count == 0)
                _ = LoadChildrenAsync(args.Row.Model);
        };

        CourseTree.Source = _source;
    }

    private async Task LoadChildrenAsync(MyCourseRow row)
    {
        if (_boundVm is not { } vm) return;

        await vm.EnsureChildrenAsync(row);

        // 拉回来一门都没有的合集（或加载失败）就别补了 —— 补了也没有子行可铺
        if (row.Children.Count == 0) return;

        // ★ 重入哨兵：补展开会同步再触发一次 RowExpanding，
        //   没有它就会「补 → 展开 → 补 → …」无限递归（空子集合那次尤其致命）。
        _compensating = true;
        try
        {
            TreeGridExpansion.ReExpand(_source, vm.VisibleRows, row);
        }
        finally
        {
            _compensating = false;
        }
    }
}
