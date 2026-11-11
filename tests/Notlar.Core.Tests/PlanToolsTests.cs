using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class PlanToolsTests : IDisposable
{
    private readonly PlanFixture _f = new();
    private readonly PlanTools _tools;

    public PlanToolsTests() => _tools = new PlanTools(_f.Store);

    public void Dispose() => _f.Dispose();

    [Fact]
    public void PlanOlustur_creates_plan_from_newline_separated_steps()
    {
        var result = _tools.PlanOlustur("Yeni özellik", _f.ProjectDir, "önce şunu yap\nsonra şunu yap\n");

        Assert.Contains("#1", result);
        Assert.Contains("2 adım", result);
        var (plan, steps) = _f.Store.GetPlan(1);
        Assert.Equal("Yeni özellik", plan.Title);
        Assert.Equal(2, steps.Count);
    }

    [Fact]
    public void PlanOlustur_missing_project_folder_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.PlanOlustur("x", Path.Combine(_f.Dir, "yok"), "a"));
        Assert.StartsWith("Proje klasörü bulunamadı", ex.Message);
    }

    [Fact]
    public void PlanListele_reports_empty_and_summarizes()
    {
        Assert.Equal("Plan yok.", _tools.PlanListele());

        _tools.PlanOlustur("plan a", _f.ProjectDir, "a1\na2");
        var result = _tools.PlanListele();
        Assert.Contains("[#1 · taslak] plan a", result);
        Assert.Contains("0/2 adım", result);
    }

    [Fact]
    public void PlanGetir_shows_steps_with_icons_and_instructions()
    {
        _tools.PlanOlustur("plan", _f.ProjectDir, "ilk adım\nikinci adım");
        var (_, steps) = _f.Store.GetPlan(1);
        _f.Store.SetStepStatus(steps[0].Id, StepStatus.Bitti, "tamamlandı");

        var result = _tools.PlanGetir(1);

        Assert.Contains("Plan #1: plan", result);
        Assert.Contains("1. ✅ ilk adım — tamamlandı", result);
        Assert.Contains("2. ⏳ ikinci adım", result);
        Assert.Contains("plan_adim_durum", result);
    }

    [Fact]
    public void PlanGetir_unknown_id_throws_McpException()
    {
        var ex = Assert.Throws<McpException>(() => _tools.PlanGetir(999));
        Assert.Equal("Plan bulunamadı: 999", ex.Message);
    }

    [Fact]
    public void PlanAdimDurum_accepts_turkish_variants_and_updates_status()
    {
        _tools.PlanOlustur("plan", _f.ProjectDir, "adım 1");
        var (_, steps) = _f.Store.GetPlan(1);

        var result = _tools.PlanAdimDurum(steps[0].Id, "yapılıyor");
        Assert.Contains("🔄", result);

        _tools.PlanAdimDurum(steps[0].Id, "atlandı", "gerek kalmadı");
        var (_, after) = _f.Store.GetPlan(1);
        Assert.Equal(StepStatus.Atlandi, after[0].Status);
        Assert.Equal("gerek kalmadı", after[0].Result);
    }

    [Fact]
    public void PlanAdimDurum_invalid_status_throws_McpException()
    {
        _tools.PlanOlustur("plan", _f.ProjectDir, "adım 1");
        var (_, steps) = _f.Store.GetPlan(1);

        var ex = Assert.Throws<McpException>(() => _tools.PlanAdimDurum(steps[0].Id, "bilinmeyen"));
        Assert.StartsWith("Geçersiz durum", ex.Message);
    }

    [Fact]
    public void PlanAdimEkle_inserts_at_position()
    {
        _tools.PlanOlustur("plan", _f.ProjectDir, "a\nb");
        var result = _tools.PlanAdimEkle(1, "yeni", 1);

        Assert.Contains("1. yeni", result);
        var (_, steps) = _f.Store.GetPlan(1);
        Assert.Equal(["yeni", "a", "b"], steps.Select(s => s.Text));
    }

    [Fact]
    public void Run_wraps_SqliteException_as_McpException_with_Turkish_message()
    {
        _tools.PlanOlustur("plan", _f.ProjectDir, "a");

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _f.DbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        File.WriteAllBytes(_f.DbPath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<McpException>(() => _tools.PlanListele());
        Assert.StartsWith("Veritabanına erişilemedi", ex.Message);
    }
}
