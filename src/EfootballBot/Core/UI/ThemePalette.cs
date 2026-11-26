namespace EfootballBot.Core.UI;

/// <summary>
/// 5 套主题 × 2 种外观世界（Apple Glass 毛玻璃 / Windows 11 云母）调色板数据：
/// 1. Obsidian（黑曜极夜 · 默认，纯黑毛玻璃，无紫无蓝）
/// 2. Silver（霜银流光 · 冷调银灰毛玻璃，极简工业质感）
/// 3. Violet（玄夜幻紫 · 幽邃暗紫毛玻璃，幻紫霓虹点缀）
/// 4. Emerald（翡翠绿茵 · 深翡翠绿茵毛玻璃，战术仪表盘）
/// 5. Crimson（绯焰流火 · 暗酒红绯焰毛玻璃，电竞锋芒）
/// </summary>
public static class ThemePalette
{
    private static readonly Dictionary<string, string> ObsidianPalette = BuildDarkPalette(
        windowBg: "#FF08080A",
        contentBg: "#F208080A",
        dialogBg: "#F5141418",
        glassBg: "#D9101012",
        comboPopupBg: "#F0151518",
        chromeGlass: "#4D08080A",
        accent: "#FFF2F2F7",
        accentHover: "#FFFFFFFF",
        accentGlow: "#FFD4D4DC",
        accentLight: "#26FFFFFF",
        accentBorder: "#4DFFFFFF",
        accentSoft: "#1AFFFFFF",
        accentBorderStrong: "#78FFFFFF",
        textOnAccent: "#FF111113",
        logTag: "#FFD4D4DC",
        brandFrom: "#FF3A3A42",
        brandTo: "#FF1C1C22",
        bannerFrom: "#FF2C2C34",
        bannerTo: "#FF18181E",
        heroGlowFrom: "#16FFFFFF",
        heroGlowFade: "#00FFFFFF",
        heroTitleMid: "#FFE5E5EA",
        heroTitleTo: "#FFA1A1AA",
        toggleOn: "#FF30D158",
        toggleThumbOn: "#FFFFFFFF",
        primaryBtnFrom: "#4AFFFFFF",
        primaryBtnMid: "#36FFFFFF",
        primaryBtnTo: "#28FFFFFF",
        primaryBtnStroke: "#7AFFFFFF",
        sideLeftBg: "#59000000",
        sideRightBg: "#42000000",
        cardBg: "#12FFFFFF");

    private static readonly Dictionary<string, string> SilverPalette = BuildDarkPalette(
        windowBg: "#FF2A2C32",
        contentBg: "#E82A2C32",
        dialogBg: "#F22E3038",
        glassBg: "#D926282E",
        comboPopupBg: "#F02C2E36",
        chromeGlass: "#6B343740",
        accent: "#FFE4E4E7",
        accentHover: "#FFFFFFFF",
        accentGlow: "#FFF4F4F5",
        accentLight: "#2EFFFFFF",
        accentBorder: "#55FFFFFF",
        accentSoft: "#1FFFFFFF",
        accentBorderStrong: "#88FFFFFF",
        textOnAccent: "#FF111113",
        logTag: "#FFD4D4D8",
        brandFrom: "#FFF4F4F5",
        brandTo: "#FFA1A1AA",
        bannerFrom: "#FF3F3F46",
        bannerTo: "#FF27272A",
        heroGlowFrom: "#1AFFFFFF",
        heroGlowFade: "#00FFFFFF",
        heroTitleMid: "#FFE4E4E7",
        heroTitleTo: "#FFA1A1AA",
        toggleOn: "#FFE4E4E7",
        toggleThumbOn: "#FF111113",
        primaryBtnFrom: "#52F4F4F8",
        primaryBtnMid: "#3CE4E4EC",
        primaryBtnTo: "#2CD4D4DC",
        primaryBtnStroke: "#88FFFFFF",
        cardBorder: "#2CFFFFFF",
        glassFillTop: "#22FFFFFF",
        glassFillMid: "#1AFFFFFF",
        glassFillBottom: "#14FFFFFF",
        sideLeftBg: "#663A3D46",
        sideRightBg: "#5432353E",
        cardBg: "#1FFFFFFF");

