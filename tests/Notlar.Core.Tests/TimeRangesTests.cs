using Notlar.Core;

namespace Notlar.Core.Tests;

public class TimeRangesTests
{
    [Fact]
    public void Normalize_sorts_merges_and_drops_invalid()
    {
        var result = TimeRanges.Normalize(
        [
            new(50_000, 60_000),
            new(-500, 2_000),       // negatif başlangıç 0'a kırpılır
            new(1_000, 3_000),      // çakışan → birleşir
            new(3_000, 4_000),      // bitişik → birleşir
            new(9_000, 9_000),      // boş
            new(20_000, 10_000),    // ters
        ]);

        TimeRange[] expected = [new(0, 4_000), new(50_000, 60_000)];
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_empty_is_empty() => Assert.Empty(TimeRanges.Normalize([]));

    [Fact]
    public void ClipRegions_without_ranges_returns_regions_unchanged()
    {
        (int, int)[] regions = [(0, 16_000), (32_000, 48_000)];
        Assert.Equal(regions, TimeRanges.ClipRegions(regions, [], 16_000));
    }

    [Fact]
    public void ClipRegions_keeps_only_intersections()
    {
        // 16 kHz: 1 sn = 16000 örnek. Bölgeler: 0–2 sn, 3–5 sn, 8–9 sn. Aralıklar: 1–4 sn ve 4,5–6 sn.
        (int, int)[] regions = [(0, 32_000), (48_000, 80_000), (128_000, 144_000)];
        TimeRange[] ranges = [new(1_000, 4_000), new(4_500, 6_000)];

        var clipped = TimeRanges.ClipRegions(regions, ranges, 16_000);

        (int, int)[] expected = [(16_000, 32_000), (48_000, 64_000), (72_000, 80_000)];
        Assert.Equal(expected, clipped);
    }
}
