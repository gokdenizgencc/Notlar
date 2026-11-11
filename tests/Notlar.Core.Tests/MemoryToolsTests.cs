using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class MemoryToolsTests : IDisposable
{
    private readonly MemoryFixture _f = new();
    private readonly MemoryTools _tools;

    public MemoryToolsTests() => _tools = new MemoryTools(_f.Store);

    public void Dispose() => _f.Dispose();

    [Fact]
    public void HafizaKaydet_adds_card_with_default_category()
    {
        var result = _tools.HafizaKaydet("Ahmet Bey", "ClickUp/Asana aktarma işleri");

        Assert.Equal("Hafızaya kaydedildi: [#1 · genel] Ahmet Bey: ClickUp/Asana aktarma işleri", result);
        Assert.Single(_f.Store.List());
    }

    [Fact]
    public void HafizaKaydet_accepts_category()
    {
        var result = _tools.HafizaKaydet("Ahmet Bey", "aktarma işleri", "kişiler");
        Assert.Equal("Hafızaya kaydedildi: [#1 · kişiler] Ahmet Bey: aktarma işleri", result);
    }

    [Fact]
    public void HafizaKaydet_empty_title_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.HafizaKaydet("  ", "icerik"));
        Assert.Equal("Hafıza başlığı boş olamaz.", ex.Message);
    }

    [Fact]
    public void HafizaListele_reports_empty()
    {
        Assert.Equal("Hafıza kaydı yok.", _tools.HafizaListele());
    }

    [Fact]
    public void HafizaListele_filters_by_category()
    {
        _tools.HafizaKaydet("a", "icerik a", "projeler");
        _tools.HafizaKaydet("b", "icerik b", "kişiler");

        var result = _tools.HafizaListele("kişiler");

        Assert.Equal("[#2 · kişiler] b: icerik b", result);
    }

    [Fact]
    public void HafizaAra_finds_matching_cards_and_reports_empty_result()
    {
        _tools.HafizaKaydet("Ahmet Bey", "ClickUp aktarma işleri", "kişiler");

        Assert.Equal("[#1 · kişiler] Ahmet Bey: ClickUp aktarma işleri", _tools.HafizaAra("ahmet"));
        Assert.Equal("Eşleşen hafıza kaydı yok.", _tools.HafizaAra("bulunamaz"));
    }

    [Fact]
    public void HafizaGuncelle_updates_partial_fields()
    {
        _tools.HafizaKaydet("eski", "icerik", "genel");
        var result = _tools.HafizaGuncelle(1, baslik: "yeni");

        Assert.Equal("Hafıza güncellendi: [#1 · genel] yeni: icerik", result);
    }

    [Fact]
    public void HafizaSil_removes_and_unknown_id_throws()
    {
        _tools.HafizaKaydet("x", "y");
        Assert.Equal("Hafıza kaydı silindi (id: 1).", _tools.HafizaSil(1));
        Assert.Empty(_f.Store.List());

        var ex = Assert.Throws<McpException>(() => _tools.HafizaSil(1));
        Assert.Equal("Hafıza kaydı bulunamadı: 1", ex.Message);
    }

    [Fact]
    public void Run_wraps_SqliteException_as_McpException_with_Turkish_message()
    {
        _tools.HafizaKaydet("ilk kayit", "icerik");

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _f.DbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        File.WriteAllBytes(_f.DbPath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<McpException>(() => _tools.HafizaListele());
        Assert.StartsWith("Veritabanına erişilemedi", ex.Message);
    }
}
