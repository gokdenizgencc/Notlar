using System;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Tüm tepsi bildirimlerinin geçtiği tek nokta: ⚙ Ayarlar → Bildirimler'i (ana anahtar, olay anahtarları,
/// ses, rahatsız etmeyin) uygular. Hatalar gösterilsin ya da gösterilmesin her zaman günlüğe yazılır.
/// UI thread'inden çağrılmalıdır.</summary>
public static class Notifier
{
    private static TrayIcon? _tray;
    private static Action<long?>? _openRecordings;

    /// <summary>Uygulama başlarken bir kez: tepsi simgesi ve "Kayıtlar'ı aç" eylemi bağlanır.</summary>
    public static void Attach(TrayIcon tray, Action<long?> openRecordings)
    {
        _tray = tray;
        _openRecordings = openRecordings;
    }

    /// <summary>Tepsi simgesi bağlı mı? Bağlı değilse çağıran eski yola (mesaj kutusu) dönebilir.</summary>
    public static bool IsAvailable => _tray is not null;

    /// <summary>Paneli Kayıtlar bölümünde açan tıklama eylemi; <paramref name="id"/> verilirse o kaydı seçer.</summary>
    public static Action OpenRecordingsAction(long? id) => () => _openRecordings?.Invoke(id);

    /// <summary>Ayarlar izin veriyorsa tepsi bildirimi gösterir. Gösterildiyse true.</summary>
    public static bool Show(NotificationKind kind, string title, string text, Action? onClick = null)
    {
        if (kind == NotificationKind.Error)
            AppLog.Info($"Bildirim (hata): {title} — {text}");

        AppSettings settings;
        try
        {
            settings = AppSettings.Load();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            settings = new AppSettings();
        }

        if (_tray is not { } tray) return false;
        if (!NotificationPolicy.ShouldShow(kind, settings.ToNotificationPreferences(), TimeOnly.FromDateTime(DateTime.Now)))
            return false;

        try
        {
            tray.ShowBalloon(title, text, onClick, isError: kind == NotificationKind.Error, silent: !settings.NotificationSound);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return false;
        }
    }
}
