namespace Notlar.Core;

/// <summary>Enerji tabanlı basit konuşma bölgesi tespiti. Whisper'a yalnızca konuşma içeren bölgeleri vermek hem
/// süreyi kısaltır hem de sessizlikte uydurulan metinleri (halüsinasyon) azaltır.</summary>
public static class SpeechSegmenter
{
    private const int FrameMs = 30;
    private const float MinThreshold = 0.008f;
    private const double NoiseFloorPercentile = 0.20;
    private const float NoiseFloorFactor = 3f;
    private const int MergeGapMs = 600;
    private const int PadMs = 200;
    private const int MinRegionMs = 250;
    private const int MaxRegionMs = 28_000;

    /// <summary>Konuşma bölgelerini örnek indisleri olarak döndürür (<c>Start</c> dahil, <c>End</c> hariç).
    /// Algoritma: 30 ms çerçeve RMS; eşik = max(0.008, gürültü tabanı (çerçeve RMS'lerinin 20. yüzdeliği) × 3);
    /// ardışık konuşma çerçeveleri bölge olur; 600 ms'den kısa boşluklar birleştirilir; 250 ms'den kısa
    /// (dolgu öncesi) bölgeler atılır; kalanlara iki yandan 200 ms dolgu eklenir (sınırlara kırpılır);
    /// 28 sn'den uzun bölgeler 28 sn'lik parçalara bölünür. Tamamen sessiz giriş → boş liste.</summary>
    public static IReadOnlyList<(int Start, int End)> Detect(ReadOnlySpan<float> samples, int sampleRate = 16000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (samples.Length == 0)
            return [];

        var frameLen = Math.Max(1, sampleRate * FrameMs / 1000);
        var frameCount = (samples.Length + frameLen - 1) / frameLen;

        // 1) Çerçeve RMS'leri.
        var rms = new float[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var start = f * frameLen;
            var frame = samples.Slice(start, Math.Min(frameLen, samples.Length - start));
            double sum = 0;
            foreach (var s in frame)
                sum += (double)s * s;
            rms[f] = (float)Math.Sqrt(sum / frame.Length);
        }

        // 2) Eşik: gürültü tabanının 3 katı, en az 0.008.
        var sorted = (float[])rms.Clone();
        Array.Sort(sorted);
        var noiseFloor = sorted[(int)(NoiseFloorPercentile * (sorted.Length - 1))];
        var threshold = Math.Max(MinThreshold, noiseFloor * NoiseFloorFactor);

        // 3) Konuşma çerçevesi dizileri → örnek aralıkları.
        var raw = new List<(int Start, int End)>();
        var runStart = -1;
        for (var f = 0; f <= frameCount; f++)
        {
            var speech = f < frameCount && rms[f] > threshold;
            if (speech && runStart < 0)
                runStart = f;
            else if (!speech && runStart >= 0)
            {
                raw.Add((runStart * frameLen, Math.Min(f * frameLen, samples.Length)));
                runStart = -1;
            }
        }
        if (raw.Count == 0)
            return [];

        // 4) Kısa boşlukları birleştir.
        var mergeGap = ToSamples(MergeGapMs, sampleRate);
        var merged = new List<(int Start, int End)> { raw[0] };
        for (var i = 1; i < raw.Count; i++)
        {
            var last = merged[^1];
            if (raw[i].Start - last.End < mergeGap)
                merged[^1] = (last.Start, raw[i].End);
            else
                merged.Add(raw[i]);
        }

        // 5) Kısa bölgeleri at, dolgu ekle, uzunları böl.
        var minLen = ToSamples(MinRegionMs, sampleRate);
        var pad = ToSamples(PadMs, sampleRate);
        var maxLen = ToSamples(MaxRegionMs, sampleRate);
        var result = new List<(int Start, int End)>();
        foreach (var (start, end) in merged)
        {
            if (end - start < minLen)
                continue;

            var padded = (Start: Math.Max(0, start - pad), End: Math.Min(samples.Length, end + pad));
            if (result.Count > 0 && padded.Start < result[^1].End)
                padded.Start = result[^1].End; // komşu dolgular çakışmasın

            for (var s = padded.Start; s < padded.End; s += maxLen)
                result.Add((s, Math.Min(s + maxLen, padded.End)));
        }
        return result;
    }

    private static int ToSamples(int ms, int sampleRate) => (int)((long)ms * sampleRate / 1000);
}
