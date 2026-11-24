using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>Toplantı kayıtları ve transkript segmentleri. Aynı SQLite dosyasını (AppPaths.DbPath) diğer
/// store'larla paylaşır. Ses dosyaları burada değil, AppPaths.RecordingsDir altında &lt;id&gt; klasöründe durur;
/// store yalnızca ses dosyalarının silinip silinmediğini (audio_deleted) izler.</summary>
public sealed class RecordingStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string Columns = "id, title, started_at, ended_at, status, language, summary, plan_id, error, audio_deleted";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Desteklenen transkripsiyon dilleri.</summary>
    public static readonly IReadOnlyList<string> Languages = ["tr", "en", "auto"];

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IChangeSignal _signal;

    public RecordingStore(string dbPath, TimeProvider time, IChangeSignal signal)
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
            CREATE TABLE IF NOT EXISTS recordings (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                title         TEXT    NOT NULL,
                started_at    TEXT    NOT NULL,
                ended_at      TEXT    NULL,
                status        TEXT    NOT NULL,
                language      TEXT    NOT NULL DEFAULT 'tr',
                summary       TEXT    NULL,
                plan_id       INTEGER NULL,
                error         TEXT    NULL,
                audio_deleted INTEGER NOT NULL DEFAULT 0,
                deleted_at    TEXT    NULL
            );
            CREATE TABLE IF NOT EXISTS recording_segments (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                recording_id INTEGER NOT NULL,
                start_ms     INTEGER NOT NULL,
                end_ms       INTEGER NOT NULL,
                speaker      TEXT    NOT NULL,
                text         TEXT    NOT NULL,
                person       INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recseg ON recording_segments(recording_id, start_ms);
            CREATE TABLE IF NOT EXISTS recording_ranges (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                recording_id INTEGER NOT NULL,
                start_ms     INTEGER NOT NULL,
                end_ms       INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recrange ON recording_ranges(recording_id, start_ms);
            CREATE TABLE IF NOT EXISTS recording_persons (
                recording_id INTEGER NOT NULL,
                person       INTEGER NOT NULL,
                auto_name    TEXT    NOT NULL,
                gender       TEXT    NULL,
                median_f0    REAL    NULL,
                user_name    TEXT    NULL,
                voiceprint   BLOB    NULL,
                address_name TEXT    NULL,
                PRIMARY KEY (recording_id, person)
            );
            """;
        cmd.ExecuteNonQuery();

        // Eski şemadaki DB'lerde person kolonu yoksa ekle (eski transkriptler kişi etiketsiz kalır).
        if (!HasColumn(conn, "recording_segments", "person"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE recording_segments ADD COLUMN person INTEGER NULL";
            alter.ExecuteNonQuery();
        }

        // Hitaptan ad çıkarmadan önceki recording_persons tablosuna address_name kolonu.
        if (!HasColumn(conn, "recording_persons", "address_name"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE recording_persons ADD COLUMN address_name TEXT NULL";
            alter.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Yeni bir kayıt başlatır: başlık "Toplantı dd.MM.yyyy HH:mm", durum Kaydediliyor.</summary>
    public Recording Start(string language)
    {
        var lang = ValidateLanguage(language);
        var now = _time.GetLocalNow();
        long id;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO recordings(title, started_at, status, language)
                VALUES($title, $now, $status, $language) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$title", $"Toplantı {now.ToString("dd.MM.yyyy HH:mm", Inv)}");
            cmd.Parameters.AddWithValue("$now", Format(now));
            cmd.Parameters.AddWithValue("$status", nameof(RecordingStatus.Kaydediliyor));
            cmd.Parameters.AddWithValue("$language", lang);
            id = (long)cmd.ExecuteScalar()!;
        }
        _signal.Notify();
        return Get(id);
    }

    /// <summary>Kaydı bitirir: ended_at = şimdi, durum Kaydedildi. Kayıt kendiliğinden çevrilmez; kullanıcı
    /// "Transkript et" deyince <see cref="Requeue"/> ile kuyruğa alınır.</summary>
    public Recording Finish(long id)
    {
        Update(id, "ended_at = $now, status = $value", nameof(RecordingStatus.Kaydedildi));
        return Get(id);
    }

    public void MarkTranscribing(long id) =>
        Update(id, "status = $value, error = NULL", nameof(RecordingStatus.Cevriliyor));

    public void MarkFailed(long id, string error)
    {
        var clean = string.IsNullOrWhiteSpace(error) ? "Bilinmeyen hata." : error.Trim();
        Update(id, "status = 'Hata', error = $value", clean);
    }

    /// <summary>Kaydın segmentlerini ve kişilerini (adlar, bkz. <see cref="RecordingPerson"/>) verilenlerle değiştirir
    /// (tek işlemde), durumu Hazir yapar ve hatayı temizler. Boş liste geçerlidir (hiç konuşma bulunamadı).
    /// Segmentlerdeki <see cref="TranscriptSegment.PersonName"/> yok sayılır; adlar <paramref name="persons"/>'tan
    /// gelir (verilmezse kaydın eski kişi adları silinir, satırlar "Kişi N" görünür).</summary>
    public Recording SaveTranscript(long id, IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<RecordingPerson>? persons = null)
    {
        using (var conn = Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE recordings SET status = $status, error = NULL WHERE id = $id AND deleted_at IS NULL";
                cmd.Parameters.AddWithValue("$status", nameof(RecordingStatus.Hazir));
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteNonQuery() == 0)
                    throw new RecordingNotFoundException(id);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM recording_segments WHERE recording_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO recording_segments(recording_id, start_ms, end_ms, speaker, text, person)
                    VALUES($id, $start, $end, $speaker, $text, $person)
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                var start = cmd.Parameters.Add("$start", SqliteType.Integer);
                var end = cmd.Parameters.Add("$end", SqliteType.Integer);
                var speaker = cmd.Parameters.Add("$speaker", SqliteType.Text);
                var text = cmd.Parameters.Add("$text", SqliteType.Text);
                var person = cmd.Parameters.Add("$person", SqliteType.Integer);
                foreach (var s in segments)
                {
                    start.Value = s.StartMs;
                    end.Value = s.EndMs;
                    speaker.Value = s.Speaker.ToString();
                    text.Value = s.Text ?? "";
                    person.Value = s.Person is { } p ? p : DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
            ReplacePersons(conn, tx, id, persons ?? []);
            tx.Commit();
        }
        _signal.Notify();
        return Get(id);
    }

    /// <summary>Kaydı metne çevrilmek üzere Bekliyor durumuna alır (ilk çeviri ya da yeniden çeviri).</summary>
    public Recording Requeue(long id)
    {
        Update(id, "status = $value, error = NULL", nameof(RecordingStatus.Bekliyor));
        return Get(id);
    }

    public Recording Rename(long id, string title)
    {
        var clean = title?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Başlık boş olamaz.");
        Update(id, "title = $value", clean);
        return Get(id);
    }

    /// <summary>Özeti ayarlar; null veya boş metin özeti temizler.</summary>
    public Recording SetSummary(long id, string? summary)
    {
        var clean = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        Update(id, "summary = $value", clean);
        return Get(id);
    }

    public Recording SetPlan(long id, long? planId)
    {
        Update(id, "plan_id = $value", planId);
        return Get(id);
    }

    public Recording SetLanguage(long id, string language)
    {
        Update(id, "language = $value", ValidateLanguage(language));
        return Get(id);
    }

    /// <summary>Ses dosyalarının silindiğini işaretler. Silinmiş (soft delete) kayıtlarda da çalışır; böylece
    /// <see cref="ListAudioOlderThan"/> ile bulunan artakalan ses klasörleri temizlenip işaretlenebilir.</summary>
    public void MarkAudioDeleted(long id) => Update(id, "audio_deleted = 1", null, includeDeleted: true);

    public Recording Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM recordings WHERE id = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : throw new RecordingNotFoundException(id);
    }

    /// <summary>Silinmemiş kayıtlar, yeniden eskiye.</summary>
    public IReadOnlyList<Recording> List(int limit = 50)
    {
        if (limit < 1)
            throw new ArgumentException("Kayıt sayısı en az 1 olmalı.");
        return Query($"SELECT {Columns} FROM recordings WHERE deleted_at IS NULL ORDER BY id DESC LIMIT $limit",
            cmd => cmd.Parameters.AddWithValue("$limit", limit));
    }

    /// <summary>Metne çevrilmiş (Hazir) en yeni kayıt; yoksa null.</summary>
    public Recording? GetLatestReady()
    {
        var list = Query($"SELECT {Columns} FROM recordings WHERE deleted_at IS NULL AND status = $status ORDER BY id DESC LIMIT 1",
            cmd => cmd.Parameters.AddWithValue("$status", nameof(RecordingStatus.Hazir)));
        return list.Count > 0 ? list[0] : null;
    }

    public IReadOnlyList<TranscriptSegment> GetSegments(long id)
    {
        Get(id); // yoksa / silinmişse RecordingNotFoundException

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.start_ms, s.end_ms, s.speaker, s.text, s.person, COALESCE(p.user_name, p.address_name, p.auto_name),
                   CASE WHEN p.user_name IS NOT NULL THEN 2 WHEN p.address_name IS NOT NULL THEN 1 ELSE 0 END
            FROM recording_segments s
            LEFT JOIN recording_persons p ON p.recording_id = s.recording_id AND p.person = s.person
            WHERE s.recording_id = $id ORDER BY s.start_ms, s.id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var segments = new List<TranscriptSegment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var person = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
            var name = person is null || reader.IsDBNull(5) ? null : reader.GetString(5);
            segments.Add(new TranscriptSegment(
                reader.GetInt64(0),
                reader.GetInt64(1),
                Enum.Parse<Speaker>(reader.GetString(2)),
                reader.GetString(3),
                person,
                name,
                name is null ? PersonNameSource.Tahmin : (PersonNameSource)reader.GetInt32(6)));
        }
        return segments;
    }

    /// <summary>Kaydın kişileri (numara sırasıyla). Kişi ayrımı yapılmamış ya da adlandırmadan önceki sürümle çevrilmiş
    /// kayıtlarda boştur (kullanıcı ad verdiyse yalnızca o kişiler).</summary>
    public IReadOnlyList<RecordingPerson> GetPersons(long id)
    {
        Get(id); // yoksa / silinmişse RecordingNotFoundException

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT person, auto_name, gender, median_f0, user_name, voiceprint, address_name FROM recording_persons
            WHERE recording_id = $id ORDER BY person
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var persons = new List<RecordingPerson>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            persons.Add(new RecordingPerson(
                reader.GetInt32(0),
                reader.GetString(1),
                !reader.IsDBNull(2) && Enum.TryParse<VoiceGender>(reader.GetString(2), out var g) ? g : VoiceGender.Belirsiz,
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : FromBlob((byte[])reader[5]),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return persons;
    }

    /// <summary>Bir kişiye kullanıcının verdiği gerçek adı koyar (kayıttaki o kişinin tüm satırları bu adla görünür);
    /// <paramref name="name"/> boş/null ise takma ada döner. Ad <see cref="SpeakerNaming.CleanUserName"/> ile temizlenir
    /// (geçersizse <see cref="ArgumentException"/>). Kişi bu kaydın transkriptinde yoksa <see cref="ArgumentException"/>.
    /// Kişinin kaydı yoksa (eski kayıt) takma adı "Kişi N" olarak oluşturulur.</summary>
    public RecordingPerson RenamePerson(long id, int person, string? name)
    {
        var clean = SpeakerNaming.CleanUserName(name);
        using (var conn = Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT 1 FROM recordings WHERE id = $id AND deleted_at IS NULL";
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteScalar() is null)
                    throw new RecordingNotFoundException(id);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT 1 FROM recording_segments WHERE recording_id = $id AND person = $person LIMIT 1";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$person", person);
                if (cmd.ExecuteScalar() is null)
                    throw new ArgumentException($"Bu kayıtta {TranscriptFormatter.PersonLabel(person)} yok.");
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO recording_persons(recording_id, person, auto_name, user_name)
                    VALUES($id, $person, $auto, $name)
                    ON CONFLICT(recording_id, person) DO UPDATE SET user_name = excluded.user_name
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$person", person);
                cmd.Parameters.AddWithValue("$auto", TranscriptFormatter.PersonLabel(person));
                cmd.Parameters.AddWithValue("$name", (object?)clean ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _signal.Notify();
        return GetPersons(id).First(p => p.Person == person);
    }

    private static void ReplacePersons(SqliteConnection conn, SqliteTransaction tx, long id, IReadOnlyList<RecordingPerson> persons)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM recording_persons WHERE recording_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        if (persons.Count == 0) return;

        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO recording_persons(recording_id, person, auto_name, gender, median_f0, user_name, voiceprint, address_name)
            VALUES($id, $person, $auto, $gender, $f0, $user, $voice, $address)
            """;
        insert.Parameters.AddWithValue("$id", id);
        var person = insert.Parameters.Add("$person", SqliteType.Integer);
        var auto = insert.Parameters.Add("$auto", SqliteType.Text);
        var gender = insert.Parameters.Add("$gender", SqliteType.Text);
        var f0 = insert.Parameters.Add("$f0", SqliteType.Real);
        var user = insert.Parameters.Add("$user", SqliteType.Text);
        var voice = insert.Parameters.Add("$voice", SqliteType.Blob);
        var address = insert.Parameters.Add("$address", SqliteType.Text);
        foreach (var p in persons.DistinctBy(p => p.Person))
        {
            person.Value = p.Person;
            auto.Value = TextSafety.Flatten(p.AutoName) is { Length: > 0 } a ? a : TranscriptFormatter.PersonLabel(p.Person);
            gender.Value = p.Gender.ToString();
            f0.Value = p.MedianF0Hz is { } hz && double.IsFinite(hz) ? hz : DBNull.Value;
            user.Value = SafeUserName(p.UserName) is { } u ? u : DBNull.Value;
            voice.Value = p.Voiceprint is { Length: > 0 } v ? ToBlob(v) : DBNull.Value;
            address.Value = SafeUserName(p.AddressName) is { } an ? an : DBNull.Value;
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Kaydederken geçersiz bir kullanıcı adı transkripti düşürmesin: ad atılır (takma ad görünür).</summary>
    private static string? SafeUserName(string? name)
    {
        try
        {
            return SpeakerNaming.CleanUserName(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[] ToBlob(float[] values) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static float[]? FromBlob(byte[] bytes) =>
        bytes.Length == 0 || bytes.Length % sizeof(float) != 0
            ? null
            : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    /// <summary>Metne çevrilecek aralıkları verilenlerle değiştirir (<see cref="TimeRanges.Normalize"/> ile
    /// temizlenmiş olarak). Boş liste "tüm kayıt" demektir.</summary>
    public IReadOnlyList<TimeRange> SetRanges(long id, IEnumerable<TimeRange> ranges)
    {
        var normalized = TimeRanges.Normalize(ranges);
        using (var conn = Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT 1 FROM recordings WHERE id = $id AND deleted_at IS NULL";
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteScalar() is null)
                    throw new RecordingNotFoundException(id);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM recording_ranges WHERE recording_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO recording_ranges(recording_id, start_ms, end_ms) VALUES($id, $start, $end)";
                cmd.Parameters.AddWithValue("$id", id);
                var start = cmd.Parameters.Add("$start", SqliteType.Integer);
                var end = cmd.Parameters.Add("$end", SqliteType.Integer);
                foreach (var r in normalized)
                {
                    start.Value = r.StartMs;
                    end.Value = r.EndMs;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        _signal.Notify();
        return normalized;
    }

    /// <summary>Metne çevrilecek aralıklar (başlangıca göre sıralı); boşsa tüm kayıt çevrilir.</summary>
    public IReadOnlyList<TimeRange> GetRanges(long id)
    {
        Get(id); // yoksa / silinmişse RecordingNotFoundException

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT start_ms, end_ms FROM recording_ranges WHERE recording_id = $id ORDER BY start_ms, id";
        cmd.Parameters.AddWithValue("$id", id);
        var ranges = new List<TimeRange>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ranges.Add(new TimeRange(reader.GetInt64(0), reader.GetInt64(1)));
        return ranges;
    }

    /// <summary>Uygulama başlangıcında çağrılır: yarım kalmış kayıtları (Kaydediliyor → Kaydedildi; ended_at boşsa
    /// şimdi; kullanıcı istemeden çevrilmez) ve yarım kalmış çevirileri (Cevriliyor → Bekliyor) kurtarır. Kuyruğa
    /// alınması gereken (kullanıcının çeviriyi başlattığı) tüm Bekliyor kayıtların id'lerini (eskiden yeniye) döndürür.</summary>
    public IReadOnlyList<long> RecoverInterrupted()
    {
        var ids = new List<long>();
        int changed;
        using (var conn = Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE recordings SET status = 'Kaydedildi', ended_at = COALESCE(ended_at, $now)
                    WHERE deleted_at IS NULL AND status = 'Kaydediliyor';
                    """;
                cmd.Parameters.AddWithValue("$now", Now());
                changed = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE recordings SET status = 'Bekliyor' WHERE deleted_at IS NULL AND status = 'Cevriliyor'";
                changed += cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id FROM recordings WHERE deleted_at IS NULL AND status = 'Bekliyor' ORDER BY id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetInt64(0));
            }
            tx.Commit();
        }
        if (changed > 0)
            _signal.Notify();
        return ids;
    }

    /// <summary>Kaydı siler (soft delete). Ses klasörünü silmek çağıranın işidir.</summary>
    public void Delete(long id) => Update(id, "deleted_at = $now", null);

    /// <summary>Ses dosyaları hâlâ duran (audio_deleted = 0) ve <paramref name="age"/>'den önce bitmiş kayıtlar.
    /// Silinmiş (soft delete) kayıtlar yaşına bakılmaksızın dahildir: silme sırasında klasör kaldırılamadıysa
    /// artakalan ses de temizlenmeli.</summary>
    public IReadOnlyList<Recording> ListAudioOlderThan(TimeSpan age)
    {
        var cutoff = _time.GetLocalNow() - age;
        var result = new List<Recording>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}, deleted_at FROM recordings
            WHERE audio_deleted = 0 AND (ended_at IS NOT NULL OR deleted_at IS NOT NULL)
            ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var r = Map(reader);
            var deleted = !reader.IsDBNull(10);
            if (deleted || (r.EndedAt is { } ended && ended <= cutoff))
                result.Add(r);
        }
        return result;
    }

    private IReadOnlyList<Recording> Query(string sql, Action<SqliteCommand> bind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        var list = new List<Recording>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(Map(reader));
        return list;
    }

    private void Update(long id, string set, object? value, bool includeDeleted = false)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"UPDATE recordings SET {set} WHERE id = $id{(includeDeleted ? "" : " AND deleted_at IS NULL")}";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$value", value ?? DBNull.Value);
            if (cmd.ExecuteNonQuery() == 0)
                throw new RecordingNotFoundException(id);
        }
        _signal.Notify();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Dil kodunu doğrular ve normalize eder (kırpılmış, küçük harf): yalnızca 'tr', 'en', 'auto'.</summary>
    public static string ValidateLanguage(string? language)
    {
        var clean = language?.Trim().ToLowerInvariant();
        if (clean is null || !Languages.Contains(clean))
            throw new ArgumentException($"Desteklenmeyen dil: '{language}'. Geçerli değerler: tr, en, auto.");
        return clean;
    }

    private string Now() => Format(_time.GetLocalNow());

    private static string Format(DateTimeOffset value) => value.ToString(TimeFormat, Inv);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, TimeFormat, Inv);

    private static Recording Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        Parse(r.GetString(2)),
        r.IsDBNull(3) ? null : Parse(r.GetString(3)),
        Enum.TryParse<RecordingStatus>(r.GetString(4), out var status) ? status : RecordingStatus.Hata,
        r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetInt64(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetInt64(9) != 0);
}
