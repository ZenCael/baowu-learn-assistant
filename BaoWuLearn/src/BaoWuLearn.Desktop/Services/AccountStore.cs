using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaoWuLearn.Desktop.Services;

/// <summary>本地保存的登录账号。</summary>
public sealed class SavedAccount
{
    /// <summary>员工号（登录时填在"员工号"里的那个）。</summary>
    public string UserNo { get; set; } = "";

    /// <summary>上次登录成功拿到的显示名（"学员甲"这种），下拉列表里更容易认。</summary>
    public string? DisplayName { get; set; }

    /// <summary>受保护的密码；未保存密码时为 null。</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>最近一次登录时间（用于把常用账号排前面）。</summary>
    public string? LastLoginAt { get; set; }
}

/// <summary>
/// 登录账号的本地存档（记住账户 / 保存密码 / 多账号切换）。
///
/// 独立于 settings.json 单独成文件：登录页与设置页都要读写它，
/// 共用一份 settings 实例容易互相覆盖，这里让它成为唯一持有者。
///
/// <b>密码处理</b>：不存明文，用 AES 加密后再落盘，密钥由「本机名 + 系统用户名」派生。
/// 这挡的是「顺手翻看配置文件就能看到密码」，不是有备而来的攻击者 ——
/// 因此界面上会明确标注"密码保存在本机、可在设置里清除"，交给使用者自己决定要不要开。
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly List<SavedAccount> _accounts = new();
    private readonly object _sync = new();

    /// <summary>是否使用默认配置目录（决定要不要做老版本 settings.json 的迁移）。</summary>
    private readonly bool _isDefaultLocation;

    private AccountFile _file = new();

    /// <param name="pathOverride">
    /// 存档文件路径。默认放用户配置目录；自检传临时文件，避免碰用户真实数据。
    /// </param>
    public AccountStore(string? pathOverride = null)
    {
        _isDefaultLocation = string.IsNullOrWhiteSpace(pathOverride);

        if (!_isDefaultLocation)
        {
            _path = pathOverride!;
            var overrideDir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(overrideDir)) Directory.CreateDirectory(overrideDir);
        }
        else
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BaoWuLearn");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "accounts.json");
        }

        Reload();
    }

    public string StorePath => _path;

    /// <summary>是否记住登录过的账号（关掉后下拉列表不再累积）。</summary>
    public bool RememberAccount { get; private set; } = true;

    /// <summary>是否保存密码。</summary>
    public bool SavePassword { get; private set; }

    /// <summary>已保存的账号，最近登录的排在前面。</summary>
    public IReadOnlyList<SavedAccount> Accounts
    {
        get { lock (_sync) return _accounts.ToList(); }
    }

    /// <summary>最近登录过的账号（用于首次打开时预填）。</summary>
    public SavedAccount? LastAccount
    {
        get
        {
            lock (_sync)
                return _accounts
                    .OrderByDescending(a => a.LastLoginAt ?? "", StringComparer.Ordinal)
                    .FirstOrDefault();
        }
    }

    /// <summary>取某个账号已保存的密码（没有或解密失败返回 null）。</summary>
    public string? PasswordFor(string userNo)
    {
        lock (_sync)
        {
            var acc = _accounts.FirstOrDefault(a => a.UserNo == userNo);
            return LocalSecret.Unprotect(acc?.ProtectedPassword);
        }
    }

    public void SetRememberAccount(bool value)
    {
        RememberAccount = value;
        Persist();
    }

    /// <summary>
    /// 开关"保存密码"。关闭时**顺手清掉已经存下的密码** ——
    /// 否则关了开关密码还躺在配置文件里，与界面上「不保存密码」的说法不符。
    /// </summary>
    public void SetSavePassword(bool value)
    {
        SavePassword = value;
        if (!value)
            lock (_sync)
                foreach (var a in _accounts) a.ProtectedPassword = null;
        Persist();
    }

    /// <summary>
    /// 登录成功后记录这个账号。
    /// <paramref name="password"/> 为 null 或未开启"保存密码"时不写密码。
    /// </summary>
    public void Upsert(string userNo, string? displayName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userNo)) return;

        lock (_sync)
        {
            var acc = _accounts.FirstOrDefault(a => a.UserNo == userNo);
            if (acc is null)
            {
                acc = new SavedAccount { UserNo = userNo };
                _accounts.Add(acc);
            }

            if (!string.IsNullOrWhiteSpace(displayName)) acc.DisplayName = displayName;
            acc.LastLoginAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (SavePassword && !string.IsNullOrEmpty(password))
                acc.ProtectedPassword = LocalSecret.Protect(password);
            else
                acc.ProtectedPassword = null;   // 没开保存密码 → 确保不留残留
        }

        Persist();
    }

    public void Remove(string userNo)
    {
        lock (_sync) _accounts.RemoveAll(a => a.UserNo == userNo);
        Persist();
    }

    /// <summary>清掉所有已保存的密码（保留账号本身）。</summary>
    public void ClearPasswords()
    {
        lock (_sync)
            foreach (var a in _accounts) a.ProtectedPassword = null;
        Persist();
    }

    private void Reload()
    {
        try
        {
            if (File.Exists(_path))
            {
                _file = JsonSerializer.Deserialize<AccountFile>(File.ReadAllText(_path), JsonOpts)
                        ?? new AccountFile();
            }
            else
            {
                _file = new AccountFile();
                // 从老版本 settings.json 迁移"记住的员工号"，别让老用户白登一次。
                // 只在默认路径下做 —— 临时存档（自检）不该去读真实配置。
                if (_isDefaultLocation)
                {
                    var legacy = ReadLegacyUserNo();
                    if (!string.IsNullOrWhiteSpace(legacy))
                        _file.Accounts.Add(new SavedAccount { UserNo = legacy });
                }
            }
        }
        catch
        {
            _file = new AccountFile();
        }

        RememberAccount = _file.RememberAccount;
        SavePassword = _file.SavePassword;
        _accounts.Clear();
        _accounts.AddRange(_file.Accounts.Where(a => !string.IsNullOrWhiteSpace(a.UserNo)));
    }

    private static string? ReadLegacyUserNo()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BaoWuLearn");
            var legacyPath = Path.Combine(dir, "settings.json");
            if (!File.Exists(legacyPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
            return doc.RootElement.TryGetProperty("RememberedUserNo", out var v)
                   && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void Persist()
    {
        try
        {
            lock (_sync)
            {
                _file.RememberAccount = RememberAccount;
                _file.SavePassword = SavePassword;
                _file.Accounts = _accounts.ToList();
                File.WriteAllText(_path, JsonSerializer.Serialize(_file, JsonOpts));
            }
        }
        catch
        {
            // 配置目录不可写时不影响登录本身
        }
    }

    private sealed class AccountFile
    {
        public bool RememberAccount { get; set; } = true;
        public bool SavePassword { get; set; }
        public List<SavedAccount> Accounts { get; set; } = new();
    }
}

/// <summary>
/// 本机级别的对称加密（AES-CBC）。
///
/// 密钥由「固定前缀 + 本机名 + 系统用户名」经 SHA-256 派生，不落盘。
/// 目的是不让密码以明文躺在配置文件里；同机同步一份配置文件到别的机器会解不开（这点符合预期）。
/// </summary>
internal static class LocalSecret
{
    private static readonly byte[] Key = SHA256.HashData(
        Encoding.UTF8.GetBytes(
            "BaoWuLearn.account.v1|" + Environment.MachineName + "|" + Environment.UserName));

    public static string Protect(string plain)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV();

        using var enc = aes.CreateEncryptor();
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = enc.TransformFinalBlock(data, 0, data.Length);

        var blob = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, blob, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, blob, aes.IV.Length, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var blob = Convert.FromBase64String(value);
            if (blob.Length <= 16) return null;

            using var aes = Aes.Create();
            aes.Key = Key;
            var iv = new byte[16];
            Buffer.BlockCopy(blob, 0, iv, 0, 16);
            aes.IV = iv;

            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(blob, 16, blob.Length - 16);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // 换机器 / 存档损坏都会走到这里，按"没有保存密码"处理
            return null;
        }
    }
}
