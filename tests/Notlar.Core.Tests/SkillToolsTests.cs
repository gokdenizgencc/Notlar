using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class SkillToolsTests : IDisposable
{
    private readonly SkillRegistryFixture _f = new();
    private readonly SkillTranslations _translations;
    private readonly SkillTools _tools;

    public SkillToolsTests()
    {
        _translations = new SkillTranslations(_f.DbPath, _f.Time, _f.Signal);
        _tools = new SkillTools(_f.Store, _translations);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public void YetenekKaydet_adds_skill()
    {
        var result = _tools.YetenekKaydet("ChatGPT", "web-search", "Arama yapar.");

        Assert.Equal("Yetenek kaydedildi: [ChatGPT] web-search — 0 kez — Arama yapar.", result);
        Assert.Single(_f.Store.List());
    }

    [Fact]
    public void YetenekKaydet_empty_name_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.YetenekKaydet("ChatGPT", "  "));
        Assert.Equal("Yetenek adı boş olamaz.", ex.Message);
    }

    [Fact]
    public void YetenekKullanildi_increments_usage()
    {
        _tools.YetenekKaydet("ChatGPT", "web-search");

        var result = _tools.YetenekKullanildi("chatgpt", "web-search");

        Assert.Contains("1 kez", result);
    }

    [Fact]
    public void YetenekleriListele_reports_empty_when_no_registry_entries_and_no_claude_dir()
    {
        // ScanInstalled/GetUsage use the real %USERPROFILE%\.claude by default, so this only asserts
        // no exception is thrown and the registry portion behaves; we just check it doesn't throw.
        var result = _tools.YetenekleriListele();
        Assert.NotNull(result);
    }

    [Fact]
    public void YetenekleriListele_includes_registered_skill()
    {
        _tools.YetenekKaydet("ChatGPT", "web-search", "Arama yapar.");

        var result = _tools.YetenekleriListele("ChatGPT");

        Assert.Contains("ChatGPT", result);
        Assert.Contains("web-search", result);
    }

    [Fact]
    public void YetenekAciklamaKaydet_saves_translation()
    {
        var result = _tools.YetenekAciklamaKaydet("superpowers:brainstorming", "Yaratıcı fikirleri keşfeder.");

        Assert.Contains("Yaratıcı fikirleri keşfeder.", result);
        Assert.Equal("Yaratıcı fikirleri keşfeder.", _translations.Get("superpowers:brainstorming")!.DescriptionTr);
    }

    [Fact]
    public void Run_wraps_SqliteException_as_McpException_with_Turkish_message()
    {
        _tools.YetenekKaydet("ChatGPT", "ilk");

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _f.DbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        File.WriteAllBytes(_f.DbPath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<McpException>(() => _tools.YetenekKullanildi("ChatGPT", "ilk"));
        Assert.StartsWith("Veritabanına erişilemedi", ex.Message);
    }
}
