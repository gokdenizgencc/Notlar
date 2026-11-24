using Notlar.Core;

namespace Notlar.Core.Tests;

public class AccentPaletteTests
{
    public static TheoryData<string, bool> SeedsAndThemes()
    {
        var data = new TheoryData<string, bool>();
        string[] seeds =
        [
            "#000000", "#FFFFFF", "#FFFF00", "#00FFFF", "#0078D4", "#0F6CBD", "#4CC2FF", "#808080",
            "#FF0000", "#00FF00", "#0000FF", "#1A1A1A", "#F3F3F3", "#202020",
        ];
        foreach (var hex in seeds.Concat(AppearanceOptions.Presets.Select(p => p.Hex)))
        {
            data.Add(hex, true);
            data.Add(hex, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SeedsAndThemes))]
    public void Derive_meets_text_contrast_everywhere(string seedHex, bool light)
    {
        var p = AccentPalette.Derive(Rgb.Parse(seedHex), light);
        AssertReadable(p, light);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Default_palette_is_unchanged_and_readable(bool light)
    {
        var p = AccentPalette.Default(light);
        Assert.Equal(light ? "#0F6CBD" : "#4CC2FF", p.Accent.ToHex());
        Assert.Equal(light ? "#FFFFFF" : "#002033", p.OnAccent.ToHex());
        Assert.Equal(light ? "#E6F0FA" : "#1F3A52", p.TagBg.ToHex());
        Assert.Equal(light ? "#DCE9F7" : "#2B3A45", p.NavActiveBg.ToHex());
        AssertReadable(p, light);
    }

    [Fact]
    public void Derive_darkens_in_light_theme_and_lightens_in_dark_theme()
    {
        var yellow = Rgb.Parse("#FFFF00");
        var light = AccentPalette.Derive(yellow, light: true).Accent;
        Assert.True(ColorMath.RelativeLuminance(light) < ColorMath.RelativeLuminance(yellow));

        var navy = Rgb.Parse("#001060");
        var dark = AccentPalette.Derive(navy, light: false).Accent;
        Assert.True(ColorMath.RelativeLuminance(dark) > ColorMath.RelativeLuminance(navy));
    }

    [Fact]
    public void Derive_keeps_seed_when_already_readable()
    {
        var seed = Rgb.Parse("#005A9E");
        Assert.Equal(seed, AccentPalette.Derive(seed, light: true).Accent);
        var lightSeed = Rgb.Parse("#99EBFF");
        Assert.Equal(lightSeed, AccentPalette.Derive(lightSeed, light: false).Accent);
    }

    [Fact]
    public void Derive_keeps_hue_direction()
    {
        // Kırmızı koyu temada açılır ama kırmızı kalır (R baskın).
        var p = AccentPalette.Derive(Rgb.Parse("#C00000"), light: false).Accent;
        Assert.True(p.R > p.G && p.R > p.B);
    }

    [Fact]
    public void Contrast_ratio_matches_wcag_extremes()
    {
        Assert.Equal(21.0, ColorMath.ContrastRatio(Rgb.Black, Rgb.White), 3);
        Assert.Equal(1.0, ColorMath.ContrastRatio(Rgb.Parse("#777777"), Rgb.Parse("#777777")), 3);
        // Bilinen değer: #767676 beyaz üzerinde ≈ 4,54.
        Assert.Equal(4.54, ColorMath.ContrastRatio(Rgb.Parse("#767676"), Rgb.White), 2);
    }

    [Fact]
    public void Mix_interpolates_and_clamps()
    {
        Assert.Equal(Rgb.Parse("#808080"), ColorMath.Mix(Rgb.Black, Rgb.White, 0.5));
        Assert.Equal(Rgb.White, ColorMath.Mix(Rgb.Black, Rgb.White, 2));
        Assert.Equal(Rgb.Black, ColorMath.Mix(Rgb.Black, Rgb.White, -1));
    }

    [Theory]
    [InlineData("#0f6cbd", true, "#0F6CBD")]
    [InlineData(" #ABCDEF ", true, "#ABCDEF")]
    [InlineData("0F6CBD", false, null)]
    [InlineData("#0F6CB", false, null)]
    [InlineData("#GGGGGG", false, null)]
    [InlineData(null, false, null)]
    public void Rgb_TryParse(string? text, bool ok, string? expected)
    {
        Assert.Equal(ok, Rgb.TryParse(text, out var c));
        if (ok) Assert.Equal(expected, c.ToHex());
    }

    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("Light", "light")]
    [InlineData("dark", "dark")]
    [InlineData("pembe", "system")]
    public void NormalizeThemeMode_defaults_to_system(string? value, string expected) =>
        Assert.Equal(expected, AppearanceOptions.NormalizeThemeMode(value));

