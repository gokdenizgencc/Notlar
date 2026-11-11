using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class SearchTests : IDisposable
{
    private static readonly TimeSpan Tr = StoreFixture.Tr;
    private readonly StoreFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Search_requires_all_terms_to_match()
    {
        _f.Store.Add("Faturayı öde");
        _f.Store.Add("Faturayı gönder");
        _f.Store.Add("Kitap oku");

        var result = _f.Store.Search("Faturayı öde");

        Assert.Single(result);
        Assert.Equal("Faturayı öde", result[0].Text);
    }

    [Fact]
    public void Search_is_case_insensitive_with_Turkish_rules()
    {
        _f.Store.Add("İŞ toplantısı var");
        _f.Store.Add("Bugün ışık açık kaldı");

        Assert.Single(_f.Store.Search("İŞ"));
        Assert.Contains(_f.Store.Search("İŞ"), n => n.Text == "İŞ toplantısı var");

        Assert.Single(_f.Store.Search("ışık"));
        var found = _f.Store.Search("ışık");
        Assert.Equal("Bugün ışık açık kaldı", found[0].Text);
    }

    [Fact]
    public void Search_finds_uppercase_IŞIK_with_lowercase_query()
    {
        _f.Store.Add("Bugün IŞIK açık kaldı");

        var result = _f.Store.Search("ışık");

        Assert.Single(result);
    }

    [Fact]
    public void Search_filters_by_author()
    {
        _f.Store.Add("ortak kelime ben", NoteAuthor.Ben);
        _f.Store.Add("ortak kelime claude", NoteAuthor.Claude);

        var result = _f.Store.Search("ortak kelime", NoteAuthor.Claude);

        Assert.Single(result);
        Assert.Equal(NoteAuthor.Claude, result[0].Author);
    }

    [Fact]
    public void Search_orders_newest_first_and_respects_limit()
    {
        _f.Time.Set(new DateTimeOffset(2026, 11, 9, 10, 0, 0, Tr));
        _f.Store.Add("eşleşen bir");
        _f.Time.Set(new DateTimeOffset(2026, 11, 10, 10, 0, 0, Tr));
        _f.Store.Add("eşleşen iki");
        _f.Time.Set(new DateTimeOffset(2026, 11, 11, 10, 0, 0, Tr));
        _f.Store.Add("eşleşen üç");

        var result = _f.Store.Search("eşleşen", limit: 2);

        Assert.Equal(2, result.Count);
        Assert.Equal("eşleşen üç", result[0].Text);
        Assert.Equal("eşleşen iki", result[1].Text);
    }

    [Fact]
    public void Search_excludes_deleted_notes()
    {
        var note = _f.Store.Add("silinecek kelime");
        _f.Store.Delete(note.Id);

        Assert.Empty(_f.Store.Search("kelime"));
    }

    [Fact]
    public void Search_throws_on_empty_query()
    {
        var ex = Assert.Throws<ArgumentException>(() => _f.Store.Search("   "));
        Assert.Equal("Arama metni boş olamaz.", ex.Message);
    }
}
