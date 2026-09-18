using System.Collections.ObjectModel;
using Avalonia.Threading;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>总览页要执行的账号动作（由主窗口注入 —— 页面 VM 不碰池的增删细节）。</summary>
public sealed record FleetActions(
    Action<AccountRuntime> Open,
    Action<AccountRuntime> Start,
    Action<AccountRuntime> Stop,
    Action<AccountRuntime> Relogin,
    Action<AccountRuntime> Remove,
    Func<Task> StartAll,
    Action AddAccount);

/// <summary>
/// 多挂机总览页（v1.0.42）：一屏看全池里所有账号的状态与进度。
///
/// 刷新策略沿用 v1.0.32 的教训 —— 行数据每秒级对表（5 秒定时器统一刷），
/// 不订阅引擎事件的行版本；行对象本身是稳定引用（账号进出池才重建）。
/// </summary>
public partial class FleetViewModel : ViewModelBase
{
    private readonly RuntimeHub _hub;
    private readonly FleetActions _act;
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<FleetRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _hasStartable;

    public bool HasRows => Rows.Count > 0;
    public bool IsEmpty => Rows.Count == 0;

    public FleetViewModel(RuntimeHub hub, FleetActions actions)
    {
        _hub = hub;
        _act = actions;
        hub.Changed += RebuildRows;
        RebuildRows();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => RefreshRows();
        _refreshTimer.Start();
    }

    /// <summary>退出收尾：停掉对表计时器。</summary>
    public void Detach() => _refreshTimer.Stop();

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var rt in _hub.All)
            Rows.Add(new FleetRowViewModel(this, rt));
        RefreshRows();
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RefreshRows()
    {
        foreach (var row in Rows)
            row.Refresh();

        var online = _hub.All.Count(r => !r.SessionExpired);
        var running = _hub.All.Count(r => r.Engine.State == EngineState.Running);
        var totalQ = _hub.All.Sum(r => r.Engine.Queue.Count);
        var doneQ = _hub.All.Sum(r => r.Engine.Queue.Count(c => c.IsFinished));

        SummaryText = _hub.All.Count == 0
            ? "池中还没有账号 —— 登录后用侧栏「挂后台·切换账号」可以逐个把账号加进来一起挂（验证码人工过，登录一次挂一天）"
            : $"账号 {online} / {_hub.All.Count} 在线 · 挂机中 {running} · 队列合计 {doneQ} / {totalQ} 门已完成"
              + (_hub.All.Any(r => r.SessionExpired)
                  ? " · ⛔ 有过期账号，去对应行「重新登录」"
                  : "");
        HasStartable = _hub.HasStartable;
    }

    // ── 行级命令（行 VM 里包成自己的 Command 绑给按钮）──────

    public void Open(AccountRuntime rt) => _act.Open(rt);
    public void Start(AccountRuntime rt) => _act.Start(rt);
    public void Stop(AccountRuntime rt) => _act.Stop(rt);
    public void Relogin(AccountRuntime rt) => _act.Relogin(rt);
    public void Remove(AccountRuntime rt) => _act.Remove(rt);

    [RelayCommand]
    private Task StartAllAsync() => _act.StartAll();

    [RelayCommand]
    private void AddAccount() => _act.AddAccount();

    /// <summary>
    /// 行状态徽章（纯函数，供 <c>--selftest</c> 离线核对）。
    /// 刻意用「字形 + 文字」而不是颜色：状态在窄列里既要一眼可辨，
    /// 又不能在黑白打印/色弱场景下丢信息。
    /// </summary>
    public static string RowStatus(EngineState st, bool expired, int queueCount)
        => expired ? "⛔ 已过期"
           : st switch
           {
               EngineState.Running => "● 挂机中",
               EngineState.Paused => "⏸ 已暂停",
               EngineState.Stopping => "◌ 停止中",
               _ => queueCount > 0 ? "▷ 空闲可挂" : "○ 空闲",
           };
}

/// <summary>总览页的一行 = 一个账号。每 5 秒从运行时拉一次实况刷文本。</summary>
public sealed partial class FleetRowViewModel : ObservableObject
{
    private readonly FleetViewModel _fleet;
    public AccountRuntime Rt { get; }

    public RelayCommand OpenCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ReloginCommand { get; }
    public RelayCommand RemoveCommand { get; }

    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string _subText = "";
    [ObservableProperty] private string _statusText = "…";
    [ObservableProperty] private string _courseText = "";
    [ObservableProperty] private string _queueText = "";
    [ObservableProperty] private double _progress;          // 0~100，进度条用
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _canRelogin;

    public FleetRowViewModel(FleetViewModel fleet, AccountRuntime rt)
    {
        _fleet = fleet;
        Rt = rt;
        OpenCommand = new RelayCommand(() => _fleet.Open(rt));
        StartCommand = new RelayCommand(() => _fleet.Start(rt));
        StopCommand = new RelayCommand(() => _fleet.Stop(rt));
        ReloginCommand = new RelayCommand(() => _fleet.Relogin(rt));
        RemoveCommand = new RelayCommand(() => _fleet.Remove(rt));
        Refresh();
    }

    public void Refresh()
    {
        var rt = Rt;
        var snap = rt.LastSnapshot;
        var st = rt.Engine.State;
        var total = rt.Engine.Queue.Count;
        var done = rt.Engine.Queue.Count(c => c.IsFinished);

        StatusText = FleetViewModel.RowStatus(st, rt.SessionExpired, total);
        NameText = $"{rt.DisplayName} · {rt.UserNo}";
        QueueText = total == 0 ? "队列为空" : $"{done} / {total} 门";

        CourseText = st is EngineState.Running or EngineState.Paused
                     && snap?.CourseName is { Length: > 0 } name
            ? $"当前：{name}（第 {Math.Max(snap.CurrentIndex, 1)}/{Math.Max(snap.TotalCourses, total)} 门）"
            : total > 0 ? "队列已就绪，等待开始" : "未加入课程";

        // 进度条：在挂的账号用快照进度（课程级），没在挂的用队列平均完成度
        Progress = snap is { TargetSeconds: > 0 } ? snap.Progress * 100
                 : total > 0 ? rt.Engine.Queue.Average(c => c.ProgressRatio) * 100
                 : 0;

        SubText = rt.SessionExpired
            ? "进度已保留，重新登录后自动续挂"
            : st == EngineState.Running && snap?.NextHeartbeatAt is { } hb
                ? $"心跳约 {hb:HH:mm:ss}"
                : st == EngineState.Running && snap is { } s2
                    ? $"本门 {s2.WareIndex}/{Math.Max(s2.WareTotal, 1)} 课件"
                    : total > 0 ? "随时可开始" : "—";

        CanRelogin = rt.SessionExpired;
        CanStart = !rt.SessionExpired
                   && st is EngineState.Idle or EngineState.Stopped && total > 0;
        CanStop = st is EngineState.Running or EngineState.Paused;
    }
}
