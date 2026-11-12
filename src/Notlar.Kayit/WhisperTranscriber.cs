using NAudio.Wave;
using Notlar.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Notlar.Kayit;

/// <summary>Tek kanallı bir WAV dosyasını yerel Whisper (Whisper.net; ayrık GPU varsa Vulkan, yoksa CPU) ile metne çevirir.
/// Ses önce <see cref="SpeechSegmenter"/> ile konuşma bölgelerine ayrılır; yalnızca bu bölgeler Whisper'a verilir
/// (sessizlikte uydurulan metni ve boşa işlem süresini azaltır). Ardışık yakın bölgeler en çok 28 sn'lik pencerelerde
/// birlikte işlenir (Whisper her çağrıda 30 sn'lik pencere kodladığından kısa bölgeleri tek tek vermek çok yavaştır).
/// Segment zamanları pencere ofseti eklenerek kaydın başına göre verilir. Kalıp uydurma metinler
/// <see cref="HallucinationFilter"/> ile elenir. Model (factory) ve işlemci her iş için oluşturulup atılır; boşta bellek
/// tutulmaz. Hız (FlashAttention, 28 sn'lik pencere): Vulkan/GTX 1660 Ti ~1,2 sn; CPU (i7-9750H, 6 iş parçacığı) ~26 sn.
/// GPU seçimi, CPU'ya düşüş ve çökme koruması için bkz. <see cref="WhisperRuntime"/>: GPU'da yükleme ya da çalışma
/// hatası olursa aynı iş CPU ile baştan yeniden denenir ve süreç boyunca GPU kapatılır.</summary>
public sealed class WhisperTranscriber
{
    /// <summary>GPU'da CPU yalnızca mel/örnekleme işini yapar; iş parçacığı sayısı hızı etkilemiyor (2–6 aynı).</summary>
    internal const int GpuThreads = 4;

    internal const int SampleRate = 16000;

    /// <summary>Bir Whisper çağrısına verilecek en uzun pencere (Whisper'ın 30 sn sınırının altında).</summary>
    internal const int MaxWindowSamples = 28 * SampleRate;

    /// <summary>Aralarındaki sessizlik bundan uzun olan bölgeler aynı pencereye konmaz.</summary>
    internal const int MaxGroupGapSamples = 5 * SampleRate;

    /// <summary>Her pencerenin iki yanına eklenen gerçek ses bağlamı (28 + 2×1 sn ≤ Whisper'ın 30 sn penceresi).</summary>
    internal const int ContextPadSamples = 1 * SampleRate;

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
        var windows = PadWindows(GroupRegions(regions, MaxWindowSamples, MaxGroupGapSamples), samples.Length, ContextPadSamples);
        if (windows.Count == 0)
        {
            progress?.Report(1.0);
            return [];
        }

