using Notlar.Core;

namespace Notlar.Core.Tests;

public class SpeechSegmenterTests
{
    private const int Sr = 16000;
    private const int Tolerance = Sr / 10; // ±100 ms
    private const int Pad = Sr / 5;        // 200 ms

    /// <summary>Sentetik ses: (saniye, sinüs mü) parçalarını art arda ekler. Sinüs 220 Hz, genlik 0.3.</summary>
    private static float[] Build(params (double Seconds, bool Tone)[] parts)
    {
        var samples = new List<float>();
        foreach (var (seconds, tone) in parts)
        {
            var n = (int)Math.Round(seconds * Sr);
            for (var i = 0; i < n; i++)
                samples.Add(tone ? (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / Sr)) : 0f);
        }
        return samples.ToArray();
    }

    private static int S(double seconds) => (int)Math.Round(seconds * Sr);

    private static void AssertNear(int expected, int actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Empty(SpeechSegmenter.Detect([]));
    }

    [Fact]
    public void Fully_silent_input_returns_empty()
    {
        Assert.Empty(SpeechSegmenter.Detect(new float[S(10)]));
    }

    [Fact]
    public void Quiet_noise_below_minimum_threshold_returns_empty()
    {
        var rnd = new Random(42);
        var samples = Enumerable.Range(0, S(5)).Select(_ => (float)((rnd.NextDouble() - 0.5) * 0.004)).ToArray();

        Assert.Empty(SpeechSegmenter.Detect(samples));
    }

    [Fact]
    public void Quiet_speech_at_two_and_a_half_times_the_noise_floor_is_detected()
    {
        // Kısık mikrofon: gürültü RMS ≈ 0.0076, konuşma RMS ≈ 0.019 (gerçek bir kayıttan ölçülen düzey).
        // Eşik gürültü tabanının 3 katı olunca (≈0.023) bu konuşma tamamen kırpılıyordu.
        var rnd = new Random(7);
        var noiseAmp = 0.0076f * MathF.Sqrt(3) * 2; // düzgün dağılım (-a/2..a/2) RMS'i = a/√12
        var samples = new float[S(6)];
        for (var i = 0; i < samples.Length; i++)
        {
            var noise = (float)((rnd.NextDouble() - 0.5) * noiseAmp);
            var speech = i >= S(2) && i < S(4) ? (float)(0.019 * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 220 * i / Sr)) : 0f;
            samples[i] = noise + speech;
        }

        var regions = SpeechSegmenter.Detect(samples);

        var (start, end) = Assert.Single(regions);
        AssertNear(S(2) - Pad, start);
        AssertNear(S(4) + Pad, end);
    }

    [Fact]
    public void Single_tone_region_is_detected_with_padding()
    {
        var samples = Build((2, false), (3, true), (2, false));

        var regions = SpeechSegmenter.Detect(samples);

        var (start, end) = Assert.Single(regions);
        AssertNear(S(2) - Pad, start);
        AssertNear(S(5) + Pad, end);
    }

    [Fact]
    public void Padding_is_clamped_to_input_bounds()
    {
        var samples = Build((2, true), (3, false));

        var (start, end) = Assert.Single(SpeechSegmenter.Detect(samples));

        Assert.Equal(0, start);
        AssertNear(S(2) + Pad, end);
        Assert.True(end <= samples.Length);
    }

    [Fact]
    public void Gaps_shorter_than_600ms_are_merged()
    {
        var samples = Build((2, false), (1, true), (0.4, false), (1, true), (2, false));

        var (start, end) = Assert.Single(SpeechSegmenter.Detect(samples));

        AssertNear(S(2) - Pad, start);
        AssertNear(S(4.4) + Pad, end);
    }

    [Fact]
    public void Gaps_of_one_second_split_regions()
    {
        var samples = Build((2, false), (1, true), (1, false), (1, true), (2, false));

        var regions = SpeechSegmenter.Detect(samples);

        Assert.Equal(2, regions.Count);
        AssertNear(S(2) - Pad, regions[0].Start);
        AssertNear(S(3) + Pad, regions[0].End);
        AssertNear(S(4) - Pad, regions[1].Start);
        AssertNear(S(5) + Pad, regions[1].End);
    }

    [Fact]
    public void Short_bursts_are_dropped()
    {
        var samples = Build((2, false), (0.1, true), (2, false), (1, true), (2, false));

        var (start, _) = Assert.Single(SpeechSegmenter.Detect(samples));

        AssertNear(S(4.1) - Pad, start);
    }

    [Fact]
    public void Long_regions_are_split_into_28_second_chunks()
    {
        var samples = Build((12, false), (60, true), (12, false));

        var regions = SpeechSegmenter.Detect(samples);

        Assert.Equal(3, regions.Count);
        Assert.All(regions, r => Assert.True(r.End - r.Start <= S(28)));
        Assert.Equal(S(28), regions[0].End - regions[0].Start);
        AssertNear(S(12) - Pad, regions[0].Start);
        Assert.Equal(regions[0].End, regions[1].Start);
        Assert.Equal(regions[1].End, regions[2].Start);
        AssertNear(S(72) + Pad, regions[2].End);
    }

    [Fact]
    public void Respects_custom_sample_rate()
    {
        const int sr = 8000;
        var samples = new float[sr * 6];
        for (var i = sr * 2; i < sr * 4; i++)
            samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / sr));

        var (start, end) = Assert.Single(SpeechSegmenter.Detect(samples, sr));

        Assert.InRange(start, sr * 2 - sr / 5 - sr / 10, sr * 2 - sr / 5 + sr / 10);
        Assert.InRange(end, sr * 4 + sr / 5 - sr / 10, sr * 4 + sr / 5 + sr / 10);
    }

    [Fact]
    public void Invalid_sample_rate_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechSegmenter.Detect(new float[10], 0));
    }
}
