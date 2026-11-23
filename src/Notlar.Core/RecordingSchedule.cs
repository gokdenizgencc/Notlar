using System.Globalization;

namespace Notlar.Core;

public enum ScheduleAction { None, Start, Stop }

/// <summary>Hafta içi otomatik toplantı kaydının karar mantığı (saf; zamanlayıcı App tarafındadır).</summary>
public static class RecordingSchedule
{
    /// <summary>Şu an ne yapılmalı?
    /// <list type="bullet">
    /// <item>Hafta içi, pencere içinde (<paramref name="start"/> ≤ saat &lt; <paramref name="end"/>), kayıt yokken ve
    /// bugün henüz otomatik başlatılmamışken → <see cref="ScheduleAction.Start"/>. Kullanıcı otomatik kaydı erken
    /// durdurursa aynı gün yeniden başlatılmaz.</item>
    /// <item>Otomatik başlatılmış kayıt sürerken pencere dışına çıkılmışsa → <see cref="ScheduleAction.Stop"/>.</item>
    /// <item>Elle başlatılmış kayda dokunulmaz. <paramref name="end"/> ≤ <paramref name="start"/> ise zamanlama
    /// geçersizdir; yeni kayıt başlatılmaz (süren otomatik kayıt durdurulur).</item>
    /// </list></summary>
    public static ScheduleAction Decide(DateTime now, TimeOnly start, TimeOnly end, bool isRecording,
        bool currentIsAuto, DateOnly? lastAutoStart)
    {
        var time = TimeOnly.FromDateTime(now);
        var weekday = now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        var inWindow = end > start && weekday && time >= start && time < end;

        if (isRecording)
            return currentIsAuto && !inWindow ? ScheduleAction.Stop : ScheduleAction.None;

        return inWindow && lastAutoStart != DateOnly.FromDateTime(now) ? ScheduleAction.Start : ScheduleAction.None;
    }

    /// <summary>"9:30" / "09:30" biçimindeki saati okur.</summary>
    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
