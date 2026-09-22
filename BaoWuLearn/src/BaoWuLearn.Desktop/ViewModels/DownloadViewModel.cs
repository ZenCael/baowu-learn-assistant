using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using BaoWuLearn.Core.Download;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 下载页（v1.0.44+）—— 真想学的课，把课件存到本地。
///
/// v1.0.45 改版：页面拆成两个 tab ——「课程列表」列挂机队列里的课
/// （下载范围和挂机范围天然一致，标题里明说来源），「下载列表」看任务
/// 进度、暂停/续传、重试、打开所在目录；排任务后自动跳到下载列表，
/// 免得用户找不到刚排进去的任务。
///
/// 与挂机解耦：这里只读取队列课程清单，不碰引擎状态；
/// 静态视频区流量也不走 ApiClient（见 DownloadService 头注）。
/// </summary>
public partial class DownloadViewModel : ViewModelBase
{
    private readonly LearnEngine _engine;
    private readonly CourseService _courses;
    private readonly DownloadService _downloads;
    private readonly Action<string> _log;
    private readonly Func<string> _rootHint;
    private readonly Action<string?> _saveRoot;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "从课程列表挑课件下载（与挂机队列同范围）";
    [ObservableProperty] private string _openRootHint = "";
    [ObservableProperty] private int _selectedTab;

    public ObservableCollection<DownloadCourseRow> Courses { get; } = new();
    public ObservableCollection<DownloadTaskRow> Tasks { get; } = new();

    /// <summary>当前解析出的保存根目录（供视图层做目录选择器的起始位置）。</summary>
    public string CurrentRoot => _rootHint();

    public string CoursesTabHeader => $"课程列表 · 来自挂机队列（{Courses.Count} 门）";
    public string TasksTabHeader => ActiveTaskCount > 0
        ? $"下载列表 · {ActiveTaskCount} 个进行中"
        : Tasks.Count > 0 ? "下载列表 · 全部处理完" : "下载列表";
    public int ActiveTaskCount => Tasks.Count(t => t.CanPause);
    public bool HasAnyTasks => Tasks.Count > 0;

    public DownloadViewModel(LearnEngine engine, CourseService courses,
        DownloadService downloads, Action<string> log,
        Func<string> rootHintProvider, Action<string?> saveRoot)
    {
        _engine = engine;
        _courses = courses;
        _downloads = downloads;
        _log = log;
        _rootHint = rootHintProvider;
        _saveRoot = saveRoot;

        _downloads.Changed += OnDownloadsChanged;
        _downloads.Progressed += OnDownloadsProgressed;

        RebuildTasks();
        RefreshRootHint();
    }

    /// <summary>换账号/刷新时由主窗口调（进页面也会调，保存目录改动即时可见）。</summary>
    public void Refresh()
    {
        var queue = _engine.Queue;
        Courses.Clear();
        foreach (var c in queue)
            Courses.Add(new DownloadCourseRow(c, _courses, LoadCatalogAsync, _log));
        StatusText = queue.Count == 0
            ? "挂机队列是空的 —— 先到挂机页挑课，这里才有可下载的课程"
            : $"共 {queue.Count} 门队列课程 · 展开目录选课件，或点「全部下载」整课排进队列";
        RefreshRootHint();
        OnPropertyChanged(nameof(CoursesTabHeader));
    }

    private void RefreshRootHint() =>
        OpenRootHint = $"保存目录：{_rootHint()}/宝武学习助手（按课程名分文件夹归档）";

    /// <summary>视图层选完目录后回调：持久化设置并刷新提示。</summary>
    public void ApplyPickedDownloadRoot(string path)
    {
        _saveRoot(path);
        RefreshRootHint();
        StatusText = $"下载目录已更新：{path}";
        OnPropertyChanged(nameof(CoursesTabHeader));
    }

    private static string RootOf(string targetPath)
    {
        try { return Path.GetDirectoryName(targetPath) ?? ""; } catch { return ""; }
    }

    private async Task LoadCatalogAsync(DownloadCourseRow row)
    {
        row.IsLoading = true;
        try
        {
            var catalog = await _courses.GetCatalogAsync(row.Course.CenterCode, row.Course.CourseNo);
            row.SetCatalog(catalog);
        }
        catch (Exception ex)
        {
            _log($"目录加载失败：{ex.Message}");
        }
        finally
        {
            row.IsLoading = false;
        }
    }

