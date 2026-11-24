namespace Notlar.Core;

/// <summary>İki kanalın (mikrofon + sistem sesi) segmentlerini zamana göre tek akışta birleştirir. Çıktıdaki her
/// segment bir "konuşma"dır: ses kesilince (ya da öbür kanal araya girince) yeni segment başlar.</summary>
public static class TranscriptMerger
{
    /// <summary>Aynı kanalda aradaki sessizlik bundan kısaysa konuşma sürüyor sayılır (SpeechSegmenter ile aynı eşik).</summary>
    private const long SameUtteranceGapMs = 600;

    /// <summary>Hiç ara vermeden süren konuşma bu süreyi aşınca yeni segmente bölünür (upuzun tek satır olmasın).</summary>
    private const long MaxUtteranceMs = 20_000;

    /// <summary>Segmentleri StartMs'e (eşitse mikrofon önce) göre sıralar; metni boş olanları atar. Ardışık, aynı
    /// kanala ait segmentler yalnızca aradaki boşluk 600 ms'den kısaysa ve birleşik süre 20 sn'yi aşmıyorsa tek
    /// segmentte birleştirilir (metinler tek boşlukla); aksi halde her biri ayrı konuşma olarak kalır.</summary>
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
                var end = Math.Max(last.EndMs, seg.EndMs);
                if (last.Speaker == seg.Speaker && seg.StartMs - last.EndMs < SameUtteranceGapMs
                    && end - last.StartMs <= MaxUtteranceMs)
                {
                    result[^1] = last with { EndMs = end, Text = last.Text + " " + seg.Text };
                    continue;
                }
            }
            result.Add(seg);
        }
        return result;
    }
}
