using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Uygulama görünüm ayarları (yazı boyutu, yazar renkleri). JSON olarak diske kaydedilir.</summary>
public sealed class AppSettings
{
    public const double MinDayFontSize = 10;
    public const double MaxDayFontSize = 40;
    public const double DefaultDayFontSize = 16;

    public double DayFontSize { get; set; } = DefaultDayFontSize;

    /// <summary>Panel sabitlenmiş mi? Sabitken Deactivated (odak kaybı) paneli gizlemez; yalnızca "—"
    /// gizle düğmesi, Esc ya da tepsi simgesine tıklama gizler. Varsayılan true: kullanıcı düğmeye
    /// basana kadar panel açık kalsın istiyor.</summary>
    public bool PanelPinned { get; set; } = true;

    /// <summary>Panel sabitliyken sürüklenerek taşındığında hatırlanan konum; null ise ekranın sağ altı
    /// kullanılır.</summary>
    public double? PanelLeft { get; set; }
    public double? PanelTop { get; set; }

    /// <summary>Panel büyük (serbest boyutlandırılabilir) modda mı, yoksa kompakt sabit boyutlu
    /// modda mı? Büyük moddayken de odak kaybı paneli gizlemez (sabitlenmiş gibi davranır).</summary>
    public bool PanelLarge { get; set; }

    /// <summary>Büyük moddaki son boyut/konum; null ise ekranın görünür alanına göre ortalanmış
    /// varsayılan (~1000×720) kullanılır.</summary>
    public double? PanelLargeWidth { get; set; }
    public double? PanelLargeHeight { get; set; }
    public double? PanelLargeLeft { get; set; }
    public double? PanelLargeTop { get; set; }

    [JsonInclude]
    public Dictionary<string, string> AuthorColors { get; set; } = CreateDefaultAuthorColors();

    /// <summary>"Keşfet" sekmesindeki eklenti listesinin sıralama kriteri. Varsayılan: popülerliğe göre.</summary>
    public PluginSort DiscoverSort { get; set; } = PluginSort.Popular;

    public static event Action? Changed;

    private static string SettingsPath => Path.Combine(AppPaths.DataDir, "ayarlar.json");

    /// <summary>Varsayılan yazar renkleri. Anahtarlar büyük/küçük harfe duyarsızdır.</summary>
    public static Dictionary<string, string> CreateDefaultAuthorColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ben"] = "",
        ["Claude"] = "#D97757",
        ["ChatGPT"] = "#10A37F",
        ["Gemini"] = "#4285F4",
    };

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
                return CreateDefault();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is null)
                return CreateDefault();

            // Eksik anahtarları varsayılanla tamamla, karşılaştırmayı büyük/küçük harfe duyarsız yap.
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in CreateDefaultAuthorColors())
                colors[kv.Key] = kv.Value;
            if (settings.AuthorColors is not null)
            {
                foreach (var kv in settings.AuthorColors)
                    colors[kv.Key] = kv.Value;
            }
            settings.AuthorColors = colors;

            if (settings.DayFontSize is double.NaN || settings.DayFontSize < MinDayFontSize || settings.DayFontSize > MaxDayFontSize)
                settings.DayFontSize = DefaultDayFontSize;

            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return CreateDefault();
        }
    }

    private static AppSettings CreateDefault() => new()
    {
        DayFontSize = DefaultDayFontSize,
        AuthorColors = CreateDefaultAuthorColors(),
    };

    /// <summary>Ayarları JSON olarak atomik şekilde (tmp dosyaya yazıp yer değiştirerek) kaydeder ve <see cref="Changed"/> olayını tetikler.</summary>
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var path = SettingsPath;
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmpPath, json);
        if (File.Exists(path))
            File.Replace(tmpPath, path, null);
        else
            File.Move(tmpPath, path);

        Changed?.Invoke();
    }
}
