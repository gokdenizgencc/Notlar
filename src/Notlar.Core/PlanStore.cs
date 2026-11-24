using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>"Planlar": kullanıcının yazdığı, sıralı adımlardan oluşan iş planları. Claude Code bu planları
/// MCP üzerinden okuyup adım adım uygular; her adımın durumu/sonucu burada tutulur. Aynı SQLite dosyasını
/// (AppPaths.DbPath) NoteStore/TaskStore/MemoryStore ile paylaşır.</summary>
public sealed class PlanStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string PlanColumns = "id, title, project_path, status, created_at, updated_at, project_id";
    private const string StepColumns = "id, plan_id, step_order, text, status, result, updated_at, author, project_id, assignee, due_text";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public PlanStore(string dbPath, TimeProvider time, IChangeSignal signal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        // DefaultTimeout: SQLITE_BUSY durumunda 5 sn yeniden dener.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 5 }.ToString();
        _time = time;
        _signal = signal;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS plans (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                title        TEXT    NOT NULL,
                project_path TEXT    NULL,
                project_id   INTEGER NULL,
                status       TEXT    NOT NULL DEFAULT 'taslak',
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL,
                deleted_at   TEXT    NULL
            );
            CREATE TABLE IF NOT EXISTS plan_steps (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                plan_id    INTEGER NOT NULL,
                step_order INTEGER NOT NULL,
                text       TEXT    NOT NULL,
                status     TEXT    NOT NULL DEFAULT 'bekliyor',
                result     TEXT    NULL,
                updated_at TEXT    NOT NULL,
                author     TEXT    NOT NULL DEFAULT 'Ben'
            );
            """;
        cmd.ExecuteNonQuery();

        MigratePlansTable(conn);
        MigrateStepsTable(conn);
        EnsurePlanProjectsTable(conn);

        // İndeksler tablo taşımasından sonra oluşturulur (yeniden oluşturulan tablo eski indeksleri düşürür).
        using var idx = conn.CreateCommand();
        idx.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_plans_live ON plans(deleted_at, updated_at);
            CREATE INDEX IF NOT EXISTS ix_plan_steps_plan ON plan_steps(plan_id, step_order);
            CREATE INDEX IF NOT EXISTS ix_plan_projects_plan ON plan_projects(plan_id, position);
            """;
        idx.ExecuteNonQuery();
    }

    /// <summary>Eski veritabanlarında plan_steps.author (adımı kimin eklediği), project_id (adımın bağlı
    /// olduğu proje), assignee (işin verildiği kişi) veya due_text (son tarih) sütunu yoksa ekler; mevcut
    /// adımlar kullanıcıya ('Ben'), projesiz, sorumlusuz ve son tarihsiz (NULL) kalır. İdempotenttir.</summary>
    private static void MigrateStepsTable(SqliteConnection conn)
    {
        var hasAuthor = false;
        var hasProjectId = false;
        var hasAssignee = false;
        var hasDueText = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(plan_steps)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                hasAuthor |= string.Equals(name, "author", StringComparison.OrdinalIgnoreCase);
                hasProjectId |= string.Equals(name, "project_id", StringComparison.OrdinalIgnoreCase);
                hasAssignee |= string.Equals(name, "assignee", StringComparison.OrdinalIgnoreCase);
                hasDueText |= string.Equals(name, "due_text", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!hasAssignee)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE plan_steps ADD COLUMN assignee TEXT NULL";
            alter.ExecuteNonQuery();
        }
        if (!hasDueText)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE plan_steps ADD COLUMN due_text TEXT NULL";
            alter.ExecuteNonQuery();
        }

        if (!hasAuthor)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE plan_steps ADD COLUMN author TEXT NOT NULL DEFAULT 'Ben'";
            alter.ExecuteNonQuery();
        }
        if (!hasProjectId)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE plan_steps ADD COLUMN project_id INTEGER NULL";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>plan_projects tablosunu (bir planın bağlı olduğu, sıralı proje listesi) yoksa oluşturur ve
    /// eski plans.project_id değerlerini (varsa) position 0 olarak bu tabloya taşır. İdempotenttir: zaten
    /// plan_projects'te satırı olan bir planı yeniden işlemez.</summary>
    private static void EnsurePlanProjectsTable(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS plan_projects (
                    plan_id    INTEGER NOT NULL,
                    project_id INTEGER NOT NULL,
                    position   INTEGER NOT NULL,
                    PRIMARY KEY (plan_id, project_id)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        using var migrate = conn.CreateCommand();
        migrate.CommandText = """
            INSERT INTO plan_projects (plan_id, project_id, position)
            SELECT id, project_id, 0 FROM plans
            WHERE project_id IS NOT NULL
              AND id NOT IN (SELECT plan_id FROM plan_projects)
            """;
        migrate.ExecuteNonQuery();
    }

    /// <summary>Verilen id'lerin canlı (silinmemiş) projeler tablosunda var olduğunu doğrular; yoksa (veya
    /// projects tablosu henüz oluşturulmamışsa) <see cref="ProjectNotFoundException"/> fırlatır.</summary>
    private static void EnsureProjectsExist(SqliteConnection conn, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;

        using (var tableCheck = conn.CreateCommand())
        {
            tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='projects'";
            if (Convert.ToInt32(tableCheck.ExecuteScalar()) == 0)
                throw new ProjectNotFoundException(ids[0]);
        }

        foreach (var id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM projects WHERE id = $id AND deleted_at IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                throw new ProjectNotFoundException(id);
        }
    }

    private static void InsertPlanProjectsInTx(SqliteConnection conn, SqliteTransaction tx, long planId, IReadOnlyList<long> projectIds)
    {
        for (var i = 0; i < projectIds.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO plan_projects(plan_id, project_id, position) VALUES ($planId, $projectId, $position)";
            cmd.Parameters.AddWithValue("$planId", planId);
            cmd.Parameters.AddWithValue("$projectId", projectIds[i]);
            cmd.Parameters.AddWithValue("$position", i);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Bir adımın bağlı olduğu proje, o anda planın proje listesinde değilse sona ekler (ilk eklenen
    /// proje ana proje olacağından plans.project_id de buna göre güncellenir).</summary>
    private static void EnsureStepProjectIsInPlanInTx(SqliteConnection conn, SqliteTransaction tx, long planId, long projectId)
    {
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM plan_projects WHERE plan_id = $planId AND project_id = $projectId";
            check.Parameters.AddWithValue("$planId", planId);
            check.Parameters.AddWithValue("$projectId", projectId);
            if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
        }

        int nextPosition;
        using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM plan_projects WHERE plan_id = $planId";
            count.Parameters.AddWithValue("$planId", planId);
            nextPosition = Convert.ToInt32(count.ExecuteScalar());
        }

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO plan_projects(plan_id, project_id, position) VALUES ($planId, $projectId, $position)";
            insert.Parameters.AddWithValue("$planId", planId);
            insert.Parameters.AddWithValue("$projectId", projectId);
            insert.Parameters.AddWithValue("$position", nextPosition);
            insert.ExecuteNonQuery();
        }

        if (nextPosition == 0)
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE plans SET project_id = $projectId WHERE id = $id";
            upd.Parameters.AddWithValue("$id", planId);
            upd.Parameters.AddWithValue("$projectId", projectId);
            upd.ExecuteNonQuery();
        }
    }

    private static List<long> ResolveProjectIds(long? projectId, IReadOnlyList<long>? projectIds)
    {
        if (projectIds is { Count: > 0 })
            return projectIds.Distinct().ToList();
        if (projectId is { } id)
            return [id];
        return [];
    }

    /// <summary>Bir planın bağlı olduğu projelerin id'lerini sırayla döndürür (ilki ana proje). Boş liste:
    /// plana hiç proje bağlı değil (ör. yalnızca <see cref="Plan.ProjectPath"/> ile bağlı olabilir).</summary>
    public IReadOnlyList<long> GetPlanProjects(long planId)
    {
        _ = GetPlanOnly(planId) ?? throw new PlanNotFoundException(planId);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM plan_projects WHERE plan_id = $planId ORDER BY position";
        cmd.Parameters.AddWithValue("$planId", planId);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>Bir planın bağlı olduğu proje listesini değiştirir: tekrarlar elenir, her id'nin var olan
    /// (canlı) bir proje olması doğrulanır, ilki ana proje olur (plans.project_id de buna göre güncellenir).
    /// Boş liste verilirse plan projesiz kalır.</summary>
    public void SetPlanProjects(long planId, IReadOnlyList<long> projectIds)
    {
        _ = GetPlanOnly(planId) ?? throw new PlanNotFoundException(planId);
        var ids = projectIds.Distinct().ToList();

        using var conn = Open();
        EnsureProjectsExist(conn, ids);

        using (var tx = conn.BeginTransaction())
        {
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM plan_projects WHERE plan_id = $planId";
                del.Parameters.AddWithValue("$planId", planId);
                del.ExecuteNonQuery();
            }

            InsertPlanProjectsInTx(conn, tx, planId, ids);

            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE plans SET project_id = $projectId, updated_at = $now WHERE id = $id";
                upd.Parameters.AddWithValue("$id", planId);
                upd.Parameters.AddWithValue("$projectId", ids.Count > 0 ? ids[0] : (object)DBNull.Value);
                upd.Parameters.AddWithValue("$now", Now());
                upd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        _signal.Notify();
    }

    /// <summary>Eski veritabanlarını yeni şemaya taşır: project_id sütunu yoksa ekler; project_path hâlâ
    /// NOT NULL ise (SQLite ALTER COLUMN desteklemediğinden) tabloyu yeniden oluşturup veriyi kopyalar.
    /// İdempotenttir: her iki adım da zaten uygulanmışsa hiçbir şey yapmaz.</summary>
    private static void MigratePlansTable(SqliteConnection conn)
    {
        var columns = new List<(string Name, bool NotNull)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(plans)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add((reader.GetString(1), reader.GetInt32(3) == 1));
        }

        if (!columns.Any(c => c.Name == "project_id"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE plans ADD COLUMN project_id INTEGER NULL";
            cmd.ExecuteNonQuery();
        }

        var projectPathNotNull = columns.FirstOrDefault(c => c.Name == "project_path").NotNull;
        if (!projectPathNotNull) return;

        using var tx = conn.BeginTransaction();
        void Exec(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec("""
            CREATE TABLE plans_new (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                title        TEXT    NOT NULL,
                project_path TEXT    NULL,
                project_id   INTEGER NULL,
                status       TEXT    NOT NULL DEFAULT 'taslak',
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL,
                deleted_at   TEXT    NULL
            )
            """);
        Exec("""
            INSERT INTO plans_new (id, title, project_path, project_id, status, created_at, updated_at, deleted_at)
            SELECT id, title, project_path, project_id, status, created_at, updated_at, deleted_at FROM plans
            """);
        Exec("DROP TABLE plans");
        Exec("ALTER TABLE plans_new RENAME TO plans");
        tx.Commit();
    }

    /// <summary><paramref name="author"/>: adımları ekleyen ("Ben" = kullanıcı; MCP üzerinden ise MCP yazarı).
    /// <paramref name="projectIds"/> verilirse (tekrarlar elenerek) planın bağlı olduğu proje listesi olur;
    /// ilki ana proje olur ve <paramref name="projectId"/>'den önceliklidir. İkisi de verilmezse plan
    /// projesiz (yalnızca <paramref name="projectPath"/> ile, varsa) oluşturulur.</summary>
    public Plan CreatePlan(
        string title, string? projectPath, IEnumerable<string> steps, long? projectId = null,
        string author = NoteAuthor.Ben, IReadOnlyList<long>? projectIds = null) =>
        CreatePlanFromItems(title, projectPath, steps.Select(s => new PlanDraftStep(s)), projectId, author, projectIds);

    /// <summary><see cref="CreatePlan"/> gibi; ancak her adım sorumlusu ve son tarihiyle birlikte verilir
    /// (bkz. <see cref="PlanDraftStep"/>; ikisi de <see cref="PlanAssignees"/> ile normalleştirilir).</summary>
    public Plan CreatePlanFromItems(
        string title, string? projectPath, IEnumerable<PlanDraftStep> items, long? projectId = null,
        string author = NoteAuthor.Ben, IReadOnlyList<long>? projectIds = null)
    {
        var stepAuthor = NoteAuthor.Normalize(author);
        var cleanTitle = ValidateTitle(title);
        var cleanPath = projectPath is null ? null : ValidateProjectPath(projectPath);
        var stepItems = items
            .Select(i => (Text: ValidateStepText(i.Text), Assignee: PlanAssignees.Normalize(i.Assignee),
                Due: PlanAssignees.NormalizeDue(i.DueText)))
            .ToList();
        var effectiveProjectIds = ResolveProjectIds(projectId, projectIds);
        var now = Now();

        long planId;
        using (var conn = Open())
        {
            EnsureProjectsExist(conn, effectiveProjectIds);

            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO plans(title, project_path, project_id, status, created_at, updated_at)
                    VALUES ($title, $path, $projectId, 'taslak', $now, $now) RETURNING id
                    """;
                cmd.Parameters.AddWithValue("$title", cleanTitle);
                cmd.Parameters.AddWithValue("$path", (object?)cleanPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$projectId", effectiveProjectIds.Count > 0 ? effectiveProjectIds[0] : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                planId = (long)cmd.ExecuteScalar()!;
            }

            var order = 1;
            foreach (var item in stepItems)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO plan_steps(plan_id, step_order, text, status, updated_at, author, assignee, due_text)
                    VALUES ($planId, $order, $text, 'bekliyor', $now, $author, $assignee, $due)
                    """;
                cmd.Parameters.AddWithValue("$author", stepAuthor);
                cmd.Parameters.AddWithValue("$planId", planId);
                cmd.Parameters.AddWithValue("$order", order++);
                cmd.Parameters.AddWithValue("$text", item.Text);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$assignee", (object?)item.Assignee ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$due", (object?)item.Due ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            if (effectiveProjectIds.Count > 0)
                InsertPlanProjectsInTx(conn, tx, planId, effectiveProjectIds);

            tx.Commit();
        }
        _signal.Notify();
        return GetPlanOnly(planId)!;
    }

    public (Plan Plan, IReadOnlyList<PlanStep> Steps) GetPlan(long id)
    {
        var plan = GetPlanOnly(id) ?? throw new PlanNotFoundException(id);
        return (plan, GetSteps(id));
    }

    public IReadOnlyList<Plan> ListPlans()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {PlanColumns} FROM plans WHERE deleted_at IS NULL ORDER BY updated_at DESC";
        var plans = new List<Plan>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            plans.Add(MapPlan(reader));
        return plans;
    }

    public Plan UpdatePlan(
        long id,
        string? title = null,
        string? projectPath = null,
        long? projectId = null,
        bool clearProject = false)
    {
        var current = GetPlanOnly(id) ?? throw new PlanNotFoundException(id);
        var newTitle = title is null ? current.Title : ValidateTitle(title);

        string? newPath;
        long? newProjectId;
        if (clearProject)
        {
            newPath = null;
            newProjectId = null;
        }
        else
        {
            newPath = projectPath is null ? current.ProjectPath : ValidateProjectPath(projectPath);
            newProjectId = projectId ?? current.ProjectId;
        }

        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE plans SET title = $title, project_path = $path, project_id = $projectId, updated_at = $now
                WHERE id = $id AND deleted_at IS NULL
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$title", newTitle);
            cmd.Parameters.AddWithValue("$path", (object?)newPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$projectId", (object?)newProjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Now());
            if (cmd.ExecuteNonQuery() == 0)
                throw new PlanNotFoundException(id);
        }
        _signal.Notify();
        return GetPlanOnly(id)!;
    }

    public void Delete(long id)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE plans SET deleted_at = $now WHERE id = $id AND deleted_at IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Now());
            if (cmd.ExecuteNonQuery() == 0)
                throw new PlanNotFoundException(id);
        }
        _signal.Notify();
    }

    public Plan Restore(long id)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE plans SET deleted_at = NULL WHERE id = $id AND deleted_at IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0)
                throw new PlanNotFoundException(id);
        }
        _signal.Notify();
        return GetPlanOnly(id)!;
    }

    /// <summary><paramref name="projectId"/> verilirse adım o projeye bağlanır; proje planın proje
    /// listesinde yoksa otomatik olarak sona eklenir (ilk proje ise ana proje olur). <paramref name="assignee"/>
    /// / <paramref name="dueText"/>: isteğe bağlı sorumlu ve son tarih (<see cref="PlanAssignees"/> ile
    /// normalleştirilir).</summary>
    public PlanStep AddStep(long planId, string text, int? position = null, string author = NoteAuthor.Ben, long? projectId = null,
        string? assignee = null, string? dueText = null)
    {
        _ = GetPlanOnly(planId) ?? throw new PlanNotFoundException(planId);
        var clean = ValidateStepText(text);
        var cleanAssignee = PlanAssignees.Normalize(assignee);
        var cleanDue = PlanAssignees.NormalizeDue(dueText);
        var now = Now();

        long stepId;
        using (var conn = Open())
        {
            if (projectId is { } pid)
                EnsureProjectsExist(conn, [pid]);

            using var tx = conn.BeginTransaction();

            if (projectId is { } pid2)
                EnsureStepProjectIsInPlanInTx(conn, tx, planId, pid2);

            var count = CountSteps(conn, tx, planId);
            var insertAt = position is { } p ? Math.Clamp(p, 1, count + 1) : count + 1;

            using (var shift = conn.CreateCommand())
            {
                shift.Transaction = tx;
                shift.CommandText = "UPDATE plan_steps SET step_order = step_order + 1 WHERE plan_id = $planId AND step_order >= $at";
                shift.Parameters.AddWithValue("$planId", planId);
                shift.Parameters.AddWithValue("$at", insertAt);
                shift.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO plan_steps(plan_id, step_order, text, status, updated_at, author, project_id, assignee, due_text)
                    VALUES ($planId, $order, $text, 'bekliyor', $now, $author, $projectId, $assignee, $due) RETURNING id
                    """;
                cmd.Parameters.AddWithValue("$assignee", (object?)cleanAssignee ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$due", (object?)cleanDue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$author", NoteAuthor.Normalize(author));
                cmd.Parameters.AddWithValue("$planId", planId);
                cmd.Parameters.AddWithValue("$order", insertAt);
                cmd.Parameters.AddWithValue("$text", clean);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
                stepId = (long)cmd.ExecuteScalar()!;
            }

            tx.Commit();
        }
        _signal.Notify();
        return GetStep(stepId)!;
    }

    /// <summary>Verilen notlardan plana yeni adımlar ekler: her adımın metni, notun metni tek satıra
    /// indirgenmiş (bkz. <see cref="TextSafety.Flatten"/>) hâlidir. Adımlar notların sırasıyla sona eklenir.</summary>
    public IReadOnlyList<PlanStep> AddStepsFromNotes(long planId, IEnumerable<Note> notes, string author = NoteAuthor.Ben, long? projectId = null)
    {
        var added = new List<PlanStep>();
        foreach (var note in notes)
            added.Add(AddStep(planId, TextSafety.Flatten(note.Text), position: null, author: author, projectId: projectId));
        return added;
    }

    /// <summary>Bir adımın bağlı olduğu projeyi değiştirir; null verilirse adım projesiz kalır. Verilen
    /// proje planın proje listesinde yoksa otomatik olarak sona eklenir.</summary>
    public PlanStep SetStepProject(long stepId, long? projectId)
    {
        using var conn = Open();

        long planId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT plan_id FROM plan_steps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            var result = cmd.ExecuteScalar() ?? throw new PlanStepNotFoundException(stepId);
            planId = (long)result;
        }

        if (projectId is { } pid)
            EnsureProjectsExist(conn, [pid]);

        using (var tx = conn.BeginTransaction())
        {
            if (projectId is { } pid2)
                EnsureStepProjectIsInPlanInTx(conn, tx, planId, pid2);

            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE plan_steps SET project_id = $projectId, updated_at = $now WHERE id = $id";
                upd.Parameters.AddWithValue("$id", stepId);
                upd.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
                upd.Parameters.AddWithValue("$now", Now());
                upd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        _signal.Notify();
        return GetStep(stepId)!;
    }

    /// <summary>Bir adımın sorumlusunu ve son tarihini değiştirir; boş/null verilen alan temizlenir
    /// (<see cref="PlanAssignees"/> ile normalleştirilir).</summary>
    public PlanStep SetStepAssignee(long stepId, string? assignee, string? dueText = null)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE plan_steps SET assignee = $assignee, due_text = $due, updated_at = $now WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            cmd.Parameters.AddWithValue("$assignee", (object?)PlanAssignees.Normalize(assignee) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$due", (object?)PlanAssignees.NormalizeDue(dueText) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Now());
            if (cmd.ExecuteNonQuery() == 0)
                throw new PlanStepNotFoundException(stepId);
        }
        _signal.Notify();
        return GetStep(stepId)!;
    }

    public PlanStep UpdateStep(long stepId, string text)
    {
        var clean = ValidateStepText(text);
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE plan_steps SET text = $text, updated_at = $now WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            cmd.Parameters.AddWithValue("$text", clean);
            cmd.Parameters.AddWithValue("$now", Now());
            if (cmd.ExecuteNonQuery() == 0)
                throw new PlanStepNotFoundException(stepId);
        }
        _signal.Notify();
        return GetStep(stepId)!;
    }

    public PlanStep MoveStep(long stepId, int newOrder)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        long planId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT plan_id FROM plan_steps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            var result = cmd.ExecuteScalar() ?? throw new PlanStepNotFoundException(stepId);
            planId = (long)result;
        }

        var steps = GetStepsInTx(conn, tx, planId);
        var moving = steps.First(s => s.Id == stepId);
        var ordered = steps.Where(s => s.Id != stepId).ToList();
        var target = Math.Clamp(newOrder, 1, steps.Count) - 1;
        ordered.Insert(target, moving);

        RenumberInTx(conn, tx, ordered);
        tx.Commit();

        _signal.Notify();
        return GetStep(stepId)!;
    }

    public void DeleteStep(long stepId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        long planId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT plan_id FROM plan_steps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            var result = cmd.ExecuteScalar() ?? throw new PlanStepNotFoundException(stepId);
            planId = (long)result;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM plan_steps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            cmd.ExecuteNonQuery();
        }

        var remaining = GetStepsInTx(conn, tx, planId);
        RenumberInTx(conn, tx, remaining);
        tx.Commit();
        _signal.Notify();
    }

    public PlanStep SetStepStatus(long stepId, StepStatus status, string? result = null)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        long planId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT plan_id FROM plan_steps WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            var found = cmd.ExecuteScalar() ?? throw new PlanStepNotFoundException(stepId);
            planId = (long)found;
        }

        var now = Now();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE plan_steps SET status = $status, result = $result, updated_at = $now WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", stepId);
            cmd.Parameters.AddWithValue("$status", ToText(status));
            cmd.Parameters.AddWithValue("$result", (object?)result?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        DerivePlanStatusInTx(conn, tx, planId, now);
        tx.Commit();
        _signal.Notify();
        return GetStep(stepId)!;
    }

    public Plan ResetPlan(long planId)
    {
        _ = GetPlanOnly(planId) ?? throw new PlanNotFoundException(planId);
        var now = Now();

        using (var conn = Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE plan_steps SET status = 'bekliyor', result = NULL, updated_at = $now WHERE plan_id = $planId";
                cmd.Parameters.AddWithValue("$planId", planId);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE plans SET status = 'taslak', updated_at = $now WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", planId);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _signal.Notify();
        return GetPlanOnly(planId)!;
    }

    private static int CountSteps(SqliteConnection conn, SqliteTransaction tx, long planId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM plan_steps WHERE plan_id = $planId";
        cmd.Parameters.AddWithValue("$planId", planId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<PlanStep> GetStepsInTx(SqliteConnection conn, SqliteTransaction tx, long planId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {StepColumns} FROM plan_steps WHERE plan_id = $planId ORDER BY step_order";
        cmd.Parameters.AddWithValue("$planId", planId);
        var steps = new List<PlanStep>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            steps.Add(MapStep(reader));
        return steps;
    }

    private static void RenumberInTx(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<PlanStep> orderedSteps)
    {
        for (var i = 0; i < orderedSteps.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE plan_steps SET step_order = $order WHERE id = $id";
            cmd.Parameters.AddWithValue("$order", i + 1);
            cmd.Parameters.AddWithValue("$id", orderedSteps[i].Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Adım durumları değiştikten sonra plan durumunu türetir: herhangi biri "yapılıyor" ise
    /// "çalışıyor"; herhangi biri "hata" ise "hata"; tümü "bitti"/"atlandı" ise "bitti"; aksi halde değişmez
    /// (plan "taslak" kalır, ta ki ilk adım başlayana kadar).</summary>
    private static void DerivePlanStatusInTx(SqliteConnection conn, SqliteTransaction tx, long planId, string now)
    {
        var steps = GetStepsInTx(conn, tx, planId);
        if (steps.Count == 0) return;

        string? newStatus = null;
        if (steps.Any(s => s.Status == StepStatus.Hata))
            newStatus = "hata";
        else if (steps.Any(s => s.Status == StepStatus.Yapiliyor))
            newStatus = "calisiyor";
        else if (steps.All(s => s.Status is StepStatus.Bitti or StepStatus.Atlandi))
            newStatus = "bitti";

        if (newStatus is null) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE plans SET status = $status, updated_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", newStatus);
        cmd.Parameters.AddWithValue("$id", planId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    private Plan? GetPlanOnly(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {PlanColumns} FROM plans WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapPlan(reader) : null;
    }

    private IReadOnlyList<PlanStep> GetSteps(long planId)
    {
        using var conn = Open();
        return GetStepsInTxNoTransaction(conn, planId);
    }

    private static List<PlanStep> GetStepsInTxNoTransaction(SqliteConnection conn, long planId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {StepColumns} FROM plan_steps WHERE plan_id = $planId ORDER BY step_order";
        cmd.Parameters.AddWithValue("$planId", planId);
        var steps = new List<PlanStep>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            steps.Add(MapStep(reader));
        return steps;
    }

    private PlanStep? GetStep(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {StepColumns} FROM plan_steps WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapStep(reader) : null;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string ValidateTitle(string? title)
    {
        var clean = title?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Plan başlığı boş olamaz.");
        return clean;
    }

    private static string ValidateStepText(string? text)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Adım metni boş olamaz.");
        return clean;
    }

    private static string ValidateProjectPath(string? path) => PlanLauncher.ValidateFolder(path);

    public static StepStatus ParseStepStatus(string value)
    {
        var normalized = value.Trim().ToLowerInvariant()
            .Replace("ı", "i").Replace("ş", "s").Replace("ç", "c")
            .Replace("ö", "o").Replace("ü", "u").Replace("ğ", "g");
        return normalized switch
        {
            "bekliyor" => StepStatus.Bekliyor,
            "yapiliyor" => StepStatus.Yapiliyor,
            "bitti" => StepStatus.Bitti,
            "hata" => StepStatus.Hata,
            "atlandi" => StepStatus.Atlandi,
            _ => throw new ArgumentException($"Geçersiz durum: {value}. Beklenen: bekliyor, yapiliyor, bitti, hata, atlandi."),
        };
    }

    private static string ToText(StepStatus status) => status switch
    {
        StepStatus.Bekliyor => "bekliyor",
        StepStatus.Yapiliyor => "yapiliyor",
        StepStatus.Bitti => "bitti",
        StepStatus.Hata => "hata",
        StepStatus.Atlandi => "atlandi",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static StepStatus ParseStepStatusDb(string value) => value switch
    {
        "bekliyor" => StepStatus.Bekliyor,
        "yapiliyor" => StepStatus.Yapiliyor,
        "bitti" => StepStatus.Bitti,
        "hata" => StepStatus.Hata,
        "atlandi" => StepStatus.Atlandi,
        _ => throw new InvalidOperationException($"Bilinmeyen adım durumu: {value}"),
    };

    private static PlanStatus ParsePlanStatusDb(string value) => value switch
    {
        "taslak" => PlanStatus.Taslak,
        "calisiyor" => PlanStatus.Calisiyor,
        "bitti" => PlanStatus.Bitti,
        "hata" => PlanStatus.Hata,
        _ => throw new InvalidOperationException($"Bilinmeyen plan durumu: {value}"),
    };

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static Plan MapPlan(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        ParsePlanStatusDb(r.GetString(3)),
        Parse(r.GetString(4)),
        Parse(r.GetString(5)),
        r.IsDBNull(6) ? null : r.GetInt64(6));

    private static PlanStep MapStep(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetInt64(1),
        r.GetInt32(2),
        r.GetString(3),
        ParseStepStatusDb(r.GetString(4)),
        r.IsDBNull(5) ? null : r.GetString(5),
        Parse(r.GetString(6)),
        r.GetString(7),
        r.IsDBNull(8) ? null : r.GetInt64(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10));
}
