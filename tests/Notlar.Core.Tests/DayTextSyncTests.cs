namespace Notlar.Core.Tests;

public sealed class DayTextSyncTests
{
    private static readonly TimeSpan Tr = TimeSpan.FromHours(3);
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateOnly Today = new(2026, 11, 11);
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 14, 32, 0, Tr);

    private static Note N(long id, string text, int hour, int minute, bool done = false, string author = NoteAuthor.Ben, DateOnly? day = null)
    {
        var d = day ?? Day;
        return new(id, text, new DateTimeOffset(d.Year, d.Month, d.Day, hour, minute, 0, Tr), null, done, author);
    }

    private static DateTimeOffset At(DateOnly d, int hour, int minute, int second = 0) =>
        new(d.Year, d.Month, d.Day, hour, minute, second, Tr);

    // --- Metin ↔ satır ---

    [Fact]
    public void SplitLines_handles_crlf_and_lf_and_empty_text()
    {
        Assert.Empty(DayTextSync.SplitLines(""));
        Assert.Equal(["a", "b", "", "c"], DayTextSync.SplitLines("a\r\nb\n\r\nc"));
    }

    [Fact]
    public void ToText_joins_note_texts_with_newlines()
    {
        Assert.Equal("a\nb", DayTextSync.ToText([N(1, "a", 9, 0), N(2, "b", 10, 0)]));
    }

    // --- Kirli (kaydedilmemiş) algısı ---

    [Fact]
    public void HasChanges_ignores_blank_lines_and_surrounding_whitespace()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "b #iş", 10, 0) };

        Assert.False(DayTextSync.HasChanges(notes, "a\r\n\r\n  b #iş  \r\n"));
        Assert.False(DayTextSync.HasChanges([], "\r\n  \r\n"));
    }

    [Fact]
    public void HasChanges_detects_edits_inserts_deletes_and_reorders()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "b", 10, 0) };

        Assert.True(DayTextSync.HasChanges(notes, "a\nbb"));
        Assert.True(DayTextSync.HasChanges(notes, "a\nb\nc"));
        Assert.True(DayTextSync.HasChanges(notes, "a"));
        Assert.True(DayTextSync.HasChanges(notes, "b\na"));
    }

    // --- Kaydetme planı ---

    [Fact]
    public void Plan_is_empty_when_nothing_changed()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "b", 10, 0) };

        Assert.Empty(DayTextSync.Plan(notes, "a\n\nb\n", Day, Now));
    }

    [Fact]
    public void Plan_updates_edited_line_and_keeps_note_identity()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "b", 10, 0, done: true, author: NoteAuthor.Claude) };

        var plan = DayTextSync.Plan(notes, "a\n  b düzeltildi  ", Day, Now);

        var edit = Assert.Single(plan);
        Assert.Equal(DayEditKind.Update, edit.Kind);
        Assert.Equal(2, edit.NoteId);
        Assert.Equal("b düzeltildi", edit.Text);
    }

    [Fact]
    public void Plan_deletes_removed_line()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "b", 10, 0), N(3, "c", 11, 0) };

        var edit = Assert.Single(DayTextSync.Plan(notes, "a\nc", Day, Now));

        Assert.Equal(DayEditKind.Delete, edit.Kind);
        Assert.Equal(2, edit.NoteId);
    }

    [Fact]
    public void Plan_splitting_a_line_updates_first_part_and_inserts_second_between_neighbours()
    {
        var notes = new[] { N(1, "alış veriş", 9, 0), N(2, "z", 12, 0) };

        var plan = DayTextSync.Plan(notes, "alış\nveriş\nz", Day, Now);

        Assert.Equal(2, plan.Count);
        Assert.Equal(new DayEdit(DayEditKind.Update, 1, "alış", default), plan[0]);
        Assert.Equal(DayEditKind.Insert, plan[1].Kind);
        Assert.Equal("veriş", plan[1].Text);
        Assert.Equal(At(Day, 9, 1), plan[1].CreatedAt);
    }

    [Fact]
    public void Plan_inserts_several_lines_in_order_between_neighbours()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "z", 9, 2) };

        var plan = DayTextSync.Plan(notes, "a\nx\ny\nz\n", Day, Now);

        Assert.Equal(["x", "y"], plan.Select(p => p.Text));
        Assert.All(plan, p => Assert.Equal(DayEditKind.Insert, p.Kind));
        // 09:00 ile 09:02 arasına sığmayan dakikalar yerine aralık eşit bölünür; sıra korunur.
        Assert.True(plan[0].CreatedAt > notes[0].CreatedAt);
        Assert.True(plan[1].CreatedAt > plan[0].CreatedAt);
        Assert.True(plan[1].CreatedAt < notes[1].CreatedAt);
    }

    [Fact]
    public void Plan_insert_times_fit_between_notes_one_minute_apart()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "z", 9, 1) };

        var plan = DayTextSync.Plan(notes, "a\nx\ny\nz", Day, Now);

        Assert.True(plan[0].CreatedAt > notes[0].CreatedAt);
        Assert.True(plan[1].CreatedAt > plan[0].CreatedAt);
        Assert.True(plan[1].CreatedAt < notes[1].CreatedAt);
    }

    [Fact]
    public void Plan_insert_at_end_of_today_uses_now()
    {
        var notes = new[] { N(1, "a", 9, 0, day: Today) };

        var edit = Assert.Single(DayTextSync.Plan(notes, "a\nyeni", Today, Now));

        Assert.Equal(Now, edit.CreatedAt);
    }

    [Fact]
    public void Plan_insert_at_end_of_past_day_follows_last_note_and_stays_in_day()
    {
        var notes = new[] { N(1, "a", 15, 0) };
        var plan = DayTextSync.Plan(notes, "a\nx\ny", Day, Now);
        Assert.Equal([At(Day, 15, 1), At(Day, 15, 2)], plan.Select(p => p.CreatedAt));

        var late = new[] { N(1, "a", 23, 59) };
        var latePlan = DayTextSync.Plan(late, "a\nx\ny", Day, Now);
        Assert.All(latePlan, p => Assert.Equal(Day, DateOnly.FromDateTime(p.CreatedAt.DateTime)));
        Assert.True(latePlan[0].CreatedAt > late[0].CreatedAt);
        Assert.True(latePlan[1].CreatedAt > latePlan[0].CreatedAt);
    }

    [Fact]
    public void Plan_insert_at_top_goes_before_first_note_within_the_day()
    {
        var notes = new[] { N(1, "a", 9, 0) };
        var plan = DayTextSync.Plan(notes, "x\ny\na", Day, Now);
        Assert.Equal([At(Day, 8, 58), At(Day, 8, 59)], plan.Select(p => p.CreatedAt));

        var early = new[] { N(1, "a", 0, 0) };
        var earlyPlan = DayTextSync.Plan(early, "x\ny\na", Day, Now);
        Assert.All(earlyPlan, p => Assert.Equal(Day, DateOnly.FromDateTime(p.CreatedAt.DateTime)));
        Assert.True(earlyPlan[0].CreatedAt <= earlyPlan[1].CreatedAt);
        Assert.True(earlyPlan[1].CreatedAt <= early[0].CreatedAt);
    }

    [Fact]
    public void Plan_on_empty_day_uses_now_for_today_and_current_time_of_day_for_other_days()
    {
        var today = DayTextSync.Plan([], "x\ny", Today, Now);
        Assert.Equal(Now, today[0].CreatedAt);
        Assert.True(today[1].CreatedAt > today[0].CreatedAt);

        var past = DayTextSync.Plan([], "x", Day, Now);
        Assert.Equal(At(Day, 14, 32), Assert.Single(past).CreatedAt);
    }

    [Fact]
    public void Plan_result_order_matches_editor_order_after_mixed_edits()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "b", 10, 0), N(3, "c", 11, 0), N(4, "d", 12, 0) };

        var plan = DayTextSync.Plan(notes, "baş\na\nb2\nyeni\nc\nson", Day, Now);

        // Kaydet → tekrar oku simülasyonu: güncellemeler/silmeler uygulanır, eklemeler yeni id alır.
        var byId = notes.ToDictionary(n => n.Id);
        var nextId = 100L;
        foreach (var e in plan)
        {
            switch (e.Kind)
            {
                case DayEditKind.Update: byId[e.NoteId] = byId[e.NoteId] with { Text = e.Text }; break;
                case DayEditKind.Delete: byId.Remove(e.NoteId); break;
                case DayEditKind.Insert: byId[nextId] = new Note(nextId++, e.Text, e.CreatedAt, null, false); break;
            }
        }
        var reloaded = byId.Values.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).Select(n => n.Text);

        Assert.Equal(["baş", "a", "b2", "yeni", "c", "son"], reloaded);
        Assert.Contains(plan, e => e is { Kind: DayEditKind.Update, NoteId: 2, Text: "b2" });
        Assert.Contains(plan, e => e is { Kind: DayEditKind.Update, NoteId: 4, Text: "son" });
    }

    // --- İmleç / satır eşleme ---

    [Fact]
    public void LineOfNote_finds_note_line_in_edited_text()
    {
        var notes = new[] { N(1, "a", 9, 0), N(2, "b", 10, 0) };

        Assert.Equal(0, DayTextSync.LineOfNote(notes, "a\nb", 1));
        Assert.Equal(3, DayTextSync.LineOfNote(notes, "yeni\na\n\nb", 2));
        Assert.Equal(-1, DayTextSync.LineOfNote(notes, "a\nb", 99));
    }

    [Fact]
    public void MapLine_follows_the_same_note_when_lines_are_added_above()
    {
        Assert.Equal(2, DayTextSync.MapLine("a\nb\nc", "yeni\na\nb\nc", 1));
        Assert.Equal(0, DayTextSync.MapLine("a\nb", "b", 1));
    }

    [Fact]
    public void MapLine_for_removed_or_blank_line_stays_near_previous_line_and_in_range()
    {
        Assert.Equal(1, DayTextSync.MapLine("a\nb\nc", "a\nc", 1));
        Assert.Equal(2, DayTextSync.MapLine("a\n\nc", "x\na\nc", 1));
        Assert.Equal(0, DayTextSync.MapLine("a\nb", "", 1));
        Assert.Equal(1, DayTextSync.MapLine("a\nb\nc\nd", "a\nb", 3));
    }

    [Fact]
    public void Caret_index_and_line_column_round_trip_with_mixed_line_endings()
    {
        const string text = "ab\r\ncde\nf";

        Assert.Equal((1, 2), DayTextSync.LineColumnOf(text, 6));
        Assert.Equal(6, DayTextSync.IndexOf(text, 1, 2));
        Assert.Equal(7, DayTextSync.IndexOf(text, 1, 99));   // satır sonuna sıkıştırılır
        Assert.Equal(8, DayTextSync.IndexOf(text, 99, 0));   // son satıra sıkıştırılır
        Assert.Equal(9, DayTextSync.IndexOf(text, 2, 1));
        Assert.Equal((2, 1), DayTextSync.LineColumnOf(text, 9));
        Assert.Equal(0, DayTextSync.IndexOf("", 3, 3));
    }
}
