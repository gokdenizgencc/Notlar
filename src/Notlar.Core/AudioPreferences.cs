namespace Notlar.Core;

/// <summary>Metne çevirmenin hangi işlemcide yapılacağı (⚙ Ayarlar → Ses ve metne çevirme).</summary>
public enum TranscriptionProcessor
{
    /// <summary>Ayrık GPU varsa GPU, yoksa CPU (tümleşik GPU bu modelde CPU'dan yavaş olduğu için kullanılmaz).</summary>
    Auto,

    /// <summary>GPU tercih edilir; ayrık GPU yoksa tümleşik GPU da denenir. Çökme koruması yine geçerlidir.</summary>
    Gpu,

    /// <summary>Yalnızca CPU.</summary>
    Cpu,
}

/// <summary>Ses ayarlarının saf (aygıttan bağımsız, test edilebilir) yardımcıları: işlemci seçimi, ses aygıtı seçimi,
/// seviye göstergesi.</summary>
public static class AudioPreferences
{
    public const string ProcessorAuto = "auto";
    public const string ProcessorGpu = "gpu";
    public const string ProcessorCpu = "cpu";

    /// <summary>"auto" | "gpu" | "cpu" (büyük/küçük harf duyarsız); tanınmayan değer → <see cref="TranscriptionProcessor.Auto"/>.</summary>
    public static TranscriptionProcessor ParseProcessor(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ProcessorGpu => TranscriptionProcessor.Gpu,
        ProcessorCpu => TranscriptionProcessor.Cpu,
        _ => TranscriptionProcessor.Auto,
    };

    public static string FormatProcessor(TranscriptionProcessor processor) => processor switch
    {
        TranscriptionProcessor.Gpu => ProcessorGpu,
        TranscriptionProcessor.Cpu => ProcessorCpu,
        _ => ProcessorAuto,
    };

    /// <summary>Kaydedilmiş aygıt kimliğini etkin aygıtlar arasında arar. Boş/null tercih → varsayılan aygıt (null).
    /// Tercih edilen aygıt artık yoksa (çıkarıldı, devre dışı) yine null döner ve <paramref name="missing"/> true olur
    /// (çağıran varsayılana düşer ve bunu günlüğe yazar).</summary>
    public static string? ResolveDevice(string? preferredId, IEnumerable<string> activeIds, out bool missing)
    {
        missing = false;
        if (string.IsNullOrWhiteSpace(preferredId)) return null;
        foreach (var id in activeIds)
        {
            if (string.Equals(id, preferredId, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        missing = true;
        return null;
    }

    /// <summary>Seviye çubuğunun alt sınırı (dBFS); bunun altı boş çubuk.</summary>
    public const double MeterFloorDb = -60;

    /// <summary>Örneklerin en büyük mutlak değeri (0–1 aralığına kırpılır; NaN yok sayılır).</summary>
    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = MathF.Abs(s);
            if (a > peak) peak = a;
        }
        return Math.Min(peak, 1f);
    }

    /// <summary>Tepe değerini (0–1) seviye çubuğu oranına (0–1) çevirir: <see cref="MeterFloorDb"/>…0 dBFS doğrusal
    /// olarak 0…1'e eşlenir; konuşma (≈ -30…-10 dBFS) çubuğun ortasına düşer.</summary>
    public static double MeterFraction(float peak)
    {
        if (!(peak > 0)) return 0;
        var db = 20 * Math.Log10(Math.Min(peak, 1f));
        return Math.Clamp((db - MeterFloorDb) / -MeterFloorDb, 0, 1);
    }

    /// <summary>Çubuğun yumuşak düşüşü: yükselişte hemen yeni değere çıkar, düşüşte her adımda en çok
    /// <paramref name="fallPerStep"/> kadar iner (titremesin).</summary>
    public static double Smooth(double previous, double next, double fallPerStep = 0.04) =>
        next >= previous ? next : Math.Max(next, previous - fallPerStep);
}
