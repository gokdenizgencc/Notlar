using Notlar.Core;

namespace Notlar.Core.Tests;

public class PanelQueryTests
{
    private static readonly TimeSpan Tr = StoreFixture.Tr;

    [Fact]
    public void ListRecentAndOpen_includes_recent_and_open_excludes_old_done_and_deleted_ordered_by_id()
    {
        using var f = new StoreFixture();
        var since = new DateOnly(2026, 10, 1);

        f.Time.Set(new DateTimeOffset(2026, 1, 1, 9, 0, 0, Tr));
        var deletedOldOpen = f.Store.Add("silinmiş eski açık");
        f.Store.Delete(deletedOldOpen.Id);

        f.Time.Set(new DateTimeOffset(2026, 10, 1, 9, 0, 0, Tr));
        var doneOnSince = f.Store.Add("sınır günü tamam");
        f.Store.SetDone(doneOnSince.Id, true);

        f.Time.Set(new DateTimeOffset(2026, 8, 2, 9, 0, 0, Tr));
        var openOld = f.Store.Add("60 gün önce açık");

        f.Time.Set(new DateTimeOffset(2026, 9, 30, 9, 0, 0, Tr));
        var doneDayBefore = f.Store.Add("sınırdan bir gün önce tamam");
        f.Store.SetDone(doneDayBefore.Id, true);

        var result = f.Store.ListRecentAndOpen(since);

        Assert.Equal([doneOnSince.Id, openOld.Id], result.Select(n => n.Id));
    }

    [Fact]
    public void HasDoneBefore_false_when_store_empty()
    {
        using var f = new StoreFixture();
        Assert.False(f.Store.HasDoneBefore(new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void HasDoneBefore_false_when_only_open_old_notes()
    {
        using var f = new StoreFixture();
        f.Time.Set(new DateTimeOffset(2026, 1, 1, 9, 0, 0, Tr));
        f.Store.Add("eski açık");

        Assert.False(f.Store.HasDoneBefore(new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void HasDoneBefore_false_when_only_done_recent_notes()
    {
        using var f = new StoreFixture();
        var before = new DateOnly(2026, 11, 1);
        f.Time.Set(new DateTimeOffset(2026, 11, 5, 9, 0, 0, Tr));
        var note = f.Store.Add("yeni tamam");
        f.Store.SetDone(note.Id, true);

        Assert.False(f.Store.HasDoneBefore(before));
    }

    [Fact]
    public void HasDoneBefore_false_when_only_deleted_done_old_notes()
    {
        using var f = new StoreFixture();
        var before = new DateOnly(2026, 11, 1);
        f.Time.Set(new DateTimeOffset(2026, 1, 1, 9, 0, 0, Tr));
        var note = f.Store.Add("eski silinmiş tamam");
        f.Store.SetDone(note.Id, true);
        f.Store.Delete(note.Id);

        Assert.False(f.Store.HasDoneBefore(before));
    }

    [Fact]
    public void HasDoneBefore_true_when_live_done_note_exists_day_before_boundary()
    {
        using var f = new StoreFixture();
        var before = new DateOnly(2026, 11, 1);
        f.Time.Set(new DateTimeOffset(2026, 10, 31, 9, 0, 0, Tr));
        var note = f.Store.Add("sınırdan bir gün önce tamam");
        f.Store.SetDone(note.Id, true);

        Assert.True(f.Store.HasDoneBefore(before));
    }

    [Fact]
    public void HasDoneBefore_false_when_done_note_is_exactly_on_before_boundary()
    {
        using var f = new StoreFixture();
        var before = new DateOnly(2026, 11, 1);
        f.Time.Set(new DateTimeOffset(2026, 11, 1, 9, 0, 0, Tr));
        var note = f.Store.Add("tam sınır günü tamam");
        f.Store.SetDone(note.Id, true);

        Assert.False(f.Store.HasDoneBefore(before));
    }
}
