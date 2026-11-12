using NAudio.Wave;
using Notlar.Core;
using Whisper.net;

namespace Notlar.Kayit;

/// <summary>Tek kanallı bir WAV dosyasını yerel Whisper (Whisper.net, CPU) ile metne çevirir.
/// Ses önce <see cref="SpeechSegmenter"/> ile konuşma bölgelerine ayrılır; yalnızca bu bölgeler Whisper'a verilir
/// (sessizlikte uydurulan metni ve boşa işlem süresini azaltır). Ardışık yakın bölgeler en çok 28 sn'lik pencerelerde
/// birlikte işlenir (Whisper her çağrıda 30 sn'lik pencere kodladığından kısa bölgeleri tek tek vermek çok yavaştır).
/// Segment zamanları pencere ofseti eklenerek kaydın başına göre verilir. Kalıp uydurma metinler
/// <see cref="HallucinationFilter"/> ile elenir. Model (factory) ve işlemci her iş için oluşturulup atılır; boşta bellek
/// tutulmaz. İş parçacığı sayısı = max(1, işlemci sayısı / 2). Hız (CPU, FlashAttention): 30 sn'lik pencere başına
/// yaklaşık 25 sn (i7-9750H, 6 iş parçacığı) — yani konuşma yoğun bir saatlik kanal ~1 saatte çevrilir.</summary>
public sealed class WhisperTranscriber
{
    internal const int SampleRate = 16000;

    /// <summary>Bir Whisper çağrısına verilecek en uzun pencere (Whisper'ın 30 sn sınırının altında).</summary>
    internal const int MaxWindowSamples = 28 * SampleRate;

    /// <summary>Aralarındaki sessizlik bundan uzun olan bölgeler aynı pencereye konmaz.</summary>
    internal const int MaxGroupGapSamples = 5 * SampleRate;

    public static int Threads => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary><paramref name="language"/>: "tr", "en" gibi iki harfli kod ya da "auto" (Whisper dili kendisi bulur).
    /// <paramref name="progress"/>: 0–1 arası oran. Konuşma yoksa boş liste döner.</summary>
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string wavPath, Speaker speaker, string language,
        string modelPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper modeli bulunamadı.", modelPath);

        var samples = await Task.Run(() => ReadMono16k(wavPath), cancellationToken).ConfigureAwait(false);
        var regions = SpeechSegmenter.Detect(samples, SampleRate);
        var windows = GroupRegions(regions, MaxWindowSamples, MaxGroupGapSamples);
        if (windows.Count == 0)
        {
            progress?.Report(1.0);
            return [];
        }

        long total = windows.Sum(w => (long)(w.End - w.Start));
        long done = 0;
        var result = new List<TranscriptSegment>();

        // FlashAttention CPU'da aynı çıktıyla ~%20 daha hızlı (i7-9750H, large-v3-turbo Q5_0 ölçümü).
        var options = WhisperFactoryOptions.Default;
        options.UseFlashAttention = true;
        using var factory = WhisperFactory.FromPath(modelPath, options);
        var builder = factory.CreateBuilder().WithThreads(Threads);
        builder = string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language.ToLowerInvariant());
        await using var processor = builder.Build();

        progress?.Report(0.0);
        foreach (var (start, end) in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = samples.AsSpan(start, end - start).ToArray();
            var offsetMs = start * 1000L / SampleRate;
            var windowEndMs = end * 1000L / SampleRate;

            await foreach (var segment in processor.ProcessAsync(window, cancellationToken).ConfigureAwait(false))
            {
                var text = HallucinationFilter.Clean(segment.Text);
                if (HallucinationFilter.IsHallucination(text)) continue;
                var startMs = Math.Min(offsetMs + (long)segment.Start.TotalMilliseconds, windowEndMs);
                var endMs = Math.Clamp(offsetMs + (long)segment.End.TotalMilliseconds, startMs, windowEndMs);
                result.Add(new TranscriptSegment(startMs, endMs, speaker, text));
            }

            done += end - start;
            progress?.Report((double)done / total);
        }
        return result;
    }

    /// <summary>WAV dosyasını 16 kHz mono float'a çevirerek okur. Kayıtçının yazdığı 16 kHz mono PCM16 doğrudan okunur;
    /// başka biçimler (float/PCM24/PCM32, çok kanal, farklı hız) mono'ya indirilip yeniden örneklenir.</summary>
    internal static float[] ReadMono16k(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        var format = reader.WaveFormat;
        var kind = AudioConversion.GetSampleKind(format);
        var channels = Math.Max(1, format.Channels);
        var frameSize = AudioConversion.BytesPerSample(kind) * channels;
        var resampler = new MonoResampler(format.SampleRate, SampleRate);

        var estimated = reader.Length / frameSize * SampleRate / Math.Max(1, format.SampleRate) + 64;
        var output = new List<float>((int)Math.Min(estimated, 1 << 28));
        var bytes = new byte[frameSize * 16384];
        var mono = new float[16384];
        var resampled = new float[resampler.MaxOutputLength(mono.Length)];
        var pending = 0;
        int read;
        while ((read = reader.Read(bytes, pending, bytes.Length - pending)) > 0 || pending > 0)
        {
            var available = pending + read;
            var frames = available / frameSize;
            if (frames == 0) break;
            AudioConversion.DownmixToMono(bytes.AsSpan(0, frames * frameSize), kind, channels, mono);
            var produced = resampler.Process(mono.AsSpan(0, frames), resampled);
            output.AddRange(resampled.AsSpan(0, produced));

            pending = available - frames * frameSize;
            if (pending > 0) Buffer.BlockCopy(bytes, frames * frameSize, bytes, 0, pending);
            if (read == 0) break;
        }
        return output.ToArray();
    }

    /// <summary>Konuşma bölgelerini, toplam uzunluğu <paramref name="maxWindow"/>'u ve aradaki sessizliği
    /// <paramref name="maxGap"/>'i aşmayacak şekilde ardışık pencerelerde toplar. Tek başına uzun bölgeler
    /// (SpeechSegmenter zaten 28 sn'de böler) kendi penceresinde kalır.</summary>
    internal static IReadOnlyList<(int Start, int End)> GroupRegions(IReadOnlyList<(int Start, int End)> regions,
        int maxWindow, int maxGap)
    {
        var windows = new List<(int Start, int End)>();
        foreach (var (start, end) in regions.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (windows.Count > 0)
            {
                var last = windows[^1];
                if (start - last.End <= maxGap && end - last.Start <= maxWindow)
                {
                    windows[^1] = (last.Start, Math.Max(last.End, end));
                    continue;
                }
            }
            windows.Add((start, end));
        }
        return windows;
    }
}
