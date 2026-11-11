using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

public sealed class NoteStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string Columns = "id, text, created_at, updated_at, done, author";
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
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS notes (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    text       TEXT    NOT NULL,
                    created_at TEXT    NOT NULL,
                    updated_at TEXT    NULL,
                    done       INTEGER NOT NULL DEFAULT 0,
                    deleted_at TEXT    NULL,
                    author     TEXT    NOT NULL DEFAULT 'ben'
                );
                CREATE INDEX IF NOT EXISTS ix_notes_created_at ON notes(created_at);
                """;
            cmd.ExecuteNonQuery();
        }

        // Eski şemadaki DB'lerde author kolonu yoksa ekle.
        var hasAuthor = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(notes)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "author", StringComparison.OrdinalIgnoreCase))
                {
                    hasAuthor = true;
                    break;
                }
            }
        }
        if (!hasAuthor)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE notes ADD COLUMN author TEXT NOT NULL DEFAULT 'ben'";
            cmd.ExecuteNonQuery();
        }

        // Eski (küçük harfli, enum tabanlı) yazar değerlerini güncel kanonik adlara taşır.
        // İdempotent: zaten taşınmış DB'lerde hiçbir satırı etkilemez.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE notes SET author = 'Ben' WHERE author = 'ben';
                UPDATE notes SET author = 'Claude' WHERE author = 'claude';
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public Note Add(string text, string author = NoteAuthor.Ben) =>
        AddAt(text, _time.GetLocalNow(), author);

    /// <summary>Notu belirtilen zamanla ekler (geçmiş/istenen tarihe not eklemek için).
    /// <paramref name="createdAt"/>, mağazanın yerel saat dilimine çevrilerek saklanır.</summary>
    public Note AddAt(string text, DateTimeOffset createdAt, string author = NoteAuthor.Ben)
    {
        var clean = Validate(text);
        var normalizedAuthor = NoteAuthor.Normalize(author);
        var local = TimeZoneInfo.ConvertTime(createdAt, _time.LocalTimeZone);
        long id;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO notes(text, created_at, author) VALUES($text, $now, $author) RETURNING id";
            cmd.Parameters.AddWithValue("$text", clean);
            cmd.Parameters.AddWithValue("$now", Format(local));
            cmd.Parameters.AddWithValue("$author", normalizedAuthor);
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
        if (filter.Author is { } author)
        {
            sql.Append(" AND author = $author COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$author", author);
        }
        sql.Append(" ORDER BY id");
        cmd.CommandText = sql.ToString();

        var notes = ReadAll(cmd);

        if (string.IsNullOrWhiteSpace(filter.Tag))
            return notes;
        var tag = TagParser.Normalize(filter.Tag);
        return notes.Where(n => n.Tags.Contains(tag)).ToList();
    }

    /// <summary>Panel görünümü: <paramref name="since"/> (dahil) ve sonrasındaki notlar + daha eski ama tamamlanmamış notlar, id sırasıyla.</summary>
    public IReadOnlyList<Note> ListRecentAndOpen(DateOnly since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM notes
            WHERE deleted_at IS NULL AND (substr(created_at, 1, 10) >= $since OR done = 0)
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("$since", since.ToString("yyyy-MM-dd", Inv));
        return ReadAll(cmd);
    }

    /// <summary><paramref name="before"/> gününden önce tamamlanmış (silinmemiş) not var mı?</summary>
    public bool HasDoneBefore(DateOnly before)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM notes
                WHERE deleted_at IS NULL AND done = 1 AND substr(created_at, 1, 10) < $before
            )
            """;
        cmd.Parameters.AddWithValue("$before", before.ToString("yyyy-MM-dd", Inv));
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return reader.GetInt64(0) != 0;
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
            var tmpPath = path + ".tmp";
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
            try
            {
                using (var source = Open())
                using (var target = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = tmpPath, Pooling = false }.ToString()))
                {
                    target.Open();
                    source.BackupDatabase(target);
                }
                File.Move(tmpPath, path);
                created = path;
            }
            catch
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
                throw;
            }
        }
        // Yalnızca tamamlanmış .db dosyaları rotasyona dahil edilir; yarım kalmış .tmp'ler sayılmaz.
        foreach (var old in Directory.GetFiles(dir, "notlar-*.db")
                     .Where(f => f.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal).Reverse().Skip(keep))
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

    private static List<Note> ReadAll(SqliteCommand cmd)
    {
        var notes = new List<Note>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            notes.Add(Map(reader));
        return notes;
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
        r.GetInt64(4) != 0,
        r.GetString(5));

    private static readonly CultureInfo TrSearch = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Canlı notlar arasında, metni (boşlukla ayrılmış) tüm terimleri içeren notları, Türkçe
    /// büyük/küçük harf duyarsız şekilde arar. En yeni not önce; <paramref name="limit"/> ile sınırlanır.</summary>
    public IReadOnlyList<Note> Search(string query, string? author = null, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Arama metni boş olamaz.");

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder($"SELECT {Columns} FROM notes WHERE deleted_at IS NULL");
        if (author is { } a)
        {
            sql.Append(" AND author = $author COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$author", a);
        }
        sql.Append(" ORDER BY created_at DESC");
        cmd.CommandText = sql.ToString();

        return ReadAll(cmd)
            .Where(n => terms.All(t => TrSearch.CompareInfo.IndexOf(n.Text, t, CompareOptions.IgnoreCase) >= 0))
            .Take(limit)
            .ToList();
    }
}
