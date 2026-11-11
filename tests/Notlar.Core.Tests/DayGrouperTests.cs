using Notlar.Core;

namespace Notlar.Core.Tests;

public class DayGrouperTests
{
    private static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    private static Note N(long id, int day, int hour, int minute, string text = "x", bool done = false) =>
        new(id, text, new DateTimeOffset(2026, 11, day, hour, minute, 0, Tr), null, done);

    [Fact]
    public void Group_orders_days_descending_and_notes_ascending()
    {
        var notes = new[] { N(1, 10, 9, 0), N(2, 11, 18, 40), N(3, 11, 9, 12), N(4, 10, 22, 5) };

        var groups = DayGrouper.Group(notes);

        Assert.Equal([new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 10)], groups.Select(g => g.Date));
        Assert.Equal([3L, 2L], groups[0].Notes.Select(n => n.Id));
        Assert.Equal([1L, 4L], groups[1].Notes.Select(n => n.Id));
    }

    [Fact]
    public void Group_splits_at_local_midnight()
    {
        var groups = DayGrouper.Group([N(1, 10, 23, 59), N(2, 11, 0, 1)]);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Group_of_empty_is_empty()
    {
        Assert.Empty(DayGrouper.Group([]));
    }

    [Fact]
    public void FormatDate_uses_turkish_names()
    {
        Assert.Equal("11 Kasım 2026, Çarşamba", DayGrouper.FormatDate(new DateOnly(2026, 11, 11)));
    }

    [Fact]
    public void Header_marks_today_and_yesterday()
    {
        var today = new DateOnly(2026, 11, 11);
        Assert.Equal("BUGÜN · 11 Kasım 2026, Çarşamba", DayGrouper.Header(today, today));
        Assert.Equal("DÜN · 10 Kasım 2026, Salı", DayGrouper.Header(new DateOnly(2026, 11, 10), today));
        Assert.Equal("9 Kasım 2026, Pazartesi", DayGrouper.Header(new DateOnly(2026, 11, 9), today));
    }
}
