using Microsoft.Data.Sqlite;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public sealed class PlanAssigneesTests
{
    private static PlanStep Step(int order, string text, string? assignee = null, string? due = null) =>
        new(order, 1, order, text, StepStatus.Bekliyor, null, DateTimeOffset.UnixEpoch, Assignee: assignee, DueText: due);

    // --- Normalize / NormalizeDue ---

    [Theory]
    [InlineData("Ahmet", "Ahmet")]
    [InlineData("  Ayşe:  ", "Ayşe")]
    [InlineData("ben", "Ben")]
    [InlineData("BEN", "Ben")]
    [InlineData("Pazarlama\nekibi", "Pazarlama ekibi")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("null", null)]
    [InlineData("Belirsiz", null)]
    [InlineData("yok", null)]
    [InlineData("-", null)]
    public void Normalize_cleans_names_and_maps_unknowns_to_null(string? input, string? expected)
    {
        Assert.Equal(expected, PlanAssignees.Normalize(input));
    }

    [Fact]
    public void Normalize_truncates_long_names()
    {
        var name = PlanAssignees.Normalize(new string('a', 200))!;
        Assert.Equal(PlanAssignees.MaxLength, name.Length);
    }

    [Theory]
    [InlineData("Cuma", "Cuma")]
    [InlineData(" ay sonu. ", "ay sonu")]
    [InlineData("belirtilmedi", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeDue_keeps_spoken_text_and_maps_unknowns_to_null(string? input, string? expected)
    {
        Assert.Equal(expected, PlanAssignees.NormalizeDue(input));
    }

    // --- Order / ColorSlot / Group ---

    [Fact]
    public void Order_lists_distinct_assignees_by_first_appearance_case_insensitively()
    {
        PlanStep[] steps = [Step(1, "a", "Ayşe"), Step(2, "b"), Step(3, "c", "Ahmet"), Step(4, "d", "ayşe"), Step(5, "e", "Ben")];

        var order = PlanAssignees.Order(steps);

        Assert.Equal(["Ayşe", "Ahmet", "Ben"], order);
        Assert.Equal(1, PlanAssignees.ColorSlot(order, "AYŞE"));
        Assert.Equal(2, PlanAssignees.ColorSlot(order, "Ahmet"));
        Assert.Equal(0, PlanAssignees.ColorSlot(order, null));
        Assert.Equal(0, PlanAssignees.ColorSlot(order, "Zeynep"));
    }

    [Fact]
    public void Group_keeps_first_appearance_order_and_puts_unassigned_last()
    {
        PlanStep[] steps = [Step(1, "a"), Step(2, "b", "Ahmet"), Step(3, "c", "Ayşe"), Step(4, "d", "ahmet"), Step(5, "e")];

        var groups = PlanAssignees.Group(steps);

        Assert.Equal(["Ahmet", "Ayşe", null], groups.Select(g => g.Assignee));
        Assert.Equal(["b", "d"], groups[0].Steps.Select(s => s.Text));
        Assert.Equal(["c"], groups[1].Steps.Select(s => s.Text));
        Assert.Equal(["a", "e"], groups[2].Steps.Select(s => s.Text));
    }

    [Fact]
    public void Group_of_empty_list_is_empty()
    {
        Assert.Empty(PlanAssignees.Group(Array.Empty<PlanStep>()));
        Assert.False(PlanAssignees.AnyAssigned(Array.Empty<PlanStep>()));
    }

    // --- BuildSummary ---

    [Fact]
    public void BuildSummary_lists_who_got_what()
    {
        PlanStep[] steps =
        [
            Step(1, "Sunumu hazırla.", "Ahmet"),
            Step(2, "Teklifi yaz", "Ayşe", "Cuma"),
            Step(3, "Bütçeyi gönder", "Ahmet"),
            Step(4, "Notları paylaş"),
        ];

        Assert.Equal(
            "Ahmet: Sunumu hazırla, Bütçeyi gönder / Ayşe: Teklifi yaz (son tarih: Cuma) / Sorumlusu belirsiz: Notları paylaş",
            PlanAssignees.BuildSummary(steps));
        Assert.True(PlanAssignees.AnyAssigned(steps));
    }

    [Fact]
    public void BuildSummary_of_no_steps_is_empty_and_without_owners_uses_unassigned_label()
    {
        Assert.Equal("", PlanAssignees.BuildSummary(Array.Empty<PlanStep>()));
        Assert.Equal("Sorumlusu belirsiz: a, b", PlanAssignees.BuildSummary([Step(1, "a"), Step(2, "b")]));
    }

    [Fact]
    public void BuildSummary_flattens_multiline_text()
    {
        Assert.Equal("Ben: satır1 satır2", PlanAssignees.BuildSummary([Step(1, "satır1\nsatır2", "ben")]));
    }

    // --- PlanStore: saklama ve şema göçü ---

    [Fact]
    public void CreatePlanFromItems_stores_normalized_assignee_and_due()
    {
        using var f = new PlanFixture();

        var plan = f.Store.CreatePlanFromItems("toplantı", null,
            [new PlanDraftStep("a", "Ayşe", "Cuma"), new PlanDraftStep("b", "ben"), new PlanDraftStep("c", "  ", "yok")],
            author: "Claude");

        var (_, steps) = f.Store.GetPlan(plan.Id);
        Assert.Equal(["Ayşe", "Ben", null], steps.Select(s => s.Assignee));
        Assert.Equal(["Cuma", null, null], steps.Select(s => s.DueText));
        Assert.All(steps, s => Assert.Equal("Claude", s.Author));
    }

    [Fact]
    public void Plain_CreatePlan_and_AddStep_leave_assignee_empty_unless_given()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("p", null, ["a"]);
        f.Store.AddStep(plan.Id, "b");
        f.Store.AddStep(plan.Id, "c", assignee: "Can", dueText: "yarın");

        var (_, steps) = f.Store.GetPlan(plan.Id);
        Assert.Equal([null, null, "Can"], steps.Select(s => s.Assignee));
        Assert.Equal("yarın", steps[2].DueText);
    }

    [Fact]
    public void SetStepAssignee_updates_and_clears_and_signals()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlanFromItems("p", null, [new PlanDraftStep("a", "Ayşe", "Cuma")]);
        var stepId = f.Store.GetPlan(plan.Id).Steps[0].Id;
        var before = f.Signal.Count;

        var changed = f.Store.SetStepAssignee(stepId, "Ahmet", "Pazartesi");
        Assert.Equal(("Ahmet", "Pazartesi"), (changed.Assignee, changed.DueText));
        Assert.True(f.Signal.Count > before);

        var cleared = f.Store.SetStepAssignee(stepId, "", null);
        Assert.Null(cleared.Assignee);
        Assert.Null(cleared.DueText);

        Assert.Throws<PlanStepNotFoundException>(() => f.Store.SetStepAssignee(999, "x"));
    }

    [Fact]
    public void Old_steps_table_without_assignee_columns_is_migrated_and_old_plans_open()
    {
        using var f = new PlanFixture();
        var plan = f.Store.CreatePlan("eski plan", null, ["a", "b"]);

        // Eski şemayı simüle et: assignee / due_text sütunları olmayan plan_steps.
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE plan_steps_old (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    plan_id    INTEGER NOT NULL,
                    step_order INTEGER NOT NULL,
                    text       TEXT    NOT NULL,
                    status     TEXT    NOT NULL DEFAULT 'bekliyor',
                    result     TEXT    NULL,
                    updated_at TEXT    NOT NULL,
                    author     TEXT    NOT NULL DEFAULT 'Ben',
                    project_id INTEGER NULL
                );
                INSERT INTO plan_steps_old (id, plan_id, step_order, text, status, result, updated_at, author, project_id)
                SELECT id, plan_id, step_order, text, status, result, updated_at, author, project_id FROM plan_steps;
                DROP TABLE plan_steps;
                ALTER TABLE plan_steps_old RENAME TO plan_steps;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopened = f.Reopen();
        _ = f.Reopen(); // idempotent

        var (_, steps) = reopened.GetPlan(plan.Id);
        Assert.Equal(["a", "b"], steps.Select(s => s.Text));
        Assert.All(steps, s => Assert.Null(s.Assignee));
        Assert.All(steps, s => Assert.Null(s.DueText));

        reopened.SetStepAssignee(steps[0].Id, "Ayşe");
        Assert.Equal("Ayşe", reopened.GetPlan(plan.Id).Steps[0].Assignee);
    }

    // --- MCP ---

    [Fact]
    public void PlanGetir_and_PlanListele_show_assignees()
    {
        using var f = new PlanFixture();
        var tools = new PlanTools(f.Store, f.Projects, f.Notes);
        f.Store.CreatePlanFromItems("toplantı", null,
            [new PlanDraftStep("Sunumu hazırla", "Ayşe", "Cuma"), new PlanDraftStep("Bütçeyi gönder", "Ben"), new PlanDraftStep("Notları paylaş")],
            author: "Claude");

        var detail = tools.PlanGetir(1);
        Assert.Contains("1. ⏳ Sunumu hazırla [sorumlu: Ayşe · son tarih: Cuma] (ekleyen: Claude)", detail);
        Assert.Contains("2. ⏳ Bütçeyi gönder [sorumlu: Ben] (ekleyen: Claude)", detail);
        Assert.Contains("3. ⏳ Notları paylaş (ekleyen: Claude)", detail);
        Assert.Contains("Kime ne verildi: Ayşe: Sunumu hazırla (son tarih: Cuma) / Ben: Bütçeyi gönder / Sorumlusu belirsiz: Notları paylaş", detail);

        Assert.Contains("0/3 adım — sorumlular: Ayşe, Ben", tools.PlanListele());
    }

    [Fact]
    public void PlanGetir_without_assignees_has_no_assignment_summary()
    {
        using var f = new PlanFixture();
        var tools = new PlanTools(f.Store, f.Projects, f.Notes);
        tools.PlanOlustur("plan", "a\nb");

        Assert.DoesNotContain("Kime ne verildi", tools.PlanGetir(1));
        Assert.DoesNotContain("sorumlular", tools.PlanListele());
    }

    [Fact]
    public void PlanAdimEkle_accepts_optional_owner_and_due()
    {
        using var f = new PlanFixture();
        var tools = new PlanTools(f.Store, f.Projects, f.Notes);
        tools.PlanOlustur("plan", "a");

        var result = tools.PlanAdimEkle(1, "yeni", sorumlu: "Can", son_tarih: "ay sonu");
        tools.PlanAdimEkle(1, "eski çağrı");

        Assert.Contains("sorumlu: Can", result);
        var (_, steps) = f.Store.GetPlan(1);
        Assert.Equal(("Can", "ay sonu"), (steps[1].Assignee, steps[1].DueText));
        Assert.Null(steps[2].Assignee);
    }
}
