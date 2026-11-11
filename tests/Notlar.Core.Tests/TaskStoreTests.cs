using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class TaskFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public TaskStore Store { get; }

    public TaskFixture() => Store = new TaskStore(DbPath, Time, Signal);

    public TaskStore Reopen() => new(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class TaskStoreTests
{
    private static readonly TimeSpan Tr = TaskFixture.Tr;

    [Fact]
    public void Add_trims_title_stamps_local_time_defaults_author_and_signals()
    {
        using var f = new TaskFixture();

        var task = f.Store.Add("  Raporu hazırla  ", "Claude");

        Assert.Equal("Raporu hazırla", task.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr), task.CreatedAt);
        Assert.Equal(Tr, task.CreatedAt.Offset);
        Assert.False(task.Done);
        Assert.Equal("Claude", task.Author);
        Assert.Equal(1, f.Signal.Count);
    }

    [Fact]
    public void Add_roundtrips_custom_author()
    {
        using var f = new TaskFixture();
        var task = f.Store.Add("x", "ChatGPT");
        Assert.Equal("ChatGPT", task.Author);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_rejects_empty_title(string title)
    {
        using var f = new TaskFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Add(title, "Claude"));
        Assert.Equal("Başlık boş olamaz.", ex.Message);
        Assert.Equal(0, f.Signal.Count);
    }

    [Fact]
    public void List_orders_open_first_then_done_each_by_id()
    {
        using var f = new TaskFixture();
        var a = f.Store.Add("a", "Claude");
        var b = f.Store.Add("b", "Claude");
        var c = f.Store.Add("c", "Claude");
        f.Store.SetDone(a.Id, true);

        var all = f.Store.List();

        Assert.Equal(["b", "c", "a"], all.Select(t => t.Title));
    }

    [Fact]
    public void List_onlyOpen_excludes_done()
    {
        using var f = new TaskFixture();
        var a = f.Store.Add("a", "Claude");
        f.Store.Add("b", "Claude");
        f.Store.SetDone(a.Id, true);

        Assert.Equal(["b"], f.Store.List(onlyOpen: true).Select(t => t.Title));
    }

    [Fact]
    public void SetDone_toggles_and_unknown_id_throws()
    {
        using var f = new TaskFixture();
        var task = f.Store.Add("x", "Claude");

        Assert.True(f.Store.SetDone(task.Id, true).Done);
        Assert.False(f.Store.SetDone(task.Id, false).Done);
        Assert.Throws<TaskNotFoundException>(() => f.Store.SetDone(999, true));
    }

    [Fact]
    public void Rename_changes_title_and_rejects_empty_and_unknown()
    {
        using var f = new TaskFixture();
        var task = f.Store.Add("eski", "Claude");

        var renamed = f.Store.Rename(task.Id, " yeni ");
        Assert.Equal("yeni", renamed.Title);

        Assert.Throws<ArgumentException>(() => f.Store.Rename(task.Id, " "));
        var ex = Assert.Throws<TaskNotFoundException>(() => f.Store.Rename(999, "y"));
        Assert.Equal("Kişisel kayıt bulunamadı: 999", ex.Message);
    }

    [Fact]
    public void Delete_hides_task_and_Restore_brings_it_back()
    {
        using var f = new TaskFixture();
        var task = f.Store.Add("x", "Claude");

        f.Store.Delete(task.Id);
        Assert.Empty(f.Store.List());
        Assert.Throws<TaskNotFoundException>(() => f.Store.Delete(task.Id));

        var restored = f.Store.Restore(task.Id);
        Assert.Equal("x", restored.Title);
        Assert.Single(f.Store.List());
        Assert.Throws<TaskNotFoundException>(() => f.Store.Restore(task.Id));
        Assert.Equal(3, f.Signal.Count);
    }

    [Fact]
    public void Data_persists_across_instances()
    {
        using var f = new TaskFixture();
        var task = f.Store.Add("kalıcı", "Claude");
        Assert.Equal("kalıcı", f.Reopen().List().Single(t => t.Id == task.Id).Title);
    }

    [Fact]
    public void Coexists_with_NoteStore_on_same_db_file()
    {
        using var f = new TaskFixture();
        var noteStore = new NoteStore(f.DbPath, f.Time, f.Signal);

        var note = noteStore.Add("bir not");
        var task = f.Store.Add("bir görev", "Claude");

        Assert.Equal("bir not", noteStore.Get(note.Id)!.Text);
        Assert.Equal("bir görev", f.Store.List().Single(t => t.Id == task.Id).Title);
    }
}
