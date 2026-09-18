using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Core.Update;
using BaoWuLearn.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 自动更新（v1.0.41）—— 主窗口侧的接线。
///
/// 节律：启动 25 秒后首查、之后每 24 小时一查；只在**有新版本**时亮横幅，
/// 绝不自动安装 —— 挂机中覆盖自己 = 把用户正在跑的队列入坑，
/// 「立即更新」按钮只在引擎空闲时可点，下载→校验→换装全程有状态可看。
///
/// 更新端点链与信任模型见 <see cref="UpdateService"/>；UI 这边只负责三件事：
/// 查（后台静默）、显（横幅）、装（用户按下按钮之后）。
/// </summary>
public partial class MainWindowViewModel
{
    private UpdateService? _updateService;
    private DispatcherTimer? _updateTimer;
    private UpdateManifest? _pendingUpdate;

    /// <summary>有新版本可装 → 顶部横幅可见。</summary>
    [ObservableProperty] private bool _updateAvailable;

    /// <summary>横幅主文案（含新版本号）。</summary>
    [ObservableProperty] private string _updateInfoText = "";

    /// <summary>安装进行中（挡重复点击）。</summary>
    [ObservableProperty] private bool _updateBusy;

    /// <summary>下载/换装的即时状态（横幅右侧小字）。</summary>
    [ObservableProperty] private string _updateBusyText = "";

    /// <summary>
    /// 能不能现在就装 —— v1.0.42 起看的是**全池**：只要有任何账号在挂就不装。
    /// Paused 算空闲（那是过期自动暂停，装完重登本来也要人点）。
    /// </summary>
    public bool CanInstallNow => _hub.AllIdle;

    /// <summary>纯版本号（VersionText 去掉 v 前缀；开发版返回 null = 不参与比对）。</summary>
    public string? CurrentVersionNumber =>
        VersionText.StartsWith('v') ? VersionText[1..] : null;