        var stateDir = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? ".";
        WhisperRuntime.EnsureConfigured(stateDir);
        if (WhisperRuntime.ShouldTryGpu)
        {
            WhisperRuntime.MarkGpuRunStarted(stateDir);
            var gpuSucceeded = false;
            Exception? gpuError;
            try
            {
                var (segments, usedGpu) = await RunAsync(samples, windows, speaker, language, modelPath, tryGpu: true,
                    progress, cancellationToken).ConfigureAwait(false);
                gpuSucceeded = usedGpu;
                return segments;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                gpuError = ex;
            }
            finally
            {
                WhisperRuntime.MarkGpuRunFinished(stateDir, gpuSucceeded);
            }

            // GPU yükleme/çalışma hatası: aynı iş CPU ile baştan yeniden denenir. CPU da başarısızsa hata GPU'ya
            // özgü değildir (ör. bozuk model); GPU açık kalır ve CPU hatası fırlatılır.
            WhisperRuntime.Raise($"GPU çevirisi başarısız, CPU ile yeniden deneniyor: {gpuError.GetType().Name}: {gpuError.Message}");
            var cpu = await RunAsync(samples, windows, speaker, language, modelPath, tryGpu: false, progress,
                cancellationToken).ConfigureAwait(false);
            WhisperRuntime.DisableGpu($"GPU çevirisi başarısız oldu ({gpuError.GetType().Name}: {gpuError.Message})");
            return cpu.Segments;
        }
        return (await RunAsync(samples, windows, speaker, language, modelPath, tryGpu: false, progress,
            cancellationToken).ConfigureAwait(false)).Segments;
    }

    private static async Task<(List<TranscriptSegment> Segments, bool UsedGpu)> RunAsync(float[] samples,
        IReadOnlyList<(int Start, int End)> windows, Speaker speaker, string language, string modelPath, bool tryGpu,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        long total = windows.Sum(w => (long)(w.End - w.Start));
        long done = 0;
        var result = new List<TranscriptSegment>();

        using var factory = CreateFactory(modelPath, tryGpu, out var device, out var usedGpu);
        WhisperRuntime.RecordBackend(usedGpu, device);
        await using var processor = Build(factory, language, usedGpu ? GpuThreads : Threads);
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
        return (result, usedGpu);
    }

    /// <summary>Modeli yükler. GPU denenecekse ayrık GPU seçilir: aygıt listesi sürecin ilk yüklemesinde öğrenildiğinden,
    /// ilk denemede tümleşik GPU'ya düşüldüyse model ayrık GPU'ya (yoksa CPU'ya) yeniden yüklenir.</summary>
    private static WhisperFactory CreateFactory(string modelPath, bool tryGpu, out int device, out bool usedGpu)
    {
        device = 0;
        usedGpu = false;
        if (tryGpu)
        {
            var preferred = WhisperRuntime.PreferredGpuDevice;
            if (preferred is null) tryGpu = false;
            else device = preferred.Value;
        }

        WhisperRuntime.BeginAttempt();
        var factory = WhisperFactory.FromPath(modelPath, Options(tryGpu, device));
        if (!tryGpu) return factory;
        usedGpu = WhisperRuntime.AttemptUsedGpu;

        if (WhisperRuntime.LoadedLibrary != nameof(RuntimeLibrary.Vulkan))
        {
            WhisperRuntime.DisableGpu($"Vulkan kütüphanesi yüklenemedi (yüklenen: {WhisperRuntime.LoadedLibrary ?? "?"})");
            return factory;
        }
        var after = WhisperRuntime.PreferredGpuDevice;
        if (after == device || (after is not null && !usedGpu)) return factory; // GPU yoksa whisper zaten CPU'ya geçti

        factory.Dispose();
        if (after is null) WhisperRuntime.DisableGpu("ayrık (UMA olmayan) Vulkan GPU'su yok; tümleşik GPU CPU'dan yavaş");
        else WhisperRuntime.Raise($"Vulkan aygıtı {device} tümleşik; ayrık aygıt {after} seçildi.");
        device = after ?? 0;
        WhisperRuntime.BeginAttempt();
        factory = WhisperFactory.FromPath(modelPath, Options(after is not null, device));
        usedGpu = after is not null && WhisperRuntime.AttemptUsedGpu;
        return factory;
    }

    /// <summary>FlashAttention: CPU'da aynı çıktıyla ~%20, Vulkan'da (GTX 1660 Ti) ~%7 daha hızlı (large-v3-turbo Q5_0).</summary>
    private static WhisperFactoryOptions Options(bool useGpu, int device)
    {
        var options = WhisperFactoryOptions.Default;
        options.UseGpu = useGpu;
        options.GpuDevice = device;
        options.UseFlashAttention = true;
        return options;
    }

    private static WhisperProcessor Build(WhisperFactory factory, string language, int threads)
    {
        var builder = factory.CreateBuilder().WithThreads(threads);
        builder = string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language.ToLowerInvariant());
        return builder.Build();
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

    /// <summary>Pencereleri iki yandan en çok <paramref name="pad"/> örnek gerçek sesle genişletir (komşu pencereyle
    /// aradaki boşluğun yarısını geçmeden, dosya sınırlarında kırpılarak). Konuşmanın hemen bitiminde kesilen
    /// pencerelerde Whisper (özellikle kısık sesli kayıtlarda) metni atlayıp "Altyazı M.K." gibi uydurma çıktı
    /// üretiyordu; arkaya sıfır (sessizlik) eklemek düzeltmiyor, gerçek ses bağlamı düzeltiyor.</summary>
    internal static IReadOnlyList<(int Start, int End)> PadWindows(IReadOnlyList<(int Start, int End)> windows,
        int totalSamples, int pad)
    {
        var padded = new List<(int Start, int End)>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            var (start, end) = windows[i];
            var before = i == 0 ? start : (start - windows[i - 1].End) / 2;
            var after = i == windows.Count - 1 ? totalSamples - end : (windows[i + 1].Start - end) / 2;
            padded.Add((start - Math.Clamp(before, 0, pad), end + Math.Clamp(after, 0, pad)));
        }
        return padded;
    }
}