    // ── 任务区 ───────────────────────────────────────────

    private void OnDownloadsChanged()
    {
        // Changed 来自任意线程：整体重建回 UI 线程做
        if (Dispatcher.UIThread.CheckAccess()) RebuildTasks();
        else Dispatcher.UIThread.Post(RebuildTasks);
    }

    private void OnDownloadsProgressed()
    {
        // 进度广播只就地刷新已有行（不重建集合，否则进度条动画被打断）
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnDownloadsProgressed);
            return;
        }
        foreach (var row in Tasks) row.RefreshProgress();
    }

    private void RebuildTasks()
    {
        var snap = _downloads.Snapshot();
        // 按课程分组内保持入队序；未完成的在前
        var ordered = snap
            .OrderByDescending(t => t.State is DownloadState.Queued or DownloadState.Running)
            .ThenByDescending(t => t.State == DownloadState.Paused)
            .ToList();

        if (Tasks.Count == ordered.Count &&
            Tasks.Select(t => t.Task.Id).SequenceEqual(ordered.Select(t => t.Id)))
        {
            foreach (var row in Tasks) row.RefreshState();
            RaiseTaskHeaders();
            return;
        }

        Tasks.Clear();
        foreach (var t in ordered) Tasks.Add(new DownloadTaskRow(t, _downloads));
        RaiseTaskHeaders();
    }

    private void RaiseTaskHeaders()
    {
        OnPropertyChanged(nameof(TasksTabHeader));
        OnPropertyChanged(nameof(ActiveTaskCount));
        OnPropertyChanged(nameof(HasAnyTasks));
    }

    [RelayCommand]
    private void ToggleRow(DownloadCourseRow? row)
    {
        if (row is not null) row.IsExpanded = !row.IsExpanded;
    }

    [RelayCommand]
    private void Download(WareDownloadRow? row)
    {
        if (row is null) return;
        try
        {
            _downloads.Enqueue(row.Course, row.Ware);
            StatusText = $"已加入下载队列：{row.Name}";
            SelectedTab = 1;   // 排完任务直接跳到下载列表，不用用户自己找
        }
        catch (Exception ex)
        {
            StatusText = $"下载失败：{ex.Message}";
            _log($"✗ {row.Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 整课下载：目录没加载过就先自动加载（v1.0.45 —— 以前要求先展开目录
    /// 再点按钮，纯属把内部实现状态甩给用户），再按目录顺序把所有可下载
    /// 课件排进队列（多视频课件 = 多条任务）。
    /// </summary>
    [RelayCommand]
    private async Task DownloadAll(DownloadCourseRow? row)
    {
        if (row is null) return;
        if (row.Wares.Count == 0)
        {
            StatusText = $"正在加载「{row.Title}」的课件目录…";
            await row.EnsureCatalogAsync();
            if (row.Wares.Count == 0)
            {
                StatusText = $"「{row.Title}」目录加载失败 —— 网络问题，稍后再点一次试试";
                return;
            }
        }
        var n = 0;
        foreach (var w in row.Wares.Where(w => w.CanDownload))
        {
            try { _downloads.Enqueue(w.Course, w.Ware); n++; }
            catch (Exception ex) { _log($"✗ {w.Name}：{ex.Message}"); }
        }
        StatusText = $"「{row.Title}」已排入 {n} 条下载任务";
        if (n > 0) SelectedTab = 1;
    }

    [RelayCommand]
    private void PauseAll() => _downloads.PauseAll();

    [RelayCommand]
    private void ResumeAll()
    {
        foreach (var t in _downloads.Snapshot()
                     .Where(t => t.State is DownloadState.Queued or DownloadState.Paused or DownloadState.Failed))
            _downloads.Resume(t.Id);
    }

    public void Detach()
    {
        _downloads.Changed -= OnDownloadsChanged;
        _downloads.Progressed -= OnDownloadsProgressed;
    }
}

/// <summary>下载页课程行（可展开课件目录）。目录按需拉一次就缓存。</summary>
public sealed partial class DownloadCourseRow : ViewModelBase
{
    private readonly Func<DownloadCourseRow, Task> _loader;
    private readonly Action<string> _log;
    private Task? _catalogInflight;

    public CourseItem Course { get; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _catalogSummary = "";

    public ObservableCollection<WareDownloadRow> Wares { get; } = new();

    public DownloadCourseRow(CourseItem course, CourseService courses,
        Func<DownloadCourseRow, Task> loader, Action<string> log)
    {
        Course = course;
        _ = courses;   // 目录服务经 loader 闭包使用，这里不直接持有
        _loader = loader;
        _log = log;
    }

    public string Title => Course.CourseName;
    public string SubText => Course.CenterName;
    public string ExpanderLabel => IsExpanded ? "收起" : "展开";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpanderLabel));
        if (!value || Wares.Count > 0 || IsLoading) return;
        _ = EnsureCatalogAsync();
    }

    /// <summary>
    /// 确保目录已加载（展开、全部下载共用一条通道）：正在加载就等同一次，
    /// 加载失败则清掉句柄让下次点击能重试。
    /// </summary>
    public async Task EnsureCatalogAsync()
    {
        if (Wares.Count > 0) return;
        if (_catalogInflight is { } inflight)
        {
            await inflight;
            return;
        }
        _catalogInflight = _loader(this);
        try { await _catalogInflight; }
        finally { if (Wares.Count == 0) _catalogInflight = null; }
    }

    public void SetCatalog(List<CatalogNode> catalog)
    {
        Wares.Clear();
        var n = 0;
        foreach (var node in catalog)
            foreach (var w in node.Wares)
            {
                Wares.Add(new WareDownloadRow(Course, w, node.CataName));
                n++;
            }
        CatalogSummary = $"{catalog.Count} 个目录 · {n} 个课件";
    }
}

