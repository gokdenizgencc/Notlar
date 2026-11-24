using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>Otomatik yedeğin sıklığı (⚙ Ayarlar → Veri ve yedekleme).</summary>
public enum BackupFrequency
{
    Off = 0,
    Daily = 1,
    Weekly = 2,
}

/// <summary>Yedek klasöründeki bir yedek: notlar veritabanının kopyası ve (varsa) aynı anda alınmış ayarlar dosyası.</summary>
public sealed record BackupEntry(string DbPath, string? SettingsPath, DateTime CreatedAt, string? Label)
{
    /// <summary>Geri yüklemeden hemen önce otomatik alınmış yedek mi?</summary>
    public bool IsBeforeRestore => string.Equals(Label, DataBackup.BeforeRestoreLabel, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Notlar veritabanı + ayarlar yedekleri: adlandırma, listeleme, döndürme (eski yedekleri silme), zamanı gelmiş
/// mi kararı, tutarlı kopya (SQLite yedek API'si) ve geri yükleme. Ses dosyaları yedeğe dahil edilmez.
/// Dosya adı: <c>notlar-yyyy-MM-dd-HHmmss[-etiket].db</c>; ayarlar yanında aynı adla <c>.ayarlar.json</c>. Eski sürümün günlük
/// yedekleri (<c>notlar-yyyy-MM-dd.db</c>) de tanınır ve döndürmeye dahildir.</summary>
public static class DataBackup
{
    public const int DefaultKeep = 7;
    public const int MinKeep = 1;
    public const int MaxKeep = 100;
    public const string SettingsSuffix = ".ayarlar.json";
    public const string BeforeRestoreLabel = "geri-yukleme-oncesi";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Regex NamePattern = new(
        @"^notlar-(?<date>\d{4}-\d{2}-\d{2})(?:-(?<time>\d{6}))?(?:-(?<label>[a-z0-9-]+))?\.db$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int NormalizeKeep(int keep) => keep is >= MinKeep and <= MaxKeep ? keep : DefaultKeep;

    /// <summary><paramref name="at"/> anında alınacak yedeğin dosya adı (yalnızca ad, klasörsüz).</summary>
    public static string FileNameFor(DateTime at, string? label = null)
    {
        var name = $"notlar-{at.ToString("yyyy-MM-dd-HHmmss", Inv)}";
        if (!string.IsNullOrWhiteSpace(label)) name += "-" + label;
        return name + ".db";
    }

    /// <summary>Yedek dosya adını çözer; yedek değilse false. Saatsiz (eski günlük) adlarda saat 00:00 kabul edilir.</summary>
    public static bool TryParseFileName(string fileName, out DateTime createdAt, out string? label)
    {
        createdAt = default;
        label = null;
        var m = NamePattern.Match(fileName ?? "");
        if (!m.Success) return false;
        var stamp = m.Groups["date"].Value + (m.Groups["time"].Success ? m.Groups["time"].Value : "000000");
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-ddHHmmss", Inv, DateTimeStyles.None, out createdAt))
            return false;
        label = m.Groups["label"].Success ? m.Groups["label"].Value : null;
        return true;
    }

    /// <summary>Klasördeki yedekler, en yeniden eskiye. Klasör yoksa boş.</summary>
    public static IReadOnlyList<BackupEntry> List(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var entries = new List<BackupEntry>();
        foreach (var path in Directory.EnumerateFiles(dir, "notlar-*.db"))
        {
            var name = Path.GetFileName(path);
            if (!TryParseFileName(name, out var createdAt, out var label)) continue;
            var settings = SettingsPathFor(path);
            entries.Add(new BackupEntry(path, File.Exists(settings) ? settings : null, createdAt, label));
        }
        return Order(entries);
    }

    /// <summary>Döndürme kararı (saf): en yeni <paramref name="keep"/> yedek kalır, gerisi silinecekler olarak döner.</summary>
    public static IReadOnlyList<BackupEntry> SelectForDeletion(IEnumerable<BackupEntry> entries, int keep)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Order(entries).Skip(Math.Max(MinKeep, keep)).ToList();
    }

    /// <summary>Otomatik yedeğin zamanı geldi mi (saf)? Günlük: bugün henüz yedek yoksa; haftalık: son yedeğin gününden
    /// bu yana en az 7 gün geçtiyse. Hiç yedek yoksa (Kapalı değilse) her zaman.</summary>
    public static bool IsDue(BackupFrequency frequency, DateTime? lastBackup, DateTime now) => frequency switch
    {
        BackupFrequency.Daily => lastBackup is not { } last || last.Date < now.Date,
        BackupFrequency.Weekly => lastBackup is not { } last || now.Date >= last.Date.AddDays(7),
        _ => false,
    };

    /// <summary>Veritabanının tutarlı bir kopyasını (SQLite çevrimiçi yedek API'si; açık bağlantılar ve WAL ile güvenli)
    /// ve varsa ayarlar dosyasını yedek klasörüne yazar. Döndürme yapmaz (bkz. <see cref="Rotate"/>).</summary>
    public static BackupEntry Create(string dbPath, string? settingsPath, string dir, DateTime now, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        if (!File.Exists(dbPath)) throw new FileNotFoundException("Notlar veritabanı bulunamadı.", dbPath);
        Directory.CreateDirectory(dir);

        // Aynı saniyede ikinci yedek: üzerine yazmak yerine ada sıra eki konur.
        var path = Path.Combine(dir, FileNameFor(now, label));
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, FileNameFor(now, string.IsNullOrWhiteSpace(label) ? n.ToString(Inv) : $"{label}-{n}"));

        var tmpPath = path + ".tmp";
        try
        {
            DeleteIfExists(tmpPath);
            using (var source = OpenConnection(dbPath))
            using (var target = OpenConnection(tmpPath))
                source.BackupDatabase(target);
            File.Move(tmpPath, path);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }

        string? settingsCopy = null;
        if (!string.IsNullOrEmpty(settingsPath) && File.Exists(settingsPath))
        {
            settingsCopy = SettingsPathFor(path);
            File.Copy(settingsPath, settingsCopy, overwrite: true);
        }

        TryParseFileName(Path.GetFileName(path), out var createdAt, out var parsedLabel);
        return new BackupEntry(path, settingsCopy, createdAt, parsedLabel);
    }

    /// <summary>En yeni <paramref name="keep"/> yedek dışındakileri (veritabanı + ayarlar + olası -wal/-shm) siler; silinen
    /// veritabanı yollarını döndürür. Silinemeyen dosya atlanır, bir sonraki döndürmede yeniden denenir.</summary>
    public static IReadOnlyList<string> Rotate(string dir, int keep)
    {
        var deleted = new List<string>();
        foreach (var entry in SelectForDeletion(List(dir), keep))
        {
            try
            {
                File.Delete(entry.DbPath);
                deleted.Add(entry.DbPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            TryDelete(SettingsPathFor(entry.DbPath));
            TryDelete(entry.DbPath + "-wal");
            TryDelete(entry.DbPath + "-shm");
        }
        return deleted;
    }

    /// <summary>Yedek dosyasının sağlam bir Notlar veritabanı olduğunu doğrular (integrity_check + notes tablosu);
    /// değilse <see cref="InvalidDataException"/>.</summary>
    public static void Validate(string backupDbPath)
    {
        if (!File.Exists(backupDbPath)) throw new FileNotFoundException("Yedek dosyası bulunamadı.", backupDbPath);
        try
        {
            using var conn = OpenConnection(backupDbPath);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Yedek dosyası bozuk (bütünlük denetimi başarısız).");
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notes'";
                if ((long)cmd.ExecuteScalar()! == 0)
                    throw new InvalidDataException("Dosya bir Notlar yedeği değil (notes tablosu yok).");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("Yedek dosyası okunamadı: " + ex.Message, ex);
        }
    }

    /// <summary>Yedeği canlı veritabanının üzerine SQLite yedek API'siyle yazar (dosya kopyalamak yerine: açık
    /// bağlantılar ve WAL ile tutarlı kalır). Önce yedek doğrulanır. Geri alınamaz; çağıran öncesinde yedek almalıdır.</summary>
    public static void RestoreDatabase(string backupDbPath, string dbPath)
    {
        Validate(backupDbPath);
        using var source = OpenConnection(backupDbPath);
        using var target = OpenConnection(dbPath);
        source.BackupDatabase(target);
    }

    /// <summary>Yedek veritabanının yanındaki ayarlar dosyasının yolu.</summary>
    public static string SettingsPathFor(string backupDbPath) =>
        Path.Combine(Path.GetDirectoryName(backupDbPath) ?? "", Path.GetFileNameWithoutExtension(backupDbPath) + SettingsSuffix);

    private static List<BackupEntry> Order(IEnumerable<BackupEntry> entries) =>
        entries.OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => Path.GetFileName(e.DbPath), StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Havuzsuz: bağlantı kapanınca dosya tutamacı bırakılır (yedek silinebilsin / taşınabilsin).
    private static SqliteConnection OpenConnection(string path)
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString());
        conn.Open();
        return conn;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            DeleteIfExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
