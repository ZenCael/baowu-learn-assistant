using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Models;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>
/// 登录页。两种渠道：**账号密码**（图形验证码原图肉眼识别）与
/// **手机号短信验证码**（v1.0.47，验证码由平台官方通道发到使用者手机）。
///
/// 验证码严格取自平台接口 —— 图片不 OCR、不生成替代图，短信不截获、不猜测，
/// 一切与直接打开网页登录等价，由使用者本人完成验证。
///
/// 异常纪律：本页所有命令都**不允许把异常抛出去**。
/// 命令由 UI 线程的 AsyncRelayCommand 调用，任何逃逸的异常都会直接终结进程，
/// 因此这里对每一步都做兜底捕获，并转换为可读的界面提示 + 日志。
///
/// 自救通道：登录报错若**不像**常规业务失败（密码/验证码错这类措辞都含关键词），
/// 就疑似平台接口变更 —— 主动探一次更新通道，有新版本时顶部横幅会亮起引导升级。
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;
    private readonly AccountStore _accounts;
    private readonly Action<LoginResult> _onSuccess;
    private readonly Action<string> _log;

    /// <summary>更新通道探测（MVM 注入）；登录疑似接口变更时自救用。</summary>
    private readonly Func<Task<string>>? _checkUpdate;

    /// <summary>短信重发倒计时（与网页端同款 60 秒）。</summary>
    public const int SmsResendSeconds = 60;
    private DispatcherTimer? _smsTimer;

    /// <summary>防止连点造成并发刷新。</summary>
    private bool _refreshing;

    [ObservableProperty] private string _userNo = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _captcha = "";
    [ObservableProperty] private Bitmap? _captchaImage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _statusText = "请输入员工号与密码";
    [ObservableProperty] private bool _showPassword;

    // ── 短信登录（v1.0.47）───────────────────────────────
    /// <summary>true = 短信登录页签；false = 账号密码页签。</summary>
    [ObservableProperty] private bool _isSmsLogin;

    /// <summary>手机号（短信页签）。与工号一起先发送、后登录。</summary>
    [ObservableProperty] private string _mobile = "";

    /// <summary>6 位短信验证码。</summary>
    [ObservableProperty] private string _smsCode = "";

    /// <summary>发送请求进行中（挡重复点击）。</summary>
    [ObservableProperty] private bool _smsSending;

    /// <summary>重发倒计时剩余秒数；&gt;0 期间发送按钮不可点。</summary>
    [ObservableProperty] private int _smsCountdown;

    /// <summary>
    /// 发送接口返回的登录回执。平台口径：没有回执就不允许提交短信登录
    /// （网页端同样门禁）；一次提交失败后回执仍可用，改码可直接重试。
    /// </summary>
    private string? _smsReceipt;

    /// <summary>发送按钮文案：倒计时期间显示剩余秒数。</summary>
    public string SmsButtonText => SmsSending
        ? "发送中…"
        : SmsCountdown > 0 ? $"{SmsCountdown} 秒后可重发" : "发送验证码";

    partial void OnSmsSendingChanged(bool value) => OnPropertyChanged(nameof(SmsButtonText));
    partial void OnSmsCountdownChanged(int value) => OnPropertyChanged(nameof(SmsButtonText));

    /// <summary>页签切换时更新提示语。</summary>
    partial void OnIsSmsLoginChanged(bool value)
    {
        Error = null;
        StatusText = value
            ? "输入员工号与手机号，发送短信验证码后登录"
            : "请输入员工号与密码";
        // 切到短信页签且手机号还空着：用当前员工号对应的存档手机号回显
        if (value && string.IsNullOrEmpty(Mobile))
            Mobile = _accounts.Accounts.FirstOrDefault(a => a.UserNo == UserNo.Trim())?.Mobile ?? "";
        OnPropertyChanged(nameof(IsPasswordMode));
    }

    /// <summary>大陆手机号（与网页端同款正则）。</summary>
    public static bool IsValidMobile(string? s) =>
        s is not null && Regex.IsMatch(s, @"^1[3-9]\d{9}$");

    /// <summary>6 位数字短信码（与网页端同款校验）。</summary>
    public static bool IsValidSmsCode(string? s) =>
        s is not null && Regex.IsMatch(s, @"^\d{6}$");

    // ── 账号记忆 ──────────────────────────────────────────
    /// <summary>记住账户：登录成功后把该账号记进下拉列表。</summary>
    [ObservableProperty] private bool _rememberAccount = true;

    /// <summary>保存密码：连同密码一起存在本机（加密后落盘）。</summary>
    [ObservableProperty] private bool _savePassword;

    /// <summary>账号下拉是否展开。</summary>
    [ObservableProperty] private bool _isAccountListOpen;

    /// <summary>已保存的账号（下拉列表数据源）。</summary>
    public ObservableCollection<SavedAccountRow> SavedAccounts { get; } = new();

    /// <summary>有没有保存过账号（没有时下拉按钮给提示而不是弹出空面板）。</summary>
    public bool HasSavedAccounts => SavedAccounts.Count > 0;

    /// <summary>验证码图片加载失败的说明（图片位置改为显示它）。</summary>
    [ObservableProperty] private string? _captchaHint = "加载中…";

    public string? CaptchaId { get; private set; }

    /// <summary>验证码是否为动态 GIF（界面据此给出提示）。</summary>
    [ObservableProperty] private bool _captchaIsAnimated;

    /// <summary>是否显示密码明文。</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>登录按钮文案：请求进行中时给出明确反馈，避免"点了没反应"的错觉。</summary>
    public string LoginButtonText => IsBusy ? "登录中…" : "登  录";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(LoginButtonText));
        OnPropertyChanged(nameof(IsPasswordMode));
    }

    /// <summary>账号密码表单可见性（页签互斥；busy 期间表单禁用由 IsEnabled 单独管）。</summary>
    public bool IsPasswordMode => !IsSmsLogin;

    /// <summary>验证码位图是否已就绪（未就绪时显示提示文字）。</summary>
    public bool HasCaptchaImage => CaptchaImage is not null;

    partial void OnCaptchaImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCaptchaImage));

    [RelayCommand]
    private void ToggleShowPassword() => ShowPassword = !ShowPassword;

    public LoginViewModel(
        AuthService auth,
        AccountStore accounts,
        Action<LoginResult> onSuccess,
        Action<string> log,
        Func<Task<string>>? checkUpdate = null)
    {
        _auth = auth;
        _accounts = accounts;
        _onSuccess = onSuccess;
        _log = log;
        _checkUpdate = checkUpdate;

        // 直接写字段：这两个值只是回显存档里的开关状态，
        // 走属性会立刻触发 OnChanged 又把同样的值写回文件，白白多一次磁盘写入。
        _rememberAccount = accounts.RememberAccount;
        _savePassword = accounts.SavePassword;

        LoadSavedAccounts();
        PrefillLastAccount();
    }

    // ── 账号下拉 ──────────────────────────────────────────

    /// <summary>把存档里的账号读进下拉列表（最近登录的排最前）。</summary>
    private void LoadSavedAccounts()
    {
        SavedAccounts.Clear();
        foreach (var a in _accounts.Accounts
                     .OrderByDescending(x => x.LastLoginAt ?? "", StringComparer.Ordinal))
        {
            SavedAccounts.Add(new SavedAccountRow(
                a.UserNo, a.DisplayName,
                !string.IsNullOrEmpty(a.ProtectedPassword),
                a.LastLoginAt,
                UseAccount, DeleteAccount));
        }

        OnPropertyChanged(nameof(HasSavedAccounts));
    }

    /// <summary>重新从存档加载账号列表（设置页清掉密码后调用，刷新提示文案）。</summary>
    public void ReloadAccounts() => LoadSavedAccounts();

    /// <summary>首次打开时预填最近登录的账号（开了"保存密码"还会把密码一并填上）。</summary>
    private void PrefillLastAccount()
    {
        if (!RememberAccount) return;

        var last = _accounts.LastAccount;
        if (last is null) return;

        UserNo = last.UserNo;
        var pwd = _accounts.PasswordFor(last.UserNo);
        if (!string.IsNullOrEmpty(pwd)) Password = pwd;
    }

    [RelayCommand]
    private void ToggleAccountList()
    {
        if (SavedAccounts.Count == 0)
        {
            StatusText = "还没有保存过账号，登录成功后会记住";
            return;
        }

        IsAccountListOpen = !IsAccountListOpen;
    }

    /// <summary>选中一个已保存的账号：填工号（有存密码/存过手机号就一并填上）。</summary>
    private void UseAccount(SavedAccountRow row)
    {
        Error = null;
        UserNo = row.UserNo;

        var pwd = _accounts.PasswordFor(row.UserNo);
        Password = pwd ?? "";
        Mobile = _accounts.Accounts.FirstOrDefault(a => a.UserNo == row.UserNo)?.Mobile ?? Mobile;
        IsAccountListOpen = false;

        StatusText = string.IsNullOrEmpty(pwd)
            ? "已填入账号，请输入密码"
            : "已填入账号与保存的密码";
    }

    /// <summary>从下拉里删除一个账号。</summary>
    private void DeleteAccount(SavedAccountRow row)
    {
        _accounts.Remove(row.UserNo);

        // 删的正好是当前填着的账号，就把输入框一并清掉，避免"删了但框里还在"
        if (string.Equals(UserNo, row.UserNo, StringComparison.Ordinal))
        {
            UserNo = "";
            Password = "";
        }

        LoadSavedAccounts();
        if (SavedAccounts.Count == 0) IsAccountListOpen = false;

        StatusText = $"已删除账号 {row.UserNo}";
        _log($"已删除已保存的账号：{row.UserNo}");
    }

    partial void OnRememberAccountChanged(bool value)
    {
        _accounts.SetRememberAccount(value);
        StatusText = value
            ? "登录成功后会记住该账号"
            : "登录成功后不再记住新账号（已保存的可在下拉里删除）";
    }

    partial void OnSavePasswordChanged(bool value)
    {
        _accounts.SetSavePassword(value);
        // 关闭时会清掉已存的密码，下拉里的"已保存密码"提示要跟着刷新
        LoadSavedAccounts();
        StatusText = value ? "密码将加密保存在本机" : "已清除本机保存的密码";
    }

    /// <summary>刷新验证码（用户点击图片时调用，会一并清掉上一次的提示信息）。</summary>
    [RelayCommand]
    public Task RefreshCaptchaAsync() => RefreshCaptchaCoreAsync(clearMessages: true);

    /// <summary>
    /// 刷新验证码。
    ///
    /// <paramref name="clearMessages"/> 为 <c>false</c> 时**保留**当前的错误提示与状态文字。
    /// 登录失败后自动换图必须走这条路径 —— 否则刚写进去的失败原因会被这里的
    /// <c>Error = null</c> 立刻抹掉，用户看到的就是「点了登录没有任何反应」。
    /// </summary>
    private async Task RefreshCaptchaCoreAsync(bool clearMessages)
    {
        if (_refreshing) return;
        _refreshing = true;

        // 只在用户主动刷新时才清提示；被动换图（登录失败后）保留原因
        if (clearMessages)
        {
            Error = null;
            StatusText = "正在获取验证码…";
        }

        CaptchaHint = "加载中…";

        try
        {
            var captcha = await _auth.GetCaptchaAsync();
            CaptchaId = captcha.Id;

            var bitmap = await DecodeAsync(captcha);

            // 换图前释放旧位图，避免长时间运行后堆积
            var old = CaptchaImage;
            CaptchaImage = bitmap;
            old?.Dispose();

            CaptchaIsAnimated = captcha.IsAnimated;

            if (bitmap is null)
            {
                CaptchaHint = "图片解码失败\n点击重试";
                StatusText = "验证码图片无法显示";
                _log("✖ 验证码图片解码失败（接口已返回数据，可能是编码格式不识别）");
            }
            else
            {
                if (clearMessages)
                    StatusText = captcha.IsAnimated ? "验证码已刷新（动图，显示第一帧）" : "验证码已刷新";
                _log($"已获取验证码（ID {ShortId(captcha.Id)}…，{bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}）");
            }
        }
        catch (Exception ex)
        {
            // 包含 ApiException 与一切意外异常 —— 绝不允许抛到 UI 线程
            CaptchaId = null;
            CaptchaHint = "点击重试";
            // 取图失败是更严重的信息，无论何种模式都覆盖提示
            Error = "验证码获取失败：" + ex.Message;
            StatusText = "验证码获取失败";
            _log("✖ 验证码获取失败：" + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>提交登录。</summary>
    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        Error = null;

        if (string.IsNullOrWhiteSpace(UserNo)) { Error = "请输入员工号"; return; }
        if (string.IsNullOrEmpty(Password)) { Error = "请输入密码"; return; }
        if (string.IsNullOrWhiteSpace(Captcha)) { Error = "请输入验证码"; return; }
        if (string.IsNullOrEmpty(CaptchaId))
        {
            Error = "验证码尚未加载，请点击图片刷新";
            return;
        }

        IsBusy = true;
        StatusText = "正在登录…";
        try
        {
            var userNo = UserNo.Trim();
            var result = await _auth.LoginAsync(userNo, Password, Captcha.Trim(), CaptchaId);
            CompleteLogin(result, Password, null);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            StatusText = "登录失败";
            _log("✖ 登录失败：" + ex.Message);
            Captcha = "";
            // 验证码一次性，失败后必须换新的。
            // 这里必须用 clearMessages:false —— 否则刚写进去的失败原因
            // 会被刷新流程清掉，界面表现为「点了登录毫无反应」。
            await RefreshCaptchaCoreAsync(clearMessages: false);
            ProbeUpdateIfApiChanged(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 登录成功后的公共收尾（两条渠道同路）：
    /// 记住账号 / 保存密码 / 记手机号必须在清空输入框之前处理。
    /// 已在列表里的老账号即使这次关了"记住账户"也照常更新 —— 否则它的
    /// 登录时间与密码永远不会刷新，用户会以为"保存密码坏了"。
    /// </summary>
    private void CompleteLogin(LoginResult result, string? password, string? mobile)
    {
        var userNo = (result.UserNo ?? UserNo).Trim();
        if (RememberAccount || _accounts.Accounts.Any(a => a.UserNo == userNo))
        {
            _accounts.Upsert(userNo, result.RealName ?? result.UserName, password, mobile);
            LoadSavedAccounts();
        }

        Password = "";
        Captcha = "";
        SmsCode = "";
        StatusText = "登录成功";
        _onSuccess(result);
    }

    // ── 短信登录命令（v1.0.47）───────────────────────────

    /// <summary>页签切换（axaml 两个页签按钮共用，参数 = 目标是否短信页）。</summary>
    [RelayCommand]
    private void SwitchMode(string target)
    {
        if (!bool.TryParse(target, out var sms)) return;
        IsSmsLogin = sms;
    }

    /// <summary>发送短信验证码。成功拿到登录回执后进入 60 秒重发倒计时。</summary>
    [RelayCommand]
    private async Task SendSmsAsync()
    {
        if (SmsSending || SmsCountdown > 0) return;
        Error = null;

        var userNo = UserNo.Trim();
        var mobile = Mobile.Trim();
        if (string.IsNullOrWhiteSpace(userNo)) { Error = "请输入员工号"; return; }
        if (!IsValidMobile(mobile)) { Error = "手机号格式不对（11 位大陆手机号）"; return; }

        SmsSending = true;
        StatusText = "正在发送短信验证码…";
        try
        {
            _smsReceipt = await _auth.SendLoginSmsAsync(userNo, mobile);
            StartSmsCountdown();
            StatusText = $"验证码已发送到 {MaskMobile(mobile)}，请查收并输入";
            _log($"已向 {MaskMobile(mobile)} 发送登录短信验证码（回执 {ShortId(_smsReceipt)}…）");
        }
        catch (Exception ex)
        {
            _smsReceipt = null;
            Error = "短信发送失败：" + ex.Message;
            StatusText = "短信发送失败";
            _log("✖ 短信发送失败：" + ex.Message);
            ProbeUpdateIfApiChanged(ex.Message);
        }
        finally
        {
            SmsSending = false;
        }
    }

    /// <summary>提交短信验证码登录。</summary>
    [RelayCommand]
    private async Task LoginSmsAsync()
    {
        if (IsBusy) return;
        Error = null;

        var userNo = UserNo.Trim();
        var mobile = Mobile.Trim();
        if (string.IsNullOrWhiteSpace(userNo)) { Error = "请输入员工号"; return; }
        if (!IsValidMobile(mobile)) { Error = "手机号格式不对（11 位大陆手机号）"; return; }
        if (string.IsNullOrEmpty(_smsReceipt))
        {
            Error = "请先点「发送验证码」收到短信后再登录";
            return;
        }
        if (!IsValidSmsCode(SmsCode)) { Error = "请输入 6 位短信验证码"; return; }

        IsBusy = true;
        StatusText = "正在登录…";
        try
        {
            var result = await _auth.LoginByMobileAsync(userNo, mobile, SmsCode.Trim(), _smsReceipt);
            CompleteLogin(result, null, mobile);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            StatusText = "登录失败";
            _log("✖ 短信登录失败：" + ex.Message);
            // 与网页端口径一致：失败不清回执与输入，改码可直接重试；
            // 验证码确已作废时用户按提示重发即可。
            ProbeUpdateIfApiChanged(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>60 秒重发倒计时（同款网页端）。</summary>
    private void StartSmsCountdown()
    {
        _smsTimer?.Stop();
        SmsCountdown = SmsResendSeconds;
        _smsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _smsTimer.Tick -= SmsTimerOnTick;
        _smsTimer.Tick += SmsTimerOnTick;
        _smsTimer.Start();
    }

    private void SmsTimerOnTick(object? sender, EventArgs e)
    {
        SmsCountdown--;
        if (SmsCountdown > 0) return;
        SmsCountdown = 0;
        _smsTimer?.Stop();
    }

    /// <summary>手机号打码（138****1234），日志里不留完整号码。</summary>
    private static string MaskMobile(string m) =>
        m.Length == 11 ? m[..3] + "****" + m[7..] : m;

    // ── 自救通道：登录疑似接口变更 → 探一次更新 ──────────

    /// <summary>
    /// 错误文本是否「不像」常规业务失败。平台常规失败的措辞都带
    /// 验证码 / 密码 / 账号·账户 / 用户 这类词（v1.0.4 起逐项实测枚举过）；
    /// 一个都不沾的报错（结构异常、路由 404、网关拦截…）疑似接口变更。
    /// ★ 例外救回：含「解密」的一律算协议级错误 —— 服务端只在字段加密口径
    ///   出问题时说这句（如「账户名或手机号解密失败…SM2加密」），
    ///   它虽然带着「账户」字样，但不是密码错那种业务失败。
    /// </summary>
    internal static bool IsSuspectedApiChange(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (message.Contains("解密")) return true;
        return !message.Contains("验证码")
            && !message.Contains("密码")
            && !message.Contains("账号")
            && !message.Contains("账户")
            && !message.Contains("用户")
            && !message.Contains("登录成功");
    }

    /// <summary>疑似接口变更时探一次更新通道；有新版本时横幅自动亮起。</summary>
    private void ProbeUpdateIfApiChanged(string message)
    {
        if (!IsSuspectedApiChange(message) || _checkUpdate is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var verdict = await _checkUpdate();
                _log($"登录报错不像常规失败，已探更新通道：{verdict}"
                     + "（有新版本时请点顶部横幅「立即更新」升级适配）");
            }
            catch (Exception ex)
            {
                _log("更新通道探测失败：" + ex.Message);
            }
        });
    }

    public void Reset()
    {
        Captcha = "";
        SmsCode = "";
        _smsReceipt = null;
        _smsTimer?.Stop();
        SmsCountdown = 0;
        Error = null;
        IsAccountListOpen = false;
        StatusText = "请输入员工号与密码";

        // 退出登录后回到登录页：记住账户时保留工号（开着"保存密码"连密码一起填回，
        // 用短信渠道登录过的连手机号一起填回），没记住就整个清干净，
        // 避免把上一个用户的信息留在界面上。
        if (RememberAccount)
        {
            var acc = _accounts.Accounts.FirstOrDefault(a => a.UserNo == UserNo.Trim());
            Password = _accounts.PasswordFor(UserNo) ?? "";
            Mobile = acc?.Mobile ?? "";
        }
        else
        {
            UserNo = "";
            Password = "";
            Mobile = "";
        }
    }

    /// <summary>
    /// 取到图片字节。解析阶段已经把 base64 解好放在 <see cref="CaptchaData.Bytes"/>，
    /// 只有接口以图片 URL 形式返回时才需要再发一次请求拉原图。
    /// </summary>
    private async Task<byte[]> GetImageBytesAsync(CaptchaData captcha)
    {
        if (captcha.Bytes is { Length: > 0 } bytes)
            return bytes;

        if (!captcha.IsBase64 && !string.IsNullOrWhiteSpace(captcha.Image))
            return await _auth.GetCaptchaImageBytesAsync(captcha.Image.Trim());

        throw new InvalidOperationException("接口未返回可用的验证码图片数据");
    }

    /// <summary>把接口返回的图片解码为位图；失败返回 null（由调用方给出提示）。</summary>
    private async Task<Bitmap?> DecodeAsync(CaptchaData captcha)
    {
        if (string.IsNullOrWhiteSpace(captcha.Image)) return null;
        try
        {
            var bytes = await GetImageBytesAsync(captcha);
            if (bytes.Length == 0) return null;
            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static string ShortId(string? id)
        => string.IsNullOrEmpty(id) ? "?" : id.Length <= 8 ? id : id[..8];

    // ── 返回多挂机会话（v1.0.42）──────────────────────────

    /// <summary>池里仍有可返回的运行时（MVM 经 <see cref="SetPoolInfo"/> 同步）。</summary>
    [ObservableProperty] private bool _canReturnPool;

    /// <summary>「返回会话」按钮文案（带池计数）。</summary>
    [ObservableProperty] private string _returnPoolText = "";

    /// <summary>返回会话的动作（MVM 注入）。</summary>
    public Action? OnReturnPool { get; set; }

    /// <summary>MVM 在池变化 / 登录态切换时同步池计数与按钮文案。</summary>
    public void SetPoolInfo(int poolCount)
    {
        CanReturnPool = poolCount > 0;
        ReturnPoolText = poolCount > 0 ? $"返回会话（{poolCount} 个账号仍在池中挂机）" : "";
    }

    [RelayCommand]
    private void ReturnToPool() => OnReturnPool?.Invoke();

    /// <summary>
    /// 重登指定账号时预填工号与已存密码（多账号池里「重新登录」跳过来的路径）。
    /// 与 Reset 的预填口径一致：只在「记住账户」开着时才碰密码。
    /// </summary>
    public void PrefillFor(string userNo)
    {
        UserNo = userNo;
        Password = RememberAccount ? _accounts.PasswordFor(userNo) ?? "" : "";
        Mobile = _accounts.Accounts.FirstOrDefault(a => a.UserNo == userNo)?.Mobile ?? "";
    }
}
