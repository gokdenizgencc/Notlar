using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Görünüm: tema modu, vurgu rengi, yazı tipi, yoğunluk. Değerler atanırken doğrulanır (bilinmeyen /
/// eksik değer varsayılana döner); böylece bu alanları içermeyen eski ayar dosyaları bugünkü görünümle açılır
/// (Windows temasını izleyen açık/koyu, uygulamanın mavisi, Segoe UI, rahat yoğunluk).</summary>
public sealed partial class AppSettings
{
    private string _themeMode = AppearanceOptions.ThemeSystem;
    private string _accentColor = AppearanceOptions.AccentDefault;
    private string _uiFontFamily = "";
    private string _density = AppearanceOptions.DensityComfortable;

    /// <summary>"system" (Windows'u izler) | "light" | "dark". Bkz. <see cref="AppearanceOptions"/>.</summary>
    public string ThemeMode
    {
        get => _themeMode;
        set => _themeMode = AppearanceOptions.NormalizeThemeMode(value);
    }

    /// <summary>"" (uygulamanın mavisi) | "windows" (Windows vurgu rengi) | "#RRGGBB" (hazır palet).</summary>
    public string AccentColor
    {
        get => _accentColor;
        set => _accentColor = AppearanceOptions.NormalizeAccent(value);
    }

    /// <summary>Uygulama yazı tipi ailesi; boş ise varsayılan (<see cref="Theme.DefaultFontFamily"/>).</summary>
    public string UiFontFamily
    {
        get => _uiFontFamily;
        set => _uiFontFamily = value?.Trim() ?? "";
    }

    /// <summary>"comfortable" | "compact": liste satırlarının iç boşlukları.</summary>
    public string Density
    {
        get => _density;
        set => _density = AppearanceOptions.NormalizeDensity(value);
    }
}