    private static readonly Dictionary<string, string> VioletPalette = BuildDarkPalette(
        windowBg: "#FF1D112C",
        contentBg: "#EA1D112C",
        dialogBg: "#F4221433",
        glassBg: "#D9180E25",
        comboPopupBg: "#F01E122E",
        chromeGlass: "#6B27173B",
        accent: "#FFA855F7",
        accentHover: "#FFC084FC",
        accentGlow: "#FFD8B4FE",
        accentLight: "#33A855F7",
        accentBorder: "#66C084FC",
        accentSoft: "#24A855F7",
        accentBorderStrong: "#99D8B4FE",
        textOnAccent: "#FFFFFFFF",
        logTag: "#FFD8B4FE",
        brandFrom: "#FFC084FC",
        brandTo: "#FF7E22CE",
        bannerFrom: "#FF7E22CE",
        bannerTo: "#FF581C87",
        heroGlowFrom: "#1FA855F7",
        heroGlowFade: "#00A855F7",
        heroTitleMid: "#FFF3E8FF",
        heroTitleTo: "#FFD8B4FE",
        toggleOn: "#FFA855F7",
        toggleThumbOn: "#FFFFFFFF",
        primaryBtnFrom: "#6BC084FC",
        primaryBtnMid: "#52A855F7",
        primaryBtnTo: "#3E9333EA",
        primaryBtnStroke: "#99D8B4FE",
        cardBorder: "#3EC084FC",
        glassFillTop: "#28C084FC",
        glassFillMid: "#16A855F7",
        glassFillBottom: "#0E1D112C",
        sideLeftBg: "#662A1840",
        sideRightBg: "#54221334",
        cardBg: "#1CA855F7");

    private static readonly Dictionary<string, string> EmeraldPalette = BuildDarkPalette(
        windowBg: "#FF0D261D",
        contentBg: "#EA0D261D",
        dialogBg: "#F4112E23",
        glassBg: "#D90B2018",
        comboPopupBg: "#F00F291F",
        chromeGlass: "#6B103225",
        accent: "#FF10B981",
        accentHover: "#FF34D399",
        accentGlow: "#FF6EE7B7",
        accentLight: "#3310B981",
        accentBorder: "#6634D399",
        accentSoft: "#2410B981",
        accentBorderStrong: "#996EE7B7",
        textOnAccent: "#FFFFFFFF",
        logTag: "#FF6EE7B7",
        brandFrom: "#FF34D399",
        brandTo: "#FF059669",
        bannerFrom: "#FF059669",
        bannerTo: "#FF047857",
        heroGlowFrom: "#1F10B981",
        heroGlowFade: "#0010B981",
        heroTitleMid: "#FFD1FAE5",
        heroTitleTo: "#FF6EE7B7",
        toggleOn: "#FF10B981",
        toggleThumbOn: "#FFFFFFFF",
        primaryBtnFrom: "#6634D399",
        primaryBtnMid: "#4D10B981",
        primaryBtnTo: "#3A059669",
        primaryBtnStroke: "#946EE7B7",
        cardBorder: "#3B34D399",
        glassFillTop: "#2834D399",
        glassFillMid: "#1610B981",
        glassFillBottom: "#0E0D261D",
        sideLeftBg: "#66103628",
        sideRightBg: "#540D2C21",
        cardBg: "#1C10B981");

    private static readonly Dictionary<string, string> CrimsonPalette = BuildDarkPalette(
        windowBg: "#FF2B0F18",
        contentBg: "#EA2B0F18",
        dialogBg: "#F430111B",
        glassBg: "#D9220B12",
        comboPopupBg: "#F02B0F18",
        chromeGlass: "#6B38121F",
        accent: "#FFE11D48",
        accentHover: "#FFF43F5E",
        accentGlow: "#FFFB7185",
        accentLight: "#33E11D48",
        accentBorder: "#66F43F5E",
        accentSoft: "#24E11D48",
        accentBorderStrong: "#99FB7185",
        textOnAccent: "#FFFFFFFF",
        logTag: "#FFFDA4AF",
        brandFrom: "#FFF43F5E",
        brandTo: "#FFBE123C",
        bannerFrom: "#FFBE123C",
        bannerTo: "#FF881337",
        heroGlowFrom: "#1FE11D48",
        heroGlowFade: "#00E11D48",
        heroTitleMid: "#FFFFE4E6",
        heroTitleTo: "#FFFB7185",
        toggleOn: "#FFE11D48",
        toggleThumbOn: "#FFFFFFFF",
        primaryBtnFrom: "#6BF43F5E",
        primaryBtnMid: "#52E11D48",
        primaryBtnTo: "#3EBE123C",
        primaryBtnStroke: "#99FB7185",
        danger: "#FFFF5C3A",
        dangerHover: "#FFFF7A5C",
        dangerText: "#FFFF8A70",
        dangerBorder: "#66FF5C3A",
        dangerSoft: "#26FF5C3A",
        cardBorder: "#48F43F5E",
        glassFillTop: "#28FB7185",
        glassFillMid: "#16E11D48",
        glassFillBottom: "#0E2B0F18",
        sideLeftBg: "#663D1422",
        sideRightBg: "#5431101B",
        cardBg: "#1CE11D48");

