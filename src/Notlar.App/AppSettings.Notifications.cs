using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Bildirimler. Varsayılanlar eski davranışı korur: tüm bildirimler açık, ses açık,
/// rahatsız etmeyin kapalı. Karar mantığı <see cref="NotificationPolicy"/>'de, uygulanışı <see cref="Notifier"/>'da.</summary>
public sealed partial class AppSettings
{
    public const string DefaultQuietHoursStart = "22:00";
    public const string DefaultQuietHoursEnd = "08:00";

    /// <summary>Ana anahtar: tepsi bildirimleri gösterilsin mi?</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Tepsi menüsünden elle başlatılan / durdurulan kayıt.</summary>
    public bool NotifyRecordingManual { get; set; } = true;

    /// <summary>Hafta içi zamanlanmış kaydın başlaması / durması.</summary>
    public bool NotifyRecordingScheduled { get; set; } = true;

    /// <summary>Teams aramasıyla başlayan / duran kayıt.</summary>
    public bool NotifyRecordingTeams { get; set; } = true;

    /// <summary>Kayıt metne çevrildi.</summary>
    public bool NotifyTranscriptReady { get; set; } = true;

    /// <summary>Claude özeti / görevleri hazır.</summary>
    public bool NotifySummaryReady { get; set; } = true;

    /// <summary>Hata bildirimleri. Kapalıyken de hatalar günlüğe yazılır.</summary>
    public bool NotifyErrors { get; set; } = true;

    /// <summary>Bildirim sesi (kapalıyken balon sessiz gösterilir).</summary>
    public bool NotificationSound { get; set; } = true;

    /// <summary>Rahatsız etmeyin: bu saatler arasında hata dışındaki bildirimler susturulur. Gece yarısını aşabilir.</summary>
    public bool QuietHoursEnabled { get; set; }
    public string QuietHoursStart { get; set; } = DefaultQuietHoursStart;
    public string QuietHoursEnd { get; set; } = DefaultQuietHoursEnd;

    partial void NormalizeNotifications()
    {
        if (!RecordingSchedule.TryParseTime(QuietHoursStart, out _))
            QuietHoursStart = DefaultQuietHoursStart;
        if (!RecordingSchedule.TryParseTime(QuietHoursEnd, out _))
            QuietHoursEnd = DefaultQuietHoursEnd;
    }

    /// <summary>Saf karar mantığı için tercihler.</summary>
    public NotificationPreferences ToNotificationPreferences()
    {
        RecordingSchedule.TryParseTime(QuietHoursStart, out var start);
        RecordingSchedule.TryParseTime(QuietHoursEnd, out var end);
        return new NotificationPreferences(
            NotificationsEnabled, NotifyRecordingManual, NotifyRecordingScheduled, NotifyRecordingTeams,
            NotifyTranscriptReady, NotifySummaryReady, NotifyErrors, QuietHoursEnabled, start, end);
    }
}
