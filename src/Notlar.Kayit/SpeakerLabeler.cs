using Notlar.Core;

namespace Notlar.Kayit;

/// <summary>Birleştirilmiş transkript satırlarına sesine göre kişi numarası verir ("Kişi 1", "Kişi 2"…): her satırın
/// kendi kanalındaki ses kesitinden gömme çıkarır (<see cref="SpeakerEmbedder"/>), sonra tüm satırları
/// <see cref="SpeakerClustering"/> ile kümeler. İki kanal BİRLİKTE kümelenir: kanal kimin konuştuğunu söylemez
/// (hoparlörden mikrofona sızan ses karşı taraftaki kişiyle aynı kümeye düşebilmeli); akustik fark yüzünden aynı
/// kişi iki kanalda ayrışırsa sonuç zaten ayrı kümelemeyle aynı olur.</summary>
public sealed class SpeakerLabeler
{
    /// <summary>Bundan kısa konuşma kesitinden gömme çıkarılmaz (satır komşusuna göre atanır).</summary>
    internal const int MinEmbedSamples = WhisperTranscriber.SampleRate / 2;

    /// <summary>Bir satırdan gömme için kullanılan en uzun konuşma (fazlası kimliğe bir şey katmaz, süre harcar).</summary>
    internal const int MaxEmbedSamples = 20 * WhisperTranscriber.SampleRate;

    public SpeakerLabeler(SpeakerModelManager models)
    {
        Models = models ?? throw new ArgumentNullException(nameof(models));
    }

    public SpeakerModelManager Models { get; }

    /// <summary>Satırları kişi numaralarıyla döndürür (sıra ve içerik aynı, yalnızca <c>Person</c> dolar).
    /// <paramref name="micPath"/>/<paramref name="loopbackPath"/>: kanalların WAV dosyaları (olmayan kanal null).
    /// Hiçbir satırdan gömme çıkmazsa satırlar etiketsiz döner. Hatalar fırlatılır; çağıran etiketsiz devam eder.</summary>
    public IReadOnlyList<TranscriptSegment> Label(IReadOnlyList<TranscriptSegment> segments, string? micPath,
        string? loopbackPath, string modelPath, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, SpeakerClusteringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0) return segments;

        var embeddings = Embed(segments, micPath, loopbackPath, modelPath, progress, cancellationToken);
        var samples = segments.Select((s, i) => new VoiceSample(s.StartMs, s.EndMs, s.Speaker, embeddings[i])).ToList();
        var persons = SpeakerClustering.Assign(samples, options);
        progress?.Report(1.0);
        return segments.Select((s, i) => s with { Person = persons[i] }).ToList();
    }

    /// <summary>Her satır için gömme (çıkarılamayanlar null). Kanallar sırayla okunur; aynı anda tek kanalın sesi
    /// bellekte tutulur.</summary>
    internal static float[]?[] Embed(IReadOnlyList<TranscriptSegment> segments, string? micPath, string? loopbackPath,
        string modelPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var embeddings = new float[]?[segments.Count];
        using var embedder = new SpeakerEmbedder(modelPath, WhisperTranscriber.Threads);
        var done = 0;
        foreach (var (channel, path) in new[] { (Speaker.Ben, micPath), (Speaker.KarsiTaraf, loopbackPath) })
        {
            var indices = Enumerable.Range(0, segments.Count).Where(i => segments[i].Speaker == channel).ToList();
            if (indices.Count == 0) continue;
            if (path is null || !File.Exists(path))
            {
                done += indices.Count;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var audio = WhisperTranscriber.ReadMono16k(path);
            var speech = SpeechSegmenter.Detect(audio, WhisperTranscriber.SampleRate);
            foreach (var i in indices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = SpeechClip(audio, speech, segments[i].StartMs, segments[i].EndMs);
                if (clip.Length >= MinEmbedSamples) embeddings[i] = embedder.Embed(clip);
                progress?.Report((double)++done / segments.Count * 0.99);
            }
        }
        return embeddings;
    }

    /// <summary>Satırın zaman aralığındaki konuşma (sessizlik atılmış, en çok <see cref="MaxEmbedSamples"/>).
    /// Aralıkta konuşma bölgesi bulunamazsa (zaman damgası kayması) aralığın tamamı kullanılır.</summary>
    internal static float[] SpeechClip(float[] audio, IReadOnlyList<(int Start, int End)> speech, long startMs, long endMs)
    {
        var start = (int)Math.Clamp(startMs * WhisperTranscriber.SampleRate / 1000, 0, audio.Length);
        var end = (int)Math.Clamp(endMs * WhisperTranscriber.SampleRate / 1000, start, audio.Length);

        var clip = new List<float>(Math.Min(end - start, MaxEmbedSamples));
        foreach (var (regionStart, regionEnd) in speech)
        {
            if (regionEnd <= start) continue;
            if (regionStart >= end || clip.Count >= MaxEmbedSamples) break;
            var from = Math.Max(start, regionStart);
            var count = Math.Min(Math.Min(end, regionEnd) - from, MaxEmbedSamples - clip.Count);
            if (count > 0) clip.AddRange(audio.AsSpan(from, count));
        }
        if (clip.Count >= MinEmbedSamples) return clip.ToArray();
        return audio.AsSpan(start, Math.Min(end - start, MaxEmbedSamples)).ToArray();
    }
}
