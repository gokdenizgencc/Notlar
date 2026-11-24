using System.Security.Cryptography;

namespace Notlar.Kayit;

/// <summary>Konuşmacı gömme modelinin (3D-Speaker CAM++, sherpa-onnx'in ONNX dışa aktarımı, ~27 MB) yerel kopyasını
/// yönetir: yoksa sherpa-onnx'in GitHub sürümünden indirir. <see cref="WhisperModelManager"/> ile aynı desen: indirme
/// önce <c>.part</c> dosyasına yapılır, tamamlanınca asıl ada taşınır; ayrıca dosyanın SHA-256 özeti doğrulanır
/// (bozuk bir ONNX dosyası sherpa-onnx'te süreci çökertebilir). Model yalnızca indirilir; ses hiçbir yere
/// gönderilmez (gömme çıkarma tümüyle yereldir).</summary>
public sealed class SpeakerModelManager
{
    public const string ModelFileName = "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";

    /// <summary>"recongition" yazımı sürüm etiketinin kendisindedir; düzeltme.</summary>
    internal const string ModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/" + ModelFileName;

    /// <summary>Model dosyasının boyutu; sunucu uzunluk bildirmezse ilerleme buna göre hesaplanır.</summary>
    internal const long ModelBytes = 28_281_165;

    /// <summary>Model dosyasının beklenen SHA-256 özeti (2026-11-24'te indirilen dosyadan alındı).</summary>
    internal const string ModelSha256 = "53CAB20BA8F2EB2F6E2D89ECC18E88EF3222169B61B4419CBC13666744A4574B";

    /// <summary>Bu süre boyunca hiç veri gelmezse indirme takılmış sayılır ve iptal edilir.</summary>
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public SpeakerModelManager(string modelsDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDir);
        ModelsDir = modelsDir;
        ModelPath = Path.Combine(modelsDir, ModelFileName);
    }

    public string ModelsDir { get; }

    public string ModelPath { get; }

    public bool IsModelPresent => File.Exists(ModelPath);

    /// <summary>Model yoksa indirir ve yolunu döndürür; varsa hemen döner. <paramref name="progress"/> 0–1 arası
    /// oran bildirir (indirme yapılmazsa hiç çağrılmaz). Ağ/disk hataları olduğu gibi fırlatılır.</summary>
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
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long done = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            stall.CancelAfter(ReadStallTimeout);
            using var response = await Http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength is > 0 and var length ? length : ModelBytes;

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            var lastReported = -1.0;
            while (true)
            {
                stall.CancelAfter(ReadStallTimeout);
                var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                done += read;

                var fraction = Math.Min(0.99, (double)done / total);
                if (fraction - lastReported >= 0.01)
                {
                    lastReported = fraction;
                    progress?.Report(fraction);
                }
            }
            await target.FlushAsync(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Konuşmacı modeli indirmesi yanıt vermiyor (60 sn boyunca veri gelmedi).");
        }

        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"İndirilen konuşmacı modeli beklenen dosya değil ({done:N0} bayt; özet tutmuyor).");
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
