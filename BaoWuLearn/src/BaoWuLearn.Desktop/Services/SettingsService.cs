using System.Text.Json;
using System.Text.Json.Serialization;
using BaoWuLearn.Core.Behavior;

namespace BaoWuLearn.Desktop.Services;

/// <summary>本地持久化设置。不含密码。</summary>
public sealed class AppSettings
{
    /// <summary>记住的员工号（不保存密码）。</summary>
    public string? RememberedUserNo { get; set; }

    /// <summary>拟人化强度：0=关闭，1=轻度，2=标准。</summary>
    public int HumanizeLevel { get; set; } = 2;

    /// <summary>课程之间的随机停顿开关。</summary>
    public bool RandomCourseGap { get; set; } = true;

    /// <summary>队列完成后是否自动停止引擎。</summary>
    public bool StopWhenQueueDone { get; set; } = true;

    /// <summary>挂课完成策略：0 = 挂满才跳（默认），1 = 及格就跳。</summary>
    public int CompletionPolicy { get; set; }

    /// <summary>界面皮肤 Id（见 <see cref="ThemeService.Skins"/>）。未知值回落深空蓝。</summary>
    public string SkinId { get; set; } = ThemeService.DefaultSkinId;

    /// <summary>界面密度：0=舒适，1=紧凑（默认紧凑）。</summary>
    public int UiDensity { get; set; } = ThemeService.DefaultUiDensity;

    /// <summary>是否启用自动检查更新（启动后与每 24 小时各一次；只下载不自动安装）。</summary>
    public bool UpdateChecksEnabled { get; set; } = true;

    /// <summary>
    /// 更新镜像前缀（GitHub 直连失败时按顺序兜底，语义是「前缀 + 直连 URL」）。
    /// 空列表 = 只走直连 + 清单下发的推荐列表。
    /// </summary>
    public List<string> UpdateMirrors { get; set; } = [];

    /// <summary>
    /// 见过的最新清单发布时间（防降级：签名旧清单原样重放也躲不过这条）。
    /// 存 ISO 字符串，避免 JSON 数字/时间表示分歧。
    /// </summary>
    public string? LastUpdatePubDate { get; set; }

    /// <summary>课件下载根目录（空 = 系统"下载"文件夹，见 <see cref="ResolvedDownloadRoot"/>）。</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>解析实际下载根目录：设置值优先，空/无效回落系统下载目录。</summary>
    public string ResolvedDownloadRoot =>
        !string.IsNullOrWhiteSpace(DownloadDirectory) && Directory.Exists(DownloadDirectory)
            ? DownloadDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/Downloads";
}

/// <summary>设置读写（存放在用户配置目录，双平台一致）。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BaoWuLearn");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return Apply(new AppSettings());
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            return Apply(settings);
        }
        catch
        {
            return Apply(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch
        {
            // 配置写入失败不应影响主流程
        }
    }

    /// <summary>把设置同步到核心层的拟人化参数与挂课策略。</summary>
    private static AppSettings Apply(AppSettings settings)
    {
        Humanize.Current = settings.HumanizeLevel switch
        {
            0 => Humanize.Level.Off,
            1 => Humanize.Level.Light,
            _ => Humanize.Level.Standard,
        };

        LearnPolicy.Current = settings.CompletionPolicy == 1
            ? CompletionPolicy.PassScore
            : CompletionPolicy.FullDuration;

        return settings;
    }

    public string ConfigPath => _path;
}
