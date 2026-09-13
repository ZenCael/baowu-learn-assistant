using Avalonia.Threading;
using BaoWuLearn.Core.Services;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 「合集（学习专区 / 专题班 / 培训班）展开 → 拉班内课程」这套异步加载。
///
/// 课程页与学习队列页都要用（两边的树网格都靠它填 Children），抽出来避免
/// 各写一份、改一边漏一边。它负责四件事：
///   ① 并发闸（最多 4 个班同时拉）—— 一个专区动辄 20+ 门课，不限并发会把平台请求打爆；
///   ② 按班级键去重 —— 预取与展开点击不该发两份一模一样的请求；
///   ③ 把结果写进行对象自己的 Children（TreeDataGrid 的层级视图在监听这个集合，
///      增删必须在 UI 线程做，否则会撞上"集合被另一个线程修改"）；
///   ④ 收尾补查「已加入学习」那几门的成绩 —— 班内课程列表接口不返回成绩与已学时长。
/// </summary>
internal sealed class ClassChildrenLoader
{
    private readonly CourseService _courses;
    private readonly Action<string> _report;
    private readonly Func<string?, string?, bool> _inQueue;

    /// <summary>并发上限：4 个班同时拉，够快又不至于把平台请求打爆。</summary>
    private readonly SemaphoreSlim _gate = new(4);

    /// <summary>按班级键去重，避免预取与展开点击重复发同一份请求。</summary>
    private readonly Dictionary<string, Task> _loads = new(StringComparer.Ordinal);

    public ClassChildrenLoader(
        CourseService courses, Action<string> report, Func<string?, string?, bool> inQueue)
    {
        _courses = courses;
        _report = report;
        _inQueue = inQueue;
    }

    /// <summary>列表换了一批行以后调用：旧的加载记录作废（新行对象，旧任务也不再有意义）。</summary>
    public void Reset()
    {
        lock (_loads) _loads.Clear();
    }

    /// <summary>
    /// 确保某班级行的子课程已拉齐（树网格展开、后台预取、勾选合集入队都走这一条路）。
    /// 已拉齐的行直接返回，不重复请求。
    /// </summary>
    public Task EnsureAsync(MyCourseRow row)
    {
        if (!row.CanExpand || row.ChildStatsReady) return Task.CompletedTask;

        lock (_loads)
        {
            if (_loads.TryGetValue(row.Key, out var running)) return running;
            var t = LoadAsync(row);
            _loads[row.Key] = t;
            return t;
        }
    }

    private async Task LoadAsync(MyCourseRow row)
    {
        await _gate.WaitAsync();
        try
        {
            var courses = await _courses.GetClassCoursesAsync(
                row.Item.CenterCode, row.Item.OlClassNo, row.Item.OlClassType);

            var children = courses
                .Select(c => MyCourseRow.FromCourse(c, row.Category, nested: true, parentKey: row.Key))
                .ToList();

            foreach (var ch in children)
                ch.InQueue = _inQueue(ch.Item.CourseNo, ch.Item.OlClassNo);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                row.Children.Clear();
                foreach (var ch in children) row.Children.Add(ch);
                row.ApplyChildStats(children);
            });

            // 补查"已开始"那几门的成绩/已学时长（班内列表接口不返回这些）
            await CourseListOps.FinishExpandAsync(row, children, _courses);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var ch in children) ch.Bump();
            });
        }
        catch (Exception ex)
        {
            _report($"⚠ 「{row.Title}」班内课程加载失败：{ex.Message}（再次展开会重试）");
            lock (_loads) _loads.Remove(row.Key);   // 允许下次展开重试
        }
        finally
        {
            _gate.Release();
        }
    }
}
