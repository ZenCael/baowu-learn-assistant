using System.Diagnostics;
using System.Security.Cryptography;

namespace BaoWuLearn.Core.Update;

/// <summary>一次检查的结论。Manifest 非空 = 有新版本可取。</summary>
public sealed record UpdateCheckResult(
    UpdateManifest? Manifest,
    string? UsedEndpoint,
    bool UpToDate,
    string? Error);

/// <summary>
/// 自动更新服务（v1.0.41）。
///
/// 设计口径（与「不新增身份」的约束一致）：
/// ① 源只有 GitHub Release（匿名仓库），镜像是「前缀拼接」式公共反代，可随时增删；
/// ② 一切信任建立在 ed25519 验签上：先验签后解析，未验签的字节一个字段都不信；
///    所以镜像只可能让我们"下载失败"，不可能让我们"装上坏东西"或"停在旧版本"；
/// ③ 本类的 HttpClient 与平台网关那套完全独立——不带平台请求头指纹，
///    也不把 GitHub 的头带给平台。UA 用中性值，不泄露更多环境信息；
/// ④ 跟随系统代理：国内用户十有八九本来挂着代理，走系统代理 = 更新直接满速；
/// ⑤ 只下载与校验，**安装永远由调用方在引擎空闲时触发**——挂机中绝不覆盖自己。
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>当前生效的镜像前缀列表（调用方从设置注入；检查成功后可用清单下发的更新）。</summary>
    public List<string> Mirrors { get; }

    public UpdateService(IReadOnlyList<string>? mirrors = null)
    {
        Mirrors = mirrors is null ? [] : [.. mirrors];
        var handler = new HttpClientHandler { UseProxy = true };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // 中性 UA：不冒充浏览器（这不是业务接口，没必要伪装），也不带个人信息
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BaoWuLearnUpdater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    // ───────────────────────────── 检查 ─────────────────────────────

    /// <summary>
    /// 沿端点链取 update.json + update.sig（同一端点成对取，防混源拼接），
    /// 验签 → 解析 → pubDate 防回滚 → 版本比较。全链失败才返回 Error。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        DateTimeOffset? lastSeenPubDate,
        CancellationToken ct = default)
    {
        var chain = UpdateManifestParser.BuildEndpointChain(
            UpdateManifestParser.ManifestUrl, Mirrors);
        string? lastError = null;

        foreach (var jsonUrl in chain)
        {
            try
            {
                var sigUrl = jsonUrl.EndsWith("update.json", StringComparison.Ordinal)
                    ? jsonUrl[..^"update.json".Length] + "update.sig"
                    : jsonUrl + ".sig";

                var jsonBytes = await GetBytesAsync(jsonUrl, ct).ConfigureAwait(false);
                var sigBytes = await GetBytesAsync(sigUrl, ct).ConfigureAwait(false);
                var sigHex = System.Text.Encoding.UTF8.GetString(sigBytes).Trim();

                if (!Ed25519Verifier.VerifyWithReleaseKey(jsonBytes, sigHex))
                {
                    // 镜像被篡改/伪造的典型特征。继续降级：可能是这个节点坏了，
                    // 下一跳（含直连）会拿到真货。
                    lastError = $"端点返回的清单验签失败（该节点可能篡改了内容）：{HostOf(jsonUrl)}";
                    continue;
                }

                var manifest = UpdateManifestParser.Parse(jsonBytes);

                if (!UpdateManifestParser.PubDateAccepts(manifest.PubDate, lastSeenPubDate))
                {
                    return new UpdateCheckResult(null, jsonUrl, false,
                        $"清单发布时间({manifest.PubDate:yyyy-MM-dd})早于已记录版本，疑似降级广播，已忽略");
                }

                if (!UpdateManifestParser.IsNewer(manifest.Version, currentVersion))
                    return new UpdateCheckResult(manifest, jsonUrl, true, null);

                return new UpdateCheckResult(manifest, jsonUrl, false, null);
            }
            // 只有调用方主动取消才往上抛；HttpClient 超时抛的 TaskCanceledException
            // （派生自 OCE 但 ct 并未取消）要当作端点失败继续降级，交给最后的兜底 catch。
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (UpdateException ex)
            {
                lastError = $"{HostOf(jsonUrl)}：{ex.Message}";
            }
            catch (HttpRequestException ex)
            {
                lastError = $"{HostOf(jsonUrl)}：{(ex.InnerException?.Message is { Length: > 0 } im ? im : ex.Message)}";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"{HostOf(jsonUrl)}：请求超时";
            }
            catch (Exception ex)
            {
                lastError = $"{HostOf(jsonUrl)}：{ex.GetType().Name}: {ex.Message}";
            }
        }

        return new UpdateCheckResult(null, null, false, lastError ?? "所有端点均不可达");
    }

    // ───────────────────────────── 下载 ─────────────────────────────

    /// <summary>
    /// 下载当前平台产物并强校验（sha256 + 尺寸），返回可信的本地临时文件路径。
    /// 校验不过沿端点链换节点重试；全链不过抛 <see cref="UpdateException"/>。
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(
        UpdateManifest manifest, string tempFilePath, CancellationToken ct = default)
    {
        var key = UpdateManifestParser.CurrentPlatformKey();
        if (!manifest.Assets.TryGetValue(key, out var asset))
            throw new UpdateException($"清单里没有本平台（{key}）的产物");

        var directUrl = UpdateManifestParser.AssetUrl(manifest.Version, asset.FileName);
        var chain = UpdateManifestParser.BuildEndpointChain(directUrl, Mirrors);
        string? lastError = null;

        foreach (var url in chain)
        {
            var ok = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath)!);
                await using (var fs = new FileStream(
                                 tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var rsp = await _http
                                 .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                 .ConfigureAwait(false))
                {
                    rsp.EnsureSuccessStatusCode();
                    await rsp.Content.CopyToAsync(fs, null, ct).ConfigureAwait(false);
                }

                var actual = await Sha256OfFileAsync(tempFilePath, ct).ConfigureAwait(false);
                if (string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    ok = true;
                else
                    lastError = $"{HostOf(url)}：sha256 不符（期望 {asset.Sha256[..12]}…，实际 {actual[..12]}…）";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = $"{HostOf(url)}：{ex.Message}";
            }

            if (ok) return tempFilePath;
            TryDelete(tempFilePath);
        }

        throw new UpdateException("产物下载校验全部失败：" + (lastError ?? "无可用端点"));
    }

    // ───────────────────────── 换装脚本（纯函数，可自检） ─────────────────────────

    /// <summary>
    /// Windows 换装脚本（v1.0.43 起为纯 PowerShell 文本，替代 v1.0.41 的 bat 模板）。
    ///
    /// 为什么弃用 bat：bat 落盘是 UTF-8，而 cmd 按**系统 OEM 代码页**（中文环境 GBK）
    /// 逐行解析——路径一旦含中文（%TEMP% 里的中文用户名、中文目录），整段乱码，
    /// 换装静默失败而主进程已退 → 用户看到的就是"点更新后闪退、新版本没来"。
    /// 本方法只产出脚本字符串，路径经 <see cref="PsQuote"/> 转义进 PS 单引号字面量，
    /// 由 <see cref="LaunchWindowsSwap"/> 以 -EncodedCommand（UTF-16LE）送达，
    /// 磁盘上不存在任何承载路径的中间文件，没有代码页能参与进来。
    ///
    /// 语义不变：等本进程退出 → 旧版挪 <c>.bak</c> → 新版上位 → 拉起 →
    /// 成功/失败（含回滚）写日志。
    /// </summary>
    public static string BuildWindowsSwapCommand(
        int pid, string targetExe, string newFile, string logPath)
    {
        var t = PsQuote(targetExe);
        var bak = PsQuote(targetExe + ".bak");
        var n = PsQuote(newFile);
        var l = PsQuote(logPath);
        return "$ErrorActionPreference='Stop';"
            + "try { while (Get-Process -Id " + pid + " -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 800 }; "
            + "Start-Sleep -Milliseconds 1500; "
            + "if (Test-Path " + bak + ") { Remove-Item -Force " + bak + " }; "
            + "Move-Item -Force " + t + " " + bak + "; "
            + "try { Move-Item -Force " + n + " " + t + "; Start-Process -FilePath " + t + "; "
            + "'swap-ok' | Out-File -Encoding utf8 " + l + " } "
            + "catch { Move-Item -Force " + bak + " " + t + "; "
            + "'swap-fail-rolledback: ' + $_ | Out-File -Encoding utf8 " + l + "; throw } } "
            + "catch { 'fatal: ' + $_ | Out-File -Encoding utf8 " + l + " }";
    }

    /// <summary>
    /// 启动 Windows 换装：powershell.exe -EncodedCommand 是唯一可靠送 Unicode 参数
    /// 进 PowerShell 的方式（命令行参数走 CreateProcess 的 UTF-16，绕开一切代码页）。
    /// 脚本必须能活过本进程——所以调用方拿到成功返回后应立刻自行退出。
    /// </summary>
    public static void LaunchWindowsSwap(int pid, string targetExe, string newFile)
    {
        var encoded = EncodePowerShell(BuildWindowsSwapCommand(pid, targetExe, newFile, SwapLogPath()));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Process.Start(psi) is null)
            throw new UpdateException("换装进程启动失败（powershell 返回了空进程）");
    }

    /// <summary>PS 脚本 → -EncodedCommand 载荷（UTF-16LE 的 Base64）。自检也走它保证同源。</summary>
    public static string EncodePowerShell(string script) =>
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    /// <summary>PS 单引号字面量转义：内部单引号翻倍，其余原样（路径里出现什么都无所谓）。</summary>
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// macOS 换装：等主进程退出 → 旧 .app 挪 <c>.old</c> → staging 上位 → 重新打开 → 删脚本。
    /// 用 mv 成对交换（同卷 rename 原子），任何一步失败都回滚 .old 并留日志。
    /// </summary>
    public static string BuildMacSwapScript(
        int pid, string appPath, string stagingApp, string oldPath, string logPath)
    {
        return $$"""
            #!/bin/sh
            # BaoWuLearn updater (v1.0.41+) — 原子交换 .app，失败回滚
            while kill -0 {{pid}} 2>/dev/null; do sleep 0.5; done
            sleep 1.5
            echo "$(date -u +%FT%TZ) swap-start" >> "{{logPath}}"
            if [ -e "{{oldPath}}" ]; then rm -rf "{{oldPath}}"; fi
            if mv "{{appPath}}" "{{oldPath}}" && mv "{{stagingApp}}" "{{appPath}}"; then
                xattr -dr com.apple.quarantine "{{appPath}}" 2>/dev/null
                open "{{appPath}}"
                echo "$(date -u +%FT%TZ) swap-ok" >> "{{logPath}}"
                rm -rf "{{oldPath}}" "{{stagingApp}}.zip"
            else
                [ -e "{{oldPath}}" ] && mv "{{oldPath}}" "{{appPath}}"
                echo "$(date -u +%FT%TZ) swap-fail-rolledback" >> "{{logPath}}"
            fi
            rm -f "$0"

            """;
    }

    /// <summary>
    /// macOS：落地并分离启动 sh 换装脚本（脚本必须能活过本进程）。
    /// 调用方拿到成功后立刻自行退出，把舞台交给脚本。
    /// Windows 不走这里——见 <see cref="LaunchWindowsSwap"/>（无脚本文件，防代码页乱码）。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macOS")]
    public static void LaunchMacSwapScript(string scriptPath)
    {
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var psi = new ProcessStartInfo(scriptPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Process.Start(psi) is null)
            throw new UpdateException("换装脚本启动失败（返回了空进程）");
    }

    /// <summary>本次安装的日志文件路径（脚本失败/成功都会写它）。</summary>
    public static string SwapLogPath() => Path.Combine(Path.GetTempPath(), "baowu-learn-swap.log");

    // ───────────────────────────── 工具 ─────────────────────────────

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var rsp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        rsp.EnsureSuccessStatusCode();
        return await rsp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
#if NET
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
#else
        var hash = SHA256.HashData(fs);
#endif
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 清不掉不影响主流程 */ }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>SHA256 对流的哈希（BCL 没有异步版，补一个够用的）。</summary>
file static class HashStreamExtensions
{
    public static async Task<byte[]> ComputeHashAsync(
        this IncrementalHash hash, Stream stream, CancellationToken ct)
    {
        var buf = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            hash.AppendData(buf, 0, n);
        return hash.GetHashAndReset();
    }
}
