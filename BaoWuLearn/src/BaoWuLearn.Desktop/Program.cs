using Avalonia;

namespace BaoWuLearn.Desktop;

internal static class Program
{
    /// <summary>是否以自检模式启动（<c>--selftest</c>）。</summary>
    public static bool SelfTestMode { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        SelfTestMode = args.Any(a =>
            a.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
