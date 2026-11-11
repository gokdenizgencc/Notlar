using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>Claude'un kullanıcı ile paylaşılan, kalıcı hafızası: kişiler, projeler, tercihler ve devam eden
/// işler gibi kısa hafıza kartlarını saklar. Aynı SQLite dosyasını (AppPaths.DbPath) NoteStore/TaskStore ile
/// paylaşır.</summary>
public sealed class MemoryStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string Columns = "id, title, content, category, created_at, updated_at";
    private const string DefaultCategory = "genel";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo TrTR = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo TrSearch = TrTR;

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public MemoryStore(string dbPath, TimeProvider time, IChangeSignal signal)
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
            CREATE TABLE IF NOT EXISTS memory (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                title      TEXT    NOT NULL,
                content    TEXT    NOT NULL,
                category   TEXT    NOT NULL DEFAULT 'genel',
                created_at TEXT    NOT NULL,
                updated_at TEXT    NOT NULL,
                deleted_at TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_memory_live ON memory(deleted_at, category, updated_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public MemoryItem Add(string title, string content, string? category = null)
    {
        var cleanTitle = ValidateTitle(title);
        var cleanContent = ValidateContent(content);
        var cleanCategory = NormalizeCategory(category);
        var now = Now();
        long id;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO memory(title, content, category, created_at, updated_at)
                VALUES ($title, $content, $category, $now, $now) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$title", cleanTitle);
            cmd.Parameters.AddWithValue("$content", cleanContent);
            cmd.Parameters.AddWithValue("$category", cleanCategory);
            cmd.Parameters.AddWithValue("$now", now);
            id = (long)cmd.ExecuteScalar()!;
        }
        _signal.Notify();
        return Get(id)!;
    }

    public MemoryItem Update(long id, string? title = null, string? content = null, string? category = null)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE memory
                SET title = $title, content = $content, category = $category, updated_at = $now
                WHERE id = $id AND deleted_at IS NULL
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Now());

            var current = Get(id) ?? throw new MemoryNotFoundException(id);
            cmd.Parameters.AddWithValue("$title", title is null ? current.Title : ValidateTitle(title));
            cmd.Parameters.AddWithValue("$content", content is null ? current.Content : ValidateContent(content));
            cmd.Parameters.AddWithValue("$category", category is null ? current.Category : NormalizeCategory(category));

            if (cmd.ExecuteNonQuery() == 0)
                throw new MemoryNotFoundException(id);
        }
        _signal.Notify();
        return Get(id)!;
    }

    public void Delete(long id)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE memory SET deleted_at = $now WHERE id = $id AND deleted_at IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Now());
            if (cmd.ExecuteNonQuery() == 0)
                throw new MemoryNotFoundException(id);
        }
        _signal.Notify();
    }

    public MemoryItem Restore(long id)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE memory SET deleted_at = NULL WHERE id = $id AND deleted_at IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0)
                throw new MemoryNotFoundException(id);
        }
        _signal.Notify();
        return Get(id)!;
    }

    public IReadOnlyList<MemoryItem> List(string? category = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = $"SELECT {Columns} FROM memory WHERE deleted_at IS NULL";
        if (category is { } c)
        {
            sql += " AND category = $category";
            cmd.Parameters.AddWithValue("$category", NormalizeCategory(c));
        }
        sql += " ORDER BY category, updated_at DESC";
        cmd.CommandText = sql;
        return ReadAll(cmd);
    }

    /// <summary>Canlı kayıtlar arasında, metni (boşlukla ayrılmış) tüm terimleri başlık, içerik veya
    /// kategoride içeren kayıtları, Türkçe büyük/küçük harf duyarsız şekilde arar.</summary>
    public IReadOnlyList<MemoryItem> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Arama metni boş olamaz.");

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM memory WHERE deleted_at IS NULL ORDER BY category, updated_at DESC";
        return ReadAll(cmd)
            .Where(m => terms.All(t =>
                TrSearch.CompareInfo.IndexOf(m.Title, t, CompareOptions.IgnoreCase) >= 0 ||
                TrSearch.CompareInfo.IndexOf(m.Content, t, CompareOptions.IgnoreCase) >= 0 ||
                TrSearch.CompareInfo.IndexOf(m.Category, t, CompareOptions.IgnoreCase) >= 0))
            .ToList();
    }

    public IReadOnlyList<string> Categories()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT category FROM memory WHERE deleted_at IS NULL ORDER BY category";
        var categories = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            categories.Add(reader.GetString(0));
        return categories;
    }

    private MemoryItem? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM memory WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static List<MemoryItem> ReadAll(SqliteCommand cmd)
    {
        var items = new List<MemoryItem>();
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

    private static string ValidateTitle(string? title)
    {
        var clean = title?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Hafıza başlığı boş olamaz.");
        return clean;
    }

    private static string ValidateContent(string? content)
    {
        var clean = content?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Hafıza içeriği boş olamaz.");
        return clean;
    }

    private static string NormalizeCategory(string? category)
    {
        var clean = category?.Trim();
        return string.IsNullOrEmpty(clean) ? DefaultCategory : clean.ToLower(TrTR);
    }

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static MemoryItem Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        Parse(r.GetString(4)),
        Parse(r.GetString(5)));
}
