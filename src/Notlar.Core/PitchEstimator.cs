namespace Notlar.Core;

/// <summary>Konuşmanın temel frekansını (F0, ses tellerinin titreşim hızı) YIN yöntemiyle kestirir (de Cheveigné &amp;
/// Kawahara, 2002). Saf C#, model yok.
/// <para>Neden YIN (zaman alanında periyot arama): Teams/telefon sesi bant sınırlıdır (≈300–3400 Hz); erkek sesinin
/// temel frekansı (≈85–155 Hz) sinyalde hiç bulunmayabilir. Spektrumda en güçlü tepeye bakan bir yöntem bu durumda
/// 2./3. harmoniği (ör. 240/360 Hz) bulur ve erkeği kadın sanır. Harmonikler (2f0, 3f0, …) ise hep aynı T0 = 1/f0
/// periyoduyla tekrar eder; dalga biçiminin kendisiyle T0 kadar kaydırılmış hâlinin farkı (YIN fark fonksiyonu)
/// temel frekans olmasa da T0'da en küçüktür. Böylece F0 doğrudan harmonik aralığından bulunur.</para>
/// <para>Akış: 16 kHz giriş alçak geçiren süzgeçle 8 kHz'e indirilir (F0 için yeterli, 4× hızlı) → 40 ms'lik
/// pencereler, 10 ms adım → çok sessiz pencereler atılır → YIN: fark fonksiyonu, birikimli ortalamayla normalize
/// (CMND), eşiğin altına inen İLK çukur (alt oktav hatasını önler), parabolik ara değer → çukur yeterince derin
/// değilse pencere sessizsiz/sessiz (gürültü, nefes, "s/ş") sayılıp atılır.</para></summary>
public static class PitchEstimator
{
    /// <summary>Aranan en düşük F0 (Hz). 60: en kalın erkek konuşma seslerinin (≈65–70 Hz) altı.</summary>
    public const double MinF0Hz = 60;

    /// <summary>Aranan en yüksek F0 (Hz). 400: yetişkin kadın konuşmasının üst sınırının (≈300 Hz) rahatça üstü.</summary>
    public const double MaxF0Hz = 400;

    /// <summary>Analiz örnekleme hızı. F0 ≤ 400 Hz için 8 kHz fazlasıyla yeterli; telefon bandı da 4 kHz'e kadardır.</summary>
    public const int AnalysisRate = 8000;

    /// <summary>Pencereler arası adım (sn).</summary>
    public const double HopSeconds = 0.010;

    /// <summary>YIN toplama penceresi (sn). En uzun periyodun (1/60 Hz ≈ 16,7 ms) en az iki katı olmalı.</summary>
    private const double IntegrationSeconds = 0.025;

    /// <summary>Normalize fark fonksiyonunun (CMND) bu değerin altına indiği ilk çukur periyot sayılır; hiçbir
    /// çukur bu kadar derine inmezse pencere sesli değildir. 0,2: YIN makalesinin önerdiği 0,1–0,2 aralığının üstü;
    /// bant sınırlı ve sıkıştırılmış (Teams) seste periyodiklik biraz zayıfladığından gevşek uç seçildi.</summary>
    public const double VoicingThreshold = 0.2;

    /// <summary>Bu RMS'in (tam ölçek = 1) altındaki pencere sessiz sayılır (≈ −50 dBFS).</summary>
    public const float SilenceRms = 0.003f;

    /// <summary>Tek pencerenin F0'ı (Hz); pencere sessiz/sessizsiz ya da çok kısaysa null. Pencere en az
    /// <c>(0,025 + 1/60) sn</c> uzunlukta olmalıdır (8 kHz'de 334 örnek).</summary>
    public static double? EstimateFrame(ReadOnlySpan<float> frame, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 2000);
        var window = (int)Math.Round(IntegrationSeconds * sampleRate);
        var tauMin = Math.Max(2, (int)Math.Floor(sampleRate / MaxF0Hz));
        var tauMax = (int)Math.Ceiling(sampleRate / MinF0Hz);
        if (frame.Length < window + tauMax + 1) return null;

        // DC'yi at, enerjiye bak.
        var n = window + tauMax + 1;
        double mean = 0;
        for (var i = 0; i < n; i++) mean += frame[i];
        mean /= n;
        var x = new float[n];
        double energy = 0;
        for (var i = 0; i < n; i++)
        {
            x[i] = (float)(frame[i] - mean);
            energy += (double)x[i] * x[i];
        }
        if (Math.Sqrt(energy / n) < SilenceRms) return null;

