using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class TaskToolsTests : IDisposable
{
    private readonly TaskFixture _f = new();
    private readonly TaskTools _tools;

    public TaskToolsTests() => _tools = new TaskTools(_f.Store);

    public void Dispose() => _f.Dispose();

    [Fact]
    public void GorevEkle_adds_task_with_default_author()
    {
        var result = _tools.GorevEkle("Rapor hazırla");

        Assert.Equal("Görev eklendi: [ ] Rapor hazırla  (id: 1, yazar: Claude)", result);
        Assert.Single(_f.Store.List());
    }

    [Fact]
    public void GorevEkle_accepts_custom_author()
    {
        var result = _tools.GorevEkle("Rapor hazırla", "ChatGPT");
        Assert.Equal("Görev eklendi: [ ] Rapor hazırla  (id: 1, yazar: ChatGPT)", result);
    }

    [Fact]
    public void GorevEkle_empty_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.GorevEkle("  "));
        Assert.Equal("Görev başlığı boş olamaz.", ex.Message);
    }

    [Fact]
    public void GorevleriListele_reports_empty()
    {
        Assert.Equal("Görev yok.", _tools.GorevleriListele());
    }

    [Fact]
    public void GorevleriListele_lists_open_before_done()
    {
        _tools.GorevEkle("a");
        _tools.GorevEkle("b");
        _tools.GorevTamamla(1);

        var result = _tools.GorevleriListele();

        Assert.Equal(
            "[ ] b  (id: 2, yazar: Claude)\n" +
            "[x] a  (id: 1, yazar: Claude)",
            result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void GorevleriListele_sadece_acik_excludes_done()
    {
        _tools.GorevEkle("a");
        _tools.GorevEkle("b");
        _tools.GorevTamamla(1);

        var result = _tools.GorevleriListele(sadece_acik: true);

        Assert.Equal("[ ] b  (id: 2, yazar: Claude)", result);
    }

    [Fact]
    public void GorevTamamla_marks_and_unmarks()
    {
        _tools.GorevEkle("x");
        Assert.Equal("Görev tamamlandı: [x] x  (id: 1, yazar: Claude)", _tools.GorevTamamla(1));
        Assert.Equal("Görev tekrar açıldı: [ ] x  (id: 1, yazar: Claude)", _tools.GorevTamamla(1, tamamlandi: false));
    }

    [Fact]
    public void GorevGuncelle_changes_title()
    {
        _tools.GorevEkle("eski");
        Assert.Equal("Görev güncellendi: [ ] yeni  (id: 1, yazar: Claude)", _tools.GorevGuncelle(1, "yeni"));
    }

    [Fact]
    public void GorevSil_removes_and_unknown_id_throws()
    {
        _tools.GorevEkle("x");
        Assert.Equal("Görev silindi (id: 1).", _tools.GorevSil(1));
        Assert.Empty(_f.Store.List());

        var ex = Assert.Throws<McpException>(() => _tools.GorevSil(1));
        Assert.Equal("Görev bulunamadı: 1", ex.Message);
    }

    [Fact]
    public void Run_wraps_SqliteException_as_McpException_with_Turkish_message()
    {
        _tools.GorevEkle("ilk görev");

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _f.DbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        File.WriteAllBytes(_f.DbPath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<McpException>(() => _tools.GorevleriListele());
        Assert.StartsWith("Veritabanına erişilemedi", ex.Message);
    }
}
