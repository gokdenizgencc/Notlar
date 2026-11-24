using System.Globalization;

namespace Notlar.Core;

/// <summary>Veri klasörünün disk kullanımı (⚙ Ayarlar → Veri ve yedekleme). Okunamayan dosya/klasör atlanır.</summary>
public static class StorageUsage
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Dosyanın ya da klasörün (alt klasörler dahil) toplam boyutu, bayt; yoksa 0.</summary>
    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            long total = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try
                {
                    total += file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>SQLite veritabanı ve yanındaki -wal / -shm dosyalarının toplamı.</summary>
    public static long DatabaseSize(string dbPath) =>
        SizeOf(dbPath) + SizeOf(dbPath + "-wal") + SizeOf(dbPath + "-shm");

    /// <summary>Megabayt olarak, Türkçe ondalıkla: "0,0 MB", "12,3 MB", "1.024,0 MB".</summary>
    public static string FormatMegabytes(long bytes) =>
        (Math.Max(0, bytes) / (1024d * 1024d)).ToString("#,0.0", Tr) + " MB";
}
