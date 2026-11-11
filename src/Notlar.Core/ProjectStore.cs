using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>"Projeler": kullanıcının kayıtlı projelerinin adı ve klasör yolu. Planlar isteğe bağlı olarak
/// buradaki bir projeye bağlanabilir; MCP üzerinden isimle aranabilir. Aynı SQLite dosyasını
/// (AppPaths.DbPath) NoteStore/TaskStore/MemoryStore/PlanStore ile paylaşır.</summary>
public sealed class ProjectStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string Columns = "id, name, path, created_at";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo TrTR = CultureInfo.GetCultureInfo("tr-TR");

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public ProjectStore(string dbPath, TimeProvider time, IChangeSignal signal)
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
            CREATE TABLE IF NOT EXISTS projects (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT    NOT NULL,
                path       TEXT    NOT NULL,
                created_at TEXT    NOT NULL,
                deleted_at TEXT    NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Project Add(string name, string path)
    {
        var cleanName = ValidateName(name);
        var cleanPath = ValidatePath(path);
        EnsureNameNotTaken(cleanName, excludeId: null);
        var now = Now();

        long id;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO projects(name, path, created_at)
                VALUES ($name, $path, $now) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$name", cleanName);
            cmd.Parameters.AddWithValue("$path", cleanPath);
            cmd.Parameters.AddWithValue("$now", now);
            id = (long)cmd.ExecuteScalar()!;
        }
        _signal.Notify();
        return Get(id);
    }

    public Project Update(long id, string? name = null, string? path = null)
    {
        var current = Get(id);
        var newName = name is null ? current.Name : ValidateName(name);
        var newPath = path is null ? current.Path : ValidatePath(path);
        if (name is not null)
            EnsureNameNotTaken(newName, excludeId: id);

        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE projects SET name = $name, path = $path WHERE id = $id AND deleted_at IS NULL
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", newName);
            cmd.Parameters.AddWithValue("$path", newPath);
            if (cmd.ExecuteNonQuery() == 0)
                throw new ProjectNotFoundException(id);
        }
        _signal.Notify();
        return Get(id);
    }

    public void Delete(long id)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE projects SET deleted_at = $now WHERE id = $id AND deleted_at IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Now());
            if (cmd.ExecuteNonQuery() == 0)
                throw new ProjectNotFoundException(id);
        }
        _signal.Notify();
    }

    public Project Restore(long id)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE projects SET deleted_at = NULL WHERE id = $id AND deleted_at IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0)
                throw new ProjectNotFoundException(id);
        }
        _signal.Notify();
        return Get(id);
    }

    public IReadOnlyList<Project> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM projects WHERE deleted_at IS NULL";
        var list = ReadAll(cmd);
        list.Sort((a, b) => TrTR.CompareInfo.Compare(a.Name, b.Name));
        return list;
    }

    public Project Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM projects WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : throw new ProjectNotFoundException(id);
    }

    /// <summary>İsme göre canlı bir projeyi bulur: önce tam (Türkçe, büyük/küçük harf duyarsız) eşleşme,
    /// sonra tekil bir ön-ek/parça eşleşmesi aranır. Birden fazla aday varsa isimlerini listeleyen bir
    /// ArgumentException fırlatılır; hiç eşleşme yoksa ProjectNotFoundException fırlatılır.</summary>
    public Project FindByName(string query)
    {
        var clean = query?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Proje adı boş olamaz.");

        var live = List();

        var exact = live.Where(p => TrTR.CompareInfo.Compare(p.Name, clean, CompareOptions.IgnoreCase) == 0).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw Ambiguous(clean, exact);

        var prefix = live.Where(p => TrTR.CompareInfo.IsPrefix(p.Name, clean, CompareOptions.IgnoreCase)).ToList();
        if (prefix.Count == 1) return prefix[0];
        if (prefix.Count > 1) throw Ambiguous(clean, prefix);

        var contains = live.Where(p => TrTR.CompareInfo.IndexOf(p.Name, clean, CompareOptions.IgnoreCase) >= 0).ToList();
        if (contains.Count == 1) return contains[0];
        if (contains.Count > 1) throw Ambiguous(clean, contains);

        throw new ProjectNotFoundException(clean);
    }

    private static ArgumentException Ambiguous(string query, IReadOnlyList<Project> candidates) =>
        new($"Birden fazla proje \"{query}\" ile eşleşti: {string.Join(", ", candidates.Select(p => p.Name))}. Lütfen daha net belirtin.");

    private void EnsureNameNotTaken(string name, long? excludeId)
    {
        var clash = List().FirstOrDefault(p =>
            (excludeId is null || p.Id != excludeId) &&
            TrTR.CompareInfo.Compare(p.Name, name, CompareOptions.IgnoreCase) == 0);
        if (clash is not null)
            throw new ArgumentException($"Bu isimde bir proje zaten var: {clash.Name}");
    }

    private static List<Project> ReadAll(SqliteCommand cmd)
    {
        var items = new List<Project>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(Map(reader));
        return items;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string ValidateName(string? name)
    {
        var clean = name?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Proje adı boş olamaz.");
        return clean;
    }

    private static string ValidatePath(string? path)
    {
        var clean = path?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Proje klasörü boş olamaz.");
        if (!Directory.Exists(clean))
            throw new ArgumentException($"Proje klasörü bulunamadı: {clean}");
        return clean;
    }

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static Project Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        Parse(r.GetString(3)));
}