    /// <summary>构造末尾调一次：按设置装好更新定时器。</summary>
    private void InitUpdateChannel()
    {
        var s = Settings.CurrentSettings;
        if (!s.UpdateChecksEnabled) return;

        _updateService = new UpdateService(s.UpdateMirrors);
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateTimer.Tick += async (_, _) => await RunUpdateCheckAsync(manual: false);
        _updateTimer.Start();

        // 启动首查延后 25 秒：避开验证码抓取与皮肤应用，慢网也不至于拖启动。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(25));
            await RunUpdateCheckAsync(manual: false);
        });
    }

    /// <summary>
    /// 跑一次检查。manual=true 时结果写设置页状态栏（设置页手动触发走这里）。
    /// 返回给用户看的结论文本。
    /// </summary>
    public async Task<string> RunUpdateCheckAsync(bool manual)
    {
        var service = _updateService ??= new UpdateService(Settings.CurrentSettings.UpdateMirrors);
        var version = CurrentVersionNumber;
        if (version is null) return "开发版构建，跳过更新检查";

        var s = Settings.CurrentSettings;
        DateTimeOffset? lastSeen =
            DateTimeOffset.TryParse(s.LastUpdatePubDate, out var d) ? d : null;

        try
        {
            var r = await service.CheckAsync(version, lastSeen);

            if (r.Manifest is not null)
            {
                // 见过的清单越新越好（防降级的记忆本身也前进）
                s.LastUpdatePubDate = r.Manifest.PubDate.ToString("O");
                // 用户没配镜像时，采纳清单下发的推荐列表（下次起生效）
                if (s.UpdateMirrors.Count == 0 && r.Manifest.Mirrors.Count > 0)
                {
                    s.UpdateMirrors = [.. r.Manifest.Mirrors];
                    service.Mirrors.Clear();
                    service.Mirrors.AddRange(r.Manifest.Mirrors);
                }
                _settings.Save(s);
            }

            if (r.Manifest is not null && !r.UpToDate)
            {
                _pendingUpdate = r.Manifest;
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateInfoText = $"发现新版本 v{r.Manifest.Version}";
                    UpdateAvailable = true;
                });
                Dispatcher.UIThread.Post(() => Settings.UpdateCheckStatus =
                    $"发现新版本 v{r.Manifest.Version}（源：{HostOf(r.UsedEndpoint)}）");
                Logs.AppendAuto($"更新通道：发现 v{r.Manifest.Version}（当前 {version}），可升级");
                return $"发现新版本 v{r.Manifest.Version}";
            }

            if (r.UpToDate)
            {
                Dispatcher.UIThread.Post(() => Settings.UpdateCheckStatus =
                    $"已是最新版本 v{version}（源：{HostOf(r.UsedEndpoint)}）");
                return $"已是最新版本 v{version}";
            }

            Logs.AppendAuto("更新通道检查失败：" + r.Error);
            return "检查失败：" + r.Error;
        }
        catch (Exception ex)
        {
            Logs.AppendAuto($"更新通道异常：{ex.GetType().Name}: {ex.Message}");
            return $"检查异常：{ex.Message}";
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        UpdateAvailable = false;
        _pendingUpdate = null; // 下个 24h 周期会重新提醒
    }

    /// <summary>横幅「立即更新」：下载 → 校验 → 落地换装脚本 → 退出，脚本接力替换并拉起新版。</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        var manifest = _pendingUpdate;
        if (manifest is null || UpdateBusy) return;

        if (!CanInstallNow)
        {
            UpdateInfoText = "有账号正在挂机，请先停止全部引擎再更新（进度会保留）";
            return;
        }

        UpdateBusy = true;
        UpdateBusyText = "下载新版本…";
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "baowu-learn-update");
            var key = UpdateManifestParser.CurrentPlatformKey();
            if (!manifest.Assets.TryGetValue(key, out var asset))
                throw new UpdateException($"清单里没有本平台（{key}）的产物");

            var file = Path.Combine(dir, asset.FileName);
            await _updateService!.DownloadVerifiedAsync(manifest, file);

            UpdateBusyText = "准备换装…";
            if (OperatingSystem.IsWindows()) FinalizeWindowsUpdate(file);
            else FinalizeMacUpdate(file, manifest.Version);
            // 走到这里说明脚本已启动，马上退出（Save 在 Finalize 里做过了）
            Dispatcher.UIThread.Post(() => Environment.Exit(0));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateBusyText = "更新失败：" + (ex is UpdateException ue ? ue.Message : ex.Message);
                UpdateBusy = false;
            });
            await Task.Delay(5000);
            Dispatcher.UIThread.Post(() => UpdateBusyText = "");
        }
    }

    /// <summary>Windows：单文件 exe 换不了运行中的自己 → .new 交给脚本等进程退出后替换。</summary>
    private void FinalizeWindowsUpdate(string newFile)
    {
        var exe = Environment.ProcessPath
            ?? throw new UpdateException("取不到当前程序路径");
        var script = Path.Combine(Path.GetTempPath(), "baowu-learn-swap.bat");
        File.WriteAllText(script,
            UpdateService.BuildWindowsSwapScript(
                Environment.ProcessId, exe, newFile, UpdateService.SwapLogPath()));
        FlushSettingsBeforeSwap();
        UpdateService.LaunchSwapScript(script);
    }

    /// <summary>
    /// macOS：zip 解到 staging → 核对 Info.plist 版本（防"校验过但装错版本"）→
    /// sh 脚本原子交换 .app。产物 zip 由 publish 的 ditto --keepParent 打包，
    /// 解压根就是 .app 目录本身。
    /// </summary>
    private void FinalizeMacUpdate(string zipFile, string expectVersion)
    {
        var processPath = Environment.ProcessPath
            ?? throw new UpdateException("取不到当前程序路径");
        var cut = processPath.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (cut <= 0)
            throw new UpdateException("当前不是从 .app 包内运行，无法就地更新");
        var appPath = processPath[..(cut + 4)];

        var staging = Path.Combine(Path.GetTempPath(), "baowu-learn-update", "staging");
        Directory.CreateDirectory(staging);
        var ditto = Process.Start(new ProcessStartInfo("ditto")
        {
            ArgumentList = { "-x", "-k", zipFile, staging },
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new UpdateException("ditto 启动失败");
        ditto.WaitForExit(TimeSpan.FromMinutes(2));
        if (ditto.ExitCode != 0)
            throw new UpdateException("解压失败（ditto 退出码 " + ditto.ExitCode + "）");

        var stagedApp = Path.Combine(staging, Path.GetFileName(appPath));
        if (!Directory.Exists(stagedApp))
            throw new UpdateException("zip 里没找到 " + Path.GetFileName(appPath));

        var plist = Path.Combine(stagedApp, "Contents", "Info.plist");
        var plistText = File.ReadAllText(plist);
        if (!plistText.Contains($"<{expectVersion}</string>", StringComparison.Ordinal)
            && !plistText.Contains($">{expectVersion}<", StringComparison.Ordinal))
            throw new UpdateException($"staging 版本与清单不符（期望 {expectVersion}）");

        var script = Path.Combine(Path.GetTempPath(), "baowu-learn-swap.sh");
        File.WriteAllText(script,
            UpdateService.BuildMacSwapScript(
                Environment.ProcessId, appPath, stagedApp, appPath + ".old",
                UpdateService.SwapLogPath()));
        FlushSettingsBeforeSwap();
        UpdateService.LaunchSwapScript(script);
    }

    /// <summary>退出前把设置落盘（镜像列表/lastPubDate 都是检查时改的）。</summary>
    private void FlushSettingsBeforeSwap()
    {
        try { _settings.Save(Settings.CurrentSettings); } catch { /* 尽力而为 */ }
    }

    private static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "未知";
        try { return new Uri(url).Host; } catch { return url; }
    }
}
