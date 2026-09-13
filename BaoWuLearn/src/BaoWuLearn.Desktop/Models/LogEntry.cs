using Avalonia.Media;

namespace BaoWuLearn.Desktop.Models;

public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>一条界面日志。</summary>
public sealed class LogEntry
{
    public required string Time { get; init; }
    public required string Text { get; init; }
    public LogLevel Level { get; init; } = LogLevel.Info;

    public string LevelLabel => Level switch
    {
        LogLevel.Success => "OK",
        LogLevel.Warn => "!",
        LogLevel.Error => "ERR",
        _ => "·",
    };

    public IBrush LevelBrush => Level switch
    {
        LogLevel.Success => new SolidColorBrush(Color.Parse("#22C55E")),
        LogLevel.Warn => new SolidColorBrush(Color.Parse("#F59E0B")),
        LogLevel.Error => new SolidColorBrush(Color.Parse("#EF4444")),
        _ => new SolidColorBrush(Color.Parse("#6B7280")),
    };
}
