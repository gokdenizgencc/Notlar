using Notlar.Core;

namespace Notlar.Core.Tests;

public class TranscriptPersonFilterTests
{
    private static readonly TranscriptSegment[] Segments =
    [
        new(1_000, 2_000, Speaker.Ben, "Merhaba", 1, "Ahmet", PersonNameSource.Kullanici),
        new(3_000, 4_000, Speaker.KarsiTaraf, "Selam", 2, "Ayşe", PersonNameSource.Tahmin),
        new(5_000, 6_000, Speaker.KarsiTaraf, "Nasılsın", 2, "Ayşe", PersonNameSource.Tahmin),
        new(7_000, 8_000, Speaker.Ben, "İyiyim", 1, "Ahmet", PersonNameSource.Kullanici),
        new(9_000, 10_000, Speaker.KarsiTaraf, "Ben de buradayım", 3, null),
        new(11_000, 12_000, Speaker.KarsiTaraf, "Etiketsiz satır"),
        new(13_000, 14_000, Speaker.KarsiTaraf, "Devam", 2, "Ayşe", PersonNameSource.Tahmin),
    ];

    [Fact]
    public void Count_orders_by_lines_then_first_appearance_and_skips_unlabelled_lines()
    {
        var persons = TranscriptPersonFilter.Count(Segments);

        Assert.Equal([2, 1, 3], persons.Select(p => p.Person));
        Assert.Equal([3, 2, 1], persons.Select(p => p.Lines));
    }

    [Fact]
    public void Count_ties_keep_first_appearance_order()
    {
        var persons = TranscriptPersonFilter.Count(
        [
            new(0, 1, Speaker.Ben, "a", 2, "Ayşe"),
            new(1, 2, Speaker.Ben, "b", 1, "Ahmet"),
        ]);

        Assert.Equal([2, 1], persons.Select(p => p.Person));
    }

    [Fact]
    public void Count_marks_guessed_names_with_tilde_but_not_numbered_or_user_names()
    {
        var persons = TranscriptPersonFilter.Count(Segments).ToDictionary(p => p.Person);

        Assert.Equal("~Ayşe", persons[2].Label);
        Assert.True(persons[2].IsGuess);
        Assert.Equal("Ahmet", persons[1].Label);
        Assert.Equal(PersonNameSource.Kullanici, persons[1].Source);
        Assert.Equal("Kişi 3", persons[3].Label);
        Assert.True(persons[3].Numbered);
        Assert.False(persons[3].IsGuess);
    }

    [Fact]
    public void Count_is_empty_for_old_transcripts_without_persons()
    {
        Assert.Empty(TranscriptPersonFilter.Count([new(0, 1, Speaker.Ben, "Eski kayıt")]));
        Assert.Empty(TranscriptPersonFilter.Count([]));
    }

    [Fact]
    public void Apply_without_selection_returns_all_lines()
    {
        Assert.Same(Segments, TranscriptPersonFilter.Apply(Segments, []));
        Assert.Same(Segments, TranscriptPersonFilter.Apply(Segments, null));
    }

    [Fact]
    public void Apply_keeps_only_selected_persons_in_order_and_hides_unlabelled_lines()
    {
        var single = TranscriptPersonFilter.Apply(Segments, [1]);
        Assert.Equal(["Merhaba", "İyiyim"], single.Select(s => s.Text));

        var multi = TranscriptPersonFilter.Apply(Segments, [3, 1]);
        Assert.Equal(["Merhaba", "İyiyim", "Ben de buradayım"], multi.Select(s => s.Text));
    }

    [Fact]
    public void Toggle_adds_and_removes_person()
    {
        IReadOnlyList<int> selected = [];
        selected = TranscriptPersonFilter.Toggle(selected, 2);
        selected = TranscriptPersonFilter.Toggle(selected, 1);
        Assert.Equal([2, 1], selected);

        selected = TranscriptPersonFilter.Toggle(selected, 2);
        Assert.Equal([1], selected);
    }

    [Fact]
    public void Prune_drops_persons_missing_from_transcript()
    {
        var persons = TranscriptPersonFilter.Count(Segments);
        Assert.Equal([1, 3], TranscriptPersonFilter.Prune([1, 7, 3, 1], persons));
    }

    [Fact]
    public void Describe_is_null_when_filter_is_off()
    {
        var persons = TranscriptPersonFilter.Count(Segments);
        Assert.Null(TranscriptPersonFilter.Describe(persons, []));
        Assert.Null(TranscriptPersonFilter.Describe(persons, null));
        Assert.Null(TranscriptPersonFilter.Describe(persons, [99]));
    }

    [Fact]
    public void Describe_single_and_multiple_persons()
    {
        var persons = TranscriptPersonFilter.Count(Segments);

        Assert.Equal("Ahmet — 2 satır gösteriliyor", TranscriptPersonFilter.Describe(persons, [1]));
        Assert.Equal("~Ayşe, Ahmet — 5 satır gösteriliyor", TranscriptPersonFilter.Describe(persons, [1, 2]));
        Assert.Equal("~Ayşe, Ahmet — 5 satır", TranscriptPersonFilter.Summarize(persons, [1, 2]));
    }

    [Fact]
    public void Describe_shortens_long_name_lists()
    {
        var segments = Enumerable.Range(1, 5)
            .Select(i => new TranscriptSegment(i, i + 1, Speaker.Ben, "x", i, "Ad" + i, PersonNameSource.Kullanici))
            .ToList();
        var persons = TranscriptPersonFilter.Count(segments);

        Assert.Equal("Ad1, Ad2, Ad3 +2 kişi — 5 satır gösteriliyor",
            TranscriptPersonFilter.Describe(persons, [1, 2, 3, 4, 5]));
    }

    private static IReadOnlyList<TranscriptPersonCount> ManyPersons(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new TranscriptPersonCount(i, "Ad" + i, PersonNameSource.Kullanici, false, 100 - i))
            .ToList();

    [Fact]
    public void Chips_shows_all_when_few_or_expanded()
    {
        var persons = ManyPersons(7);
        Assert.Equal((7, 0), Count(TranscriptPersonFilter.Chips(persons, [], maxVisible: 6, expanded: false)));

        var many = ManyPersons(12);
        Assert.Equal((12, 0), Count(TranscriptPersonFilter.Chips(many, [], maxVisible: 6, expanded: true)));
    }

    [Fact]
    public void Chips_collapses_to_limit_and_keeps_selected_hidden_persons_visible()
    {
        var persons = ManyPersons(12);

        var (visible, hidden) = TranscriptPersonFilter.Chips(persons, [], maxVisible: 6, expanded: false);
        Assert.Equal([1, 2, 3, 4, 5, 6], visible.Select(p => p.Person));
        Assert.Equal(6, hidden);

        (visible, hidden) = TranscriptPersonFilter.Chips(persons, [10], maxVisible: 6, expanded: false);
        Assert.Equal([1, 2, 3, 4, 5, 6, 10], visible.Select(p => p.Person));
        Assert.Equal(5, hidden);
    }

    private static (int Visible, int Hidden) Count((IReadOnlyList<TranscriptPersonCount> Visible, int Hidden) chips) =>
        (chips.Visible.Count, chips.Hidden);
}
