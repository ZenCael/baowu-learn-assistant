using System.Net;
using System.Text;
using System.Text.Json;

namespace BaoWuLearn.Core.Http;

/// <summary>
/// 平台 HTTP 客户端。
///
/// 设计要点：
///  - 全部请求走 <see cref="ApiEndpoints.Host"/>，通过 JSON 交换数据，不加载任何平台页面。
///  - 鉴权靠请求头 <c>token</c>（登录后从响应取得）。
///  - 保留 CookieContainer，验证码会话（captchaId 与 Cookie 绑定）才能贯通。
///  - 统一抛出 <see cref="ApiException"/>，并对外广播日志事件供界面展示。
///  - 请求构造集中在这里（<see cref="CreateRequest"/>），避免各处重复拼装产生不一致。
/// </summary>
public sealed class ApiClient : IDisposable
{
    /// <summary>
    /// 请求媒体类型。
    /// 注意：**只能写纯媒体类型**。写成 <c>"application/json;charset=UTF-8"</c> 会让
    /// <see cref="System.Net.Http.StringContent"/> 在 .NET 10 上直接抛 FormatException，
    /// 且异常发生在请求发出之前 —— 表现是"所有接口静默失败"。
    /// 编码由 <c>StringContent</c> 的 <see cref="Encoding"/> 参数负责，
    /// 它会自动补出 <c>application/json; charset=utf-8</c>。
    /// </summary>
    private const string JsonMediaType = "application/json";

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private bool _disposed;

    /// <summary>登录成功后取得的访问令牌，后续请求自动携带。</summary>
    public string? Token { get; set; }

    /// <summary>请求日志广播（供 UI 日志页订阅）。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 登录过期广播。平台把「token 过期」表达在 <b>HTTP 200 的响应体</b>里
    /// （实测结算接口返回 200 + isSuccess:false + message:"token过期"），
    /// 只看状态码永远发现不了 —— 挂机会空转几个小时，一个字的学时都记不进去。
    /// 任何带鉴权的响应体一旦命中过期特征就触发一次；重新登录成功后闭锁复位。
    /// </summary>
    public event Action<string>? TokenExpired;

    /// <summary>过期闭锁：触发过一次就不再重复触发，直到重新登录。</summary>
    private bool _expiredLatched;

    /// <summary>重新登录成功后调用，解除过期闭锁。</summary>
    public void ResetTokenLatch() => _expiredLatched = false;

