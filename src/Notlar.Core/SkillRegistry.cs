using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>Manuel kaydedilen (veya başka AI asistanlarının kendi yazdığı) bir yetenek/skill kaydı.</summary>
public sealed record RegistrySkill(long Id, string Ai, string Name, string Description, int UsageCount, DateTimeOffset? LastUsed, DateTimeOffset CreatedAt);

public sealed class SkillNotFoundException(long id) : Exception($"Yetenek kaydı bulunamadı: {id}")
{
    public long Id { get; } = id;
}

/// <summary>Claude Code dışındaki (veya elle eklenen) yapay zekâ yeteneklerinin kataloğu. Aynı SQLite
/// dosyasını (AppPaths.DbPath) diğer store'larla paylaşır.</summary>
public sealed class SkillRegistry
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string Columns = "id, ai, name, description, usage_count, last_used, created_at";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public SkillRegistry(string dbPath, TimeProvider time, IChangeSignal signal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 5 }.ToString();
        _time = time;
        _signal = signal;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS skills (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ai           TEXT    NOT NULL,
                name         TEXT    NOT NULL,
                description  TEXT    NOT NULL DEFAULT '',
                usage_count  INTEGER NOT NULL DEFAULT 0,
                last_used    TEXT    NULL,
                created_at   TEXT    NOT NULL,
                deleted_at   TEXT    NULL,
                UNIQUE(ai, name)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public RegistrySkill Upsert(string ai, string name, string? description = null)
    {
        var cleanAi = NormalizeAi(ai);
        var cleanName = ValidateName(name);
        var cleanDescription = description?.Trim() ?? "";
        var now = Now();

        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO skills(ai, name, description, created_at)
                VALUES ($ai, $name, $description, $now)
                ON CONFLICT(ai, name) DO UPDATE SET
                    description = excluded.description,
                    deleted_at = NULL
                """;
            cmd.Parameters.AddWithValue("$ai", cleanAi);
            cmd.Parameters.AddWithValue("$name", cleanName);
            cmd.Parameters.AddWithValue("$description", cleanDescription);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        _signal.Notify();
        return Get(cleanAi, cleanName)!;
    }

    public RegistrySkill RecordUse(string ai, string name)
    {
        var cleanAi = NormalizeAi(ai);
        var cleanName = ValidateName(name);
        var now = Now();

        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO skills(ai, name, description, usage_count, last_used, created_at)
                VALUES ($ai, $name, '', 1, $now, $now)
                ON CONFLICT(ai, name) DO UPDATE SET
                    usage_count = usage_count + 1,
                    last_used = $now,
                    deleted_at = NULL
                """;
            cmd.Parameters.AddWithValue("$ai", cleanAi);
            cmd.Parameters.AddWithValue("$name", cleanName);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        _signal.Notify();
        return Get(cleanAi, cleanName)!;
    }

    public IReadOnlyList<RegistrySkill> List(string? ai = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = $"SELECT {Columns} FROM skills WHERE deleted_at IS NULL";
        if (ai is { } a)
        {
            sql += " AND ai = $ai";
            cmd.Parameters.AddWithValue("$ai", NormalizeAi(a));
        }
        sql += " ORDER BY ai, usage_count DESC, name";
        cmd.CommandText = sql;

        var items = new List<RegistrySkill>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(Map(reader));
        return items;
    }

    public void Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE skills SET deleted_at = $now WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", Now());
        if (cmd.ExecuteNonQuery() == 0)
            throw new SkillNotFoundException(id);
        _signal.Notify();
    }

    public RegistrySkill Restore(long id)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE skills SET deleted_at = NULL WHERE id = $id AND deleted_at IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0)
                throw new SkillNotFoundException(id);
        }
        _signal.Notify();
        return GetById(id)!;
    }

    private RegistrySkill? Get(string ai, string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM skills WHERE ai = $ai AND name = $name AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$ai", ai);
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private RegistrySkill? GetById(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM skills WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>AI adını yazar normalizasyonuyla aynı kurallarla normalize eder (NoteAuthor.Normalize);
    /// boş olamaz.</summary>
    internal static string NormalizeAi(string? ai)
    {
        if (string.IsNullOrWhiteSpace(ai))
            throw new ArgumentException("Yapay zekâ adı boş olamaz.");
        return NoteAuthor.Normalize(ai);
    }

    private static string ValidateName(string? name)
    {
        var clean = name?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Yetenek adı boş olamaz.");
        return clean;
    }

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static RegistrySkill Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        (int)r.GetInt64(4),
        r.IsDBNull(5) ? null : Parse(r.GetString(5)),
        Parse(r.GetString(6)));
}
