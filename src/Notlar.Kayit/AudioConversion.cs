using System.Buffers.Binary;
using NAudio.Dsp;
using NAudio.Wave;

namespace Notlar.Kayit;

/// <summary>Ham ses baytlarının örnek türü (WAVE biçiminden çözülür).</summary>
internal enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

/// <summary>Yakalanan ham ses verisini çözme ve mono'ya indirme yardımcıları (aygıttan bağımsız, test edilebilir).</summary>
internal static class AudioConversion
{
    /// <summary>Biçimin örnek türünü bulur. <see cref="WaveFormatExtensible"/> için alt biçim (SubFormat) GUID'ine
    /// bakılır; 32 bit kapta 24 geçerli bit taşıyan PCM, sola yaslı olduğu için PCM32 olarak okunur.
    /// Desteklenmeyen biçimde Türkçe <see cref="NotSupportedException"/>.</summary>
    internal static SampleKind GetSampleKind(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible)
        {
            if (format is WaveFormatExtensible ext)
            {
                if (ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT) encoding = WaveFormatEncoding.IeeeFloat;
                else if (ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM) encoding = WaveFormatEncoding.Pcm;
                else throw new NotSupportedException(
                    $"Desteklenmeyen ses alt biçimi: {AudioMediaSubtypes.GetAudioSubtypeName(ext.SubFormat)}.");
            }
            else
            {
                // Alt biçim bilgisi yoksa: WASAPI paylaşımlı mod karışım biçimi pratikte her zaman 32 bit float'tır.
                encoding = format.BitsPerSample == 32 ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm;
            }
        }

        return (encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleKind.Float32,
            (WaveFormatEncoding.Pcm, 16) => SampleKind.Pcm16,
            (WaveFormatEncoding.Pcm, 24) => SampleKind.Pcm24,
            (WaveFormatEncoding.Pcm, 32) => SampleKind.Pcm32,
            _ => throw new NotSupportedException(
                $"Desteklenmeyen ses biçimi: {format.Encoding}, {format.BitsPerSample} bit."),
        };
    }

    internal static int BytesPerSample(SampleKind kind) => kind switch
    {
        SampleKind.Pcm16 => 2,
        SampleKind.Pcm24 => 3,
        _ => 4,
    };

    /// <summary>Bir örneği [-1, 1] aralığında float'a çevirir (küçük-endian).</summary>
    internal static float ReadSample(ReadOnlySpan<byte> bytes, SampleKind kind) => kind switch
    {
        SampleKind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        SampleKind.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        SampleKind.Pcm24 => (bytes[0] | (bytes[1] << 8) | ((sbyte)bytes[2] << 16)) / 8388608f,
        SampleKind.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Çok kanallı ham veriyi kanalların ortalamasıyla mono float'a indirir; yazılan çerçeve sayısını
    /// döndürür. Sondaki yarım çerçeve (varsa) yok sayılır.</summary>
    internal static int DownmixToMono(ReadOnlySpan<byte> data, SampleKind kind, int channels, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var bytesPerSample = BytesPerSample(kind);
        var frameSize = bytesPerSample * channels;
        var frames = data.Length / frameSize;
        if (destination.Length < frames)
            throw new ArgumentException("Hedef arabellek çok küçük.", nameof(destination));

        var scale = 1f / channels;
        for (var f = 0; f < frames; f++)
        {
            var frame = data.Slice(f * frameSize, frameSize);
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += ReadSample(frame.Slice(c * bytesPerSample, bytesPerSample), kind);
            destination[f] = sum * scale;
        }
        return frames;
    }

    /// <summary>[-1, 1] float örneği kırparak 16 bit PCM'e çevirir.</summary>
    internal static short ToPcm16(float sample)
    {
        if (float.IsNaN(sample)) return 0;
        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)MathF.Round(clamped * short.MaxValue);
    }
}

/// <summary>Akan mono float veriyi <see cref="WdlResampler"/> ile hedef örnekleme hızına çevirir (girdi güdümlü mod;
/// parça parça beslenebilir, filtre durumu çağrılar arasında korunur).</summary>
internal sealed class MonoResampler
{
    private readonly WdlResampler? _resampler;
    private readonly double _ratio;

    public MonoResampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        _ratio = (double)outputRate / inputRate;
        if (inputRate == outputRate) return;

        // NAudio'nun WdlResamplingSampleProvider'ıyla aynı ayarlar: doğrusal aradeğerleme + 2 kademe alçak geçiren filtre.
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true);
        _resampler.SetRates(inputRate, outputRate);
    }

    /// <summary>Verilen girdi uzunluğu için gereken en büyük çıktı arabelleği.</summary>
    public int MaxOutputLength(int inputLength) => (int)Math.Ceiling(inputLength * _ratio) + 32;

    /// <summary>Girdiyi işler, üretilen örnekleri <paramref name="output"/>'a yazar ve sayısını döndürür.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.IsEmpty) return 0;
        if (_resampler is null)
        {
            input.CopyTo(output);
            return input.Length;
        }

        var produced = 0;
        while (!input.IsEmpty && produced < output.Length)
        {
            var wanted = _resampler.ResamplePrepare(input.Length, 1, out var inBuffer);
            var count = Math.Min(Math.Min(wanted, input.Length), inBuffer.Length);
            if (count <= 0) break;
            input[..count].CopyTo(inBuffer);
            produced += _resampler.ResampleOut(output[produced..], count, output.Length - produced, 1);
            input = input[count..];
        }
        return produced;
    }
}
