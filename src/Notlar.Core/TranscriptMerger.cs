namespace Notlar.Core;

/// <summary>İki kanalın (mikrofon "Ben" + sistem sesi "Karşı taraf") segmentlerini zamana göre birleştirir.</summary>
public static class TranscriptMerger
{
    private const long MergeGapMs = 2000;

    /// <summary>Segmentleri StartMs'e (eşitse Ben önce) göre sıralar; metni boş olanları atar; ardışık aynı
    /// konuşmacıya ait ve aradaki boşluk 2 sn'den kısa segmentleri tek segmentte birleştirir (metinler tek
    /// boşlukla).</summary>
    public static IReadOnlyList<TranscriptSegment> Merge(IEnumerable<TranscriptSegment> a, IEnumerable<TranscriptSegment> b)
    {
        var ordered = a.Concat(b)
            .Select(s => s with { Text = (s.Text ?? "").Trim() })
            .Where(s => s.Text.Length > 0)
            .OrderBy(s => s.StartMs)
            .ThenBy(s => s.Speaker == Speaker.Ben ? 0 : 1);

        var result = new List<TranscriptSegment>();
        foreach (var seg in ordered)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (last.Speaker == seg.Speaker && seg.StartMs - last.EndMs < MergeGapMs)
                {
                    result[^1] = last with { EndMs = Math.Max(last.EndMs, seg.EndMs), Text = last.Text + " " + seg.Text };
                    continue;
                }
            }
            result.Add(seg);
        }
        return result;
    }
}
