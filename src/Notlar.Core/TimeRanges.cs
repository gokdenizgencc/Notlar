namespace Notlar.Core;

/// <summary>Bir kaydın içinde, kaydın başına göre milisaniye cinsinden zaman aralığı (<c>StartMs</c> dahil,
/// <c>EndMs</c> hariç). Kullanıcının "yalnızca şu kısmı metne çevir" seçimi.</summary>
public sealed record TimeRange(long StartMs, long EndMs);

public static class TimeRanges
{
    /// <summary>Aralıkları temizler: negatif zamanlar 0'a kırpılır, boş/ters aralıklar atılır, başlangıca göre
    /// sıralanır, çakışan ya da bitişik aralıklar birleştirilir.</summary>
    public static IReadOnlyList<TimeRange> Normalize(IEnumerable<TimeRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var result = new List<TimeRange>();
        var sorted = ranges
            .Select(r => new TimeRange(Math.Max(0, r.StartMs), Math.Max(0, r.EndMs)))
            .Where(r => r.EndMs > r.StartMs)
            .OrderBy(r => r.StartMs)
            .ThenBy(r => r.EndMs);
        foreach (var range in sorted)
        {
            if (result.Count > 0 && range.StartMs <= result[^1].EndMs)
            {
                var last = result[^1];
                result[^1] = last with { EndMs = Math.Max(last.EndMs, range.EndMs) };
            }
            else
            {
                result.Add(range);
            }
        }
        return result;
    }

    /// <summary>Konuşma bölgelerini (örnek indisleri; <c>Start</c> dahil, <c>End</c> hariç) seçili aralıklarla
    /// kesiştirir. <paramref name="ranges"/> boşsa "tüm kayıt" demektir; bölgeler aynen döner.</summary>
    public static IReadOnlyList<(int Start, int End)> ClipRegions(IReadOnlyList<(int Start, int End)> regions,
        IReadOnlyList<TimeRange> ranges, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var normalized = Normalize(ranges);
        if (normalized.Count == 0) return regions;

        var result = new List<(int Start, int End)>();
        foreach (var (regionStart, regionEnd) in regions)
        {
            foreach (var range in normalized)
            {
                var start = Math.Max(regionStart, ToSample(range.StartMs, sampleRate));
                var end = Math.Min(regionEnd, ToSample(range.EndMs, sampleRate));
                if (end > start) result.Add(((int)start, (int)end));
            }
        }
        return result;
    }

    private static long ToSample(long ms, int sampleRate) => Math.Min(int.MaxValue, ms * sampleRate / 1000);
}
