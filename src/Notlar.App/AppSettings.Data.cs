using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Veri ve yedekleme: ses dosyalarının saklama süresi, otomatik yedek, ayarların dışa/içe
/// aktarılması ve varsayılana sıfırlanması.</summary>
public sealed partial class AppSettings
{
    /// <summary>Kayıt seslerinin kaç gün saklanacağı; 0 = hiç silme (bkz. <see cref="AudioRetentionPolicy"/>).
    /// Varsayılan 14 gün. Temizlik uygulama açılışında yapılır (RecordingController).</summary>
    public int AudioRetentionDays { get; set; } = AudioRetentionPolicy.DefaultDays;

    /// <summary>Otomatik yedek sıklığı. Varsayılan günlük (önceki sürümün her açılıştaki günlük yedeğiyle aynı).</summary>
    public BackupFrequency AutoBackupFrequency { get; set; } = BackupFrequency.Daily;

    /// <summary>Yedek klasöründe tutulacak en fazla yedek sayısı; fazlası en eskiden başlayarak silinir. Varsayılan 7.</summary>
    public int BackupKeepCount { get; set; } = DataBackup.DefaultKeep;

    /// <summary>Kullanıcı tercihi olmayan, makineye/duruma ait alanlar: dışa aktarılmaz, içe aktarılırken yok sayılır ve
    /// "varsayılana sıfırla"da korunur (ör. KVKK uyarısının onaylanmış olması, bugünkü otomatik kayıt işareti, pencere
    /// konumu).</summary>
    public static IReadOnlySet<string> MachineStateKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        nameof(DataDirSecured),
        nameof(RecordingConsentShown),
        nameof(AutoRecordLastDate),
        nameof(PanelLeft),
        nameof(PanelTop),
        nameof(PanelLarge),
        nameof(PanelLargeWidth),
        nameof(PanelLargeHeight),
        nameof(PanelLargeLeft),
        nameof(PanelLargeTop),
    };

    /// <summary>Ayarlar dosyasının yolu (yedeğe ayarları da koymak için).</summary>
    public static string FilePath => SettingsPath;

    partial void NormalizeData()
    {
        AudioRetentionDays = AudioRetentionPolicy.Normalize(AudioRetentionDays);
        if (!Enum.IsDefined(AutoBackupFrequency))
            AutoBackupFrequency = BackupFrequency.Daily;
        BackupKeepCount = DataBackup.NormalizeKeep(BackupKeepCount);
    }

    /// <summary>Güncel ayarlar, makine durumu alanları çıkarılmış, girintili JSON olarak.</summary>
    public static string ExportJson()
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(Load())!;
        return SettingsTransfer.StripExcluded(node, MachineStateKeys).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>JSON'daki geçerli alanları güncel ayarlara uygular, doğrulayıp kaydeder (<see cref="Changed"/> tetiklenir).
    /// Bilinmeyen, dışlanan, türü uymayan ya da çözülemeyen alanlar yok sayılır; JSON hiç okunamazsa
    /// <see cref="FormatException"/>.</summary>
    public static SettingsImportResult ImportJson(string json)
    {
        var current = (JsonObject)JsonSerializer.SerializeToNode(Load())!;
        var result = SettingsTransfer.Merge(current, json, MachineStateKeys, CanDeserializeField);
        if (result.Applied.Count > 0)
            WriteNormalized(result.Merged);
        return result;
    }

    /// <summary>Tüm tercihleri varsayılana döndürür; <see cref="MachineStateKeys"/> korunur. Notlara/kayıtlara dokunmaz.</summary>
    public static void ResetToDefaults()
    {
        var current = (JsonObject)JsonSerializer.SerializeToNode(Load())!;
        var fresh = (JsonObject)JsonSerializer.SerializeToNode(new AppSettings())!;
        foreach (var key in MachineStateKeys)
        {
            if (current.TryGetPropertyValue(key, out var value))
                fresh[key] = value?.DeepClone();
        }
        WriteNormalized(fresh);
    }

    /// <summary>Tek alanın gerçek tipe çözülebildiğini dener (ör. sözlükte metin olmayan değer, taşan sayı).</summary>
    private static bool CanDeserializeField(string key, JsonNode? value)
    {
        try
        {
            JsonSerializer.Deserialize<AppSettings>(new JsonObject { [key] = value?.DeepClone() });
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>JSON'u nesneye çözer, Load'un doğrulamasından geçirir ve kaydeder. Doğrulama yalnızca Load'da
    /// (dosyadan okurken) yapıldığından önce ham hâli atomik yazılır, sonra yüklenip normalize hâli kaydedilir.</summary>
    private static void WriteNormalized(JsonObject node)
    {
        // Çözülemeyen bir bütün varsa dosyaya hiç yazılmaz.
        _ = JsonSerializer.Deserialize<AppSettings>(node) ?? throw new FormatException("Ayarlar çözülemedi.");

        Directory.CreateDirectory(AppPaths.DataDir);
        var path = SettingsPath;
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(path))
            File.Replace(tmpPath, path, null);
        else
            File.Move(tmpPath, path);

        Load().Save();
    }
}
