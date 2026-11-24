using Whisper.net.Ggml;

namespace Notlar.Kayit;

/// <summary>Whisper modelinin (<c>ggml-large-v3-turbo</c>, Q5_0 nicemleme) yerel kopyasını yönetir: yoksa
/// Hugging Face'ten <see cref="WhisperGgmlDownloader"/> ile indirir. İndirme önce <c>.part</c> dosyasına yapılır,
/// tamamlanınca asıl ada taşınır; böylece yarıda kalan bir indirme hiçbir zaman "model var" sanılmaz.</summary>
public sealed class WhisperModelManager
{
    public const string ModelFileName = "ggml-large-v3-turbo-q5_0.bin";

    /// <summary>Model dosyasının beklenen SHA-256 özeti. İndirilen dosya bununla uyuşmazsa kullanılmaz: kaynak
    /// (Hugging Face) ele geçirilse ya da dosya değiştirilse bile yabancı bir model yerel ayrıştırıcıya girmez.
    /// Model sürümü değişirse bu değer de güncellenmelidir.</summary>
    internal const string ModelSha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2";

    /// <summary>İndirme akışı uzunluk bildirmediğinden ilerleme bu yaklaşık boyuta göre hesaplanır.</summary>
    internal const long ApproximateModelBytes = 574_041_195;

    /// <summary>Bundan küçük bir dosya (ör. hata sayfası) geçerli model sayılmaz.</summary>
    private const long MinimumModelBytes = 100L * 1024 * 1024;

    /// <summary>Bu süre boyunca hiç veri gelmezse indirme takılmış sayılır ve iptal edilir.</summary>
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperModelManager(string modelsDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDir);
        ModelsDir = modelsDir;
        ModelPath = Path.Combine(modelsDir, ModelFileName);
    }

    public string ModelsDir { get; }

    /// <summary>Modelin tam yolu (<c>&lt;ModelsDir&gt;\ggml-large-v3-turbo-q5_0.bin</c>).</summary>
    public string ModelPath { get; }

    public bool IsModelPresent => File.Exists(ModelPath);

    /// <summary>Model yoksa indirir ve yolunu döndürür; varsa hemen döner. <paramref name="progress"/> 0–1 arası
    /// oran bildirir (indirme yapılmazsa hiç çağrılmaz). Ağ/disk hataları olduğu gibi fırlatılır; çağıran Türkçe
    /// mesajla sarar. Aynı örnek üzerinde eşzamanlı çağrılar tek indirmede birleşir.</summary>
    public async Task<string> EnsureModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsModelPresent) return ModelPath;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsModelPresent) return ModelPath;
            Directory.CreateDirectory(ModelsDir);
            var partPath = ModelPath + ".part";
            try
            {
                await DownloadAsync(partPath, progress, cancellationToken).ConfigureAwait(false);
                File.Move(partPath, ModelPath, overwrite: true);
            }
            catch
            {
                TryDelete(partPath);
                throw;
            }
            progress?.Report(1.0);
            return ModelPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task DownloadAsync(string partPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using var source = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(GgmlType.LargeV3Turbo, QuantizationType.Q5_0, cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 16, useAsync: true);

        var total = TryGetLength(source) ?? ApproximateModelBytes;
        var buffer = new byte[1 << 20];
        long done = 0;
        var lastReported = -1.0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            stall.CancelAfter(ReadStallTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Model indirmesi yanıt vermiyor (60 sn boyunca veri gelmedi).");
            }
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;

            var fraction = Math.Min(0.99, (double)done / total);
            if (fraction - lastReported >= 0.005)
            {
                lastReported = fraction;
                progress?.Report(fraction);
            }
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (done < MinimumModelBytes)
            throw new InvalidDataException($"İndirilen model dosyası eksik görünüyor ({done:N0} bayt).");

        await target.DisposeAsync().ConfigureAwait(false);
        await ModelIntegrity.VerifyAsync(partPath, ModelSha256, cancellationToken).ConfigureAwait(false);
    }

    private static long? TryGetLength(Stream stream)
    {
        try
        {
            return stream.CanSeek && stream.Length > 0 ? stream.Length : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bir sonraki denemede FileMode.Create ile zaten üzerine yazılır.
        }
    }
}