    [Theory]
    [InlineData(null, "comfortable")]
    [InlineData("COMPACT", "compact")]
    [InlineData("xyz", "comfortable")]
    public void NormalizeDensity_defaults_to_comfortable(string? value, string expected) =>
        Assert.Equal(expected, AppearanceOptions.NormalizeDensity(value));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Windows", "windows")]
    [InlineData("#8764b8", "#8764B8")]
    [InlineData("mavi", "")]
    public void NormalizeAccent_accepts_default_windows_or_hex(string? value, string expected) =>
        Assert.Equal(expected, AppearanceOptions.NormalizeAccent(value));

    [Fact]
    public void ResolveLight_follows_system_only_in_system_mode()
    {
        Assert.True(AppearanceOptions.ResolveLight("system", systemIsLight: true));
        Assert.False(AppearanceOptions.ResolveLight(null, systemIsLight: false));
        Assert.True(AppearanceOptions.ResolveLight("light", systemIsLight: false));
        Assert.False(AppearanceOptions.ResolveLight("dark", systemIsLight: true));
    }

    [Fact]
    public void ResolvePalette_handles_default_windows_and_hex()
    {
        Assert.Equal(AccentPalette.Default(true), AppearanceOptions.ResolvePalette("", true, Rgb.Parse("#FF0000")));
        // Windows rengi okunamadıysa varsayılana düşer.
        Assert.Equal(AccentPalette.Default(false), AppearanceOptions.ResolvePalette("windows", false, null));
        var w = Rgb.Parse("#0078D4");
        Assert.Equal(AccentPalette.Derive(w, false), AppearanceOptions.ResolvePalette("windows", false, w));
        Assert.Equal(AccentPalette.Derive(Rgb.Parse("#8764B8"), true),
            AppearanceOptions.ResolvePalette("#8764b8", true, null));
    }

    [Fact]
    public void Presets_are_distinct_valid_colors()
    {
        Assert.InRange(AppearanceOptions.Presets.Count, 8, 10);
        Assert.All(AppearanceOptions.Presets, p => Assert.True(Rgb.TryParse(p.Hex, out _)));
        Assert.Equal(AppearanceOptions.Presets.Count, AppearanceOptions.Presets.Select(p => p.Hex).Distinct().Count());
    }

    private static void AssertReadable(AccentPalette p, bool light)
    {
        const double min = AccentPalette.MinTextContrast;
        var bg = AccentPalette.Bg(light);
        var surface = AccentPalette.Surface(light);
        var fg = AccentPalette.Fg(light);

        Assert.True(ColorMath.ContrastRatio(p.Accent, bg) >= min, $"Accent/Bg {p.Accent}");
        Assert.True(ColorMath.ContrastRatio(p.Accent, surface) >= min, $"Accent/Surface {p.Accent}");
        Assert.True(ColorMath.ContrastRatio(p.Accent, p.TagBg) >= min, $"Accent/TagBg {p.Accent} {p.TagBg}");
        Assert.True(ColorMath.ContrastRatio(p.OnAccent, p.Accent) >= min, $"OnAccent/Accent {p.OnAccent} {p.Accent}");
        Assert.True(ColorMath.ContrastRatio(fg, p.TagBg) >= min, $"Fg/TagBg {p.TagBg}");
        Assert.True(ColorMath.ContrastRatio(fg, p.NavActiveBg) >= min, $"Fg/NavActiveBg {p.NavActiveBg}");
    }
}
