using System.Collections.ObjectModel;
using Avalonia.Threading;
using BaoWuLearn.Desktop.Models;
using BaoWuLearn.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>日志页。所有服务的日志汇聚到这里。</summary>
public partial class LogsViewModel : ViewModelBase
{
    private const int MaxEntries = 3000;

    [ObservableProperty] private string _filter = "";

    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>界面上实际显示的日志（受过滤条件影响）。</summary>
    public ObservableCollection<LogEntry> VisibleEntries { get; } = new();

    partial void OnFilterChanged(string value) => RebuildVisible();

    private bool Matches(LogEntry entry)
    {
        var f = Filter?.Trim();
        return string.IsNullOrEmpty(f) ||
               entry.Text.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildVisible()
    {
        VisibleEntries.Clear();
        foreach (var entry in Entries)
            if (Matches(entry))
                VisibleEntries.Add(entry);
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        VisibleEntries.Clear();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            var dir = AppPaths.LogDirectory;

            var file = Path.Combine(dir, $"baowu-learn-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await File.WriteAllLinesAsync(file, Entries.Select(e => $"[{e.Time}] {e.Text}"));
            Append($"日志已导出：{file}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            // 写盘失败（权限不足、磁盘已满）也必须给出反馈，
            // 否则用户点了「导出日志」会毫无反应，且异常无人观察。
            Append("✖ 日志导出失败：" + ex.Message, LogLevel.Error);
        }
    }

    /// <summary>线程安全地追加一条日志（可能来自后台线程）。</summary>
    public void Append(string text, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Text = text,
            Level = level,
        };

        void Add()
        {
            Entries.Add(entry);

            // 界面显示的是 VisibleEntries，必须同步追加，否则日志页会一直是空的
            if (Matches(entry)) VisibleEntries.Add(entry);

            // 上限按可见集合控制，超出后同步裁剪两个集合的头部
            while (Entries.Count > MaxEntries)
            {
                var removed = Entries[0];
                Entries.RemoveAt(0);
                VisibleEntries.Remove(removed);
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);

        // 同时落盘：日志页是内存里的，程序一关就没了。
        // 排查「挂了半天分数不动」这类问题必须有历史日志可翻，
        // 否则只能靠外部探测工具反推，成本高得多。
        Persist(text);
    }

    /// <summary>日志落盘（按天滚动，单文件超过 4MB 另起一份）。</summary>
    private static void Persist(string text)
    {
        try
        {
            var dir = AppPaths.LogDirectory;
            var file = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");

            lock (FileLock)
            {
                if (File.Exists(file) && new FileInfo(file).Length > 4 * 1024 * 1024)
                    file = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // 落盘失败（权限不足、磁盘满）不能反过来打断挂课：日志只是辅助手段
        }
    }

    private static readonly object FileLock = new();

    /// <summary>根据文本内容猜测级别（服务层只传纯文本）。</summary>
    public void AppendAuto(string text)
    {
        var level = LogLevel.Info;
        if (text.StartsWith("✖") || text.Contains("失败") || text.Contains("异常")) level = LogLevel.Error;
        else if (text.StartsWith("⚠") || text.Contains("超时")) level = LogLevel.Warn;
        else if (text.StartsWith("✓") || text.StartsWith("🎉") || text.Contains("已导出")) level = LogLevel.Success;

        Append(text, level);
    }
}
