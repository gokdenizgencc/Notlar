using Notlar.Core;

namespace Notlar.Core.Tests;

public class SpeakerClusteringTests
{
    private const int Dim = 8;

    /// <summary>Ses "A", "B", "C"…: birbirine dik birim vektörler; <paramref name="wobble"/> aynı sesin satırdan
    /// satıra küçük değişimini taklit eder (benzerlik ≈ 0,98).</summary>
    private static float[] Voice(int voice, int wobble = 0)
    {
        var v = new float[Dim];
        v[voice] = 1f;
        if (wobble != 0) v[(voice + wobble) % Dim] += 0.2f;
        return v;
    }

    /// <summary>İki sesin karışımı: <paramref name="a"/> sesine kosinüs benzerliği tam <paramref name="similarity"/>.</summary>
    private static float[] Blend(int a, int b, double similarity)
    {
        var v = new float[Dim];
        v[a] = (float)similarity;
        v[b] = (float)Math.Sqrt(1 - similarity * similarity);
        return v;
    }

    private static VoiceSample Mic(long start, long end, float[]? e) => new(start, end, Speaker.Ben, e);
    private static VoiceSample Loop(long start, long end, float[]? e) => new(start, end, Speaker.KarsiTaraf, e);

    private static int?[] Assign(params VoiceSample[] samples) => SpeakerClustering.Assign(samples).ToArray();

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Empty(SpeakerClustering.Assign([]));
    }

    [Fact]
    public void Without_any_embedding_all_lines_stay_unlabelled()
    {
        var result = Assign(Mic(0, 5000, null), Loop(6000, 9000, null));

        Assert.Equal([null, null], result);
    }

    [Fact]
    public void Same_voice_gets_same_person_across_lines()
    {
        var result = Assign(
            Loop(0, 5000, Voice(0)),
            Loop(6000, 11000, Voice(1)),
            Loop(12000, 17000, Voice(0, wobble: 2)),
            Loop(18000, 23000, Voice(1, wobble: 3)));

        Assert.Equal([1, 2, 1, 2], result);
    }

    [Fact]
    public void Single_voice_is_one_person()
    {
        var result = Assign(Mic(0, 5000, Voice(0)), Mic(6000, 9000, Voice(0, 1)), Mic(10000, 15000, Voice(0, 2)));

        Assert.Equal([1, 1, 1], result);
    }

    [Fact]
    public void Persons_are_numbered_by_first_appearance()
    {
        var result = Assign(
            Loop(0, 5000, Voice(3)),
            Loop(6000, 11000, Voice(3, 1)),
            Loop(12000, 17000, Voice(1)),
            Loop(18000, 23000, Voice(5)),
            Loop(24000, 29000, Voice(1, 1)),
            Loop(30000, 35000, Voice(5, 1)));

        Assert.Equal([1, 1, 2, 3, 2, 3], result);
    }

    [Fact]
    public void Same_voice_on_both_channels_is_one_person()
    {
        // Hoparlörden mikrofona sızan ses: kanal farklı, ses aynı.
        var result = Assign(Loop(0, 5000, Voice(0)), Mic(100, 5100, Voice(0, 1)), Mic(6000, 11000, Voice(1)));

        Assert.Equal([1, 1, 2], result);
    }

    [Fact]
    public void Embedding_scale_does_not_matter()
    {
        var loud = Voice(0).Select(x => x * 250f).ToArray();
        var quiet = Voice(0, 1).Select(x => x * 0.01f).ToArray();

        var result = Assign(Loop(0, 5000, loud), Loop(6000, 11000, Voice(1)), Loop(12000, 17000, quiet));

        Assert.Equal([1, 2, 1], result);
    }

    [Fact]
    public void Clusters_merge_at_threshold_and_split_below_it()
    {
        VoiceSample[] samples = [Loop(0, 5000, Voice(0)), Loop(6000, 11000, Blend(0, 1, similarity: 0.6))];

        var merged = SpeakerClustering.Assign(samples, new SpeakerClusteringOptions { Threshold = 0.55, MinPersonMs = 0 });
        var split = SpeakerClustering.Assign(samples, new SpeakerClusteringOptions { Threshold = 0.65, MinPersonMs = 0 });

        Assert.Equal([1, 1], merged);
        Assert.Equal([1, 2], split);
    }

    [Fact]
    public void Average_linkage_keeps_two_groups_apart_when_one_line_resembles_both()
    {
        // Tek bir satır iki sese de eşiğin üstünde benziyor (0,75 ve 0,66): daha çok benzediği gruba katılır ama
        // iki grubu birbirine bağlamaz (grupların ortalama benzerliği eşiğin altında kalır).
        var bridge = Blend(0, 1, similarity: 0.75);
        var result = SpeakerClustering.Assign(
            [
                Loop(0, 5000, Voice(0)), Loop(6000, 11000, Voice(0, 2)), Loop(12000, 17000, Voice(0, 3)),
                Loop(18000, 23000, Voice(1)), Loop(24000, 29000, Voice(1, 2)), Loop(30000, 35000, bridge),
            ],
            new SpeakerClusteringOptions { Threshold = 0.5 });

        Assert.Equal([1, 1, 1, 2, 2, 1], result);
    }

    [Fact]
    public void Short_line_without_embedding_takes_nearest_line_on_same_channel()
    {
        var result = Assign(
            Mic(0, 5000, Voice(0)),
            Loop(5500, 10500, Voice(1)),
            Mic(10600, 11000, null), // karşı tarafın satırına daha yakın ama mikrofonda: mikrofondaki kişiye gider
            Loop(12000, 17000, Voice(1, 1)));

        Assert.Equal([1, 2, 1, 2], result);
    }

    [Fact]
    public void Short_line_without_embedding_uses_other_channel_when_own_channel_has_no_labelled_line()
    {
        var result = Assign(Loop(0, 5000, Voice(0)), Mic(5200, 5600, null), Loop(20000, 25000, Voice(1)));

        Assert.Equal([1, 1, 2], result);
    }

    [Fact]
    public void Short_line_with_matching_voice_goes_to_that_person_even_if_neighbour_differs()
    {
        var result = Assign(
            Loop(0, 5000, Voice(0)),
            Loop(6000, 11000, Voice(1)),
            Loop(11100, 11900, Voice(0, 2)), // kısa (0,8 sn) ama sesi 1. kişiye benziyor; komşusu 2. kişi
            Loop(12000, 17000, Voice(1, 1)));

        Assert.Equal([1, 2, 1, 2], result);
    }

    [Fact]
    public void Short_line_with_unfamiliar_voice_does_not_become_a_person_and_takes_neighbour()
    {
        var result = Assign(
            Loop(0, 5000, Voice(0)),
            Loop(6000, 11000, Voice(1)),
            Loop(11100, 11900, Voice(4)), // kısa satırın gömmesi güvenilmez: hiçbir kişiye benzemiyor
            Loop(30000, 35000, Voice(0, 1)));

        Assert.Equal([1, 2, 2, 1], result);
    }

    [Fact]
    public void Tie_between_neighbours_prefers_the_earlier_line()
    {
        var result = Assign(Loop(0, 5000, Voice(0)), Loop(5500, 5900, null), Loop(6400, 11400, Voice(1)));

        Assert.Equal([1, 1, 2], result);
    }

    [Fact]
    public void Outlier_with_little_speech_is_absorbed_instead_of_becoming_a_person()
    {
        // 2 sn'lik tek bir aykırı satır (üst üste konuşma, gürültü…): ayrı kişi sayılmaz, komşusuna gider.
        var result = Assign(
            Loop(0, 5000, Voice(0)),
            Loop(6000, 11000, Voice(1)),
            Loop(11500, 13500, Voice(4)),
            Loop(20000, 25000, Voice(0, 1)));

        Assert.Equal([1, 2, 2, 1], result);
    }

    [Fact]
    public void Voice_with_enough_total_speech_is_a_person_even_if_it_speaks_once()
    {
        var result = Assign(
            Loop(0, 5000, Voice(0)),
            Loop(6000, 11000, Voice(1)),
            Loop(11500, 17500, Voice(4)),
            Loop(20000, 25000, Voice(0, 1)));

        Assert.Equal([1, 2, 3, 1], result);
    }

    [Fact]
    public void Largest_cluster_survives_even_when_all_speech_is_brief()
    {
        var result = Assign(Loop(0, 2000, Voice(0)), Loop(3000, 5000, Voice(1)), Loop(6000, 7500, Voice(1, 1)));

        // Hiçbir küme 4 sn'yi bulmuyor: en çok konuşan (2. ses) kalır, diğeri ona katılır.
        Assert.Equal([1, 1, 1], result);
    }

    [Fact]
    public void Only_short_lines_are_still_clustered()
    {
        var result = SpeakerClustering.Assign(
            [Loop(0, 800, Voice(0)), Loop(1000, 1900, Voice(1)), Loop(2000, 2900, Voice(0, 1)), Loop(3000, 3900, Voice(1, 1))],
            new SpeakerClusteringOptions { MinPersonMs = 1000 });

        Assert.Equal([1, 2, 1, 2], result);
    }

    [Fact]
    public void Broken_embeddings_are_treated_as_missing()
    {
        var nan = Voice(2);
        nan[0] = float.NaN;

        var result = Assign(
            Loop(0, 5000, Voice(0)),
            Loop(6000, 11000, nan),
            Loop(12000, 17000, new float[Dim]), // sıfır vektör
            Loop(18000, 23000, [1f, 0f]),       // farklı boyut
            Loop(24000, 29000, Voice(1)));

        Assert.Equal([1, 1, 1, 2, 2], result);
    }

    [Fact]
    public void Many_lines_of_three_voices_are_grouped_exactly()
    {
        var samples = new List<VoiceSample>();
        var expected = new List<int?>();
        for (var i = 0; i < 300; i++)
        {
            var voice = i * 7 % 3; // 0, 1, 2, 0, 1, 2… (ilk görülme sırası 0, 1, 2)
            samples.Add(Loop(i * 4000L, i * 4000L + 3000, Voice(voice * 2, wobble: 1 + i % 5)));
            expected.Add(voice + 1);
        }

        Assert.Equal(expected, SpeakerClustering.Assign(samples));
    }

    [Fact]
    public void Cosine_is_one_for_same_direction_zero_for_orthogonal_or_invalid()
    {
        Assert.Equal(1.0, SpeakerClustering.Cosine([2f, 0f], [5f, 0f]), 6);
        Assert.Equal(0.0, SpeakerClustering.Cosine([1f, 0f], [0f, 1f]), 6);
        Assert.Equal(-1.0, SpeakerClustering.Cosine([1f, 0f], [-3f, 0f]), 6);
        Assert.Equal(0.0, SpeakerClustering.Cosine(null, [1f, 0f]));
        Assert.Equal(0.0, SpeakerClustering.Cosine([0f, 0f], [1f, 0f]));
        Assert.Equal(0.0, SpeakerClustering.Cosine([1f], [1f, 0f]));
    }
}
