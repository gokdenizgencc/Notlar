using System.Globalization;

namespace Notlar.Core;

/// <summary>sRGB renk (alfa yok). "#RRGGBB" biçiminde okunur/yazılır.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (text is null) return false;
        var s = text.Trim();
        if (s.Length != 7 || s[0] != '#') return false;
        if (!int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        color = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static Rgb Parse(string text) =>
        TryParse(text, out var color) ? color : throw new FormatException($"Geçersiz renk kodu: {text}");

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();
}

/// <summary>WCAG 2.x göreli parlaklık / kontrast hesapları ve renk karıştırma.</summary>
public static class ColorMath
{
    /// <summary>WCAG göreli parlaklık (0 = siyah, 1 = beyaz).</summary>
    public static double RelativeLuminance(Rgb c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    /// <summary>İki renk arasındaki WCAG kontrast oranı (1–21). Sıra önemsizdir.</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="from"/>'dan <paramref name="to"/>'ya <paramref name="t"/> (0–1) oranında doğrusal karışım.</summary>
    public static Rgb Mix(Rgb from, Rgb to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(Lerp(from.R, to.R, t), Lerp(from.G, to.G, t), Lerp(from.B, to.B, t));
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

/// <summary>Temanın vurgu rengine bağlı renkleri: <see cref="Accent"/> (bağlantı/vurgu metni, seçili düğme zemini),
/// <see cref="OnAccent"/> (vurgu zeminindeki yazı), <see cref="TagBg"/> (etiket/seçili satır zemini; üzerinde
/// <see cref="Accent"/> renginde yazı durur) ve <see cref="NavActiveBg"/> (etkin gezinme / üzerine gelinen öğe).</summary>
public sealed record AccentPalette(Rgb Accent, Rgb OnAccent, Rgb TagBg, Rgb NavActiveBg)
{
    /// <summary>Metin kontrastı alt sınırı (WCAG AA, normal metin).</summary>
    public const double MinTextContrast = 4.5;

    // Temanın vurguya bağlı olmayan temel renkleri (Theme.cs de bunları kullanır; tek kaynak burasıdır).
    public static readonly Rgb LightBg = Rgb.Parse("#FFFFFF");
    public static readonly Rgb LightSurface = Rgb.Parse("#F3F3F3");
    public static readonly Rgb LightFg = Rgb.Parse("#1A1A1A");
    public static readonly Rgb DarkBg = Rgb.Parse("#202020");
    public static readonly Rgb DarkSurface = Rgb.Parse("#2D2D2D");
    public static readonly Rgb DarkFg = Rgb.Parse("#F0F0F0");

    public static Rgb Bg(bool light) => light ? LightBg : DarkBg;
    public static Rgb Surface(bool light) => light ? LightSurface : DarkSurface;
    public static Rgb Fg(bool light) => light ? LightFg : DarkFg;

    /// <summary>Uygulamanın kendi (varsayılan) mavisi: önceki sürümlerin birebir renkleri. Elle ayarlanmıştır;
    /// <see cref="Derive"/> ile üretilmez ki eski görünüm değişmesin.</summary>
    public static AccentPalette Default(bool light) => light
        ? new(Rgb.Parse("#0F6CBD"), Rgb.Parse("#FFFFFF"), Rgb.Parse("#E6F0FA"), Rgb.Parse("#DCE9F7"))
        : new(Rgb.Parse("#4CC2FF"), Rgb.Parse("#002033"), Rgb.Parse("#1F3A52"), Rgb.Parse("#2B3A45"));

    // Zemin üzerine vurgu karışım oranları (eski elle seçilmiş tonlara yakın).
    private const double LightTagMix = 0.10;
    private const double LightNavMix = 0.15;
    private const double DarkTagMix = 0.22;
    private const double DarkNavMix = 0.13;
    private const double Step = 0.02;

    /// <summary>Seçilen renkten temaya uygun vurgu renklerini türetir. <see cref="Accent"/>, Bg / Surface /
    /// TagBg / NavActiveBg üzerinde en az <see cref="MinTextContrast"/> kontrast verene kadar açık temada
    /// koyulaştırılır, koyu temada açıklaştırılır (ton korunur, siyaha/beyaza doğru karıştırılır).
    /// <see cref="OnAccent"/> vurgu zemini üzerinde en az aynı kontrastı verir.</summary>
    public static AccentPalette Derive(Rgb seed, bool light)
    {
        var bg = Bg(light);
        var target = light ? Rgb.Black : Rgb.White;

        for (var k = 0.0; ; k += Step)
        {
            var accent = ColorMath.Mix(seed, target, k);
            var tag = ColorMath.Mix(bg, accent, light ? LightTagMix : DarkTagMix);
            var nav = ColorMath.Mix(bg, accent, light ? LightNavMix : DarkNavMix);
            // k = 1'de vurgu saf siyah/beyazdır ve tüm koşullar sağlanır; döngü her zaman biter.
            if (k < 1 - 1e-9 && !ReadableOn(accent, bg, Surface(light), tag, nav))
                continue;
            return new AccentPalette(accent, PickOnAccent(accent, light), tag, nav);
        }
    }

    /// <summary>Vurgu zeminindeki yazı rengi: açık temada beyaz, koyu temada vurgunun koyu tonu (eski görünüm gibi);
    /// tercih edilen yeterli kontrast vermezse diğeri, o da vermezse siyah/beyazdan kontrastı yüksek olan.</summary>
    public static Rgb PickOnAccent(Rgb accent, bool light)
    {
        var darkTint = ColorMath.Mix(accent, Rgb.Black, 0.8);
        Rgb[] candidates = light ? [Rgb.White, darkTint] : [darkTint, Rgb.White];
        foreach (var c in candidates)
        {
            if (ColorMath.ContrastRatio(c, accent) >= MinTextContrast) return c;
        }
        return ColorMath.ContrastRatio(Rgb.White, accent) >= ColorMath.ContrastRatio(Rgb.Black, accent)
            ? Rgb.White
            : Rgb.Black;
    }

    private static bool ReadableOn(Rgb fg, params Rgb[] backgrounds)
    {
        foreach (var b in backgrounds)
        {
            if (ColorMath.ContrastRatio(fg, b) < MinTextContrast) return false;
        }
        return true;
    }
}

/// <summary>Görünüm ayarlarının (tema modu, vurgu rengi, yoğunluk) değer kümeleri ve doğrulaması. Ayar dosyasındaki
/// eksik ya da bilinmeyen değerler varsayılana döner; varsayılanlar eski sürümlerin görünümünü verir.</summary>
public static class AppearanceOptions
{
    public const string ThemeSystem = "system";
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";

    /// <summary>Uygulamanın kendi mavisi (eski görünüm).</summary>
    public const string AccentDefault = "";

    /// <summary>Windows'un vurgu rengi (Kişiselleştirme → Renkler).</summary>
    public const string AccentWindows = "windows";

    public const string DensityComfortable = "comfortable";
    public const string DensityCompact = "compact";

    /// <summary>Hazır vurgu paleti (seçim kutucukları). Orta tonlardır; temaya göre <see cref="AccentPalette.Derive"/>
    /// ile açıklaştırılır/koyulaştırılır.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Presets =
    [
        ("Mor", "#8764B8"),
        ("Çivit", "#4F6BED"),
        ("Camgöbeği", "#038387"),
        ("Yeşil", "#107C10"),
        ("Altın", "#C19C00"),
        ("Turuncu", "#CA5010"),
        ("Kırmızı", "#D13438"),
        ("Pembe", "#E3008C"),
        ("Grafit", "#69797E"),
    ];

    public static string NormalizeThemeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ThemeLight => ThemeLight,
        ThemeDark => ThemeDark,
        _ => ThemeSystem,
    };

    public static string NormalizeDensity(string? value) =>
        string.Equals(value?.Trim(), DensityCompact, StringComparison.OrdinalIgnoreCase) ? DensityCompact : DensityComfortable;

    /// <summary>"" (varsayılan), "windows" ya da "#RRGGBB" (büyük harfe çevrilir); diğer her şey varsayılana döner.</summary>
    public static string NormalizeAccent(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.Equals(AccentWindows, StringComparison.OrdinalIgnoreCase)) return AccentWindows;
        return Rgb.TryParse(v, out var color) ? color.ToHex() : AccentDefault;
    }

    /// <summary>Tema modunun açık mı koyu mu olduğunu çözer; "system" için <paramref name="systemIsLight"/> kullanılır.</summary>
    public static bool ResolveLight(string? themeMode, bool systemIsLight) => NormalizeThemeMode(themeMode) switch
    {
        ThemeLight => true,
        ThemeDark => false,
        _ => systemIsLight,
    };

    /// <summary>Ayardaki vurgu seçimine göre paleti üretir. <paramref name="windowsAccent"/> Windows vurgu rengidir
    /// (okunamadıysa null: varsayılan palet kullanılır).</summary>
    public static AccentPalette ResolvePalette(string? accent, bool light, Rgb? windowsAccent)
    {
        var normalized = NormalizeAccent(accent);
        if (normalized == AccentWindows)
            return windowsAccent is { } w ? AccentPalette.Derive(w, light) : AccentPalette.Default(light);
        return normalized.Length == 0 ? AccentPalette.Default(light) : AccentPalette.Derive(Rgb.Parse(normalized), light);
    }
}
