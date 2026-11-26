using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EfootballBot.Core.UI;

/// <summary>
/// 主题切换管理：5 套暗黑毛玻璃配色 × 2 种外观世界（默认 Apple Glass 黑玻璃 / Windows 11 云母）。
/// 1. Obsidian（黑曜极夜 · 默认）
/// 2. Silver（霜银流光）
/// 3. Violet（玄夜幻紫）
/// 4. Emerald（翡翠绿茵）
/// 5. Crimson（绯焰流火）
/// </summary>
public static class ThemeManager
{
    public const string Obsidian = "Obsidian";
    public const string Silver = "Silver";
    public const string Violet = "Violet";
    public const string Emerald = "Emerald";
    public const string Crimson = "Crimson";

    public static readonly string[] All = [Obsidian, Silver, Violet, Emerald, Crimson];

    /// <summary>视觉世界：Apple Glass（默认，近纯黑毛玻璃 + 玻璃渐变卡片 + 抖色噪点）。</summary>
    public const string WorldGlass = "Glass";
    /// <summary>视觉世界：Windows 11 云母（系统云母/亚克力 + 现代扁平卡片）。</summary>
    public const string WorldFluent = "Fluent";
    public static readonly string[] Worlds = [WorldGlass, WorldFluent];

#if DEBUG
    private static bool _validated;
#endif
    private static bool _wpfUiDarkApplied;
    private static string _activeTheme = Obsidian;
    private static readonly Lazy<Brush> _cachedNoiseBrush = new(CreateNoiseBrush);

