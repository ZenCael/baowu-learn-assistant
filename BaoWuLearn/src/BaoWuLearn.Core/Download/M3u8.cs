namespace BaoWuLearn.Core.Download;

/// <summary>master 清单里的一路变体（多码率）。</summary>
public sealed record HlsVariant(int Bandwidth, int? Height, string Uri);

/// <summary>#EXT-X-KEY 标签。Method 目前只实现 AES-128；其余按不可解处理。</summary>
public sealed record HlsKey(string Method, string? Uri, byte[]? Iv)
{
    public bool IsAes128 => Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase);
    public bool IsNone => Method.Equals("NONE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>媒体清单里的一个分片（携带其生效的 key）。</summary>
public sealed record HlsSegment(string Uri, HlsKey? Key);

/// <summary>媒体清单解析结果。</summary>
public sealed class HlsMediaPlaylist
{
    /// <summary>EXT-X-MAP init 段（fMP4 流才有）。存在时产物是 MP4，否则是 MPEG-TS。</summary>
    public string? InitSegment { get; init; }
    public List<HlsSegment> Segments { get; } = new();
    public bool EndList { get; init; }
    public bool IsFmp4 => InitSegment is not null;
}

/// <summary>
/// HLS 清单解析（子集实现，覆盖平台视频实际用到的标签；纯函数供自检断言）。
///
/// 平台口径（2026-09-22 静态提取）：入口恒为 index.m3u8（master，多码率，
/// RESOLUTION 标高度）；网页端播放器按 height 升序排、展示选档菜单。
/// 我们默认取最高档。清单是否带 #EXT-X-KEY 属待实测项，解析器先行支持标准 AES-128。
/// </summary>
public static class M3u8
{
    /// <summary>解析 master 清单；返回按 Height、Bandwidth 升序的变体表（空表 = 不是 master）。</summary>
    public static List<HlsVariant> ParseMaster(string text, string baseUri)
    {
        var variants = new List<HlsVariant>();
        var lines = SplitLines(text);

        for (var i = 0; i < lines.Count - 1; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;

            var attrs = ParseAttributes(line.Substring(line.IndexOf(':') + 1));
            var bw = attrs.TryGetValue("BANDWIDTH", out var b) && int.TryParse(b, out var bi) ? bi : 0;
            int? height = null;
            if (attrs.TryGetValue("RESOLUTION", out var res))
            {
                var tail = res.Split('x');
                if (tail.Length == 2 && int.TryParse(tail[1], out var h)) height = h;
            }

            // URI 是不以 # 开头的下一行（规范如此）
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].Length == 0 || lines[j][0] == '#') continue;
                variants.Add(new HlsVariant(bw, height, Combine(baseUri, lines[j])));
                break;
            }
        }

        return variants
            .OrderBy(v => v.Height ?? 0)
            .ThenBy(v => v.Bandwidth)
            .ToList();
    }

    /// <summary>选最高一档（Height 优先、再比 Bandwidth）；空表返回 null。</summary>
    public static HlsVariant? PickHighest(IEnumerable<HlsVariant> variants)
        => variants.OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bandwidth)
                   .FirstOrDefault();

    /// <summary>解析媒体清单（#EXTINF 分片流 + EXT-X-KEY/EXT-X-MAP）。</summary>
    public static HlsMediaPlaylist ParseMedia(string text, string baseUri)
    {
        var playlist = new HlsMediaPlaylistBuilder();
        var lines = SplitLines(text);
        HlsKey? currentKey = null;   // 无 IV 时按序号做 IV 的规则在引擎侧补

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                var attrs = ParseAttributes(line.Substring(line.IndexOf(':') + 1));
                var method = attrs.GetValueOrDefault("METHOD", "NONE");
                var uri = attrs.GetValueOrDefault("URI");
                byte[]? iv = attrs.TryGetValue("IV", out var ivHex) ? ParseHexIv(ivHex) : null;
                currentKey = new HlsKey(method, uri is null ? null : Combine(baseUri, uri), iv);
            }
            else if (line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
            {
                var attrs = ParseAttributes(line.Substring(line.IndexOf(':') + 1));
                if (attrs.TryGetValue("URI", out var mapUri))
                    playlist.Init(Combine(baseUri, mapUri));
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                playlist.EndList();
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                playlist.Segment(Combine(baseUri, line), currentKey);
            }
        }

        return playlist.Build();
    }

    /// <summary>相对地址解析（RFC 3986 简化版：以清单 URL 的目录为基）。</summary>
    public static string Combine(string baseUri, string reference)
    {
        var r = reference.Trim();
        if (r.Length == 0) return r;
        if (r.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            r.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return r;

        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var b)) return r;

        // 根绝对路径（/a/b）挂在主机根下；否则相对清单目录
        return r[0] == '/'
            ? $"{b.Scheme}://{b.Authority}{r}"
            : new Uri(b, r).AbsoluteUri;
    }

    private static List<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n')
               .Split('\n').Select(l => l.Trim()).ToList();

    /// <summary>EXT-X-* 属性表：KEY=VALUE 逗号分隔，URI 值带引号且内部可能有逗号。</summary>
    public static Dictionary<string, string> ParseAttributes(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < s.Length)
        {
            var eq = s.IndexOf('=', i);
            if (eq < 0) break;
            var key = s[i..eq].Trim();

            string value;
            var v = eq + 1;
            if (v < s.Length && s[v] == '"')
            {
                var close = v + 1;
                while (close < s.Length && s[close] != '"') close++;
                value = s[(v + 1)..close];
                i = close + 1;
                var comma = s.IndexOf(',', i);
                i = comma < 0 ? s.Length : comma + 1;
            }
            else
            {
                var comma = s.IndexOf(',', v);
                if (comma < 0) comma = s.Length;
                value = s[v..comma].Trim();
                i = comma + 1;
            }
            dict[key] = value;
        }
        return dict;
    }

    private static byte[]? ParseHexIv(string raw)
    {
        var hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return null;
        var iv = new byte[16];
        for (var i = 0; i < 16; i++) iv[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return iv;
    }

    private sealed class HlsMediaPlaylistBuilder
    {
        private string? _init;
        private bool _endList;
        public HlsMediaPlaylist Build()
        {
            var p = new HlsMediaPlaylist { InitSegment = _init, EndList = _endList };
            foreach (var (uri, key) in _segments) p.Segments.Add(new HlsSegment(uri, key));
            return p;
        }
        private readonly List<(string Uri, HlsKey? Key)> _segments = new();
        public void Init(string uri) => _init = uri;
        public void EndList() => _endList = true;
        public void Segment(string uri, HlsKey? key) => _segments.Add((uri, key));
    }
}
