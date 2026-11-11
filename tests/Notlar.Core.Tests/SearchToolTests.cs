using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class SearchToolTests : IDisposable
{
    private static readonly TimeSpan Tr = StoreFixture.Tr;
    private readonly StoreFixture _f = new();
    private readonly NoteTools _tools;

    public SearchToolTests() => _tools = new NoteTools(_f.Store, _f.Time);

    public void Dispose() => _f.Dispose();

    [Fact]
    public void NotlardaAra_reports_count_and_grouped_lines()
    {
        _tools.NotEkle("Faturayı öde #ev");
        _tools.NotEkle("Kitap oku");

        var result = _tools.NotlardaAra("Faturayı");

        Assert.StartsWith("1 not bulundu.\n\n", result.ReplaceLineEndings("\n"));
        Assert.Contains("Faturayı öde #ev", result);
        Assert.DoesNotContain("Kitap oku", result);
    }

    [Fact]
    public void NotlardaAra_reports_no_match()
    {
        _tools.NotEkle("bir şey");

        Assert.Equal("Eşleşen not yok.", _tools.NotlardaAra("olmayan kelime"));
    }

    [Fact]
    public void NotlardaAra_is_case_insensitive_with_Turkish_rules()
    {
        _tools.NotEkle("İŞ toplantısı");

        Assert.Contains("İŞ toplantısı", _tools.NotlardaAra("iş"));
    }

    [Fact]
    public void NotlardaAra_filters_by_yazar()
    {
        _f.Store.Add("ortak kelime ben notu");
        _tools.NotEkle("ortak kelime claude notu");

        var result = _tools.NotlardaAra("ortak kelime", yazar: "ben");

        Assert.Contains("ben notu", result);
        Assert.DoesNotContain("claude notu", result);
    }

    [Fact]
    public void NotlardaAra_rejects_bad_yazar()
    {
        var ex = Assert.Throws<McpException>(() => _tools.NotlardaAra("x", yazar: "biri"));
        Assert.Equal("Geçersiz yazar 'biri', 'ben' veya 'claude' bekleniyor.", ex.Message);
    }

    [Fact]
    public void NotlardaAra_clamps_limit()
    {
        for (var i = 0; i < 5; i++)
            _tools.NotEkle($"eşleşen {i}");

        var result = _tools.NotlardaAra("eşleşen", limit: 0);

        Assert.StartsWith("1 not bulundu.", result);
    }

    [Fact]
    public void NotlardaAra_rejects_empty_query()
    {
        var ex = Assert.Throws<McpException>(() => _tools.NotlardaAra("   "));
        Assert.Equal("Arama metni boş olamaz.", ex.Message);
    }
}
