using System.Text.Json;

namespace BaoWuLearn.Core.Update;

/// <summary>清单里的单个平台产物。</summary>
public sealed record UpdateAsset(string FileName, string Sha256, long SizeBytes);

/// <summary>解析后的 update.json。</summary>
public sealed record UpdateManifest(
    int Schema,
    string Version,
    DateTimeOffset PubDate,
    string Notes,
    IReadOnlyDictionary<string, UpdateAsset> Assets,
    IReadOnlyList<string> Mirrors);

/// <summary>更新链路上可预期的失败（网络不通/验签不过/格式不对…），Message 直接可展示。</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// update.json 的解析与判定。全部纯函数，自检可以直接断言。
///
/// 清单格式（schema 1）：
/// <code>
/// {
///   "schema": 1,
///   "version": "1.0.41",
///   "pubDate": "2026-09-17T22:00:00+08:00",
///   "notes": "更新说明",
///   "assets": { "win-x64": { "file": "…exe", "sha256": "…", "size": 52428800 },
///               "macos-arm64": { "file": "…zip", "sha256": "…", "size": 50916352 } },
///   "mirrors": ["https://ghfast.top/", "https://ghproxy.it/", "https://gh-proxy.org/", "https://gh-proxy.com/"]
/// }
/// </code>
/// 签名是 detached 的（同目录 <c>update.sig</c>，64 字节 hex），覆盖 update.json 的原始字节 ——
/// 先验签、后解析，顺序不可反：未验签的字节一个字段都不许信。
/// </summary>
public static class UpdateManifestParser
{
    public const string GithubRepo = "ZenCael/baowu-learn-assistant";

    /// <summary>
    /// 清单的 GitHub 直连地址。挂在固定的 <c>latest</c> 滚动 tag 上 ——
    /// 版本号要拿到清单之后才知道，清单不能写死在自己版本的 tag 下（鸡生蛋），
    /// 发布侧每次把 latest tag 指向新 release 即可。
    /// </summary>
    public static string ManifestUrl { get; } =
        $"https://github.com/{GithubRepo}/releases/download/latest/update.json";

    /// <summary>清单同名目录里的 detached 签名（hex 文本）。</summary>
    public static string ManifestSigUrl { get; } =
        $"https://github.com/{GithubRepo}/releases/download/latest/update.sig";

    /// <summary>某 tag 下产物文件的直连地址（tag 取清单里的 version 加 v 前缀）。</summary>
    public static string AssetUrl(string version, string assetFileName) =>
        $"https://github.com/{GithubRepo}/releases/download/v{version}/{assetFileName}";

    /// <summary>当前平台在清单 assets 里的键。</summary>
    public static string CurrentPlatformKey() =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() && Environment.Is64BitProcess && IsArm64() ? "macos-arm64"
        : OperatingSystem.IsMacOS() ? "macos-x64"
        : "linux-x64";

    private static bool IsArm64()
    {
        try
        {
            return System.Runtime.InteropServices.RuntimeInformation
                .OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        }
        catch { return false; }
    }

    /// <summary>
    /// 构造取同一个资产的候选端点链：v1.0.46 起**镜像在前、GitHub 直连兜底**——
    /// 主要用户群在国内，直连常态是黑洞/10KB/s 级，排前面等于每个端点先白等一轮。
    /// 镜像语义是「前缀拼接」：最终 URL = mirror + 直连 URL（公共 gh 代理的通用用法）。
    /// 空/重复前缀会被剔除；非法条目静默忽略（列表来源是用户输入与网络下发，都可能脏）。
    /// </summary>
    public static IReadOnlyList<string> BuildEndpointChain(
        string directUrl, IReadOnlyList<string>? mirrors)
    {
        var chain = new List<string>();
        if (mirrors is not null)
        {
            foreach (var m in mirrors)
            {
                var t = m?.Trim() ?? "";
                if (t.Length == 0 || !t.StartsWith("http", StringComparison.Ordinal)) continue;
                if (!t.EndsWith("/", StringComparison.Ordinal)) t += "/";
                var url = t + directUrl;
                if (!chain.Contains(url, StringComparer.Ordinal)) chain.Add(url);
            }
        }
        chain.Add(directUrl); // 直连永远垫底：镜像全挂时它仍可能是活路（挂代理的用户）
        return chain;
    }

    /// <summary>解析并校验清单结构（不做验签，验签是调用方的事）。</summary>
    public static UpdateManifest Parse(byte[] json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"更新清单不是合法 JSON：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!TryGetInt(root, "schema", out var schema) || schema != 1)
                throw new UpdateException("更新清单 schema 版本不支持（需要 schema 1）");
            var version = TryGetString(root, "version")
                ?? throw new UpdateException("更新清单缺少 version 字段");
            if (Version.TryParse(version, out _))
            {
                // ok
            }
            else
            {
                throw new UpdateException($"更新清单 version 无法解析：{version}");
            }

            DateTimeOffset pubDate = DateTimeOffset.MinValue;
            var pubRaw = TryGetString(root, "pubDate");
            if (pubRaw is not null && !DateTimeOffset.TryParse(pubRaw, out pubDate))
                throw new UpdateException($"更新清单 pubDate 无法解析：{pubRaw}");

            var notes = TryGetString(root, "notes") ?? "";

            var assets = new Dictionary<string, UpdateAsset>(StringComparer.Ordinal);
            if (root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in a.EnumerateObject())
                {
                    var file = TryGetString(p.Value, "file");
                    var sha = TryGetString(p.Value, "sha256");
                    if (file is null || sha is null || sha.Length != 64) continue;
                    long size = 0;
                    if (p.Value.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                        size = sz.GetInt64();
                    assets[p.Name] = new UpdateAsset(file, sha.ToLowerInvariant(), size);
                }
            }

            var mirrors = new List<string>();
            if (root.TryGetProperty("mirrors", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in m.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        mirrors.Add(item.GetString()!);
                }
            }

            return new UpdateManifest(1, version, pubDate, notes, assets, mirrors);
        }
    }

    /// <summary>清单版本是否比当前版本新（语义化比较；解析不了的当前版本一律当旧）。</summary>
    public static bool IsNewer(string manifestVersion, string currentVersion)
    {
        if (!Version.TryParse(manifestVersion, out var m)) return false;
        if (!Version.TryParse(currentVersion, out var c)) return true;
        return m > c;
    }

    /// <summary>
    /// pubDate 防回滚：清单发布时间必须**不早于**上次见过的时间。
    /// 降级攻击的特征就是"签名的旧清单原样重放"——版本号回不去（IsNewer 挡），
    /// 但"重新广播一个旧版清单让你不升级/换产物"要靠这一条。
    /// 时间倒挂（本机时钟被改快过）时放行一次并告警的取舍：这里选择只要不比记忆中旧就放行，
    /// 由调用方把 max(两者) 记回设置，时钟恢复后自然收敛。
    /// </summary>
    public static bool PubDateAccepts(DateTimeOffset candidate, DateTimeOffset? lastSeen) =>
        lastSeen is null || candidate >= lastSeen.Value;

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool TryGetInt(JsonElement el, string name, out int value)
    {
        value = 0;
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
        {
            value = v.GetInt32();
            return true;
        }
        return false;
    }
}
