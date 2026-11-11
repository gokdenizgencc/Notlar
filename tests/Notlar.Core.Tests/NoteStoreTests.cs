using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public class NoteStoreTests
{
    private static readonly TimeSpan Tr = StoreFixture.Tr;

    [Fact]
    public void Add_trims_text_stamps_local_time_and_signals()
    {
        using var f = new StoreFixture();

        var note = f.Store.Add("  Faturayı öde #ev  ");

        Assert.Equal("Faturayı öde #ev", note.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr), note.CreatedAt);
        Assert.Equal(Tr, note.CreatedAt.Offset);
        Assert.False(note.Done);
        Assert.Null(note.UpdatedAt);
        Assert.Equal(["ev"], note.Tags);
        Assert.Equal(1, f.Signal.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_rejects_empty_text(string text)
    {
        using var f = new StoreFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Add(text));
        Assert.Equal("Not metni boş olamaz.", ex.Message);
        Assert.Equal(0, f.Signal.Count);
    }

    [Fact]
    public void AddAt_stores_given_day_and_time_and_signals()
    {
        using var f = new StoreFixture();
        var created = new DateTimeOffset(2026, 11, 1, 9, 0, 0, Tr);

        var note = f.Store.AddAt("geçmiş not", created);

        Assert.Equal(created, note.CreatedAt);
        Assert.Equal(new DateOnly(2026, 11, 1), note.Day);
        Assert.Equal(1, f.Signal.Count);
    }

    [Fact]
    public void AddAt_converts_to_store_local_time_zone()
    {
        using var f = new StoreFixture();
        var createdUtc = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero); // 09:00 TR

        var note = f.Store.AddAt("x", createdUtc);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, Tr), note.CreatedAt);
        Assert.Equal(Tr, note.CreatedAt.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAt_rejects_empty_text(string text)
    {
        using var f = new StoreFixture();
        Assert.Throws<ArgumentException>(() => f.Store.AddAt(text, f.Time.GetLocalNow()));
        Assert.Equal(0, f.Signal.Count);
    }

    [Fact]
    public void Add_delegates_to_AddAt_with_current_local_time()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("x");
        Assert.Equal(f.Time.GetLocalNow(), note.CreatedAt);
    }

    [Fact]
    public void List_filters_by_inclusive_date_range()
    {
        using var f = new StoreFixture();
        f.Time.Set(new DateTimeOffset(2026, 11, 10, 23, 59, 0, Tr)); f.Store.Add("dün");
        f.Time.Set(new DateTimeOffset(2026, 11, 11, 0, 1, 0, Tr)); f.Store.Add("bugün");
        f.Time.Set(new DateTimeOffset(2026, 11, 12, 8, 0, 0, Tr)); f.Store.Add("yarın");

        var only11 = f.Store.List(new NoteFilter(From: new DateOnly(2026, 11, 11), To: new DateOnly(2026, 11, 11)));
        var from11 = f.Store.List(new NoteFilter(From: new DateOnly(2026, 11, 11)));

        Assert.Equal(["bugün"], only11.Select(n => n.Text));
        Assert.Equal(["bugün", "yarın"], from11.Select(n => n.Text));
    }

    [Fact]
    public void List_filters_by_tag_case_insensitive_with_or_without_hash()
    {
        using var f = new StoreFixture();
        f.Store.Add("a #İş");
        f.Store.Add("b #ev");

        Assert.Equal(["a #İş"], f.Store.List(new NoteFilter(Tag: "#iş")).Select(n => n.Text));
        Assert.Equal(["b #ev"], f.Store.List(new NoteFilter(Tag: "EV")).Select(n => n.Text));
    }

    [Fact]
    public void Add_defaults_to_Ben_author()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("x");
        Assert.Equal(NoteAuthor.Ben, note.Author);
    }

    [Fact]
    public void Add_roundtrips_author()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("x", NoteAuthor.Claude);
        Assert.Equal(NoteAuthor.Claude, note.Author);
        Assert.Equal(NoteAuthor.Claude, f.Store.Get(note.Id)!.Author);
        Assert.Equal(NoteAuthor.Claude, f.Reopen().Get(note.Id)!.Author);
    }

    [Fact]
    public void List_filters_by_author()
    {
        using var f = new StoreFixture();
        f.Store.Add("a", NoteAuthor.Ben);
        f.Store.Add("b", NoteAuthor.Claude);

        Assert.Equal(["a"], f.Store.List(new NoteFilter(Author: NoteAuthor.Ben)).Select(n => n.Text));
        Assert.Equal(["b"], f.Store.List(new NoteFilter(Author: NoteAuthor.Claude)).Select(n => n.Text));
        Assert.Equal(2, f.Store.List(new NoteFilter()).Count);
    }

    [Fact]
    public void Migrates_old_schema_without_author_column()
    {
        var dir = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldDbPath = Path.Combine(dir, "eski.db");

        try
        {
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = oldDbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE notes (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    text       TEXT    NOT NULL,
                    created_at TEXT    NOT NULL,
                    updated_at TEXT    NULL,
                    done       INTEGER NOT NULL DEFAULT 0,
                    deleted_at TEXT    NULL
                );
                INSERT INTO notes(text, created_at) VALUES ('eski not', '2026-11-11T14:32:00.000+03:00');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var time = new FakeTime(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
        var signal = new CountingSignal();
        var store = new NoteStore(oldDbPath, time, signal);

        var existing = store.Get(1)!;
        Assert.Equal("eski not", existing.Text);
        Assert.Equal(NoteAuthor.Ben, existing.Author);

        var added = store.Add("yeni not", NoteAuthor.Claude);
        Assert.Equal(NoteAuthor.Claude, added.Author);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void List_only_open_excludes_done()
    {
        using var f = new StoreFixture();
        var a = f.Store.Add("a");
        f.Store.Add("b");
        f.Store.SetDone(a.Id, true);

        Assert.Equal(["b"], f.Store.List(new NoteFilter(OnlyOpen: true)).Select(n => n.Text));
    }

    [Fact]
    public void Update_changes_text_keeps_created_at_sets_updated_at()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("eski");
        f.Time.Advance(TimeSpan.FromHours(1));

        var updated = f.Store.Update(note.Id, " yeni #iş ");

        Assert.Equal("yeni #iş", updated.Text);
        Assert.Equal(note.CreatedAt, updated.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 15, 32, 0, Tr), updated.UpdatedAt);
        Assert.Equal(2, f.Signal.Count);
    }

    [Fact]
    public void Update_rejects_empty_and_unknown()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("x");
        Assert.Throws<ArgumentException>(() => f.Store.Update(note.Id, " "));
        var ex = Assert.Throws<NoteNotFoundException>(() => f.Store.Update(999, "y"));
        Assert.Equal("Not bulunamadı: 999", ex.Message);
    }

    [Fact]
    public void SetDone_toggles()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("x");
        Assert.True(f.Store.SetDone(note.Id, true).Done);
        Assert.False(f.Store.SetDone(note.Id, false).Done);
        Assert.Throws<NoteNotFoundException>(() => f.Store.SetDone(999, true));
    }

    [Fact]
    public void Delete_hides_note_and_Restore_brings_it_back()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("x");

        f.Store.Delete(note.Id);
        Assert.Null(f.Store.Get(note.Id));
        Assert.Empty(f.Store.List(new NoteFilter()));
        Assert.Throws<NoteNotFoundException>(() => f.Store.Delete(note.Id));

        var restored = f.Store.Restore(note.Id);
        Assert.Equal("x", restored.Text);
        Assert.Single(f.Store.List(new NoteFilter()));
        Assert.Throws<NoteNotFoundException>(() => f.Store.Restore(note.Id));
        Assert.Equal(3, f.Signal.Count);
    }

    [Fact]
    public void PurgeDeleted_removes_only_old_soft_deleted()
    {
        using var f = new StoreFixture();
        var old = f.Store.Add("eski");
        var recent = f.Store.Add("yeni");
        f.Store.Add("duruyor");
        f.Store.Delete(old.Id);
        f.Time.Advance(TimeSpan.FromDays(6));
        f.Store.Delete(recent.Id);
        f.Time.Advance(TimeSpan.FromDays(2));

        Assert.Equal(1, f.Store.PurgeDeleted(TimeSpan.FromDays(7)));
        Assert.Throws<NoteNotFoundException>(() => f.Store.Restore(old.Id));
        Assert.Equal("yeni", f.Store.Restore(recent.Id).Text);
    }

    [Fact]
    public void Data_persists_across_instances()
    {
        using var f = new StoreFixture();
        var note = f.Store.Add("kalıcı");
        Assert.Equal("kalıcı", f.Reopen().Get(note.Id)?.Text);
    }

    [Fact]
    public void Backup_creates_one_file_per_day_and_keeps_last_n()
    {
        using var f = new StoreFixture();
        f.Store.Add("x");
        var dir = Path.Combine(f.Dir, "yedek");

        for (var day = 1; day <= 9; day++)
        {
            f.Time.Set(new DateTimeOffset(2026, 11, day, 9, 0, 0, Tr));
            Assert.NotNull(f.Store.Backup(dir, keep: 7));
        }
        Assert.Null(f.Store.Backup(dir, keep: 7));

        var files = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(7, files.Count);
        Assert.Equal("notlar-2026-11-03.db", files[0]);
        Assert.Equal("notlar-2026-11-09.db", files[^1]);
    }

    [Fact]
    public void Backup_ignores_leftover_tmp_from_failed_attempt_and_is_not_counted()
    {
        using var f = new StoreFixture();
        f.Store.Add("x");
        var dir = Path.Combine(f.Dir, "yedek");
        Directory.CreateDirectory(dir);
        var leftoverTmp = Path.Combine(dir, "notlar-2026-11-11.db.tmp");
        File.WriteAllText(leftoverTmp, "yarım kalmış yedekten artık dosya");

        var created = f.Store.Backup(dir, keep: 7);

        Assert.NotNull(created);
        Assert.Equal("notlar-2026-11-11.db", Path.GetFileName(created));
        Assert.False(File.Exists(leftoverTmp));
        Assert.Equal(["notlar-2026-11-11.db"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }
}
