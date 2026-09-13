using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BaoWuLearn.Desktop.Diagnostics;
using BaoWuLearn.Desktop.Services;
using BaoWuLearn.Desktop.ViewModels;
using BaoWuLearn.Desktop.Views;

namespace BaoWuLearn.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 避免 CommunityToolkit.Mvvm 与 Avalonia 内置校验重复触发
            DisableAvaloniaDataAnnotationValidation();

            // 先装好全局异常兜底，再创建任何会跑业务逻辑的对象
            CrashGuard.Install();

            var viewModel = new MainWindowViewModel();
            CrashGuard.Reported += viewModel.ReportCrash;

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // 只有"没经过窗口关闭流程"的退出才会走到这里（Cmd+Q、自检结束等）。
            // 正常点关闭按钮时，MainWindow.OnClosing 已经把引擎收尾做完了，这里什么都不用做。
            // 兜底路径刻意不做同步等待 —— 退出阶段阻塞 UI 线程正是之前卡死的根源。
            desktop.ShutdownRequested += (_, _) => viewModel.AbortForShutdown();

            if (Program.SelfTestMode)
            {
                Dispatcher.UIThread.Post(
                    () => _ = RunSelfTestAsync(desktop, viewModel),
                    DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 自检模式（<c>--selftest</c>）：窗口起来后跑一遍验证码链路，打印报告并退出。
    /// 跑在真实进程里，运行环境与正常使用完全一致。
    /// </summary>
    private static async Task RunSelfTestAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel viewModel)
    {
        try
        {
            var run = SelfTest.RunAsync(viewModel, desktop.MainWindow);
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2)));
            if (finished != run)
                Console.WriteLine("自检超时（120 秒），已中止。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("自检过程异常：" + ex);
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var toRemove = BindingPlugins.DataValidators
            .OfType<DataAnnotationsValidationPlugin>()
            .ToArray();

        foreach (var plugin in toRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}
