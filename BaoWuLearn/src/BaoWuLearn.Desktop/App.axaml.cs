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

            // 皮肤与密度必须在主窗口创建前落地，否则首帧会闪一下默认深空蓝/紧凑资源
            ThemeService.Apply(viewModel.CurrentSettings);

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

            if (Program.UpdateCheckMode)
            {
                Dispatcher.UIThread.Post(
                    () => _ = RunUpdateDiagAsync(desktop, viewModel),
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

    /// <summary>
    /// 更新链路诊断（<c>--updatecheck</c>，可加 <c>--download</c> 顺带验证产物下载）：
    /// 打印端点链 → 跑一遍「取清单 + 验签 + 防回滚 + 版本比较」→ 可选下载校验。
    /// 发布方用它验证"路是通的"，用户拿到的每个字节都先经过这条链。
    /// </summary>
    private static async Task RunUpdateDiagAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel viewModel)
    {
        using var svc = new BaoWuLearn.Core.Update.UpdateService(
            viewModel.Settings.CurrentSettings.UpdateMirrors);
        try
        {
            Console.WriteLine("========== 宝武学习助手 · 更新链路诊断 ==========");
            Console.WriteLine($"版本     : {viewModel.VersionText}");
            Console.WriteLine($"时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var chain = BaoWuLearn.Core.Update.UpdateManifestParser.BuildEndpointChain(
                BaoWuLearn.Core.Update.UpdateManifestParser.ManifestUrl, svc.Mirrors);
            Console.WriteLine($"端点链（{chain.Count} 跳）：");
            for (var i = 0; i < chain.Count; i++)
                Console.WriteLine($"  {i + 1}. {chain[i]}");

            var r = await svc.CheckAsync(
                viewModel.CurrentVersionNumber ?? "1.0.0", null);

            if (r.Manifest is not null)
            {
                Console.WriteLine($"清单通过 ✓ 命中端点：{r.UsedEndpoint}");
                Console.WriteLine($"清单版本 : v{r.Manifest.Version}（发布 {r.Manifest.PubDate:yyyy-MM-dd HH:mm}）");
                Console.WriteLine($"清单说明 : {r.Manifest.Notes}");
                foreach (var (key, asset) in r.Manifest.Assets)
                    Console.WriteLine($"  产物 {key,-12}: {asset.FileName}（{(asset.SizeBytes / 1048576.0):F1} MB）");
            }

            if (r.UpToDate)
                Console.WriteLine(">>> 结论：已是最新，检查/验签链路全部正常。");
            else if (r.Manifest is null)
                Console.WriteLine(">>> 结论：检查失败 —— " + r.Error);
            else
                Console.WriteLine($">>> 结论：可升级到 v{r.Manifest.Version}（本诊断不安装）。");

            if (Program.UpdateCheckDownload && r.Manifest is not null)
            {
                var key = BaoWuLearn.Core.Update.UpdateManifestParser.CurrentPlatformKey();
                if (!r.Manifest.Assets.TryGetValue(key, out var asset))
                {
                    Console.WriteLine($">>> 下载：清单里没有本平台（{key}）产物，跳过。");
                }
                else
                {
                    Console.WriteLine($">>> 下载校验 {asset.FileName}（只验不装）…");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var tmp = Path.Combine(Path.GetTempPath(), "baowu-learn-update", asset.FileName);
                    try
                    {
                        await svc.DownloadVerifiedAsync(r.Manifest, tmp);
                        sw.Stop();
                        Console.WriteLine($">>> sha256 校验通过 ✓ {sw.Elapsed.TotalSeconds:F1} 秒，文件留在 {tmp}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($">>> 下载校验失败 ✖ {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("更新诊断异常：" + ex);
        }
        finally
        {
            Console.WriteLine("==============================================");
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