        // Fark fonksiyonu ve birikimli ortalamayla normalize edilmiş hâli (CMND).
        var cmnd = new double[tauMax + 2];
        cmnd[0] = 1;
        double running = 0;
        for (var tau = 1; tau <= tauMax + 1; tau++)
        {
            double d = 0;
            for (var j = 0; j < window; j++)
            {
                var diff = x[j] - x[j + tau];
                d += diff * diff;
            }
            running += d;
            cmnd[tau] = running <= 0 ? 1 : d * tau / running;
        }

        // Eşiğin altına inen ilk çukur: oradan yerel en küçüğe in.
        var best = -1;
        for (var tau = tauMin; tau <= tauMax; tau++)
        {
            if (cmnd[tau] >= VoicingThreshold) continue;
            while (tau + 1 <= tauMax && cmnd[tau + 1] < cmnd[tau]) tau++;
            best = tau;
            break;
        }
        if (best < 0) return null;

        // Parabolik ara değer (periyot tam sayı örnek olmak zorunda değil).
        var period = (double)best;
        if (best > 1 && best < tauMax + 1)
        {
            double a = cmnd[best - 1], b = cmnd[best], c = cmnd[best + 1];
            var denom = a - 2 * b + c;
            if (denom > 1e-12)
                period += Math.Clamp(0.5 * (a - c) / denom, -0.5, 0.5);
        }
        var f0 = sampleRate / period;
        return f0 is >= MinF0Hz and <= MaxF0Hz ? f0 : null;
    }

    /// <summary>Sesin sesli pencerelerinin F0 değerleri (Hz, zaman sırasıyla; sessiz/sessizsiz pencereler yok).
    /// <paramref name="sampleRate"/> 8 kHz'in tam katıysa (16 kHz…) önce süzülüp 8 kHz'e indirilir.</summary>
    public static IReadOnlyList<double> Track(ReadOnlySpan<float> audio, int sampleRate)
    {
        var (signal, rate) = ToAnalysisRate(audio, sampleRate);
        var frameLength = (int)Math.Round(IntegrationSeconds * rate) + (int)Math.Ceiling(rate / MinF0Hz) + 1;
        var hop = Math.Max(1, (int)Math.Round(HopSeconds * rate));
        var result = new List<double>();
        for (var start = 0; start + frameLength <= signal.Length; start += hop)
        {
            if (EstimateFrame(signal.AsSpan(start, frameLength), rate) is { } f0)
                result.Add(f0);
        }
        return result;
    }

    /// <summary>Değerlerin ortancası; boşsa null.</summary>
    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>8 kHz'in tam katı hızdaki sesi alçak geçiren FIR (pencereli sinc, kesim 3,4 kHz) ile süzüp seyreltir;
    /// diğer hızlarda ses olduğu gibi kalır (YIN her hızda çalışır, yalnızca daha yavaştır).</summary>
    private static (float[] Signal, int Rate) ToAnalysisRate(ReadOnlySpan<float> audio, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 2000);
        if (sampleRate <= AnalysisRate || sampleRate % AnalysisRate != 0)
            return (audio.ToArray(), sampleRate);

        var factor = sampleRate / AnalysisRate;
        var taps = LowPass(3400.0 / sampleRate, 16 * factor + 1);
        var half = taps.Length / 2;
        var output = new float[audio.Length / factor];
        for (var o = 0; o < output.Length; o++)
        {
            var center = o * factor;
            double sum = 0;
            for (var k = 0; k < taps.Length; k++)
            {
                var i = center + k - half;
                if ((uint)i < (uint)audio.Length) sum += taps[k] * audio[i];
            }
            output[o] = (float)sum;
        }
        return (output, AnalysisRate);
    }

    /// <summary>Hamming pencereli sinc alçak geçiren süzgeç; <paramref name="cutoff"/> örnekleme hızına oranla.</summary>
    private static double[] LowPass(double cutoff, int length)
    {
        var taps = new double[length];
        var m = length - 1;
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            var t = i - m / 2.0;
            var sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
            taps[i] = sinc * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / m));
            sum += taps[i];
        }
        for (var i = 0; i < length; i++) taps[i] /= sum;
        return taps;
    }
}