    public ApiClient()
    {
        _cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiEndpoints.Host),
            Timeout = TimeSpan.FromSeconds(30),
        };

        // 与网页端一致的请求指纹，避免被网关按 UA 直接拒绝。
        // ★ UA 版本要跟当前 Chrome 稳定版保持量级（v1.0.29 起对齐 Chrome 152，2026-08 稳定版）：
        //   旧版报 Chrome/131（2024-11）——同一个账号平时浏览器用最新版，客户端却报两年前的
        //   版本，在"按账号聚合 UA"的审计里是个薄弱点。每次 Chrome 大版本更新后顺手抬一位。
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", ApiEndpoints.Host);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", ApiEndpoints.Host + "/");

        // 真实 Chrome 对每个 XHR/fetch 都会自动带上 Client Hints 与 Sec-Fetch 头，
        // 声称是 Chrome 却不带这些头，与 UA 本身一样是可聚合的异常。逐条对齐：
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
            "\"Google Chrome\";v=\"152\", \"Chromium\";v=\"152\", \"Not_A Brand\";v=\"24\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
    }

    // ── 请求构造（唯一入口） ──────────────────────────────

    private HttpRequestMessage CreateRequest(string url, object? payload, bool withToken)
    {
        var body = payload is null ? "{}" : JsonSerializer.Serialize(payload, JsonOpts);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, JsonMediaType),
        };
        req.Headers.TryAddWithoutValidation("clientid", "pc");
        if (withToken && !string.IsNullOrEmpty(Token))
            req.Headers.TryAddWithoutValidation("token", Token);
        return req;
    }

    // ── 公开请求方法 ──────────────────────────────────────

    /// <summary>POST 并返回原始 JSON 文档（由调用方决定字段布局，兼容平台的多套响应结构）。</summary>
    public async Task<JsonDocument?> PostRawAsync(string url, object? payload, bool withToken = true, CancellationToken ct = default)
    {
        using var req = CreateRequest(url, payload, withToken);
        var text = await SendAsync(req, ct);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new ApiException($"响应不是合法 JSON：{Truncate(text, 200)}");
        }
    }

    /// <summary>POST 并返回文本（用于非 JSON 响应）。</summary>
    public async Task<string> PostTextAsync(string url, object? payload, bool withToken = true, CancellationToken ct = default)
    {
        using var req = CreateRequest(url, payload, withToken);
        return await SendAsync(req, ct);
    }

    /// <summary>POST 并返回二进制（一般用不到，保留给非 JSON 响应）。</summary>
    public async Task<byte[]> PostBytesAsync(string url, object? payload, bool withToken = true, CancellationToken ct = default)
    {
        using var req = CreateRequest(url, payload, withToken);
        using var resp = await SendCoreAsync(req, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>GET 文本。</summary>
    public async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(Token))
            req.Headers.TryAddWithoutValidation("token", Token);
        return await SendAsync(req, ct);
    }

    /// <summary>GET 二进制（用于验证码以图片 URL 形式返回时拉取原图）。</summary>
    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(Token))
            req.Headers.TryAddWithoutValidation("token", Token);
        using var resp = await SendCoreAsync(req, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // ── 发送与异常归一 ────────────────────────────────────

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await SendCoreAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        CheckTokenExpiry(text);
        return text;
    }

    /// <summary>
    /// 扫描响应体里的「登录过期」特征（公开静态：自检要离线验算）。
    ///
    /// 特征取自真实报文与网关通用风格，刻意保守 —— 只匹配过期语义，
    /// 不做模糊推断，避免把正常业务数据误判成过期。
    /// </summary>
    public static bool LooksLikeTokenExpiry(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length > 64 * 1024) text = text[..(64 * 1024)];   // 防御超大响应

        foreach (var kw in new[]
        {
            "token过期", "token已过期", "token 已过期", "token失效", "token 失效",
            "登录已过期", "登录过期", "登录状态已失效", "认证失败", "身份验证失败",
            "请重新登录", "未登录",
        })
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>命中过期特征时广播事件（一次闭锁只广播一次）。</summary>
    private void CheckTokenExpiry(string text)
    {
        if (_expiredLatched || !LooksLikeTokenExpiry(text)) return;

        _expiredLatched = true;
        Log?.Invoke("⛔ 检测到登录已过期（平台在 HTTP 200 响应体里返回的）");
        TokenExpired?.Invoke("token过期");
    }

    /// <summary>
    /// 真正发出请求。**所有**网络层异常都会归一为 <see cref="ApiException"/>，
    /// 调用方只需捕获这一种异常即可，不会有意料之外的异常逃逸到 UI 线程。
    /// </summary>
    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            sw.Stop();
            Log?.Invoke($"{(int)resp.StatusCode} {ShortUrl(req.RequestUri)} ({sw.ElapsedMilliseconds}ms)");

            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 401)
                Log?.Invoke($"⚠ 非 2xx 响应：HTTP {(int)resp.StatusCode}");

            return resp;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new ApiException($"请求超时（{ShortUrl(req.RequestUri)}）");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            // 兜底：任何其它异常也不允许穿透到界面层
            throw new ApiException($"请求失败（{ShortUrl(req.RequestUri)}）：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static string ShortUrl(Uri? uri)
        => uri is null ? "?" : uri.AbsolutePath.Replace("/learn-gateway/service/", "…/");

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

/// <summary>接口调用异常。</summary>
public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
    public ApiException(string message, Exception inner) : base(message, inner) { }
}
