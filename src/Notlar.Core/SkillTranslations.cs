using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>Bir yeteneğin (skill) elle yazılmış kısa Türkçe açıklaması. Orijinal (İngilizce) açıklamanın
/// panelde gösterilecek bir çevirisidir; SKILL.md içeriği değişmez.</summary>
public sealed record SkillTranslation(string SkillId, string DescriptionTr, DateTimeOffset UpdatedAt);

/// <summary>"skill_translations" tablosunu yönetir: skill id'sine göre Türkçe açıklama kaydı. Aynı SQLite
/// dosyasını (AppPaths.DbPath) diğer store'larla paylaşır.</summary>
public sealed class SkillTranslations
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public SkillTranslations(string dbPath, TimeProvider time, IChangeSignal signal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 5 }.ToString();
        _time = time;
        _signal = signal;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS skill_translations (
                skill_id        TEXT PRIMARY KEY,
                description_tr  TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public SkillTranslation Set(string skillId, string descriptionTr)
    {
        var cleanId = ValidateId(skillId);
        var cleanDesc = descriptionTr?.Trim() ?? "";
        if (cleanDesc.Length == 0)
            throw new ArgumentException("Türkçe açıklama boş olamaz.");
        var now = Now();

        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO skill_translations(skill_id, description_tr, updated_at)
                VALUES ($id, $desc, $now)
                ON CONFLICT(skill_id) DO UPDATE SET
                    description_tr = excluded.description_tr,
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$id", cleanId);
            cmd.Parameters.AddWithValue("$desc", cleanDesc);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        _signal.Notify();
        return Get(cleanId)!;
    }

    public SkillTranslation? Get(string skillId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT skill_id, description_tr, updated_at FROM skill_translations WHERE skill_id = $id";
        cmd.Parameters.AddWithValue("$id", skillId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyDictionary<string, SkillTranslation> All()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT skill_id, description_tr, updated_at FROM skill_translations";
        var result = new Dictionary<string, SkillTranslation>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var t = Map(reader);
            result[t.SkillId] = t;
        }
        return result;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string ValidateId(string? skillId)
    {
        var clean = skillId?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Yetenek id'si boş olamaz.");
        return clean;
    }

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static SkillTranslation Map(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), Parse(r.GetString(2)));
}
