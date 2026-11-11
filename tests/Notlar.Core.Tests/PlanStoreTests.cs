using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class PlanFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public string ProjectDir { get; }
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public PlanStore Store { get; }

    public PlanFixture()
    {
        ProjectDir = Path.Combine(Dir, "proje");
        Directory.CreateDirectory(ProjectDir);
        Store = new PlanStore(DbPath, Time, Signal);
    }

    public PlanStore Reopen() => new(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class PlanStoreTests
{
    private static readonly TimeSpan Tr = PlanFixture.Tr;

    [Fact]
    public void CreatePlan_stores_title_path_steps_in_order_and_signals()
    {
        using var f = new PlanFixture();

        var plan = f.Store.CreatePlan("  Yeni özellik  ", f.ProjectDir, ["önce şunu yap", "sonra şunu yap"]);

        Assert.Equal("Yeni özellik", plan.Title);
        Assert.Equal(f.ProjectDir, plan.ProjectPath);
        Assert.Equal(PlanStatus.Taslak, plan.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr), plan.CreatedAt);
        Assert.Equal(1, f.Signal.Count);

        var (gotPlan, steps) = f.Store.GetPlan(plan.Id);
        Assert.Equal(plan.Id, gotPlan.Id);
        Assert.Equal(["önce şunu yap", "sonra şunu yap"], steps.Select(s => s.Text));
        Assert.Equal([1, 2], steps.Select(s => s.Order));
        Assert.All(steps, s => Assert.Equal(StepStatus.Bekliyor, s.Status));
    }

    [Fact]
    public void CreatePlan_rejects_empty_title()
    {
        using var f = new PlanFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.CreatePlan("  ", f.ProjectDir, ["adım"]));
        Assert.Equal("Plan başlığı boş olamaz.", ex.Message);
    }

    [Fact]
    public void CreatePlan_rejects_empty_step_text()
    {
        using var f = new PlanFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.CreatePlan("plan", f.ProjectDir, ["adım 1", "  "]));
        Assert.Equal("Adım metni boş olamaz.", ex.Message);
    }

    [Fact]
    public void CreatePlan_rejects_missing_project_folder()
    {
        using var f = new PlanFixture();
        var missing = Path.Combine(f.Dir, "yok-boyle-bir-klasor");
        var ex = Assert.Throws<ArgumentException>(() => f.Store.CreatePlan("plan", missing, ["adım"]));
        Assert.Equal($"Proje klasörü bulunamadı: {missing}", ex.Message);
    }

    [Fact]
    public void GetPlan_unknown_id_throws()
    {
        using var f = new PlanFixture();
        Assert.Throws<PlanNotFoundException>(() => f.Store.GetPlan(999));
    }

    [Fact]
    public void ListPlans_returns_live_plans()
    {
        using var f = new PlanFixture();
        var a = f.Store.CreatePlan("a", f.ProjectDir, ["x"]);
        var b = f.Store.CreatePlan("b", f.ProjectDir, ["x"]);
        f.Store.Delete(a.Id);

        var list = f.Store.ListPlans();
        Assert.Single(list);
        Assert.Equal(b.Id, list[0].Id);
    }

    [Fact]
    public void UpdatePlan_partial_keeps_other_fields()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("eski", f.ProjectDir, ["x"]);

        f.Time.Advance(TimeSpan.FromMinutes(5));
        var updated = f.Store.UpdatePlan(plan.Id, title: "yeni");

        Assert.Equal("yeni", updated.Title);
        Assert.Equal(f.ProjectDir, updated.ProjectPath);
        Assert.True(updated.UpdatedAt > plan.CreatedAt);
    }

    [Fact]
    public void Delete_hides_plan_and_Restore_brings_it_back()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("x", f.ProjectDir, ["a"]);

        f.Store.Delete(plan.Id);
        Assert.Empty(f.Store.ListPlans());
        Assert.Throws<PlanNotFoundException>(() => f.Store.Delete(plan.Id));

        var restored = f.Store.Restore(plan.Id);
        Assert.Equal("x", restored.Title);
        Assert.Single(f.Store.ListPlans());
    }

    [Fact]
    public void AddStep_appends_by_default_and_inserts_at_position()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a", "b"]);

        f.Store.AddStep(plan.Id, "c");
        f.Store.AddStep(plan.Id, "z", position: 1);

        var (_, steps) = f.Store.GetPlan(plan.Id);
        Assert.Equal(["z", "a", "b", "c"], steps.Select(s => s.Text));
        Assert.Equal([1, 2, 3, 4], steps.Select(s => s.Order));
    }

    [Fact]
    public void AddStep_rejects_empty_text()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a"]);
        var ex = Assert.Throws<ArgumentException>(() => f.Store.AddStep(plan.Id, "  "));
        Assert.Equal("Adım metni boş olamaz.", ex.Message);
    }

    [Fact]
    public void UpdateStep_changes_text()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a"]);
        var (_, steps) = f.Store.GetPlan(plan.Id);

        var updated = f.Store.UpdateStep(steps[0].Id, "yeni metin");
        Assert.Equal("yeni metin", updated.Text);
    }

    [Fact]
    public void UpdateStep_unknown_id_throws()
    {
        using var f = new PlanFixture();
        Assert.Throws<PlanStepNotFoundException>(() => f.Store.UpdateStep(999, "x"));
    }

    [Fact]
    public void MoveStep_renumbers_densely()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a", "b", "c"]);
        var (_, steps) = f.Store.GetPlan(plan.Id);

        f.Store.MoveStep(steps[2].Id, newOrder: 1);

        var (_, after) = f.Store.GetPlan(plan.Id);
        Assert.Equal(["c", "a", "b"], after.Select(s => s.Text));
        Assert.Equal([1, 2, 3], after.Select(s => s.Order));
    }

    [Fact]
    public void DeleteStep_removes_and_renumbers()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a", "b", "c"]);
        var (_, steps) = f.Store.GetPlan(plan.Id);

        f.Store.DeleteStep(steps[0].Id);

        var (_, after) = f.Store.GetPlan(plan.Id);
        Assert.Equal(["b", "c"], after.Select(s => s.Text));
        Assert.Equal([1, 2], after.Select(s => s.Order));
    }

    [Fact]
    public void SetStepStatus_stores_result_and_derives_plan_status_calisiyor()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a", "b"]);
        var (_, steps) = f.Store.GetPlan(plan.Id);

        f.Store.SetStepStatus(steps[0].Id, StepStatus.Yapiliyor);

        var (updatedPlan, after) = f.Store.GetPlan(plan.Id);
        Assert.Equal(PlanStatus.Calisiyor, updatedPlan.Status);
        Assert.Equal(StepStatus.Yapiliyor, after[0].Status);
    }

    [Fact]
    public void SetStepStatus_hata_marks_plan_hata()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a", "b"]);
        var (_, steps) = f.Store.GetPlan(plan.Id);

        f.Store.SetStepStatus(steps[0].Id, StepStatus.Bitti, "tamam");
        f.Store.SetStepStatus(steps[1].Id, StepStatus.Hata, "başarısız oldu");

        var (updatedPlan, after) = f.Store.GetPlan(plan.Id);
        Assert.Equal(PlanStatus.Hata, updatedPlan.Status);
        Assert.Equal("başarısız oldu", after[1].Result);
    }

    [Fact]
    public void SetStepStatus_all_done_or_skipped_marks_plan_bitti()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a", "b"]);
        var (_, steps) = f.Store.GetPlan(plan.Id);

        f.Store.SetStepStatus(steps[0].Id, StepStatus.Bitti, "ok");
        f.Store.SetStepStatus(steps[1].Id, StepStatus.Atlandi);

        var (updatedPlan, _) = f.Store.GetPlan(plan.Id);
        Assert.Equal(PlanStatus.Bitti, updatedPlan.Status);
    }

    [Fact]
    public void SetStepStatus_unknown_id_throws()
    {
        using var f = new PlanFixture();
        Assert.Throws<PlanStepNotFoundException>(() => f.Store.SetStepStatus(999, StepStatus.Bitti));
    }

    [Fact]
    public void ResetPlan_clears_steps_and_plan_status()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("plan", f.ProjectDir, ["a", "b"]);
        var (_, steps) = f.Store.GetPlan(plan.Id);
        f.Store.SetStepStatus(steps[0].Id, StepStatus.Bitti, "ok");
        f.Store.SetStepStatus(steps[1].Id, StepStatus.Hata, "kötü");

        var reset = f.Store.ResetPlan(plan.Id);

        Assert.Equal(PlanStatus.Taslak, reset.Status);
        var (_, after) = f.Store.GetPlan(plan.Id);
        Assert.All(after, s =>
        {
            Assert.Equal(StepStatus.Bekliyor, s.Status);
            Assert.Null(s.Result);
        });
    }

    [Fact]
    public void Data_persists_across_instances()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("kalıcı", f.ProjectDir, ["a"]);
        Assert.Equal("kalıcı", f.Reopen().ListPlans().Single(p => p.Id == plan.Id).Title);
    }

    [Fact]
    public void Coexists_with_other_stores_on_same_db_file()
    {
        using var f = new PlanFixture();
        var noteStore = new NoteStore(f.DbPath, f.Time, f.Signal);
        var taskStore = new TaskStore(f.DbPath, f.Time, f.Signal);
        var memStore = new MemoryStore(f.DbPath, f.Time, f.Signal);

        var note = noteStore.Add("bir not");
        var task = taskStore.Add("bir görev", "Claude");
        var mem = memStore.Add("bir hafıza", "içerik");
        var plan = f.Store.CreatePlan("bir plan", f.ProjectDir, ["adım"]);

        Assert.Equal("bir not", noteStore.Get(note.Id)!.Text);
        Assert.Equal("bir görev", taskStore.List().Single(t => t.Id == task.Id).Title);
        Assert.Equal("bir hafıza", memStore.List().Single(m => m.Id == mem.Id).Title);
        Assert.Equal("bir plan", f.Store.ListPlans().Single(p => p.Id == plan.Id).Title);
    }
}
