using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace Notlar.Kayit;

/// <summary>Bir ses kesitinden konuşmacı gömmesi (ses parmak izi) çıkarır: sherpa-onnx + ONNX Runtime, yalnızca CPU,
/// tümüyle yerel. Model her iş için yüklenip atılır (boşta bellek tutulmaz).</summary>
internal sealed class SpeakerEmbedder : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    public SpeakerEmbedder(string modelPath, int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Konuşmacı modeli bulunamadı.", modelPath);

        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = Math.Max(1, threads),
            Debug = 0,
            Provider = "cpu",
        };
        _extractor = new SpeakerEmbeddingExtractor(config);

        // sherpa-onnx model yüklenemeyince (bozuk dosya vb.) istisna fırlatmaz: yerel tutamaç boş kalır ve sonraki
        // ilk çağrı süreci erişim ihlaliyle (0xC0000005) öldürür. Tutamaç yalnızca özel alandan okunabiliyor; alan
        // bulunamazsa (paket sürümü değişmiş) güvenli tarafta kalınıp kişi ayrımı yapılmaz.
        var handle = HandleField?.GetValue(_extractor) is HandleRef reference ? reference.Handle : IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            GC.SuppressFinalize(_extractor);
            throw new InvalidDataException("Konuşmacı modeli yüklenemedi (dosya bozuk olabilir).");
        }
    }

    private static readonly FieldInfo? HandleField =
        typeof(SpeakerEmbeddingExtractor).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>16 kHz mono örneklerden gömme; kesit model için fazla kısaysa null.</summary>
    public float[]? Embed(float[] samples)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(WhisperTranscriber.SampleRate, samples);
        stream.InputFinished();
        return _extractor.IsReady(stream) ? _extractor.Compute(stream) : null;
    }

    public void Dispose() => _extractor.Dispose();
}
