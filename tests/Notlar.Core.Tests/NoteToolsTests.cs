using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class NoteToolsTests : IDisposable
{
    private static readonly TimeSpan Tr = StoreFixture.Tr;
    private readonly StoreFixture _f = new();
    private readonly NoteTools _tools;

    public NoteToolsTests() => _tools = new NoteTools(_f.Store, _f.Time);

    public void Dispose() => _f.Dispose();

    [Fact]
    public void NotEkle_adds_note_and_reports_line_and_day()
    {
        var result = _tools.NotEkle("Faturayı öde #ev");

        Assert.Equal("Not eklendi: [ ] 14:32 Faturayı öde #ev  (id: 1, yazar: Claude) — 11 Kasım 2026, Çarşamba", result);
        Assert.Single(_f.Store.List(new NoteFilter()));
    }

    [Fact]
    public void NotEkle_empty_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.NotEkle("  "));
        Assert.Equal("Not metni boş olamaz.", ex.Message);
    }

    [Fact]
    public void NotlariListele_defaults_to_last_7_days_grouped_by_day()
    {
        _f.Time.Set(new DateTimeOffset(2026, 11, 4, 10, 0, 0, Tr)); _tools.NotEkle("eski");
        _f.Time.Set(new DateTimeOffset(2026, 11, 5, 10, 0, 0, Tr)); _tools.NotEkle("sınırda");
        _f.Time.Set(new DateTimeOffset(2026, 11, 11, 9, 0, 0, Tr)); _tools.NotEkle("bugün");

        var result = _tools.NotlariListele();

        Assert.Equal(
            "## BUGÜN · 11 Kasım 2026, Çarşamba\n" +
            "[ ] 09:00 bugün  (id: 3, yazar: Claude)\n" +
            "\n" +
            "## 5 Kasım 2026, Perşembe\n" +
            "[ ] 10:00 sınırda  (id: 2, yazar: Claude)",
            result.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void NotlariListele_respects_explicit_range_tag_and_open_filter()
    {
        _tools.NotEkle("a #iş");
        _tools.NotEkle("b #ev");
        _tools.NotEkle("c #iş");
        _tools.NotTamamla(3);

        var result = _tools.NotlariListele(baslangic: "2026-11-11", bitis: "2026-11-11", etiket: "iş", sadece_acik: true);

        Assert.Contains("a #iş", result);
        Assert.DoesNotContain("b #ev", result);
        Assert.DoesNotContain("c #iş", result);
    }

    [Fact]
    public void NotlariListele_reports_empty_range()
    {
        Assert.Equal("Bu aralıkta not yok.", _tools.NotlariListele());
    }

    [Fact]
    public void NotlariListele_rejects_bad_date()
    {
        var ex = Assert.Throws<McpException>(() => _tools.NotlariListele(baslangic: "11.11.2026"));
        Assert.Equal("Geçersiz tarih '11.11.2026', yyyy-MM-dd bekleniyor.", ex.Message);
    }

    [Fact]
    public void NotlariListele_filters_by_yazar()
    {
        _f.Store.Add("ben notu");
        _tools.NotEkle("claude notu");

        var result = _tools.NotlariListele(yazar: "ben");

        Assert.Contains("ben notu", result);
        Assert.DoesNotContain("claude notu", result);
    }

    [Fact]
    public void NotlariListele_rejects_bad_yazar()
    {
        var ex = Assert.Throws<McpException>(() => _tools.NotlariListele(yazar: "biri"));
        Assert.Equal("Geçersiz yazar 'biri', 'ben' veya 'claude' bekleniyor.", ex.Message);
    }

    [Fact]
    public void NotGuncelle_changes_text()
    {
        _tools.NotEkle("eski");
        Assert.Equal("Not güncellendi: [ ] 14:32 yeni  (id: 1, yazar: Claude)", _tools.NotGuncelle(1, "yeni"));
    }

    [Fact]
    public void NotTamamla_marks_and_unmarks()
    {
        _tools.NotEkle("x");
        Assert.Equal("Not tamamlandı: [x] 14:32 x  (id: 1, yazar: Claude)", _tools.NotTamamla(1));
        Assert.Equal("Not tekrar açıldı: [ ] 14:32 x  (id: 1, yazar: Claude)", _tools.NotTamamla(1, tamamlandi: false));
    }

    [Fact]
    public void NotSil_removes_and_unknown_id_throws()
    {
        _tools.NotEkle("x");
        Assert.Equal("Not silindi (id: 1).", _tools.NotSil(1));
        Assert.Empty(_f.Store.List(new NoteFilter()));

        var ex = Assert.Throws<McpException>(() => _tools.NotSil(1));
        Assert.Equal("Not bulunamadı: 1", ex.Message);
    }

    [Fact]
    public void NotEkle_flattens_line_endings_in_reported_line()
    {
        var result = _tools.NotEkle("satır1\nsatır2\r\nsatır3");

        Assert.Equal(
            "Not eklendi: [ ] 14:32 satır1 satır2 satır3  (id: 1, yazar: Claude) — 11 Kasım 2026, Çarşamba",
            result);
    }

    [Fact]
    public void Run_wraps_SqliteException_as_McpException_with_Turkish_message()
    {
        _tools.NotEkle("ilk not");

        // Dosyayı SQLite için geçersiz hale getiriyoruz ki sonraki sorgu SqliteException fırlatsın.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _f.DbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        File.WriteAllBytes(_f.DbPath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<McpException>(() => _tools.NotlariListele());
        Assert.StartsWith("Veritabanına erişilemedi", ex.Message);
    }
}
