using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;

namespace BaoWuLearn.Core.Download;

/// <summary>课件的可下载形态。</summary>
public enum WareSourceKind
{
    /// <summary>没有任何可下载来源（缺字段/平台特有格式）。</summary>
    NotDownloadable,
    /// <summary>HLS 流：目录树带 hashCode，拼 VideoStreamBase/{hashCode}/index.m3u8。</summary>
    HlsStream,
    /// <summary>直链文件：无 hashCode 但有 wareUrl（MP4 等原始文件）。</summary>
    DirectFile,
    /// <summary>预览通道：PDF/附件，先 queryPreviewUrl 拿 fileId 再 downloadFile。</summary>
    PreviewFile,
}

/// <summary>解析结果。Url 对 PreviewFile 只是第一步（预览查询），下载 URL 由引擎二段解析。</summary>
public sealed record WareDownloadSource(
    WareSourceKind Kind,
    string? Url,
    string? SuggestedExtension);

/// <summary>
/// 课件 → 下载来源的决策函数（v1.0.44+）。
///
/// 规则完全照抄网页端播放器（stu bundle index-BFFGkfPu.js，2026-09-22 静态提取）：
/// 视频课件（contentType "1"）有 hashCode 走 HLS、否则 wareUrl 直链；
/// 其余课件走预览-下载通道（fileId）。HLS/直链是零鉴权静态区；
/// 预览通道要 token —— 两通道在引擎侧走不同的 HttpClient 配置。
/// </summary>
public static class WareSourceResolver
{
    /// <summary>视频课件的内容类型值（播放器同款判定）。</summary>
    public const string VideoContentType = "1";

    public static WareDownloadSource Resolve(WareItem w)
    {
        var isVideo = string.Equals(w.ContentType ?? w.WareType, VideoContentType,
                                   StringComparison.Ordinal);

        if (isVideo)
        {
            if (!string.IsNullOrWhiteSpace(w.HashCode))
                return new WareDownloadSource(WareSourceKind.HlsStream,
                    HlsPlaylistUrl(w.HashCode!), ".ts");

            if (!string.IsNullOrWhiteSpace(w.WareUrl))
                return new WareDownloadSource(WareSourceKind.DirectFile,
                    DirectUrl(w.WareUrl!), SuggestExtension(w.WareUrl!));

            return new WareDownloadSource(WareSourceKind.NotDownloadable, null, null);
        }

        // PDF/附件：预览通道的钥匙是 wareCode
        return string.IsNullOrWhiteSpace(w.WareId)
            ? new WareDownloadSource(WareSourceKind.NotDownloadable, null, null)
            : new WareDownloadSource(WareSourceKind.PreviewFile, null, null);
    }

    /// <summary>HLS 主清单地址（零鉴权静态区）。</summary>
    public static string HlsPlaylistUrl(string hashCode)
        => $"{ApiEndpoints.VideoStreamBase}/{hashCode.Trim('/')}/index.m3u8";

    /// <summary>直链地址：wareUrl 是相对路径，补网关根。</summary>
    public static string DirectUrl(string wareUrl)
    {
        var trimmed = wareUrl.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;   // 极少数绝对地址原样放行
        return ApiEndpoints.Host + (trimmed.StartsWith('/') ? trimmed : "/" + trimmed);
    }

    /// <summary>预览查询报文体（{businessNo: wareCode}，网页端同款）。</summary>
    public static object PreviewPayload(string wareCode) => new { businessNo = wareCode };

    /// <summary>按 fileId 拼文件下载地址（要 token）。</summary>
    public static string FileDownloadUrl(string fileId)
        => $"{ApiEndpoints.FileDownload}?fileId={Uri.EscapeDataString(fileId)}";

    /// <summary>从 URL 尾段猜扩展名（去掉 query）；猜不出返回 null。</summary>
    public static string? SuggestExtension(string urlOrPath)
    {
        var path = urlOrPath;
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.Length is < 2 or > 6) return null;
        // 只接受字母数字扩展名，挡掉畸形路径
        return ext[1..].All(char.IsLetterOrDigit) ? ext : null;
    }

    /// <summary>
    /// 文件名片名：去非法字符（Windows/POSIX 交集口径）、去首尾点与空格、限长、兜底。
    /// 课程/课件名直接做磁盘文件名是坑（斜杠、引号、冒号都在标题里出现过）。
    /// </summary>
    public static string SanitizeFileName(string? raw, string fallback = "未命名")
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0) name = fallback;

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch switch
            {
                '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' => '_',
                '\t' or '\r' or '\n' => ' ',
                _ => ch,
            });
        }
        name = sb.ToString().Trim('.', ' ');
        if (name.Length == 0) name = fallback;

        // 文件系统单名 200 字节内安全；中文按 3 字节/字保守截
        const int maxChars = 80;
        if (name.Length > maxChars) name = name[..maxChars].Trim('.', ' ');
        return name;
    }
}
