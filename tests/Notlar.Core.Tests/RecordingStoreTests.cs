using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class RecordingFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public RecordingStore Store { get; }

    public RecordingFixture() => Store = new RecordingStore(DbPath, Time, Signal);

    public RecordingStore Reopen() => new(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class RecordingStoreTests
{
    private static readonly TimeSpan Tr = RecordingFixture.Tr;

    private static readonly TranscriptSegment[] TwoSegments =
    [
        new(0, 1500, Speaker.Ben, "Merhaba"),
        new(2000, 4000, Speaker.KarsiTaraf, "Selam, nasılsın?"),
    ];

    [Fact]
    public void Start_uses_default_title_recording_status_and_signals()
    {
        using var f = new RecordingFixture();

        var r = f.Store.Start("tr");

        Assert.Equal(1, r.Id);
        Assert.Equal("Toplantı 11.11.2026 14:32", r.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr), r.StartedAt);
        Assert.Null(r.EndedAt);
        Assert.Null(r.Duration);
        Assert.Equal(RecordingStatus.Kaydediliyor, r.Status);
        Assert.Equal("tr", r.Language);
        Assert.Null(r.Summary);
        Assert.Null(r.PlanId);
        Assert.Null(r.Error);
        Assert.False(r.AudioDeleted);
        Assert.Equal(1, f.Signal.Count);
    }

    [Theory]
    [InlineData("tr", "tr")]
    [InlineData("en", "en")]
    [InlineData("auto", "auto")]
    [InlineData(" EN ", "en")]
    public void Start_accepts_supported_languages(string input, string expected)
    {
        using var f = new RecordingFixture();
        Assert.Equal(expected, f.Store.Start(input).Language);
    }

    [Theory]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("türkçe")]
    public void Start_rejects_unsupported_language(string language)
    {
        using var f = new RecordingFixture();

        var ex = Assert.Throws<ArgumentException>(() => f.Store.Start(language));

        Assert.Contains("dil", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Store.List());
        Assert.Equal(0, f.Signal.Count);
    }

    [Fact]
    public void Finish_sets_end_time_and_recorded_status()
    {
        using var f = new RecordingFixture();
        var r = f.Store.Start("tr");
        f.Time.Advance(TimeSpan.FromMinutes(42));

        var done = f.Store.Finish(r.Id);

        Assert.Equal(RecordingStatus.Kaydedildi, done.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 15, 14, 0, Tr), done.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(42), done.Duration);
        Assert.Equal(2, f.Signal.Count);
    }

    [Fact]
    public void Lifecycle_transcribing_failed_requeue_and_save()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;
        f.Store.Finish(id);

        f.Store.MarkTranscribing(id);
        Assert.Equal(RecordingStatus.Cevriliyor, f.Store.Get(id).Status);

        f.Store.MarkFailed(id, "Model indirilemedi: ağ yok");
        var failed = f.Store.Get(id);
        Assert.Equal(RecordingStatus.Hata, failed.Status);
        Assert.Equal("Model indirilemedi: ağ yok", failed.Error);

        var requeued = f.Store.Requeue(id);
        Assert.Equal(RecordingStatus.Bekliyor, requeued.Status);

        f.Store.MarkTranscribing(id);
        var ready = f.Store.SaveTranscript(id, TwoSegments);
        Assert.Equal(RecordingStatus.Hazir, ready.Status);
        Assert.Null(ready.Error);
        Assert.Equal(TwoSegments, f.Store.GetSegments(id));
    }

    [Fact]
    public void SaveTranscript_replaces_previous_segments_and_signals()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;
        f.Store.SaveTranscript(id, TwoSegments);
        var before = f.Signal.Count;

        f.Store.SaveTranscript(id, [new TranscriptSegment(500, 900, Speaker.KarsiTaraf, "Yeni")]);

        Assert.Equal([new TranscriptSegment(500, 900, Speaker.KarsiTaraf, "Yeni")], f.Store.GetSegments(id));
        Assert.Equal(before + 1, f.Signal.Count);
    }

    [Fact]
    public void SaveTranscript_keeps_person_numbers_and_unlabelled_lines()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;
        TranscriptSegment[] segments =
        [
            new(0, 1500, Speaker.Ben, "Merhaba", 1),
            new(2000, 4000, Speaker.KarsiTaraf, "Selam", 2),
            new(5000, 5400, Speaker.KarsiTaraf, "Evet"),
            new(6000, 9000, Speaker.KarsiTaraf, "Başlayalım", 1),
        ];

        f.Store.SaveTranscript(id, segments);

        Assert.Equal(segments, f.Store.GetSegments(id));
        Assert.Equal([1, 2, null, 1], f.Reopen().GetSegments(id).Select(s => s.Person));
    }

    [Fact]
    public void Database_without_person_column_is_migrated_and_old_segments_stay_unlabelled()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;
        f.Store.SaveTranscript(id, TwoSegments);
        // Kişi ayrımından önceki şema: person kolonu yok.
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={f.DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE recording_segments DROP COLUMN person";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopened = f.Reopen();

        Assert.Equal(TwoSegments, reopened.GetSegments(id));
        Assert.All(reopened.GetSegments(id), s => Assert.Null(s.Person));
        reopened.SaveTranscript(id, [new(0, 1000, Speaker.Ben, "yeni", 3)]);
        Assert.Equal(3, Assert.Single(reopened.GetSegments(id)).Person);
        // Göç idempotent: yeniden açınca hata vermez.
        Assert.Equal(3, Assert.Single(f.Reopen().GetSegments(id)).Person);
    }

    [Fact]
    public void SaveTranscript_with_no_segments_marks_ready_with_empty_transcript()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;

        var r = f.Store.SaveTranscript(id, []);

        Assert.Equal(RecordingStatus.Hazir, r.Status);
        Assert.Empty(f.Store.GetSegments(id));
    }

    [Fact]
    public void GetSegments_orders_by_start_time()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;

        f.Store.SaveTranscript(id, [new(3000, 4000, Speaker.Ben, "iki"), new(1000, 2000, Speaker.KarsiTaraf, "bir")]);

        Assert.Equal(["bir", "iki"], f.Store.GetSegments(id).Select(s => s.Text));
    }

    [Fact]
    public void Segments_are_isolated_per_recording()
    {
        using var f = new RecordingFixture();
        var a = f.Store.Start("tr").Id;
        var b = f.Store.Start("tr").Id;

        f.Store.SaveTranscript(a, TwoSegments);
        f.Store.SaveTranscript(b, [new(0, 10, Speaker.Ben, "b")]);

        Assert.Equal(2, f.Store.GetSegments(a).Count);
        Assert.Single(f.Store.GetSegments(b));
    }

    [Fact]
    public void Rename_trims_and_rejects_empty()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;

        Assert.Equal("Sprint planlama", f.Store.Rename(id, "  Sprint planlama  ").Title);
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Rename(id, "   "));
        Assert.Equal("Başlık boş olamaz.", ex.Message);
    }

    [Fact]
    public void SetSummary_stores_trimmed_text_and_clears_with_null_or_blank()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;

        Assert.Equal("## Özet\nKısa.", f.Store.SetSummary(id, "  ## Özet\nKısa.  ").Summary);
        Assert.Null(f.Store.SetSummary(id, "  ").Summary);
        f.Store.SetSummary(id, "x");
        Assert.Null(f.Store.SetSummary(id, null).Summary);
    }

    [Fact]
    public void SetPlan_and_SetLanguage_roundtrip()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;

        Assert.Equal(7, f.Store.SetPlan(id, 7).PlanId);
        Assert.Null(f.Store.SetPlan(id, null).PlanId);
        Assert.Equal("en", f.Store.SetLanguage(id, "en").Language);
        Assert.Throws<ArgumentException>(() => f.Store.SetLanguage(id, "fr"));
        Assert.Equal("en", f.Store.Get(id).Language);
    }

    [Fact]
    public void Get_missing_throws_RecordingNotFoundException()
    {
        using var f = new RecordingFixture();
        var ex = Assert.Throws<RecordingNotFoundException>(() => f.Store.Get(99));
        Assert.Equal(99, ex.Id);
        Assert.Throws<RecordingNotFoundException>(() => f.Store.Finish(99));
        Assert.Throws<RecordingNotFoundException>(() => f.Store.SaveTranscript(99, TwoSegments));
        Assert.Throws<RecordingNotFoundException>(() => f.Store.GetSegments(99));
    }

    [Fact]
    public void Delete_is_soft_and_hides_recording()
    {
        using var f = new RecordingFixture();
        var keep = f.Store.Start("tr").Id;
        var gone = f.Store.Start("tr").Id;
        f.Store.SaveTranscript(gone, TwoSegments);
        var before = f.Signal.Count;

        f.Store.Delete(gone);

        Assert.Equal(before + 1, f.Signal.Count);
        Assert.Equal([keep], f.Store.List().Select(r => r.Id));
        Assert.Throws<RecordingNotFoundException>(() => f.Store.Get(gone));
        Assert.Throws<RecordingNotFoundException>(() => f.Store.Delete(gone));
        Assert.Null(f.Store.GetLatestReady());

        // Soft delete: satır veritabanında durur.
        using var conn = new SqliteConnection($"Data Source={f.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT deleted_at IS NOT NULL FROM recordings WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", gone);
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void List_returns_newest_first_and_honours_limit()
    {
        using var f = new RecordingFixture();
        for (var i = 0; i < 4; i++)
        {
            f.Store.Start("tr");
            f.Time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal([4L, 3L, 2L, 1L], f.Store.List().Select(r => r.Id));
        Assert.Equal([4L, 3L], f.Store.List(2).Select(r => r.Id));
    }

    [Fact]
    public void GetLatestReady_returns_newest_ready_recording()
    {
        using var f = new RecordingFixture();
        Assert.Null(f.Store.GetLatestReady());

        var a = f.Store.Start("tr").Id;
        var b = f.Store.Start("tr").Id;
        f.Store.Start("tr");
        f.Store.SaveTranscript(a, TwoSegments);
        f.Store.SaveTranscript(b, TwoSegments);

        Assert.Equal(b, f.Store.GetLatestReady()!.Id);
    }

    [Fact]
    public void RecoverInterrupted_requeues_only_transcriptions_the_user_started()
    {
        using var f = new RecordingFixture();
        var recording = f.Store.Start("tr").Id;                // Kaydediliyor, ended_at yok
        var transcribing = f.Store.Start("tr").Id;
        f.Store.Finish(transcribing);
        f.Store.MarkTranscribing(transcribing);                // Cevriliyor
        var waiting = f.Store.Start("tr").Id;
        f.Store.Finish(waiting);
        f.Store.Requeue(waiting);                              // Bekliyor (kullanıcı çeviriyi başlattı)
        var recorded = f.Store.Start("tr").Id;
        f.Store.Finish(recorded);                              // Kaydedildi (kullanıcıyı bekliyor)
        var ready = f.Store.Start("tr").Id;
        f.Store.SaveTranscript(ready, TwoSegments);            // Hazir
        var failed = f.Store.Start("tr").Id;
        f.Store.MarkFailed(failed, "x");                       // Hata
        var deleted = f.Store.Start("tr").Id;
        f.Store.Delete(deleted);
        f.Time.Advance(TimeSpan.FromMinutes(10));

        var ids = f.Reopen().RecoverInterrupted();

        Assert.Equal([transcribing, waiting], ids);
        var r = f.Store.Get(recording);
        Assert.Equal(RecordingStatus.Kaydedildi, r.Status);
        Assert.Equal(f.Time.GetLocalNow(), r.EndedAt);
        Assert.Equal(RecordingStatus.Kaydedildi, f.Store.Get(recorded).Status);
        Assert.Equal(RecordingStatus.Bekliyor, f.Store.Get(waiting).Status);
        Assert.Equal(RecordingStatus.Bekliyor, f.Store.Get(transcribing).Status);
        Assert.Equal(RecordingStatus.Hazir, f.Store.Get(ready).Status);
        Assert.Equal(RecordingStatus.Hata, f.Store.Get(failed).Status);
    }

    [Fact]
    public void Ranges_are_normalized_stored_and_replaced()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;
        Assert.Empty(f.Store.GetRanges(id));

        var saved = f.Store.SetRanges(id, [new(60_000, 90_000), new(0, 10_000), new(80_000, 120_000), new(5, 5)]);

        TimeRange[] expected = [new(0, 10_000), new(60_000, 120_000)];
        Assert.Equal(expected, saved);
        Assert.Equal(expected, f.Reopen().GetRanges(id));

        f.Store.SetRanges(id, []);
        Assert.Empty(f.Store.GetRanges(id));
    }

    [Fact]
    public void Ranges_unknown_recording_throws()
    {
        using var f = new RecordingFixture();
        Assert.Throws<RecordingNotFoundException>(() => f.Store.SetRanges(99, [new(0, 1000)]));
        Assert.Throws<RecordingNotFoundException>(() => f.Store.GetRanges(99));
    }

    [Fact]
    public void RecoverInterrupted_keeps_existing_end_time()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;
        f.Time.Advance(TimeSpan.FromMinutes(5));
        var end = f.Store.Finish(id).EndedAt;
        f.Store.MarkTranscribing(id);
        f.Time.Advance(TimeSpan.FromHours(1));

        f.Store.RecoverInterrupted();

        Assert.Equal(end, f.Store.Get(id).EndedAt);
    }

    [Fact]
    public void ListAudioOlderThan_returns_finished_recordings_with_old_audio()
    {
        using var f = new RecordingFixture();
        var old = f.Store.Start("tr").Id;
        f.Store.Finish(old);
        var purged = f.Store.Start("tr").Id;
        f.Store.Finish(purged);
        f.Store.MarkAudioDeleted(purged);
        var running = f.Store.Start("tr").Id;                  // ended_at yok
        f.Time.Advance(TimeSpan.FromDays(10));
        var fresh = f.Store.Start("tr").Id;
        f.Store.Finish(fresh);
        f.Time.Advance(TimeSpan.FromDays(5));

        var result = f.Store.ListAudioOlderThan(TimeSpan.FromDays(14));

        Assert.Equal([old], result.Select(r => r.Id));
        Assert.True(f.Store.Get(purged).AudioDeleted);
        Assert.DoesNotContain(running, result.Select(r => r.Id));
    }

    [Fact]
    public void ListAudioOlderThan_includes_deleted_recordings_so_leftover_audio_is_purged()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("tr").Id;
        f.Store.Finish(id);
        f.Store.Delete(id);

        var result = f.Store.ListAudioOlderThan(TimeSpan.FromDays(14));

        Assert.Equal([id], result.Select(r => r.Id));
        f.Store.MarkAudioDeleted(id);
        Assert.Empty(f.Store.ListAudioOlderThan(TimeSpan.FromDays(14)));
    }

    [Fact]
    public void Data_survives_reopen()
    {
        using var f = new RecordingFixture();
        var id = f.Store.Start("auto").Id;
        f.Store.SaveTranscript(id, TwoSegments);
        f.Store.SetSummary(id, "özet");

        var reopened = f.Reopen();

        var r = reopened.Get(id);
        Assert.Equal("auto", r.Language);
        Assert.Equal("özet", r.Summary);
        Assert.Equal(TwoSegments, reopened.GetSegments(id));
    }

    [Fact]
    public void Store_shares_database_with_other_stores()
    {
        using var f = new RecordingFixture();
        var notes = new NoteStore(f.DbPath, f.Time, f.Signal);
        var plans = new PlanStore(f.DbPath, f.Time, f.Signal);

        notes.Add("not", "Claude");
        f.Store.Start("tr");

        Assert.Single(notes.List(new NoteFilter()));
        Assert.Single(f.Store.List());
        Assert.Empty(plans.ListPlans());
    }
}
