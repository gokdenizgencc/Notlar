using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class MemoryFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public MemoryStore Store { get; }

    public MemoryFixture() => Store = new MemoryStore(DbPath, Time, Signal);

    public MemoryStore Reopen() => new(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class MemoryStoreTests
{
    private static readonly TimeSpan Tr = MemoryFixture.Tr;

    [Fact]
    public void Add_trims_fields_defaults_category_stamps_time_and_signals()
    {
        using var f = new MemoryFixture();

        var item = f.Store.Add("  Ahmet Bey  ", "  ClickUp/Asana aktarma işleri  ");

        Assert.Equal("Ahmet Bey", item.Title);
        Assert.Equal("ClickUp/Asana aktarma işleri", item.Content);
        Assert.Equal("genel", item.Category);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr), item.CreatedAt);
        Assert.Equal(item.CreatedAt, item.UpdatedAt);
        Assert.Equal(1, f.Signal.Count);
    }

    [Fact]
    public void Add_normalizes_category_to_lowercase_tr_TR()
    {
        using var f = new MemoryFixture();
        var item = f.Store.Add("x", "y", "KİŞİLER");
        Assert.Equal("kişiler", item.Category);
    }

    [Theory]
    [InlineData("", "y")]
    [InlineData("  ", "y")]
    public void Add_rejects_empty_title(string title, string content)
    {
        using var f = new MemoryFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Add(title, content));
        Assert.Equal("Hafıza başlığı boş olamaz.", ex.Message);
    }

    [Theory]
    [InlineData("x", "")]
    [InlineData("x", "  ")]
    public void Add_rejects_empty_content(string title, string content)
    {
        using var f = new MemoryFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Add(title, content));
        Assert.Equal("Hafıza içeriği boş olamaz.", ex.Message);
    }

    [Fact]
    public void Update_partial_keeps_other_fields_and_bumps_updated_at()
    {
        using var f = new MemoryFixture();
        var item = f.Store.Add("başlık", "içerik", "projeler");

        f.Time.Advance(TimeSpan.FromMinutes(5));
        var updated = f.Store.Update(item.Id, content: "yeni içerik");

        Assert.Equal("başlık", updated.Title);
        Assert.Equal("yeni içerik", updated.Content);
        Assert.Equal("projeler", updated.Category);
        Assert.Equal(item.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt > item.CreatedAt);
    }

    [Fact]
    public void Update_unknown_id_throws()
    {
        using var f = new MemoryFixture();
        Assert.Throws<MemoryNotFoundException>(() => f.Store.Update(999, title: "x"));
    }

    [Fact]
    public void Delete_hides_item_and_Restore_brings_it_back()
    {
        using var f = new MemoryFixture();
        var item = f.Store.Add("x", "y");

        f.Store.Delete(item.Id);
        Assert.Empty(f.Store.List());
        Assert.Throws<MemoryNotFoundException>(() => f.Store.Delete(item.Id));

        var restored = f.Store.Restore(item.Id);
        Assert.Equal("x", restored.Title);
        Assert.Single(f.Store.List());
        Assert.Throws<MemoryNotFoundException>(() => f.Store.Restore(item.Id));
    }

    [Fact]
    public void List_orders_by_category_then_updated_at_desc()
    {
        using var f = new MemoryFixture();
        f.Store.Add("a", "a", "projeler");
        f.Time.Advance(TimeSpan.FromMinutes(1));
        f.Store.Add("b", "b", "kişiler");
        f.Time.Advance(TimeSpan.FromMinutes(1));
        f.Store.Add("c", "c", "kişiler");

        var all = f.Store.List();
        Assert.Equal(["c", "b", "a"], all.Select(m => m.Title));
    }

    [Fact]
    public void List_filters_by_category()
    {
        using var f = new MemoryFixture();
        f.Store.Add("a", "a", "projeler");
        f.Store.Add("b", "b", "kişiler");

        Assert.Equal(["b"], f.Store.List("kişiler").Select(m => m.Title));
    }

    [Fact]
    public void Search_matches_all_terms_case_insensitive_turkish()
    {
        using var f = new MemoryFixture();
        f.Store.Add("Ahmet Bey", "ClickUp/Asana aktarma İŞLERİ", "kişiler");
        f.Store.Add("başka", "alakasız içerik", "genel");

        var results = f.Store.Search("ahmet işler");
        Assert.Single(results);
        Assert.Equal("Ahmet Bey", results[0].Title);
    }

    [Fact]
    public void Search_empty_query_throws()
    {
        using var f = new MemoryFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Search("  "));
        Assert.Equal("Arama metni boş olamaz.", ex.Message);
    }

    [Fact]
    public void Categories_returns_distinct_live_categories()
    {
        using var f = new MemoryFixture();
        f.Store.Add("a", "a", "projeler");
        f.Store.Add("b", "b", "kişiler");
        var c = f.Store.Add("c", "c", "kişiler");
        f.Store.Delete(c.Id);

        Assert.Equal(["kişiler", "projeler"], f.Store.Categories());
    }

    [Fact]
    public void Data_persists_across_instances()
    {
        using var f = new MemoryFixture();
        var item = f.Store.Add("kalıcı", "içerik");
        Assert.Equal("kalıcı", f.Reopen().List().Single(m => m.Id == item.Id).Title);
    }

    [Fact]
    public void Coexists_with_NoteStore_and_TaskStore_on_same_db_file()
    {
        using var f = new MemoryFixture();
        var noteStore = new NoteStore(f.DbPath, f.Time, f.Signal);
        var taskStore = new TaskStore(f.DbPath, f.Time, f.Signal);

        var note = noteStore.Add("bir not");
        var task = taskStore.Add("bir görev", "Claude");
        var mem = f.Store.Add("bir hafıza", "içerik");

        Assert.Equal("bir not", noteStore.Get(note.Id)!.Text);
        Assert.Equal("bir görev", taskStore.List().Single(t => t.Id == task.Id).Title);
        Assert.Equal("bir hafıza", f.Store.List().Single(m => m.Id == mem.Id).Title);
    }
}
