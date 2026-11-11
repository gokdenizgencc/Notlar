using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>"Planlar": kullanıcının yazdığı, sıralı adımlardan oluşan iş planları. Claude Code bu planları
/// MCP üzerinden okuyup adım adım uygular; her adımın durumu/sonucu burada tutulur. Aynı SQLite dosyasını
/// (AppPaths.DbPath) NoteStore/TaskStore/MemoryStore ile paylaşır.</summary>
public sealed class PlanStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string PlanColumns = "id, title, project_path, status, created_at, updated_at";
    private const string StepColumns = "id, plan_id, step_order, text, status, result, updated_at";
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
                project_path TEXT    NOT NULL,
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
                updated_at TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Plan CreatePlan(string title, string projectPath, IEnumerable<string> steps)
    {
        var cleanTitle = ValidateTitle(title);
        var cleanPath = ValidateProjectPath(projectPath);
        var stepTexts = steps.Select(ValidateStepText).ToList();
        var now = Now();

        long planId;
        using (var conn = Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO plans(title, project_path, status, created_at, updated_at)
                    VALUES ($title, $path, 'taslak', $now, $now) RETURNING id
                    """;
                cmd.Parameters.AddWithValue("$title", cleanTitle);
                cmd.Parameters.AddWithValue("$path", cleanPath);
                cmd.Parameters.AddWithValue("$now", now);
                planId = (long)cmd.ExecuteScalar()!;
            }

            var order = 1;
            foreach (var text in stepTexts)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO plan_steps(plan_id, step_order, text, status, updated_at)
                    VALUES ($planId, $order, $text, 'bekliyor', $now)
                    """;
                cmd.Parameters.AddWithValue("$planId", planId);
                cmd.Parameters.AddWithValue("$order", order++);
                cmd.Parameters.AddWithValue("$text", text);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

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

    public Plan UpdatePlan(long id, string? title = null, string? projectPath = null)
    {
        var current = GetPlanOnly(id) ?? throw new PlanNotFoundException(id);
        var newTitle = title is null ? current.Title : ValidateTitle(title);
        var newPath = projectPath is null ? current.ProjectPath : ValidateProjectPath(projectPath);

        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE plans SET title = $title, project_path = $path, updated_at = $now
                WHERE id = $id AND deleted_at IS NULL
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$title", newTitle);
            cmd.Parameters.AddWithValue("$path", newPath);
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

    public PlanStep AddStep(long planId, string text, int? position = null)
    {
        _ = GetPlanOnly(planId) ?? throw new PlanNotFoundException(planId);
        var clean = ValidateStepText(text);
        var now = Now();

        long stepId;
        using (var conn = Open())
        using (var tx = conn.BeginTransaction())
        {
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
                    INSERT INTO plan_steps(plan_id, step_order, text, status, updated_at)
                    VALUES ($planId, $order, $text, 'bekliyor', $now) RETURNING id
                    """;
                cmd.Parameters.AddWithValue("$planId", planId);
                cmd.Parameters.AddWithValue("$order", insertAt);
                cmd.Parameters.AddWithValue("$text", clean);
                cmd.Parameters.AddWithValue("$now", now);
                stepId = (long)cmd.ExecuteScalar()!;
            }

            tx.Commit();
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

    private static string ValidateProjectPath(string? path)
    {
        var clean = path?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Proje klasörü boş olamaz.");
        if (!Directory.Exists(clean))
            throw new ArgumentException($"Proje klasörü bulunamadı: {clean}");
        return clean;
    }

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
        r.GetString(2),
        ParsePlanStatusDb(r.GetString(3)),
        Parse(r.GetString(4)),
        Parse(r.GetString(5)));

    private static PlanStep MapStep(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetInt64(1),
        r.GetInt32(2),
        r.GetString(3),
        ParseStepStatusDb(r.GetString(4)),
        r.IsDBNull(5) ? null : r.GetString(5),
        Parse(r.GetString(6)));
}
