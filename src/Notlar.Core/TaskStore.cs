using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

public sealed class TaskStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string Columns = "id, title, created_at, done, author";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public TaskStore(string dbPath, TimeProvider time, IChangeSignal signal)
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
            CREATE TABLE IF NOT EXISTS tasks (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                title      TEXT    NOT NULL,
                created_at TEXT    NOT NULL,
                done       INTEGER NOT NULL DEFAULT 0,
                author     TEXT    NOT NULL DEFAULT 'Claude',
                deleted_at TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_live ON tasks(deleted_at, done, id);
            """;
        cmd.ExecuteNonQuery();
    }

    public TaskItem Add(string title, string author)
    {
        var clean = Validate(title);
        long id;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO tasks(title, created_at, author) VALUES($title, $now, $author) RETURNING id";
            cmd.Parameters.AddWithValue("$title", clean);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$author", string.IsNullOrWhiteSpace(author) ? NoteAuthor.Claude : NoteAuthor.Normalize(author));
            id = (long)cmd.ExecuteScalar()!;
        }
        _signal.Notify();
        return Get(id)!;
    }

    public IReadOnlyList<TaskItem> List(bool onlyOpen = false)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM tasks
            WHERE deleted_at IS NULL {(onlyOpen ? "AND done = 0" : "")}
            ORDER BY done, id
            """;
        var tasks = new List<TaskItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tasks.Add(Map(reader));
        return tasks;
    }

    public TaskItem SetDone(long id, bool done)
    {
        Execute(id, "done = $value", done ? 1 : 0, deleted: false);
        return Get(id)!;
    }

    public TaskItem Rename(long id, string title)
    {
        Execute(id, "title = $value", Validate(title), deleted: false);
        return Get(id)!;
    }

    public void Delete(long id) => Execute(id, "deleted_at = $now", null, deleted: false);

    public TaskItem Restore(long id)
    {
        Execute(id, "deleted_at = NULL", null, deleted: true);
        return Get(id)!;
    }

    private TaskItem? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tasks WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private void Execute(long id, string set, object? value, bool deleted)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"UPDATE tasks SET {set} WHERE id = $id AND deleted_at IS {(deleted ? "NOT NULL" : "NULL")}";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Now());
            if (value is not null)
                cmd.Parameters.AddWithValue("$value", value);
            if (cmd.ExecuteNonQuery() == 0)
                throw new TaskNotFoundException(id);
        }
        _signal.Notify();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string Validate(string? title)
    {
        var clean = title?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Başlık boş olamaz.");
        return clean;
    }

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static TaskItem Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        Parse(r.GetString(2)),
        r.GetInt64(3) != 0,
        r.GetString(4));
}
