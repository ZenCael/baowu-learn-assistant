using BaoWuLearn.Core.Behavior;
using BaoWuLearn.Core.Http;
using BaoWuLearn.Core.Services;
using BaoWuLearn.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaoWuLearn.Desktop.ViewModels;

/// <summary>设置页：拟人化强度、账号记忆、接口自检。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly AccountStore _accounts;
    private readonly AuthService _auth;
    private readonly Action<string> _log;
    private AppSettings _current;

    [ObservableProperty] private int _humanizeLevel = 2;
    [ObservableProperty] private bool _randomCourseGap = true;

    /// <summary>已保存账号的概况（账号与密码都在登录页管理，这里只做说明与清理）。</summary>
    [ObservableProperty] private string _accountSummary = "";

    /// <summary>清除密码后的即时反馈。</summary>
    [ObservableProperty] private string _accountActionStatus = "";

    /// <summary>挂课完成策略：0 = 挂满才跳，1 = 及格就跳。</summary>
    [ObservableProperty] private int _completionPolicy;

    /// <summary>界面皮肤 Id。</summary>
    [ObservableProperty] private string _skinId = ThemeService.DefaultSkinId;

    /// <summary>界面密度：0=舒适，1=紧凑。</summary>
    [ObservableProperty] private int _uiDensity = ThemeService.DefaultUiDensity;

    [ObservableProperty] private string _configPath = "";
    [ObservableProperty] private string _selfTestResult = "尚未测试";
    [ObservableProperty] private bool _isTesting;

    /// <summary>保存设置的即时反馈：成功与失败都写这里，避免点了按钮毫无反应。</summary>
    [ObservableProperty] private string _saveStatus = "";

    /// <summary>是否启用自动检查更新（保存后下次启动生效；本会话内已装的定时器不动）。</summary>
    [ObservableProperty] private bool _updateChecksEnabled = true;

    /// <summary>镜像前缀列表（一行一个，保存生效）。空 = 只走直连与清单下发的推荐列表。</summary>
    [ObservableProperty] private string _updateMirrorsText = "";

    /// <summary>更新检查的即时结论（含手动「立即检查」的结果）。</summary>
    [ObservableProperty] private string _updateCheckStatus = "尚未检查";

    /// <summary>供主窗口启动时读取的当前设置快照。</summary>
    public AppSettings CurrentSettings => _current;

    public IReadOnlyList<SkinOption> AvailableSkins => ThemeService.Skins;

    /// <summary>皮肤下拉的选中项（与 <see cref="SkinId"/> 同步）。</summary>
    public SkinOption? SelectedSkin
    {
        get => ThemeService.Skins.FirstOrDefault(s => s.Id == SkinId);
        set
        {
            if (value is null || value.Id == SkinId) return;
            SkinId = value.Id;
        }
    }

    public string HeartbeatRange => HumanizeLevel switch
    {
        0 => "固定 60 秒",
        1 => "59 – 61 秒（轻度抖动）",
        _ => "58 – 63 秒（标准抖动）",
    };

    public string HumanizeDescription => HumanizeLevel switch
    {
        0 => "不做任何随机化，心跳严格 60 秒一次。仅建议在调试时使用。",
        1 => "心跳间隔做小幅随机，不插入模拟暂停。",
        _ => "心跳 58–63 秒随机、随机暂停、课间随机停顿、学时只按真实时间上报。",
    };

    public string PolicyDescription => CompletionPolicy == 1
        ? "及格就跳：只挂到「课程分数线折算出来的时长」就换下一门。省时间，代价是成绩停在及格线附近；"
          + "有些课的要求时长比课件总时长还长，这种情况只有它能挂得完。"
        : "挂满才跳：把课件时长全部挂完才换下一门。成绩最稳，适合要冲学分的课。";

    public string SkinDisplayName =>
        ThemeService.Skins.FirstOrDefault(s => s.Id == SkinId)?.DisplayName ?? SkinId;

    /// <summary>「立即检查更新」委托，由主窗口注入（检查逻辑与定时器都住在主窗口那边）。</summary>
    private readonly Func<Task<string>>? _checkUpdate;

    public SettingsViewModel(
        SettingsService settings,
        AccountStore accounts,
        AuthService auth,
        Action<string> log,
        Func<Task<string>>? checkUpdate = null)
    {
        _settings = settings;
        _accounts = accounts;
        _auth = auth;
        _log = log;
        _checkUpdate = checkUpdate;
        _current = settings.Load();
        ConfigPath = settings.ConfigPath;
        UpdateChecksEnabled = _current.UpdateChecksEnabled;
        UpdateMirrorsText = string.Join("\n", _current.UpdateMirrors);

        _humanizeLevel = _current.HumanizeLevel;
        _randomCourseGap = _current.RandomCourseGap;
        _completionPolicy = _current.CompletionPolicy;
        _skinId = ThemeService.NormalizeSkinId(_current.SkinId);
        _uiDensity = ThemeService.NormalizeDensity(_current.UiDensity);

        // 启动即按上次选择上皮肤（主窗口创建前由 App 再调一次，保证首帧正确）
        ThemeService.Apply(_current);

        RefreshAccountSummary();
    }

    /// <summary>刷新账号概况（登录页那边改动后，重新进入设置页即更新）。</summary>
    public void RefreshAccountSummary()
    {
        var list = _accounts.Accounts;
        var withPwd = list.Count(a => !string.IsNullOrEmpty(a.ProtectedPassword));

        AccountSummary = list.Count == 0
            ? "还没有保存任何账号。在登录页勾选「记住账户」，登录成功的账号就会出现在下拉列表里。"
            : $"已保存 {list.Count} 个账号，其中 {withPwd} 个保存了密码。"
              + "账号信息与密码都只保存在本机，可在登录页的下拉列表里逐个删除。";
    }

    /// <summary>清除本机保存的全部密码（保留账号本身）。</summary>
    [RelayCommand]
    private void ClearSavedPasswords()
    {
        _accounts.ClearPasswords();
        RefreshAccountSummary();
        AccountActionStatus = "已清除本机保存的所有密码";
        _log("已清除本机保存的登录密码");
        AccountsChanged?.Invoke();
    }

    /// <summary>
    /// 账号存档被改动（例如刚清掉了密码）。
    /// 登录页据此刷新下拉里"已保存密码 / 未保存密码"那行提示 ——
    /// 不然刚清完密码，回登录页下拉还写着"已保存密码"，自相矛盾。
    /// </summary>
    public event Action? AccountsChanged;

    partial void OnHumanizeLevelChanged(int value)
    {
        OnPropertyChanged(nameof(HeartbeatRange));
        OnPropertyChanged(nameof(HumanizeDescription));
        ApplyToCore();
    }

    partial void OnRandomCourseGapChanged(bool value) => ApplyToCore();

    partial void OnCompletionPolicyChanged(int value)
    {
        OnPropertyChanged(nameof(PolicyDescription));
        ApplyToCore();
    }

    partial void OnSkinIdChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedSkin));
        if (string.IsNullOrEmpty(value)) return;
        _current.SkinId = ThemeService.NormalizeSkinId(value);
        ThemeService.Apply(_current);
    }

    partial void OnUiDensityChanged(int value)
    {
        _current.UiDensity = ThemeService.NormalizeDensity(value);
        ThemeService.Apply(_current);
    }

    private void ApplyToCore()
    {
        Humanize.Current = HumanizeLevel switch
        {
            0 => Humanize.Level.Off,
            1 => Humanize.Level.Light,
            _ => Humanize.Level.Standard,
        };
        // 策略同步到核心层：改了立刻生效，不必重启挂课（下一门课就按新策略折算）。
        // 注意 CompletionPolicy 在这里指界面属性（int），枚举类型要用全限定名。
        LearnPolicy.Current = CompletionPolicy == 1
            ? BaoWuLearn.Core.Behavior.CompletionPolicy.PassScore
            : BaoWuLearn.Core.Behavior.CompletionPolicy.FullDuration;
    }

    [RelayCommand]
    private void Save()
    {
        _current.HumanizeLevel = HumanizeLevel;
        _current.RandomCourseGap = RandomCourseGap;
        _current.CompletionPolicy = CompletionPolicy;
        _current.SkinId = ThemeService.NormalizeSkinId(SkinId);
        _current.UiDensity = ThemeService.NormalizeDensity(UiDensity);
        _current.UpdateChecksEnabled = UpdateChecksEnabled;
        _current.UpdateMirrors = UpdateMirrorsText
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        && !l.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            .ToList(); // 手动剔除混进来的直连地址——链首本来就有，混进来只会重复请求

        try
        {
            _settings.Save(_current);
            SaveStatus = "设置已保存";
            _log($"✓ 设置已保存（拟人化：{HeartbeatRange}，完成策略：{(CompletionPolicy == 1 ? "及格就跳" : "挂满才跳")}，皮肤：{SkinDisplayName}，密度：{(UiDensity == ThemeService.DensityCompact ? "紧凑" : "舒适")}）");
        }
        catch (Exception ex)
        {
            // 配置目录不可写时也要让用户看到结果，不能点了没反应
            SaveStatus = "保存失败：" + ex.Message;
            _log("✖ 设置保存失败：" + ex.Message);
        }
    }

    /// <summary>接口自检：拉一张验证码，验证网关连通性与响应结构。</summary>
    [RelayCommand]
    private async Task SelfTestAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        SelfTestResult = "正在测试…";
        try
        {
            var captcha = await _auth.GetCaptchaAsync();
            SelfTestResult = captcha.Image is null
                ? "网关可达，但未解析到图片字段（结构可能已变更）"
                : $"网关正常 ✓ 验证码 ID={captcha.Id ?? "?"}，图片 {captcha.Image.Length} 字符，格式={(captcha.IsAnimated ? "动图 GIF" : "静态图")}";
            _log("✓ 接口自检通过");
        }
        catch (Exception ex)
        {
            // 必须捕获全部异常：只捕 ApiException 时，一旦解析层抛出别的类型，
            // 结果会停在"正在测试…"且无人观察 —— 用户看到的就是点了没反应。
            SelfTestResult = "自检失败：" + ex.Message;
            _log("✖ 接口自检失败：" + ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// 立即检查一次更新（走主窗口的更新通道：端点链降级 + 验签都在那边）。
    /// 注意镜像列表改动要先点「保存设置」才会被这次检查用上。
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdateNowAsync()
    {
        if (_checkUpdate is null) { UpdateCheckStatus = "更新通道不可用"; return; }
        UpdateCheckStatus = "正在检查…";
        try
        {
            UpdateCheckStatus = await _checkUpdate();
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = "检查异常：" + ex.Message;
        }
    }
}
