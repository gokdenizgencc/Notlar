namespace Notlar.Core;

/// <summary>Tepsi bildirimi üreten olay türleri (⚙ Ayarlar → Bildirimler'deki anahtarlarla eşleşir).</summary>
public enum NotificationKind
{
    /// <summary>Kayıt elle (tepsi menüsünden) başlatıldı / durduruldu.</summary>
    RecordingManual,

    /// <summary>Hafta içi zamanlanmış kayıt başladı / durdu.</summary>
    RecordingScheduled,

    /// <summary>Teams araması algılandı, kayıt başladı / durdu.</summary>
    RecordingTeams,

    /// <summary>Kayıt metne çevrildi.</summary>
    TranscriptReady,

    /// <summary>Claude özeti ya da görev planı hazır.</summary>
    SummaryReady,

    /// <summary>Diğer bilgi mesajları (eskiden gizliyken mesaj kutusuyla gösterilenler).</summary>
    Info,

    /// <summary>Hata / uyarı. Rahatsız etmeyin saatlerinde de gösterilir; gösterilmese de günlüğe yazılır.</summary>
    Error,

    /// <summary>⚙ Ayarlar'daki "Örnek bildirim gönder": ayarlardan bağımsız her zaman gösterilir.</summary>
    Test,
}

/// <summary>Bildirim tercihleri (AppSettings'ten türetilir; saf karar mantığı için).</summary>
public sealed record NotificationPreferences(
    bool Enabled = true,
    bool RecordingManual = true,
    bool RecordingScheduled = true,
    bool RecordingTeams = true,
    bool TranscriptReady = true,
    bool SummaryReady = true,
    bool Errors = true,
    bool QuietHoursEnabled = false,
    TimeOnly QuietStart = default,
    TimeOnly QuietEnd = default);

/// <summary>Bir bildirimin gösterilip gösterilmeyeceğine karar verir (saf; tepsi tarafı App'teki Notifier'dadır).</summary>
public static class NotificationPolicy
{
    /// <summary>
    /// <list type="bullet">
    /// <item><see cref="NotificationKind.Test"/> her zaman gösterilir.</item>
    /// <item>Ana anahtar kapalıysa hiçbir şey gösterilmez.</item>
    /// <item><see cref="NotificationKind.Error"/> yalnızca kendi anahtarına bakar; rahatsız etmeyin saatlerinden etkilenmez.</item>
    /// <item>Diğerleri kendi anahtarı açıksa ve rahatsız etmeyin saatlerinde değilse gösterilir
    /// (<see cref="NotificationKind.Info"/>'nun ayrı anahtarı yoktur).</item>
    /// </list></summary>
    public static bool ShouldShow(NotificationKind kind, NotificationPreferences prefs, TimeOnly now)
    {
        if (kind == NotificationKind.Test) return true;
        if (!prefs.Enabled) return false;
        if (kind == NotificationKind.Error) return prefs.Errors;

        var kindEnabled = kind switch
        {
            NotificationKind.RecordingManual => prefs.RecordingManual,
            NotificationKind.RecordingScheduled => prefs.RecordingScheduled,
            NotificationKind.RecordingTeams => prefs.RecordingTeams,
            NotificationKind.TranscriptReady => prefs.TranscriptReady,
            NotificationKind.SummaryReady => prefs.SummaryReady,
            _ => true,
        };
        if (!kindEnabled) return false;

        return !(prefs.QuietHoursEnabled && IsInQuietHours(now, prefs.QuietStart, prefs.QuietEnd));
    }

    /// <summary><paramref name="start"/> ≤ saat &lt; <paramref name="end"/>; <paramref name="start"/> &gt;
    /// <paramref name="end"/> ise aralık gece yarısını aşar (ör. 22:00–08:00). Başlangıç = bitiş boş aralıktır.</summary>
    public static bool IsInQuietHours(TimeOnly now, TimeOnly start, TimeOnly end)
    {
        if (start == end) return false;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }
}
