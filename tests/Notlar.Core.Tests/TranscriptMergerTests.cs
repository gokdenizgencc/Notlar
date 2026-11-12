using Notlar.Core;

namespace Notlar.Core.Tests;

public class TranscriptMergerTests
{
    private static TranscriptSegment Ben(long s, long e, string t) => new(s, e, Speaker.Ben, t);
    private static TranscriptSegment Karsi(long s, long e, string t) => new(s, e, Speaker.KarsiTaraf, t);

    [Fact]
    public void Empty_inputs_return_empty()
    {
        Assert.Empty(TranscriptMerger.Merge([], []));
    }

    [Fact]
    public void Interleaves_channels_by_start_time()
    {
        var result = TranscriptMerger.Merge(
            [Ben(0, 1000, "a"), Ben(10000, 11000, "c")],
            [Karsi(5000, 6000, "b")]);

        Assert.Equal([Ben(0, 1000, "a"), Karsi(5000, 6000, "b"), Ben(10000, 11000, "c")], result);
    }

    [Fact]
    public void Equal_start_puts_Ben_first_regardless_of_argument_order()
    {
        var result = TranscriptMerger.Merge([Karsi(1000, 2000, "karşı")], [Ben(1000, 2000, "ben")]);

        Assert.Equal([Ben(1000, 2000, "ben"), Karsi(1000, 2000, "karşı")], result);
    }

    [Fact]
    public void Merges_consecutive_same_speaker_with_gap_under_two_seconds()
    {
        var result = TranscriptMerger.Merge([Ben(0, 1000, "Merhaba"), Ben(2500, 4000, "nasılsın?")], []);

        Assert.Equal([Ben(0, 4000, "Merhaba nasılsın?")], result);
    }

    [Fact]
    public void Does_not_merge_when_gap_is_two_seconds_or_more()
    {
        var result = TranscriptMerger.Merge([Ben(0, 1000, "a"), Ben(3000, 4000, "b")], []);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Does_not_merge_across_other_speaker()
    {
        var result = TranscriptMerger.Merge(
            [Ben(0, 1000, "a"), Ben(1500, 2000, "c")],
            [Karsi(1200, 1400, "b")]);

        Assert.Equal(["a", "b", "c"], result.Select(s => s.Text));
    }

    [Fact]
    public void Merges_chains_and_keeps_latest_end()
    {
        var result = TranscriptMerger.Merge([Karsi(0, 5000, "bir"), Karsi(1000, 2000, "iki"), Karsi(6000, 7000, "üç")], []);

        Assert.Equal([Karsi(0, 7000, "bir iki üç")], result);
    }

    [Fact]
    public void Trims_text_and_drops_empty_segments()
    {
        var result = TranscriptMerger.Merge([Ben(0, 1000, "  a  "), Ben(1500, 1600, "   ")], [Karsi(5000, 6000, "")]);

        Assert.Equal([Ben(0, 1000, "a")], result);
    }
}
