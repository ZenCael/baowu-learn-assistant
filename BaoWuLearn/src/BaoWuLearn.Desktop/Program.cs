using Avalonia;

namespace BaoWuLearn.Desktop;

internal static class Program
{
    /// <summary>是否以自检模式启动（<c>--selftest</c>）。</summary>
    public static bool SelfTestMode { get; private set; }

    /// <summary>更新链路诊断模式（<c>--updatecheck</c>）：跑一遍检查+验签并打印，随后退出。</summary>
    public static bool UpdateCheckMode { get; private set; }

    /// <summary>诊断模式顺带下载当前清单产物做 sha256 验证（<c>--updatecheck --download</c>，不安装）。</summary>
    public static bool UpdateCheckDownload { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        SelfTestMode = args.Any(a =>
            a.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
        UpdateCheckMode = args.Any(a =>
            a.Equals("--updatecheck", StringComparison.OrdinalIgnoreCase));
        UpdateCheckDownload = args.Any(a =>
            a.Equals("--download", StringComparison.OrdinalIgnoreCase));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