    /// <summary>按 (主题名, 外观世界) 取调色板；默认回落到黑曜极夜 / Apple Glass。</summary>
    public static IReadOnlyDictionary<string, string> For(string? themeName, string? world = null)
    {
        var baseDict = ThemeManager.NormalizeTheme(themeName) switch
        {
            ThemeManager.Silver => SilverPalette,
            ThemeManager.Violet => VioletPalette,
            ThemeManager.Emerald => EmeraldPalette,
            ThemeManager.Crimson => CrimsonPalette,
            _ => ObsidianPalette,
        };
        var colors = new Dictionary<string, string>(baseDict, StringComparer.Ordinal);
        if (!ThemeManager.IsGlassWorld(world))
        {
            // Fluent（Win11 云母）外观世界：扁平卡片、透明顶栏、主题色分类边框（主按钮保持通透玻璃渐变与高亮描边）
            colors["SelectBorder"] = "#00000000";
            colors["SubTabSelFill"] = colors["Surface2"];
            colors["SubTabSelStroke"] = colors["AccentBorder"];
            colors["GlassFillTop"] = colors["CardBg"];
            colors["GlassFillMid"] = colors["CardBg"];
            colors["GlassFillBottom"] = colors["CardBg"];
            colors["TopBarBg"] = "#00000000";
            colors["ChromeGlass"] = "#00000000";
        }
        return colors;
    }

    /// <summary>自检：2 世界 × 5 主题共 10 组调色板的 Token 键集必须完全一致。返回空数组表示完整。</summary>
    public static string[] Validate()
    {
        var reference = For(ThemeManager.Obsidian, ThemeManager.WorldGlass);
        var problems = new List<string>();
        foreach (var world in ThemeManager.Worlds)
        {
            foreach (var theme in ThemeManager.All)
            {
                var other = For(theme, world);
                string label = $"{ThemeManager.WorldDisplayName(world)}/{ThemeManager.DisplayName(theme)}";
                problems.AddRange(reference.Keys.Except(other.Keys).Select(k => $"{label} 缺 {k}"));
                problems.AddRange(other.Keys.Except(reference.Keys).Select(k => $"{label} 多 {k}"));
            }
        }
        return [.. problems];
    }

    private static Dictionary<string, string> BuildDarkPalette(
        string windowBg,
        string contentBg,
        string dialogBg,
        string glassBg,
        string comboPopupBg,
        string chromeGlass,
        string accent,
        string accentHover,
        string accentGlow,
        string accentLight,
        string accentBorder,
        string accentSoft,
        string accentBorderStrong,
        string textOnAccent,
        string logTag,
        string brandFrom,
        string brandTo,
        string bannerFrom,
        string bannerTo,
        string heroGlowFrom,
        string heroGlowFade,
        string heroTitleMid,
        string heroTitleTo,
        string toggleOn,
        string toggleThumbOn,
        string? primaryBtnFrom = null,
        string? primaryBtnMid = null,
        string? primaryBtnTo = null,
        string? primaryBtnStroke = null,
        string textOnPrimary = "#FFFFFFFF",
        string danger = "#FFEF4444",
        string dangerHover = "#FFF87171",
        string dangerText = "#FFFCA5A5",
        string dangerBorder = "#66EF4444",
        string dangerSoft = "#26EF4444",
        string cardBorder = "#20FFFFFF",
        string glassFillTop = "#14FFFFFF",
        string glassFillMid = "#0DFFFFFF",
        string glassFillBottom = "#09FFFFFF",
        string sideLeftBg = "#59000000",
        string sideRightBg = "#42000000",
        string cardBg = "#12FFFFFF")
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBg"] = windowBg,
            ["ContentBg"] = contentBg,
            ["CardBg"] = cardBg,
            ["ComboBg"] = "#2A242428",
            ["ComboPopupBg"] = comboPopupBg,
            ["CardBorder"] = cardBorder,
            ["SideLeftBg"] = sideLeftBg,
            ["SideRightBg"] = sideRightBg,
            ["Surface2"] = "#1CFFFFFF",
            ["InsetBg"] = "#0A000000",
            ["TrackBg"] = "#4D000000",
            ["GlassBg"] = glassBg,
            ["GlassStroke"] = "#24FFFFFF",
            ["DialogBg"] = dialogBg,
            ["ControlFill"] = "#18FFFFFF",
            ["ControlStroke"] = "#2EFFFFFF",
            ["ChipFill"] = "#3D000000",
            ["ChipStroke"] = "#3DFFFFFF",
            ["ChromeGlass"] = chromeGlass,
            ["TopBarBg"] = chromeGlass,

