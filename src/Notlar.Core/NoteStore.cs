using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

public sealed class NoteStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string Columns = "id, text, created_at, updated_at, done";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public NoteStore(string dbPath, TimeProvider time, IChangeSignal signal)
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
            CREATE TABLE IF NOT EXISTS notes (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                text       TEXT    NOT NULL,
                created_at TEXT    NOT NULL,
                updated_at TEXT    NULL,
                done       INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_notes_created_at ON notes(created_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public Note Add(string text)
    {
        var clean = Validate(text);
        long id;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO notes(text, created_at) VALUES($text, $now) RETURNING id";
            cmd.Parameters.AddWithValue("$text", clean);
            cmd.Parameters.AddWithValue("$now", Now());
            id = (long)cmd.ExecuteScalar()!;
        }
        _signal.Notify();
        return Get(id)!;
    }

    public Note? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM notes WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Note> List(NoteFilter filter)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder($"SELECT {Columns} FROM notes WHERE deleted_at IS NULL");
        if (filter.From is { } from)
        {
            sql.Append(" AND substr(created_at, 1, 10) >= $from");
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", Inv));
        }
        if (filter.To is { } to)
        {
            sql.Append(" AND substr(created_at, 1, 10) <= $to");
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", Inv));
        }
        if (filter.OnlyOpen)
            sql.Append(" AND done = 0");
        sql.Append(" ORDER BY id");
        cmd.CommandText = sql.ToString();

        var notes = new List<Note>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                notes.Add(Map(reader));

        if (string.IsNullOrWhiteSpace(filter.Tag))
            return notes;
        var tag = TagParser.Normalize(filter.Tag);
        return notes.Where(n => n.Tags.Contains(tag)).ToList();
    }

    public Note Update(long id, string text)
    {
        Execute(id, "text = $value, updated_at = $now", Validate(text), deleted: false);
        return Get(id)!;
    }

    public Note SetDone(long id, bool done)
    {
        Execute(id, "done = $value", done ? 1 : 0, deleted: false);
        return Get(id)!;
    }

    public void Delete(long id) => Execute(id, "deleted_at = $now", null, deleted: false);

    public Note Restore(long id)
    {
        Execute(id, "deleted_at = NULL", null, deleted: true);
        return Get(id)!;
    }

    public int PurgeDeleted(TimeSpan olderThan)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Cutoff string olarak karşılaştırılıyor; bu yalnızca tüm zaman damgaları aynı yerel ofsetle
        // (Türkiye, sabit +03:00, DST yok) yazıldığı için doğru sonuç verir.
        cmd.CommandText = "DELETE FROM notes WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", Format(_time.GetLocalNow() - olderThan));
        return cmd.ExecuteNonQuery();
    }

    public string? Backup(string dir, int keep)
    {
        Directory.CreateDirectory(dir);
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var path = Path.Combine(dir, $"notlar-{today.ToString("yyyy-MM-dd", Inv)}.db");
        string? created = null;
        if (!File.Exists(path))
        {
            using var source = Open();
            using var target = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            target.Open();
            source.BackupDatabase(target);
            created = path;
        }
        foreach (var old in Directory.GetFiles(dir, "notlar-*.db").Order(StringComparer.Ordinal).Reverse().Skip(keep))
            File.Delete(old);
        return created;
    }

    private void Execute(long id, string set, object? value, bool deleted)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"UPDATE notes SET {set} WHERE id = $id AND deleted_at IS {(deleted ? "NOT NULL" : "NULL")}";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Now());
            if (value is not null)
                cmd.Parameters.AddWithValue("$value", value);
            if (cmd.ExecuteNonQuery() == 0)
                throw new NoteNotFoundException(id);
        }
        _signal.Notify();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string Validate(string? text)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Not metni boş olamaz.");
        return clean;
    }

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static Note Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        Parse(r.GetString(2)),
        r.IsDBNull(3) ? null : Parse(r.GetString(3)),
        r.GetInt64(4) != 0);
}
