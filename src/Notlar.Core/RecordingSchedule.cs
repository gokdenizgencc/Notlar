using System.Globalization;

namespace Notlar.Core;

public enum ScheduleAction { None, Start, Stop }

/// <summary>Otomatik toplantı kaydının (varsayılan hafta içi) karar mantığı (saf; zamanlayıcı App tarafındadır).</summary>
public static class RecordingSchedule
{
    /// <summary>Varsayılan kayıt günleri: Pazartesi–Cuma.</summary>
    public static IReadOnlyList<DayOfWeek> DefaultDays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>Haftanın günleri Pazartesi'den başlayarak (arayüz sırası).</summary>
    public static IReadOnlyList<DayOfWeek> WeekOrder { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
         DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static readonly string[] ShortNames = ["Paz", "Pzt", "Sal", "Çar", "Per", "Cum", "Cmt"];

    /// <summary>Şu an ne yapılmalı?
    /// <list type="bullet">
    /// <item>Seçili bir günde (<paramref name="days"/>; null → Pazartesi–Cuma), pencere içinde
    /// (<paramref name="start"/> ≤ saat &lt; <paramref name="end"/>), kayıt yokken ve bugün henüz otomatik
    /// başlatılmamışken → <see cref="ScheduleAction.Start"/>. Kullanıcı otomatik kaydı erken durdurursa aynı gün
    /// yeniden başlatılmaz.</item>
    /// <item>Otomatik başlatılmış kayıt sürerken pencere dışına çıkılmışsa (ya da bugün artık seçili gün değilse)
    /// → <see cref="ScheduleAction.Stop"/>.</item>
    /// <item>Elle başlatılmış kayda dokunulmaz. <paramref name="end"/> ≤ <paramref name="start"/> ise zamanlama
    /// geçersizdir; yeni kayıt başlatılmaz (süren otomatik kayıt durdurulur). Boş gün listesi hiç başlatmaz.</item>
    /// </list></summary>
    public static ScheduleAction Decide(DateTime now, TimeOnly start, TimeOnly end, bool isRecording,
        bool currentIsAuto, DateOnly? lastAutoStart, IReadOnlyCollection<DayOfWeek>? days = null)
    {
        var time = TimeOnly.FromDateTime(now);
        var dayOk = (days ?? DefaultDays).Contains(now.DayOfWeek);
        var inWindow = end > start && dayOk && time >= start && time < end;

        if (isRecording)
            return currentIsAuto && !inWindow ? ScheduleAction.Stop : ScheduleAction.None;

        return inWindow && lastAutoStart != DateOnly.FromDateTime(now) ? ScheduleAction.Start : ScheduleAction.None;
    }

    /// <summary>Kaydedilmiş gün listesini temizler: geçersiz değerler ve tekrarlar atılır, Pazartesi'den başlayan sıraya
    /// konur. null (eski ayar dosyası) → <see cref="DefaultDays"/>; boş liste boş kalır (kullanıcı tüm günleri kapattı).</summary>
    public static List<DayOfWeek> NormalizeDays(IEnumerable<DayOfWeek>? days)
    {
        if (days is null) return [.. DefaultDays];
        var set = new HashSet<DayOfWeek>(days.Where(d => d is >= DayOfWeek.Sunday and <= DayOfWeek.Saturday));
        return WeekOrder.Where(set.Contains).ToList();
    }

    /// <summary>Kısa Türkçe gün adı: "Pzt", "Sal", … "Paz".</summary>
    public static string ShortName(DayOfWeek day) => ShortNames[(int)day];

    /// <summary>Seçili günlerin okunur açıklaması: "Pazartesi–Cuma", "her gün", "hafta sonu", "hiçbir gün" ya da
    /// "Pzt, Çar, Cum".</summary>
    public static string DescribeDays(IEnumerable<DayOfWeek>? days)
    {
        var list = NormalizeDays(days);
        if (list.Count == 0) return "hiçbir gün";
        if (list.Count == 7) return "her gün";
        if (list.SequenceEqual(DefaultDays)) return "Pazartesi–Cuma";
        if (list.SequenceEqual([DayOfWeek.Saturday, DayOfWeek.Sunday])) return "hafta sonu";
        return string.Join(", ", list.Select(ShortName));
    }

    /// <summary>"9:30" / "09:30" biçimindeki saati okur.</summary>
    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