            ["TextPrimary"] = "#FFFFFFFF",
            ["TextSecondary"] = "#FFE5E5EA",
            ["TextMuted"] = "#FF98989F",
            ["NavLabelFg"] = "#FF98989F",
            ["NavDescFg"] = "#FFB8B8BF",
            ["SegTabFg"] = "#FFE5E5EA",
            ["SegTabHover"] = "#1AFFFFFF",
            ["SegTabHoverFg"] = "#FFFFFFFF",
            ["LogInfo"] = "#FFE5E5EA",
            ["LogTime"] = "#FF7E8694",
            ["LogTag"] = logTag,

            ["SelectFill"] = "#26FFFFFF",
            ["SelectFillStrong"] = "#36FFFFFF",
            ["SelectBorder"] = "#38FFFFFF",
            ["SelectFg"] = "#FFFFFFFF",
            ["SelectBar"] = accent,
            ["SubTabSelFill"] = "#5C42424A",
            ["SubTabSelStroke"] = "#4DFFFFFF",

            ["Accent"] = accent,
            ["AccentHover"] = accentHover,
            ["AccentGlow"] = accentGlow,
            ["AccentLight"] = accentLight,
            ["AccentBorder"] = accentBorder,
            ["AccentSoft"] = accentSoft,
            ["AccentBorderStrong"] = accentBorderStrong,
            ["TextOnAccent"] = textOnAccent,

            ["PrimaryBtnFrom"] = primaryBtnFrom ?? accent,
            ["PrimaryBtnMid"] = primaryBtnMid ?? accent,
            ["PrimaryBtnTo"] = primaryBtnTo ?? brandTo,
            ["TextOnPrimary"] = textOnPrimary,
            ["PrimaryBtnStroke"] = primaryBtnStroke ?? "#78FFFFFF",
            ["BtnFlash"] = "#FFFFFFFF",

            ["GlassFillTop"] = glassFillTop,
            ["GlassFillMid"] = glassFillMid,
            ["GlassFillBottom"] = glassFillBottom,

            ["Danger"] = danger,
            ["DangerHover"] = dangerHover,
            ["DangerText"] = dangerText,
            ["DangerBorder"] = dangerBorder,
            ["DangerSoft"] = dangerSoft,
            ["TextOnDanger"] = "#FFFFFFFF",
            ["Warning"] = "#FFF59E0B",
            ["Success"] = "#FF10B981",
            ["Info"] = accentGlow,
            ["Amber"] = "#FFF59E0B",
            ["Orange"] = "#FFFB923C",
            ["Teal"] = "#FF2DD4BF",

            ["InputBg"] = "#1FFFFFFF",
            ["InputBorder"] = "#33FFFFFF",
            ["HoverOverlay"] = "#1AFFFFFF",
            ["ScrollThumb"] = "#66FFFFFF",
            ["ToggleOff"] = "#38FFFFFF",
            ["ToggleOn"] = toggleOn,
            ["ToggleThumbOn"] = toggleThumbOn,
            ["ModuleDot"] = "#FF8E8E93",
            ["EnvDotIdle"] = "#FF8E8E93",

            ["Divider"] = "#29FFFFFF",
            ["HairlineStrong"] = "#3DFFFFFF",
            ["Scrim"] = "#F0000000",
            ["ScrimSoft"] = "#CC000000",
            ["PreviewBg"] = "#FF000000",
            ["NavSectionBg"] = "#33FFFFFF",
            ["NavSectionBorder"] = "#1FFFFFFF",
            ["TabTrackBg"] = "#4D000000",
            ["IntlTabBg"] = "#4D000000",
            ["IntlTabBorder"] = "#1FFFFFFF",

            ["BrandFrom"] = brandFrom,
            ["BrandTo"] = brandTo,
            ["BannerFrom"] = bannerFrom,
            ["BannerTo"] = bannerTo,
            ["TextOnBanner"] = "#FFFFFFFF",
            ["HeroGlowFrom"] = heroGlowFrom,
            ["HeroGlowFade"] = heroGlowFade,
            ["HeroTitleFrom"] = "#FFFFFFFF",
            ["HeroTitleMid"] = heroTitleMid,
            ["HeroTitleTo"] = heroTitleTo,

            ["TooltipBg"] = "#F2242428",
            ["TooltipBorder"] = "#33FFFFFF",
            ["TooltipFg"] = "#FFFFFFFF",
        };
    }
}
