using EfootballBot.Core.UI;
using Xunit;

namespace EfootballBot.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void ThemeOrderAndWorldsMatchExpectedDefaults()
    {
        Assert.Equal(
            [ThemeManager.Obsidian, ThemeManager.Silver, ThemeManager.Violet, ThemeManager.Emerald, ThemeManager.Crimson],
            ThemeManager.All);
        Assert.Equal([ThemeManager.WorldGlass, ThemeManager.WorldFluent], ThemeManager.Worlds);
        Assert.True(ThemeManager.IsGlassWorld(null));
        Assert.True(ThemeManager.IsGlassWorld(""));
        Assert.Equal(ThemeManager.WorldGlass, ThemeManager.NormalizeWorld(null));
        Assert.Equal(ThemeManager.Obsidian, ThemeManager.NormalizeTheme("Dark"));
        Assert.Equal(ThemeManager.Obsidian, ThemeManager.NormalizeTheme("Light"));
        Assert.Equal(ThemeManager.Obsidian, ThemeManager.NormalizeTheme("DeepBlue"));
        Assert.Equal(ThemeManager.Obsidian, ThemeManager.NormalizeTheme("Amber"));
        Assert.Equal(ThemeManager.Obsidian, ThemeManager.NormalizeTheme(null));
        foreach (var theme in ThemeManager.All)
            Assert.Equal(4, ThemeManager.DisplayName(theme).Length);
        Assert.Equal("玄夜幻紫", ThemeManager.DisplayName(ThemeManager.Violet));
    }

    [Fact]
    public void AllPalettesHaveIdenticalTokenKeys()
    {
        var problems = ThemePalette.Validate();
        Assert.Empty(problems);
    }

    [Fact]
    public void SemanticTokensExistAndZeroBlueOrPurpleResidualInAllCombinations()
    {
        var required = new[] { "TextOnDanger", "ToggleThumbOn", "DialogBg", "LogTime", "LogTag", "TextOnAccent" };
        var bannedBlueColors = new[] { "#FF2563EB", "#FF3B82F6", "#FF60A5FA", "#FF0A84FF", "#FF60CDFF" };
        var bannedPurpleColors = new[] { "#FF9333EA", "#FFA855F7", "#FFC084FC", "#FF7E22CE", "#FFD8B4FE" };

        foreach (var world in ThemeManager.Worlds)
        {
            foreach (var theme in ThemeManager.All)
            {
                var dict = ThemePalette.For(theme, world);
                foreach (var key in required)
                    Assert.True(dict.ContainsKey(key), $"{world}/{theme} missing token {key}");

                Assert.NotEqual(dict["ToggleOff"], dict["ToggleOn"]);
                foreach (var banned in bannedBlueColors)
                    Assert.DoesNotContain(banned, dict.Values);
                if (theme != ThemeManager.Violet)
                {
                    foreach (var banned in bannedPurpleColors)
                        Assert.DoesNotContain(banned, dict.Values);
                }
            }
        }
    }

    [Fact]
    public void EachThemeHasDistinctTintedGlassBackground()
    {
        var windowBgs = ThemeManager.All.Select(t => ThemePalette.For(t, ThemeManager.WorldGlass)["WindowBg"]).ToList();
        var sideBgs = ThemeManager.All.Select(t => ThemePalette.For(t, ThemeManager.WorldGlass)["SideLeftBg"]).ToList();

        Assert.Equal(5, windowBgs.Distinct().Count());
        Assert.Equal(5, sideBgs.Distinct().Count());
        Assert.Equal("#FF08080A", windowBgs[0]); // Obsidian 默认纯黑毛玻璃
    }

    [Fact]
    public void HighContrastTextOnAccentMatchesThemeLuminance()
    {
        var obsidian = ThemePalette.For(ThemeManager.Obsidian, ThemeManager.WorldGlass);
        var silver = ThemePalette.For(ThemeManager.Silver, ThemeManager.WorldGlass);
        var violet = ThemePalette.For(ThemeManager.Violet, ThemeManager.WorldGlass);
        var emerald = ThemePalette.For(ThemeManager.Emerald, ThemeManager.WorldGlass);
        var crimson = ThemePalette.For(ThemeManager.Crimson, ThemeManager.WorldGlass);

        Assert.NotEqual("#FFFFFFFF", obsidian["TextOnAccent"]);
        Assert.NotEqual("#FFFFFFFF", silver["TextOnAccent"]);
        Assert.Equal("#FFFFFFFF", violet["TextOnAccent"]);
        Assert.Equal("#FFFFFFFF", emerald["TextOnAccent"]);
        Assert.Equal("#FFFFFFFF", crimson["TextOnAccent"]);
    }

    [Fact]
    public void PrimaryButtonIsTranslucentAndReadableAcrossAllThemes()
    {
        foreach (var world in ThemeManager.Worlds)
        {
            foreach (var theme in ThemeManager.All)
            {
                var dict = ThemePalette.For(theme, world);
                foreach (var key in new[] { "PrimaryBtnFrom", "PrimaryBtnMid", "PrimaryBtnTo" })
                {
                    var hex = dict[key];
                    Assert.False(hex.StartsWith("#FF", StringComparison.OrdinalIgnoreCase), $"{world}/{theme}/{key} should be translucent, got {hex}");
                    Assert.False(hex.StartsWith("#00", StringComparison.OrdinalIgnoreCase), $"{world}/{theme}/{key} should not be invisible, got {hex}");
                }
                Assert.Equal("#FFFFFFFF", dict["TextOnPrimary"]);
                Assert.NotEqual("#3DFFFFFF", dict["GlassFillTop"]);
            }
        }
    }
}
