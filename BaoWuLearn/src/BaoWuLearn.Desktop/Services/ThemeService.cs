using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace BaoWuLearn.Desktop.Services;

/// <summary>皮肤下拉项（独立类型，便于 XAML 编译绑定）。</summary>
public sealed record SkinOption(string Id, string DisplayName, ThemeVariant Variant);

/// <summary>界面皮肤与密度。启动时、以及设置页改动时都会调用。</summary>
public static class ThemeService
{
    public const string DefaultSkinId = "dark-blue";

    /// <summary>默认紧凑 —— 用户明确要求「打开不用最大化」。</summary>
    public const int DensityComfortable = 0;
    public const int DensityCompact = 1;
    public const int DefaultUiDensity = DensityCompact;

    public static readonly IReadOnlyList<SkinOption> Skins =
    [
        new("dark-blue", "深空蓝", ThemeVariant.Dark),
        new("graphite-teal", "石墨青", ThemeVariant.Dark),
        new("dusk-purple", "暮山紫", ThemeVariant.Dark),
        new("morning-light", "晨雾", ThemeVariant.Light),
    ];

    private static readonly Dictionary<string, string> SkinSources = new(StringComparer.Ordinal)
    {
        ["dark-blue"] = "avares://BaoWuLearn/Styles/Skins/DarkBlue.axaml",
        ["graphite-teal"] = "avares://BaoWuLearn/Styles/Skins/GraphiteTeal.axaml",
        ["dusk-purple"] = "avares://BaoWuLearn/Styles/Skins/DuskPurple.axaml",
        ["morning-light"] = "avares://BaoWuLearn/Styles/Skins/MorningLight.axaml",
    };

    private static ResourceInclude? _activeSkin;
    private static string? _activeSkinId;
    private static int _activeDensity = -1;

    public static string NormalizeSkinId(string? skinId) =>
        !string.IsNullOrWhiteSpace(skinId) && SkinSources.ContainsKey(skinId)
            ? skinId
            : DefaultSkinId;

    public static int NormalizeDensity(int density) =>
        density is DensityComfortable or DensityCompact ? density : DefaultUiDensity;

    public static void Apply(string? skinId, int uiDensity)
    {
        var app = Application.Current;
        if (app is null) return;

        var skin = NormalizeSkinId(skinId);
        var density = NormalizeDensity(uiDensity);

        if (!string.Equals(_activeSkinId, skin, StringComparison.Ordinal))
        {
            if (_activeSkin is not null)
                app.Resources.MergedDictionaries.Remove(_activeSkin);

            // ★ ResourceInclude 构造函数收的是 baseUri，Source 必须另赋；
            //   只传构造参数会在 Loaded 时抛 "Source must be set"（编译期发现不了）
            var skinUri = new Uri(SkinSources[skin], UriKind.Absolute);
            _activeSkin = new ResourceInclude(skinUri) { Source = skinUri };
            app.Resources.MergedDictionaries.Add(_activeSkin);
            _activeSkinId = skin;

            var option = Skins.First(s => s.Id == skin);
            app.RequestedThemeVariant = option.Variant;
        }

        if (_activeDensity != density)
        {
            ApplyDensityResources(app.Resources, density);
            _activeDensity = density;
        }
    }

    public static void Apply(AppSettings settings) =>
        Apply(settings.SkinId, settings.UiDensity);

    private static void ApplyDensityResources(IResourceDictionary resources, int density)
    {
        var compact = density == DensityCompact;

        resources["PageMargin"] = compact ? new Thickness(12) : new Thickness(20);
        resources["CardPadding"] = compact ? new Thickness(10, 8) : new Thickness(16);
        resources["TilePadding"] = compact ? new Thickness(10, 8) : new Thickness(14, 12);
        resources["TopbarPadding"] = compact ? new Thickness(12, 7) : new Thickness(16, 10);
        resources["RowPadding"] = compact ? new Thickness(8, 5) : new Thickness(10, 8);
        resources["ButtonPadding"] = compact ? new Thickness(10, 5) : new Thickness(14, 7);
        resources["NavButtonPadding"] = compact ? new Thickness(12, 7) : new Thickness(14, 9);
        resources["ConsolePadding"] = compact ? new Thickness(12, 10) : new Thickness(16, 14);
        resources["ConsoleMargin"] = compact ? new Thickness(12, 0, 12, 12) : new Thickness(20, 0, 20, 16);
        resources["SidebarWidth"] = compact ? 176d : 200d;
        resources["CourseSidebarWidth"] = compact ? 196d : 230d;
        resources["SpacePage"] = compact ? 8d : 14d;
        resources["SpaceSection"] = compact ? 8d : 12d;
        resources["HeroNumberSize"] = compact ? 26d : 30d;
    }
}
