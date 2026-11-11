using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class TaskToolsTests : IDisposable
{
    private readonly TaskFixture _f = new();
    private readonly TaskTools _tools;

    public TaskToolsTests()
    {
        var flags = new AppFlags(_f.DbPath);
        flags.SetBool(AppFlags.KisiselMcpOkuma, true);
        _tools = new TaskTools(_f.Store, flags: flags);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public void KisiselEkle_adds_task_with_default_author()
    {
        var result = _tools.KisiselEkle("Rapor hazırla");

        Assert.Equal("Kişisel kayıt eklendi: [ ] Rapor hazırla  (id: 1, yazar: Claude)", result);
        Assert.Single(_f.Store.List());
    }

    [Fact]
    public void KisiselEkle_accepts_custom_author()
    {
        var result = _tools.KisiselEkle("Rapor hazırla", "ChatGPT");
        Assert.Equal("Kişisel kayıt eklendi: [ ] Rapor hazırla  (id: 1, yazar: ChatGPT)", result);
    }

    [Fact]
    public void KisiselEkle_empty_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.KisiselEkle("  "));
        Assert.Equal("Başlık boş olamaz.", ex.Message);
    }

    [Fact]
    public void KisiselListele_reports_empty()
    {
        Assert.Equal("Kişisel kayıt yok.", _tools.KisiselListele());
    }

    [Fact]
    public void KisiselListele_lists_open_before_done()
    {
        _tools.KisiselEkle("a");
        _tools.KisiselEkle("b");
        _tools.KisiselTamamla(1);

        var result = _tools.KisiselListele();

        Assert.Equal(
            "[ ] b  (id: 2, yazar: Claude)\n" +
            "[x] a  (id: 1, yazar: Claude)",
            result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void KisiselListele_sadece_acik_excludes_done()
    {
        _tools.KisiselEkle("a");
        _tools.KisiselEkle("b");
        _tools.KisiselTamamla(1);

        var result = _tools.KisiselListele(sadece_acik: true);

        Assert.Equal("[ ] b  (id: 2, yazar: Claude)", result);
    }

    [Fact]
    public void KisiselTamamla_marks_and_unmarks()
    {
        _tools.KisiselEkle("x");
        Assert.Equal("Kişisel kayıt tamamlandı: [x] x  (id: 1, yazar: Claude)", _tools.KisiselTamamla(1));
        Assert.Equal("Kişisel kayıt tekrar açıldı: [ ] x  (id: 1, yazar: Claude)", _tools.KisiselTamamla(1, tamamlandi: false));
    }

    [Fact]
    public void KisiselGuncelle_changes_title()
    {
        _tools.KisiselEkle("eski");
        Assert.Equal("Kişisel kayıt güncellendi: [ ] yeni  (id: 1, yazar: Claude)", _tools.KisiselGuncelle(1, "yeni"));
    }

    [Fact]
    public void KisiselSil_removes_and_unknown_id_throws()
    {
        _tools.KisiselEkle("x");
        Assert.Equal("Kişisel kayıt silindi (id: 1).", _tools.KisiselSil(1));
        Assert.Empty(_f.Store.List());

        var ex = Assert.Throws<McpException>(() => _tools.KisiselSil(1));
        Assert.Equal("Kişisel kayıt bulunamadı: 1", ex.Message);
    }

    [Fact]
    public void Run_wraps_SqliteException_as_McpException_with_Turkish_message()
    {
        _tools.KisiselEkle("ilk kayıt");

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _f.DbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        File.WriteAllBytes(_f.DbPath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<McpException>(() => _tools.KisiselListele());
        Assert.StartsWith("Veritabanına erişilemedi", ex.Message);
    }
}
