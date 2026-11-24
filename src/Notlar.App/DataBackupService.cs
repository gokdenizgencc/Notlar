using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Uygulamanın yedekleme işleri (⚙ Ayarlar → Veri ve yedekleme): elle / otomatik yedek, döndürme ve geri
/// yükleme. Yedek = notlar veritabanının tutarlı kopyası + ayarlar dosyası; ses dosyaları dahil değildir. Tüm işlemler
/// tek kilit altında sırayla yürür; çağıranlar arka plan thread'inden çağırmalıdır (disk I/O).</summary>
internal static class DataBackupService
{
    private static readonly object Gate = new();
    private static Timer? _timer;

    public static IReadOnlyList<BackupEntry> List() => DataBackup.List(AppPaths.BackupDir);

    /// <summary>Şimdi yedek alır ve ayarlardaki sayıya göre eski yedekleri siler.</summary>
    public static BackupEntry CreateNow()
    {
        lock (Gate)
        {
            var entry = DataBackup.Create(AppPaths.DbPath, AppSettings.FilePath, AppPaths.BackupDir, DateTime.Now);
            DataBackup.Rotate(AppPaths.BackupDir, AppSettings.Load().BackupKeepCount);
            return entry;
        }
    }

    /// <summary>Ayardaki sıklığa göre zamanı geldiyse yedek alır (açılışta ve saatte bir çağrılır).</summary>
    public static void RunAutomaticIfDue()
    {
        lock (Gate)
        {
            var settings = AppSettings.Load();
            var last = List().FirstOrDefault()?.CreatedAt;
            if (DataBackup.IsDue(settings.AutoBackupFrequency, last, DateTime.Now))
                CreateNow();
        }
    }

    /// <summary>Uygulama açıkken günler boyunca çalışabildiğinden otomatik yedek saatte bir de denetlenir.</summary>
    public static void StartScheduler()
    {
        _timer ??= new Timer(_ =>
        {
            try
            {
                RunAutomaticIfDue();
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    /// <summary>Seçilen yedeğin veritabanını geri yükler: önce yedeği doğrular, sonra mevcut durumun "geri yükleme
    /// öncesi" yedeğini alır (veritabanı + ayarlar), ardından veritabanını geri yazar. Geri yükleme öncesi yedeği
    /// döndürür. Döndürme yapılmaz (seçili yedek bu sırada silinmesin). Ayarlar ayrıca <see cref="RestoreSettings"/>
    /// ile, UI thread'inde geri yüklenir (Changed dinleyicileri UI thread'i bekler).</summary>
    public static BackupEntry RestoreDatabase(BackupEntry entry)
    {
        lock (Gate)
        {
            DataBackup.Validate(entry.DbPath);
            var before = DataBackup.Create(AppPaths.DbPath, AppSettings.FilePath, AppPaths.BackupDir, DateTime.Now,
                DataBackup.BeforeRestoreLabel);
            DataBackup.RestoreDatabase(entry.DbPath, AppPaths.DbPath);
            return before;
        }
    }

    /// <summary>Yedekte ayarlar dosyası varsa (makine durumu alanları hariç) geri yükler; yüklendiyse true. Okunamayan
    /// dosya mevcut ayarları bozmaz, günlüğe yazılır.</summary>
    public static bool RestoreSettings(BackupEntry entry)
    {
        if (entry.SettingsPath is not { } settings || !File.Exists(settings)) return false;
        try
        {
            AppSettings.ImportJson(File.ReadAllText(settings));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write(ex);
            return false;
        }
    }
}