    /// <summary>规范化主题名称；旧配置（Dark / Light / DeepBlue / Amber）及未知值回落到黑曜极夜。</summary>
    public static string NormalizeTheme(string? name)
    {
        var trimmed = name?.Trim();
        foreach (var item in All)
        {
            if (string.Equals(trimmed, item, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return Obsidian;
    }

    public static string DisplayName(string? key) => NormalizeTheme(key) switch
    {
        Silver => "霜银流光",
        Violet => "玄夜幻紫",
        Emerald => "翡翠绿茵",
        Crimson => "绯焰流火",
        _ => "黑曜极夜",
    };

    public static string WorldDisplayName(string? world) => IsGlassWorld(world)
        ? "Apple Glass"
        : "Windows 11（云母）";

    /// <summary>是否是 Apple Glass 世界（空值或未知值一律视为默认的 Glass）。</summary>
    public static bool IsGlassWorld(string? world)
        => !string.Equals(world?.Trim(), WorldFluent, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeWorld(string? world) => IsGlassWorld(world) ? WorldGlass : WorldFluent;

    /// <summary>把指定暗黑主题 + 外观世界的所有 Token 颜色写入 Application.Current.Resources。</summary>
    public static void ApplyTheme(string? themeName, string? world = null)
    {
        bool glass = IsGlassWorld(world);
        var normalized = NormalizeTheme(themeName);
        _activeTheme = normalized;
        var source = ThemePalette.For(normalized, world);

        // 对齐同层轨道 token，避免滚动条/页签轨道与栏底出现色差接缝。
        var colors = new Dictionary<string, string>(source, StringComparer.Ordinal);
        foreach (var key in new[] { "NavSectionBg", "TrackBg", "TabTrackBg" })
            colors[key] = colors["TrackBg"];

#if DEBUG
        if (!_validated && ThemePalette.Validate() is { Length: > 0 } problems)
        {
            _validated = true;
            var msg = "主题 Token 不完整：" + string.Join("；", problems);
            global::System.Diagnostics.Debug.WriteLine(msg);
            try
            {
                global::EfootballBot.Core.Logging.FileLogger.Instance
                    .Write(msg, global::EfootballBot.Core.LogLevel.Warn);
            }
            catch { /* 日志未初始化：忽略 */ }
        }
#endif

        var app = Application.Current.Resources;
        foreach (var (key, hex) in colors)
        {
            var color = ParseColor(hex, key);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app[key] = brush;
            app[key + "Color"] = color;
        }

        // ---- 世界级复合资源（渐变色块 / 投影 / 主题辉光 / 噪点）----
        app["GlassFill"] = BuildGradient(colors, "GlassFillTop", "GlassFillMid", "GlassFillBottom", 0.0, 0.16, 1.0);
        app["PrimaryBtnFill"] = BuildGradient(colors, "PrimaryBtnFrom", "PrimaryBtnMid", "PrimaryBtnTo", 0.0, 0.55, 1.0);
        if (glass)
        {
            var cardShadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 4, Opacity = 0.36 };
            cardShadow.Freeze();
            app["CardShadowEffect"] = cardShadow;
        }
        else
        {
            app["CardShadowEffect"] = null;
        }

        var accentColor = ParseColor(colors.GetValueOrDefault("Accent"), "Accent");
        var accentShadow = new DropShadowEffect
        {
            Color = Color.FromArgb(0x55, accentColor.R, accentColor.G, accentColor.B),
            BlurRadius = 18,
            ShadowDepth = 0,
            Opacity = 0.32,
        };
        accentShadow.Freeze();
        app["AccentShadow"] = accentShadow;
        app["DitherNoiseBrush"] = glass ? _cachedNoiseBrush.Value : Brushes.Transparent;

        // 5 套全部为暗黑主题：首次应用即可，避免每次切主题重复触发全库资源重刷
        if (!_wpfUiDarkApplied)
        {
            try
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                _wpfUiDarkApplied = true;
            }
            catch
            {
                // 主题服务尚未初始化时忽略：自定义 Token 仍然生效
            }
        }
    }

    /// <summary>解析 #RRGGBB / #AARRGGBB；非法值返回透明并写调试输出，绝不让主题切换抛异常。</summary>
    private static Color ParseColor(string? hex, string key)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color c) return c;
            }
            catch { /* 非法颜色串：走下面的降级 */ }
        }
        global::System.Diagnostics.Debug.WriteLine($"[theme] 颜色 Token 非法：{key}={hex}");
        return Colors.Transparent;
    }

    private static LinearGradientBrush BuildGradient(
        IReadOnlyDictionary<string, string> colors, string from, string mid, string to,
        double at0, double at1, double at2,
        Point? startPoint = null, Point? endPoint = null)
    {
        Color Pick(string key) => colors.TryGetValue(key, out var hex)
            ? ParseColor(hex, key)
            : Colors.Transparent;

        var brush = new LinearGradientBrush
        {
            StartPoint = startPoint ?? new Point(0, 0),
            EndPoint = endPoint ?? new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(Pick(from), at0));
        brush.GradientStops.Add(new GradientStop(Pick(mid), at1));
        brush.GradientStops.Add(new GradientStop(Pick(to), at2));
        brush.Freeze();
        return brush;
    }

    /// <summary>抖色噪点：打散 Acrylic 深色背景的色带（banding），只贴在窗口最底层、不参与命中测试。</summary>
    private static Brush CreateNoiseBrush()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri("pack://application:,,,/Assets/dither-noise.png", UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 128, 128),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    /// <summary>
    /// 窗口材质（Acrylic / Mica / Tabbed）。必须在 <see cref="ApplyTheme"/> 之后调用。
    /// Glass 世界默认强制 Acrylic + 0x8C（近纯黑毛玻璃透出壁纸）；
    /// Fluent 世界使用 Mica + 0xF2（柔和云母）。
    /// </summary>
    public static bool ApplyWindowBackdrop(Window? window, string? preference, string? world = null)
    {
        if (window is null || Application.Current is null) return false;

        var want = (preference ?? "Auto").Trim().ToLowerInvariant() switch
        {
            "none" => WindowBackdropType.None,
            "acrylic" => WindowBackdropType.Acrylic,
            "tabbed" => WindowBackdropType.Tabbed,
            "mica" => WindowBackdropType.Mica,
            _ => WindowBackdropType.Mica,
        };

        if (IsGlassWorld(world) && want is WindowBackdropType.Mica or WindowBackdropType.Auto)
            want = WindowBackdropType.Acrylic;
        else if (!IsGlassWorld(world) && want == WindowBackdropType.Acrylic)
            want = WindowBackdropType.Mica;

        byte shellAlpha = IsGlassWorld(world)
            ? (_activeTheme == Obsidian ? (byte)0x8C : (byte)0x9A)
            : (byte)0xF2;

        try
        {
            if (want != WindowBackdropType.None && !WindowBackdrop.IsSupported(want))
                want = WindowBackdropType.None;

            if (want == WindowBackdropType.None)
            {
                WindowBackdrop.RemoveBackdrop(window);
                if (Application.Current.Resources["WindowBg"] is Brush solid) window.Background = solid;
                return false;
            }

            WindowBackdrop.ApplyBackdrop(window, want);

            if (Application.Current.Resources["WindowBg"] is SolidColorBrush cur)
            {
                var glass = new SolidColorBrush(Color.FromArgb(shellAlpha, cur.Color.R, cur.Color.G, cur.Color.B));
                glass.Freeze();
                Application.Current.Resources["WindowBg"] = glass;
                window.Background = glass;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取某主题（可指定世界）下某个 Token 的颜色。</summary>
    public static Brush GetBrush(string themeName, string token, string? world = null)
    {
        var colors = ThemePalette.For(themeName, world);
        if (!colors.TryGetValue(token, out var hex)) return Brushes.Transparent;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
