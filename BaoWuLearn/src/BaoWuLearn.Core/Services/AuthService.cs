using System.Text.Json;
using BaoWuLearn.Core.Crypto;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;

namespace BaoWuLearn.Core.Services;

/// <summary>
/// 登录相关：图形验证码 + 账号密码登录。
///
/// 登录字段中 <c>loginName</c> / <c>password</c> / <c>mobile</c> 做 SM2 加密，
/// 验证码 <c>captchaCode</c> 与 <c>captchaId</c> 明文透传 —— 与网页端行为完全一致；
/// 验证码不 OCR、不绕过，交由使用者肉眼识别。
/// </summary>
public sealed class AuthService
{
    private readonly ApiClient _api;

    public AuthService(ApiClient api) => _api = api;

    /// <summary>
    /// 从 <see cref="ApiEndpoints.RefreshToken"/> 的响应里提取新 token（纯函数：自检要离线验算）。
    ///
    /// ★ 运行时<b>刻意不调用</b>该端点（见 <see cref="ApiEndpoints.RefreshToken"/> 注释：
    ///   死代码接口的调用流量是唯一的审计指纹）。此解析器与配套自检仅作调查结论存档，
    ///   将来若平台前端启用续期、正常流量出现，可零成本接回。
    ///
    /// 端点真实响应形态未知（探测只确认了路由存在、需登录凭证），按网关通用封套
    /// 依次尝试 <c>data</c> 字符串 / <c>data.accessToken</c> / <c>data.jwt</c> / <c>data.token</c> /
    /// <c>jwt</c> 顶层字段；isSuccess 不为 true、或找不到可用 token 一律返回 null。
    /// </summary>
    public static string? TryReadRefreshedToken(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("isSuccess", out var ok) || ok.ValueKind != JsonValueKind.True)
                return null;

            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(data.GetString()))
                    return data.GetString();

                if (data.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "accessToken", "jwt", "token" })
                    {
                        if (data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(v.GetString()))
                            return v.GetString();
                    }
                }
            }

            if (root.TryGetProperty("jwt", out var jwt) && jwt.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(jwt.GetString()))
                return jwt.GetString();
        }
        catch
        {
            // 结构异常一律当没拿到，调用方有兜底
        }

        return null;
    }

    /// <summary>拉取一张新的图形验证码。</summary>
    public async Task<CaptchaData> GetCaptchaAsync(CancellationToken ct = default)
    {
        var doc = await _api.PostRawAsync(ApiEndpoints.CaptchaImage, new { }, withToken: false, ct: ct);
        if (doc is null) throw new ApiException("验证码接口返回空响应");

        using (doc)
        {
            var parsed = ParseCaptcha(doc.RootElement);
            if (string.IsNullOrEmpty(parsed.Image))
                throw new ApiException("验证码响应中未找到图片字段：" + Truncate(doc.RootElement.GetRawText(), 200));
            return parsed;
        }
    }

    /// <summary>
    /// 当验证码以图片 URL 形式返回时，用它拉取原图字节。
    /// 实测平台返回的是裸 base64，此方法作为兼容分支保留。
    /// </summary>
    public async Task<byte[]> GetCaptchaImageBytesAsync(string url, CancellationToken ct = default)
    {
        var absolute = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : ApiEndpoints.Host + (url.StartsWith('/') ? url : "/" + url);

        var bytes = await _api.GetBytesAsync(absolute, ct);
        if (bytes.Length == 0) throw new ApiException("验证码图片为空");
        return bytes;
    }

    /// <summary>账号密码登录。</summary>
    public async Task<LoginResult> LoginAsync(
        string userNo,
        string password,
        string captcha,
        string captchaId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userNo);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // 报文结构严格对齐网页端登录页（账号密码 tab）提交的对象。
        //
        // ⚠ 两处易错点，任一搞错都会收到服务端「请输入验证码」（statusCode 1000）：
        //
        //   1) 字段名是 captchaCode，不是 captchaNum。
        //      网页端表单状态为
        //          { mobile, loginName, password, captchaCode, captchaNum }
        //      其中 **captchaCode 才是图形验证码**（输入框绑定的就是它），
        //      而 captchaNum 是「手机号登录」那条 tab 的短信验证码，账号登录时恒为空串。
        //      服务端按 captchaCode 校验图形验证码，读不到就报「请输入验证码」。
        //
        //   2) 验证码是**明文**提交，不能加密。
        //      网页端的加密函数只重写 loginName / password / mobile 三个字段
        //      （byPassword 走 default 分支：{...e, loginName, password, mobile}），
        //      captchaCode / captchaNum / captchaId 一律原样透传。
        //
        // 上述两条已用真实接口 A/B 实验逐项证实，详见《缺陷修复说明-v1.0.4.md》。
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "byPassword",
            ["clientType"] = "PC",
            ["mobile"] = "",
            ["loginName"] = Sm2Crypto.Encrypt(userNo),
            ["password"] = Sm2Crypto.Encrypt(password),
            ["captchaCode"] = captcha,   // 图形验证码：明文
            ["captchaNum"] = "",         // 短信验证码：账号密码登录恒为空
            ["captchaId"] = captchaId,
        };

        var doc = await _api.PostRawAsync(ApiEndpoints.Login, payload, withToken: false, ct);
        if (doc is null) throw new ApiException("登录接口返回空响应");

        using (doc)
        {
            var root = doc.RootElement;
            if (!ApiResponseReader.IsOk(root))
                throw new ApiException(ApiResponseReader.Message(root) ?? "登录失败");

            var data = ApiResponseReader.Data(root);
            var result = new LoginResult { Raw = data?.Clone() };

            var scope = data ?? root;
            // 平台在根节点也会给 jwt 字段，一并纳入候选
            result.AccessToken = FindString(scope, "accessToken", "token", "access_token", "jwt")
                                 ?? FindString(root, "accessToken", "token", "access_token", "jwt");
            result.UserName = FindString(scope, "userName", "nickName", "realName", "name");
            result.RealName = FindString(scope, "realName", "nickName", "name") ?? result.UserName;

            // 工号必须来自明确字段；平台返回的 userName 是「张三」这类显示名，
            // 不能拿它冒充工号 —— 取不到就回退到用户本次输入的工号。
            result.UserNo = FindString(scope, "userNo", "loginName", "empNo", "employeeNo", "empCode")
                            ?? userNo;

            if (string.IsNullOrEmpty(result.AccessToken))
                throw new ApiException("登录成功但未取到 accessToken");

            _api.Token = result.AccessToken;
            _api.ResetTokenLatch();   // 新令牌到手，解除上一轮的过期闭锁
            return result;
        }
    }

    /// <summary>
    /// 从 JWT 里解析过期时刻（exp，UTC 秒）。公开静态：自检要用合成 token 离线验算。
    /// token 不是 JWT（三段式）或没有 exp 时返回 null —— 此时无法在本地得知有效期，
    /// 只能靠过期检测兜底。
    /// </summary>
    public static DateTimeOffset? TryGetTokenExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            var payload = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("exp", out var exp)
                || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var seconds))
                return null;

            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>base64url 解码（JWT payload），自动补齐 padding。</summary>
    private static string DecodeBase64Url(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    // ── 解析辅助 ──────────────────────────────────────────

    /// <summary>
    /// 递归扫描响应，提取验证码图片与 ID。
    /// 平台可能返回 <c>{code, img, uuid}</c>（RuoYi 风格）
    /// 或 <c>{isSuccess, data:{captchaId, img}}</c>，两种都要兼容。
    /// </summary>
    private static CaptchaData ParseCaptcha(JsonElement root)
    {
        string? id = null;
        string? image = null;

        void Scan(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in el.EnumerateObject())
            {
                var name = prop.Name.ToLowerInvariant();

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var val = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    if (id is null && name is "captchaid" or "uuid" or "captchakey")
                        id = val;

                    if (image is null && name is "img" or "image" or "captchaimage" or "imgbase64" or "base64")
                        image = val;
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    Scan(prop.Value);
                }
            }
        }

        Scan(root);

        var result = new CaptchaData { Id = id, Image = image };
        if (string.IsNullOrWhiteSpace(image)) return result;

        var raw = image.Trim();

        // 形式一：data URI（data:image/png;base64,xxxx）
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = raw.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
            var payload = idx >= 0 ? raw[(idx + 7)..] : raw;

            result.Bytes = TryDecodeBase64(payload);
            result.IsBase64 = result.Bytes is not null;
            result.IsAnimated = IsGif(result.Bytes);
            return result;
        }

        // 形式二：裸 base64。
        // 注意：**不能只看前缀判断**。平台的 base64 常以 '/' 开头
        // （JPEG 的 base64 前缀就是 /9j/…），单看首字符会误判成相对 URL。
        // 正确做法是真正解一次，再用图片魔数确认。
        var decoded = TryDecodeBase64(raw);
        if (decoded is not null && LooksLikeImage(decoded))
        {
            result.Bytes = decoded;
            result.IsBase64 = true;
            result.IsAnimated = IsGif(decoded);
            return result;
        }

        // 形式三：图片 URL（http(s) 绝对地址或 /path 相对地址），由客户端另行拉取原图
        result.IsBase64 = false;
        return result;
    }

    /// <summary>宽容的 base64 解码：自动去空白并补齐 4 字节对齐，失败返回 null。</summary>
    private static byte[]? TryDecodeBase64(string value)
    {
        try
        {
            var b64 = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
                case 1: return null;
            }
            return Convert.FromBase64String(b64);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用图片文件头判断字节内容是否真的是图片。</summary>
    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 4) return false;
        return (b[0] == 0xFF && b[1] == 0xD8)                                     // JPEG
            || (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)      // PNG
            || (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)                      // GIF
            || (b[0] == 0x42 && b[1] == 0x4D)                                      // BMP
            || (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46);     // WEBP(RIFF)
    }

    /// <summary>检测是否为动态 GIF（决定 UI 是否需要逐帧解码）。</summary>
    private static bool IsGif(byte[]? bytes)
        => bytes is { Length: >= 6 } &&
           bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' &&
           bytes[3] == '8' && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a';

    private static string? FindString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
            foreach (var prop in el.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();

        // 向下一层找
        foreach (var prop in el.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var found = FindString(prop.Value, names);
                if (found is not null) return found;
            }

        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
