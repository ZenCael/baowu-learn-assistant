using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 课程页与学习队列页共用的行操作：勾选内容解析成可入队的课程、合集展开后的收尾补查。
///
/// 历史上这里还有一套「把子行插进平铺列表 / 收起时再摘掉」的列表手术
/// （InsertAfter / Collapse / ReplayChildren）—— 两页都换成 TreeDataGrid 之后
/// 子行挂在行对象自己的 <c>Children</c> 上、展开折叠由控件管理，那套手术已整体删除。
/// </summary>
internal static class CourseListOps
{
    /// <summary>
    /// 展开班级后的收尾，两件事：
    ///
    /// ① 用班内课程的真实 learnStatus 汇总父行门数（父行副标题与子行「状态」列从此同源）。
    /// ② 给**已加入学习**（learnStatus = 1 进行中 / 2 已完成）的那几门课单独补查成绩与已学时长 ——
    ///    班内课程列表接口本身不返回成绩、也不返回已学时长。
    ///
    /// 为什么只挑着查：一个专区动辄 20+ 门课，全查又慢又没必要 —— 没开始学的课本来就没有成绩。
    /// 实测「宝武人讲AI学习专区」21 门课里只有 2 门需要查，其余 19 门直接是"未开始"。
    /// 查询失败不影响展开结果，只是那几门课显示为「—」。
    /// </summary>
    public static async Task FinishExpandAsync(
        MyCourseRow parent,
        IReadOnlyList<MyCourseRow> children,
        CourseService courses,
        CancellationToken ct = default)
    {
        parent.ApplyChildStats(children);

        var todo = children
            .Where(c => c.SourceCourse is { } sc
                        && sc.LearnStatus is "1" or "2"
                        && !sc.HasScoreInfo)
            .Select(c => c.SourceCourse!)
            .ToList();

        if (todo.Count == 0) return;

        await courses.EnrichWithScoreAsync(todo, concurrency: 6, ct);

        // 成绩到货后让界面重读这几行（得分列 / 副标题里的"已学 xx"）。
        foreach (var c in children) c.Bump();

        // 顺手把「视频占分」补齐（不管课开没开始，权重都是选课的依据）。
        // 有会话缓存的课程不会重复发请求。
        await PrefetchWeightsAsync(children, courses, ct);
    }

    /// <summary>
    /// 后台补齐行的「视频占分」权重（finishInfo CE002 的 percentage）。
    ///
    /// ★ 为什么要显示它：平台得分 = 已学时长 ÷ 要求时长 × 视频占分权重，
    ///   各课权重不同（实测 70/30、80/20 都有）—— 同样挂到一半，权重 80 的课拿 40 分、
    ///   权重 70 的课拿 35 分，「挂多久才到分数线」完全由它决定。
    ///   权重是课程配置，一次查到整个会话有效（CourseService 内部有会话缓存）。
    /// 结果写回行对象（SourceCourse 或 Item）并让界面重读；失败按"不显示"处理。
    /// </summary>
    public static async Task PrefetchWeightsAsync(
        IEnumerable<MyCourseRow> rows,
        CourseService courses,
        CancellationToken ct = default)
    {
        var todo = new List<(MyCourseRow Row, CourseItem Probe)>();
        foreach (var r in rows)
        {
            // 只有同时能定位到课程（courseNo + olClassNo）的行才查得动
            if (string.IsNullOrEmpty(r.Item.CourseNo) || string.IsNullOrEmpty(r.Item.OlClassNo)) continue;
            if (r.WeightText is not null) continue;   // 已经知道，不重复查

            var probe = r.SourceCourse ?? r.ToCourseItem();

            // 会话缓存命中：直接回填，不发请求（列表反复刷新就靠这条兜住）
            if (courses.CachedDurationWeight(probe) is { } cached)
            {
                ApplyWeight(r, cached);
                continue;
            }

            todo.Add((r, probe));
        }

        if (todo.Count == 0) return;

        using var gate = new SemaphoreSlim(4);
        var tasks = todo.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await courses.FetchDurationWeightAsync(t.Probe, ct) is { } weight)
                    ApplyWeight(t.Row, weight);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>把权重写回行对象并在 UI 线程让该行重读副标题。</summary>
    private static void ApplyWeight(MyCourseRow row, double weight)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (row.SourceCourse is { } sc) sc.DurationScoreWeight = weight;
            else row.Item.DurationScoreWeight = weight;
            row.Bump();
        });
    }

    /// <summary>
    /// 把勾选的行解析成真正可以入队的课程列表。
    ///
    /// 课程级行直接取它的课程对象；班级级（合集）行先展开，把班内课程全部收进来 ——
    /// 勾一个合集就等于挂整个合集，不必逐门展开再勾。
    /// 最后按 <c>courseNo@olClassNo</c> 去重，多选重叠时也不会重复入队。
    /// </summary>
    public static async Task<List<CourseItem>> ResolveQueueItemsAsync(
        IEnumerable<MyCourseRow> rows,
        CourseService courses,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var items = new List<CourseItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(CourseItem c)
        {
            var key = (c.CourseNo ?? "") + "@" + (c.OlClassNo ?? "");
            if (seen.Add(key)) items.Add(c);
        }

        foreach (var row in rows)
        {
            if (row.IsDirectCourse)
            {
                Add(row.ToCourseItem());
                continue;
            }

            // ⚠ 这里必须判「有 olClassNo」而不是 Item.CanLearn：
            //   CanLearn 要求同时有 courseNo + olClassNo，而班级行本来就没有 courseNo，
            //   用它判断会让勾选的合集被整条跳过（等于"勾了班级却没加进任何课程"）。
            if (!row.IsClassLevel || string.IsNullOrEmpty(row.Item.OlClassNo)) continue;

            // 快路径：树网格的展开（或后台预取）已经把班内明细拉齐过，
            // 直接吃行上已有的子行，勾选大合集时不必再等一次网络往返。
            if (row.ChildrenReady)
            {
                foreach (var ch in row.Children)
                    if (ch.SourceCourse is { } cached) Add(cached);
                continue;
            }

            onProgress?.Invoke($"正在展开「{row.Title}」…");
            var classCourses = await courses.GetClassCoursesAsync(
                row.Item.CenterCode, row.Item.OlClassNo, row.Item.OlClassType, ct);

            foreach (var c in classCourses) Add(c);
        }

        return items;
    }
}
