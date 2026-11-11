using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class ProjectToolsTests : IDisposable
{
    private readonly ProjectFixture _f = new();
    private readonly ProjectTools _tools;

    public ProjectToolsTests() => _tools = new ProjectTools(_f.Store);

    public void Dispose() => _f.Dispose();

    [Fact]
    public void ProjeListele_reports_empty_and_summarizes()
    {
        Assert.Equal("Proje yok.", _tools.ProjeListele());

        _tools.ProjeEkle("Harika Proje", _f.ProjectDir);
        var result = _tools.ProjeListele();
        Assert.Contains("[#1] Harika Proje", result);
        Assert.Contains(_f.ProjectDir, result);
    }

    [Fact]
    public void ProjeEkle_missing_folder_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.ProjeEkle("x", Path.Combine(_f.Dir, "yok")));
        Assert.StartsWith("Proje klasörü bulunamadı", ex.Message);
    }

    [Fact]
    public void ProjeGuncelle_renames_project()
    {
        _tools.ProjeEkle("Eski Ad", _f.ProjectDir);
        var result = _tools.ProjeGuncelle(1, ad: "Yeni Ad");

        Assert.Contains("Yeni Ad", result);
        Assert.Equal("Yeni Ad", _f.Store.Get(1).Name);
    }

    [Fact]
    public void ProjeSil_soft_deletes()
    {
        _tools.ProjeEkle("P", _f.ProjectDir);
        _tools.ProjeSil(1);

        Assert.Equal("Proje yok.", _tools.ProjeListele());
    }

    [Fact]
    public void ProjeSil_unknown_id_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.ProjeSil(999));
        Assert.Equal("Proje bulunamadı: 999", ex.Message);
    }
}
