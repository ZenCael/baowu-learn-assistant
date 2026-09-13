using System.Text;
using Avalonia.Threading;

namespace BaoWuLearn.Desktop.Services;

/// <summary>
/// 全局异常兜底。
///
/// 桌面程序里任何逃到 UI 消息循环之外的异常都会直接终结进程 ——
/// 表现就是"用着用着突然闪退"，而且用户拿不到任何线索。
/// 这里挂上三层兜底：
///   1. UI 线程未处理异常 → 记录并标记已处理，程序继续活着
///   2. 后台任务未观察异常 → 记录并标记已观察，避免污染进程状态
///   3. 进程级致命异常 → 至少把现场写进崩溃日志文件，便于事后排查
/// </summary>
public static class CrashGuard
{
    private static readonly object Sync = new();
    private static string? _logFile;
    private static bool _installed;

    /// <summary>新异常写入时触发（UI 层订阅后可在日志页实时显示）。</summary>
    public static event Action<string>? Reported;

    /// <summary>崩溃日志文件路径（未安装或不可写时为 null）。</summary>
    public static string? LogFile => _logFile;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            _logFile = Path.Combine(AppPaths.LogDirectory, "crash.log");
        }
        catch
        {
            _logFile = null;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Report("进程级未处理异常", ex, fatal: true);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("后台任务异常", e.Exception, fatal: false);
            e.SetObserved();
        };

        try
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Report("界面线程异常", e.Exception, fatal: false);
                // 标记已处理，窗口不会因此被直接关闭；错误已进日志页
                e.Handled = true;
            };
        }
        catch
        {
            // 不同 Avalonia 版本事件名可能有差异，缺失时不影响其它兜底
        }
    }

    /// <summary>记录一次异常，并广播给界面。</summary>
    public static void Report(string source, Exception ex, bool fatal)
    {
        var text = $"{(fatal ? "致命" : "异常")}｜{source}｜{ex.GetType().Name}: {ex.Message}";

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine(new string('-', 72));

            if (_logFile is not null)
            {
                lock (Sync)
                    File.AppendAllText(_logFile, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志本身失败也不能再抛
        }

        try { Reported?.Invoke(text); } catch { /* 忽略 */ }
    }
}
