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
    public void Merges_consecutive_same_channel_while_speech_continues()
    {
        var result = TranscriptMerger.Merge([Ben(0, 1000, "Merhaba"), Ben(1500, 4000, "nasılsın?")], []);

        Assert.Equal([Ben(0, 4000, "Merhaba nasılsın?")], result);
    }

    [Fact]
    public void Starts_new_segment_after_a_finished_sentence_even_without_a_pause()
    {
        var result = TranscriptMerger.Merge(
            [Karsi(0, 2000, "Adım adım"), Karsi(2000, 3000, "gideceğiz."), Karsi(3000, 5000, "Peki ne olacak?"), Karsi(5000, 6000, "Bilmiyorum")], []);

        Assert.Equal(["Adım adım gideceğiz.", "Peki ne olacak?", "Bilmiyorum"], result.Select(s => s.Text));
    }

    [Fact]
    public void Starts_new_segment_after_a_pause()
    {
        var result = TranscriptMerger.Merge([Ben(0, 1000, "a"), Ben(1600, 4000, "b")], []);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Splits_uninterrupted_speech_longer_than_twenty_seconds()
    {
        var result = TranscriptMerger.Merge(
            [Karsi(0, 12000, "bir"), Karsi(12000, 19000, "iki"), Karsi(19000, 26000, "üç")], []);

        Assert.Equal([Karsi(0, 19000, "bir iki"), Karsi(19000, 26000, "üç")], result);
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
        var result = TranscriptMerger.Merge([Karsi(0, 5000, "bir"), Karsi(1000, 2000, "iki"), Karsi(5500, 7000, "üç")], []);

        Assert.Equal([Karsi(0, 7000, "bir iki üç")], result);
    }

    [Fact]
    public void Trims_text_and_drops_empty_segments()
    {
        var result = TranscriptMerger.Merge([Ben(0, 1000, "  a  "), Ben(1500, 1600, "   ")], [Karsi(5000, 6000, "")]);

        Assert.Equal([Ben(0, 1000, "a")], result);
    }
}