/// <summary>课件行：形态标签 + 下载按钮。</summary>
public sealed partial class WareDownloadRow : ViewModelBase
{
    public CourseItem Course { get; }
    public WareItem Ware { get; }
    public string CatalogName { get; }

    private readonly WareSourceKind _kind;

    public WareDownloadRow(CourseItem course, WareItem ware, string catalogName)
    {
        Course = course;
        Ware = ware;
        CatalogName = catalogName;
        _kind = WareSourceResolver.Resolve(ware).Kind;
    }

    public string Name => Ware.WareName;
    public string Duration => Ware.DurationText;
    public string TypeLabel => _kind switch
    {
        WareSourceKind.HlsStream => "视频·流",
        WareSourceKind.DirectFile => "视频·文件",
        WareSourceKind.PreviewFile => "文档/附件",
        _ => "不可下载",
    };
    public bool CanDownload => _kind != WareSourceKind.NotDownloadable;
}

/// <summary>任务行（进度/控制）。</summary>
public sealed partial class DownloadTaskRow : ViewModelBase
{
    private readonly DownloadService _service;
    public DownloadTask Task { get; }

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _hasProgress;

    public DownloadTaskRow(DownloadTask task, DownloadService service)
    {
        Task = task;
        _service = service;
        RefreshState();
    }

    public string Title => Task.WareName;
    public string CourseTitle => Task.CourseTitle;
    public string KindLabel => Task.Kind switch
    {
        WareSourceKind.HlsStream => "HLS",
        WareSourceKind.DirectFile => "直链",
        WareSourceKind.PreviewFile => "文档",
        _ => "?",
    };

    public bool CanPause => Task.State is DownloadState.Queued or DownloadState.Running;
    public bool CanResume => Task.State is DownloadState.Paused or DownloadState.Failed;

    public void RefreshState()
    {
        StateText = Task.State switch
        {
            DownloadState.Queued => "排队中",
            DownloadState.Running => "下载中",
            DownloadState.Paused => "已暂停",
            DownloadState.Completed => "已完成",
            DownloadState.Failed => "失败",
            _ => "—",
        };
        ErrorText = Task.State == DownloadState.Failed ? Task.Error ?? "" : "";
        RefreshProgress();
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    public void RefreshProgress()
    {
        ProgressText = Task.ProgressText;
        var f = Task.Fraction;
        HasProgress = f.HasValue;
        Percent = f.HasValue ? Math.Round(f.Value * 100, 1) : 0;
    }

    [RelayCommand] private void Pause() => _service.Pause(Task.Id);
    [RelayCommand] private void Resume() => _service.Resume(Task.Id);
    [RelayCommand] private void Remove() => _service.Remove(Task.Id);

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(Task.TargetPath);
            if (dir is null || !Directory.Exists(dir)) return;
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{Task.TargetPath}\"")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"-R \"{Task.TargetPath}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{dir}\"");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch { /* 打不开目录不算事故 */ }
    }
}
