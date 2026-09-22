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
        ReportSwapOutcome();
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
        Logs.AppendAuto("更新通道：开始下载产物，沿端点链（镜像在前）尝试");
        // Progress<T> 在 UI 线程 new → 回调自动回 UI；服务端已按 ≥2Hz 节流
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = p.Total > 0 ? (int)(p.Received * 100 / p.Total) : 0;
            UpdateBusyText = $"下载中 {p.Received / 1048576.0:F1}/{p.Total / 1048576.0:F1} MB · {pct}% · {p.BytesPerSecond / 1024} KB/s · {p.Endpoint}";
        });
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "baowu-learn-update");
            var key = UpdateManifestParser.CurrentPlatformKey();
            if (!manifest.Assets.TryGetValue(key, out var asset))
                throw new UpdateException($"清单里没有本平台（{key}）的产物");

            var file = Path.Combine(dir, asset.FileName);
            await _updateService!.DownloadVerifiedAsync(manifest, file, progress);

            UpdateBusyText = "准备换装…";
            if (OperatingSystem.IsWindows()) FinalizeWindowsUpdate(file);
            else FinalizeMacUpdate(file, manifest.Version);
            // 走到这里说明脚本已启动，马上退出（Save 在 Finalize 里做过了）
            Dispatcher.UIThread.Post(() => Environment.Exit(0));
        }
        catch (Exception ex)
        {
            var msg = ex is UpdateException ue ? ue.Message : ex.Message;
            // v1.0.46：失败文本常驻不蒸发（旧版 5 秒后清空，用户以为程序死了），
            // 且记进运行日志可回看；断点已保留，再点「立即更新」会 Range 续传。
            Logs.AppendAuto($"更新通道：安装失败——{msg}（断点已保留，再点「立即更新」从断处续传）");
            Dispatcher.UIThread.Post(() =>
            {
                UpdateBusyText = "更新失败：" + msg + "（再点「立即更新」从断点续传）";
                UpdateBusy = false;
            });
        }
    }

    /// <summary>
    /// Windows：单文件 exe 换不了运行中的自己 → 交给 PowerShell 等进程退出后替换。
    /// v1.0.43 起不落 bat 文件（cmd 按 OEM 代码页解析 UTF-8 会把中文路径整成乱码，
    /// 换装静默失败 = 用户眼中的"更新后闪退"），改走 -EncodedCommand 直传。
    /// </summary>
    private void FinalizeWindowsUpdate(string newFile)
    {
        var exe = Environment.ProcessPath
            ?? throw new UpdateException("取不到当前程序路径");
        FlushSettingsBeforeSwap();
        UpdateService.LaunchWindowsSwap(Environment.ProcessId, exe, newFile);
    }

    /// <summary>
    /// macOS：zip 解到 staging → 核对 Info.plist 版本（防"校验过但装错版本"）→
    /// sh 脚本原子交换 .app。产物 zip 由 publish 的 ditto --keepParent 打包，
    /// 解压根就是 .app 目录本身。
    /// </summary>
    private void FinalizeMacUpdate(string zipFile, string expectVersion)
    {
        if (!OperatingSystem.IsMacOS())
            throw new UpdateException("mac 换装通道仅在 macOS 上可用");
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
        UpdateService.LaunchMacSwapScript(script);
    }

    /// <summary>退出前把设置落盘（镜像列表/lastPubDate 都是检查时改的）。</summary>
    private void FlushSettingsBeforeSwap()
    {
        try { _settings.Save(Settings.CurrentSettings); } catch { /* 尽力而为 */ }
    }

    /// <summary>
    /// 启动时消费上一轮的换装日志：成功只记一笔，失败必须大声——
    /// 写日志页 + 设置页状态条，并告知旧版已回滚、可直接重试。
    /// v1.0.42 之前换装失败是静默的（用户视角=闪退），这条是补的哨兵。
    /// </summary>
    private void ReportSwapOutcome()
    {
        try
        {
            var p = UpdateService.SwapLogPath();
            if (!File.Exists(p)) return;
            var text = File.ReadAllText(p).Trim();
            try { File.Delete(p); } catch { /* 清不掉最多下次重报，不碍事 */ }
            var first = text.Split('\n')[0].Trim();
            if (first.Contains("swap-ok"))
            {
                Logs.AppendAuto("更新通道：上次换装成功");
                return;
            }
            Logs.AppendAuto($"更新通道：上次换装未成功（{first}），旧版已自动回滚，" +
                            "点横幅「立即更新」可重试；反复失败请手动下载新版覆盖");
            Dispatcher.UIThread.Post(() => Settings.UpdateCheckStatus =
                $"上次更新换装未成功：{first}（旧版已回滚，可重试）");
        }
        catch
        {
            // 汇报是附属功能，任何异常都不能拦启动
        }
    }

    private static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "未知";
        try { return new Uri(url).Host; } catch { return url; }
    }
}
