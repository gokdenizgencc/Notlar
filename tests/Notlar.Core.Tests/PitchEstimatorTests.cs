using Notlar.Core;

namespace Notlar.Core.Tests;

public class PitchEstimatorTests
{
    private const int Rate = 16000;

    /// <summary>Verilen harmoniklerin toplamı (1 = temel frekans), genlik 0,3'e ölçekli.</summary>
    private static float[] Harmonics(double f0, IEnumerable<int> harmonics, double seconds = 1.0, int rate = Rate)
    {
        var list = harmonics.ToList();
        var audio = new float[(int)(seconds * rate)];
        for (var i = 0; i < audio.Length; i++)
        {
            double sum = 0;
            foreach (var h in list)
                sum += Math.Sin(2 * Math.PI * f0 * h * i / rate + h) / h; // harmonik başına farklı faz
            audio[i] = (float)sum;
        }
        var peak = audio.Max(Math.Abs);
        for (var i = 0; i < audio.Length; i++) audio[i] = (float)(audio[i] / peak * 0.3);
        return audio;
    }

    /// <summary>Telefon bandı: yalnızca 300–3400 Hz arasındaki harmonikler (temel frekans yok).</summary>
    private static IEnumerable<int> TelephoneBand(double f0) =>
        Enumerable.Range(1, 40).Where(h => f0 * h >= 300 && f0 * h <= 3400);

    private static double MedianOf(float[] audio, int rate = Rate)
    {
        var track = PitchEstimator.Track(audio, rate);
        Assert.NotEmpty(track);
        return PitchEstimator.Median(track)!.Value;
    }

    [Theory]
    [InlineData(85)]
    [InlineData(120)]
    [InlineData(150)]
    [InlineData(210)]
    [InlineData(300)]
    public void Pure_sine_gives_its_frequency(double f0)
    {
        var median = MedianOf(Harmonics(f0, [1]));
        Assert.InRange(median, f0 * 0.98, f0 * 1.02);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(130)]
    [InlineData(220)]
    public void Harmonic_series_gives_fundamental(double f0)
    {
        var median = MedianOf(Harmonics(f0, Enumerable.Range(1, 12)));
        Assert.InRange(median, f0 * 0.98, f0 * 1.02);
    }

    [Theory]
    [InlineData(95)]
    [InlineData(110)]
    [InlineData(140)]
    [InlineData(200)]
    [InlineData(240)]
    public void Missing_fundamental_in_telephone_band_still_gives_fundamental(double f0)
    {
        // Teams/telefon sesi: 300 Hz altı kesilmiş; erkek sesinde temel frekans ve 2. harmonik yok.
        var bands = TelephoneBand(f0).ToList();
        Assert.DoesNotContain(1, bands);
        var median = MedianOf(Harmonics(f0, bands));
        Assert.InRange(median, f0 * 0.98, f0 * 1.02);
    }

    [Fact]
    public void Odd_harmonics_only_do_not_halve_or_double()
    {
        // 3., 5., 7. harmonikler: periyot yine T0; en güçlü bileşen 3f0 olsa da F0 bulunmalı.
        var median = MedianOf(Harmonics(120, [3, 5, 7, 9]));
        Assert.InRange(median, 117.6, 122.4);
    }

    [Fact]
    public void Works_at_8_khz_too()
    {
        var median = MedianOf(Harmonics(180, Enumerable.Range(2, 10), rate: 8000), rate: 8000);
        Assert.InRange(median, 176, 184);
    }

    [Fact]
    public void Silence_and_noise_are_unvoiced()
    {
        Assert.Empty(PitchEstimator.Track(new float[Rate], Rate));

        var random = new Random(42);
        var noise = new float[Rate];
        for (var i = 0; i < noise.Length; i++) noise[i] = (float)(random.NextDouble() * 0.6 - 0.3);
        var voiced = PitchEstimator.Track(noise, Rate);
        Assert.True(voiced.Count < 5, $"gürültüde {voiced.Count} sesli pencere bulundu");
    }

    [Fact]
    public void Very_quiet_signal_is_ignored()
    {
        var audio = Harmonics(120, Enumerable.Range(1, 8));
        for (var i = 0; i < audio.Length; i++) audio[i] *= 0.005f; // tepe 0,0015 ≈ −56 dBFS
        Assert.Empty(PitchEstimator.Track(audio, Rate));
    }

    [Fact]
    public void Voiced_frames_are_counted_per_hop()
    {
        var track = PitchEstimator.Track(Harmonics(150, Enumerable.Range(1, 6), seconds: 1.0), Rate);
        // 1 sn ≈ 100 pencere (10 ms adım), pencere uzunluğu kadar eksik.
        Assert.InRange(track.Count, 90, 100);
    }

    [Fact]
    public void Too_short_frame_returns_null()
    {
        Assert.Null(PitchEstimator.EstimateFrame(new float[100], Rate));
    }

    [Fact]
    public void Median_handles_empty_odd_and_even()
    {
        Assert.Null(PitchEstimator.Median([]));
        Assert.Equal(2, PitchEstimator.Median([3, 1, 2]));
        Assert.Equal(2.5, PitchEstimator.Median([4, 1, 2, 3]));
    }
}
