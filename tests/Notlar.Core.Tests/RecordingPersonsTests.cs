using Microsoft.Data.Sqlite;
using Notlar.Core;
using Notlar.Mcp;

namespace Notlar.Core.Tests;

public class RecordingPersonsTests
{
    private static readonly TranscriptSegment[] Labelled =
    [
        new(5_000, 6_000, Speaker.Ben, "Merhaba, başlayalım.", 1),
        new(8_000, 9_000, Speaker.KarsiTaraf, "Tamam.", 2),
        new(10_000, 11_000, Speaker.Ben, "Bütçeyi cuma gönderelim.", 1),
    ];

    private static readonly RecordingPerson[] Persons =
    [
        new(1, "Ahmet", VoiceGender.Erkek, 118.5, null, [0.6f, 0.8f]),
        new(2, "Ayşe", VoiceGender.Kadin, 212, null, null),
    ];

    private static Recording Ready(RecordingFixture f, IReadOnlyList<RecordingPerson>? persons = null)
    {
        var r = f.Store.Start("tr");
        f.Store.Finish(r.Id);
        return f.Store.SaveTranscript(r.Id, Labelled, persons ?? Persons);
    }

    [Fact]
    public void Saved_persons_round_trip_and_name_the_segments()
    {
        using var f = new RecordingFixture();
        var r = Ready(f);

        var persons = f.Store.GetPersons(r.Id);
        Assert.Equal(2, persons.Count);
        Assert.Equal("Ahmet", persons[0].Name);
        Assert.Equal(VoiceGender.Erkek, persons[0].Gender);
        Assert.Equal(118.5, persons[0].MedianF0Hz);
        Assert.Equal([0.6f, 0.8f], persons[0].Voiceprint!);
        Assert.Null(persons[1].Voiceprint);

        var segments = f.Store.GetSegments(r.Id);
        Assert.Equal(["Ahmet", "Ayşe", "Ahmet"], segments.Select(s => s.PersonName));
        Assert.All(segments, s => Assert.Equal(PersonNameSource.Tahmin, s.PersonNameSource));
        Assert.Equal("[00:05] Ahmet: Merhaba, başlayalım.\n[00:08] Ayşe: Tamam.\n[00:10] Ahmet: Bütçeyi cuma gönderelim.",
            TranscriptFormatter.Format(segments));
    }

    [Fact]
    public void Rename_updates_every_line_of_that_person_and_can_be_reset()
    {
        using var f = new RecordingFixture();
        var r = Ready(f);
        var before = f.Signal.Count;

        var renamed = f.Store.RenamePerson(r.Id, 1, "  Kemal   Yılmaz ");

        Assert.Equal("Kemal Yılmaz", renamed.Name);
        Assert.Equal("Ahmet", renamed.AutoName);
        Assert.True(renamed.IsUserNamed);
        Assert.True(f.Signal.Count > before);
        var segments = f.Store.GetSegments(r.Id);
        Assert.Equal(["Kemal Yılmaz", "Ayşe", "Kemal Yılmaz"], segments.Select(s => s.PersonName));
        Assert.Equal([PersonNameSource.Kullanici, PersonNameSource.Tahmin, PersonNameSource.Kullanici],
            segments.Select(s => s.PersonNameSource));

        var reset = f.Store.RenamePerson(r.Id, 1, " ");
        Assert.Equal("Ahmet", reset.Name);
        Assert.False(reset.IsUserNamed);
        Assert.Equal("Ahmet", f.Store.GetSegments(r.Id)[0].PersonName);
    }

    [Fact]
    public void Rename_rejects_unknown_person_invalid_name_and_missing_recording()
    {
        using var f = new RecordingFixture();
        var r = Ready(f);

        Assert.Throws<ArgumentException>(() => f.Store.RenamePerson(r.Id, 3, "Ali"));
        Assert.Throws<ArgumentException>(() => f.Store.RenamePerson(r.Id, 1, "Ali: Veli"));
        Assert.Throws<RecordingNotFoundException>(() => f.Store.RenamePerson(999, 1, "Ali"));
    }

    [Fact]
    public void Legacy_transcript_without_names_shows_numbers_and_can_be_renamed()
    {
        using var f = new RecordingFixture();
        var r = f.Store.Start("tr");
        f.Store.SaveTranscript(r.Id, Labelled); // adlandırmadan önceki sürüm: kişi var, ad yok

        var segments = f.Store.GetSegments(r.Id);
        Assert.All(segments, s => Assert.Null(s.PersonName));
        Assert.Empty(f.Store.GetPersons(r.Id));
        Assert.StartsWith("[00:05] Kişi 1: Merhaba", TranscriptFormatter.Format(segments));

        f.Store.RenamePerson(r.Id, 2, "Selin");
        Assert.Equal("[00:05] Kişi 1: Merhaba, başlayalım.\n[00:08] Selin: Tamam.\n[00:10] Kişi 1: Bütçeyi cuma gönderelim.",
            TranscriptFormatter.Format(f.Store.GetSegments(r.Id)));
        var person = Assert.Single(f.Store.GetPersons(r.Id));
        Assert.Equal("Kişi 2", person.AutoName);

        f.Store.RenamePerson(r.Id, 2, null);
        Assert.Equal("Kişi 2", f.Store.GetSegments(r.Id)[1].PersonName);
        Assert.StartsWith("[00:05] Kişi 1: Merhaba", TranscriptFormatter.Format(f.Store.GetSegments(r.Id)));
    }

    [Fact]
    public void Retranscription_replaces_persons()
    {
        using var f = new RecordingFixture();
        var r = Ready(f);
        f.Store.RenamePerson(r.Id, 1, "Kemal");

        f.Store.SaveTranscript(r.Id, Labelled, [new RecordingPerson(1, "Mehmet", VoiceGender.Erkek, 120, null, null)]);

        var persons = f.Store.GetPersons(r.Id);
        Assert.Equal("Mehmet", Assert.Single(persons).Name);
        Assert.Equal([ "Mehmet", null, "Mehmet" ], f.Store.GetSegments(r.Id).Select(s => s.PersonName));

        f.Store.SaveTranscript(r.Id, [new(0, 1000, Speaker.Ben, "etiketsiz")]);
        Assert.Empty(f.Store.GetPersons(r.Id));
    }

    [Fact]
    public void Old_database_gets_the_persons_table()
    {
        using var f = new RecordingFixture();
        _ = f.Store; // şema kuruldu; tabloyu düşürüp eski veritabanını taklit et
        using (var conn = new SqliteConnection($"Data Source={f.DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE recording_persons";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = f.Reopen();
        var r = store.Start("tr");
        store.SaveTranscript(r.Id, Labelled, Persons);
        Assert.Equal("Ahmet", store.GetSegments(r.Id)[0].PersonName);
    }

    [Fact]
    public void Mcp_and_prompts_mark_guessed_and_real_names()
    {
        using var f = new RecordingFixture();
        var r = Ready(f);
        f.Store.RenamePerson(r.Id, 2, "Zeynep Kaya");

        var result = new RecordingTools(f.Store).KayitGetir(r.Id);
        Assert.Contains("[00:05] Ahmet: Merhaba, başlayalım.", result);
        Assert.Contains("[00:08] Zeynep Kaya: Tamam.", result);
        Assert.Contains("- Ahmet: sesin tonundan tahmin edilmiş TAKMA ad", result);
        Assert.Contains("- Zeynep Kaya: kullanıcının verdiği GERÇEK ad", result);
        Assert.True(result.IndexOf("Konuşmacılar:", StringComparison.Ordinal) < result.IndexOf("<transkript_verisi>", StringComparison.Ordinal));

        var segs = f.Store.GetSegments(r.Id);
        foreach (var input in new[] { ClaudeHeadless.BuildSummaryInput(f.Store.Get(r.Id), segs), ClaudeHeadless.BuildPlanInput(f.Store.Get(r.Id), segs) })
        {
            Assert.Contains("TAKMA ad diye işaretlenenleri", input);
            Assert.Contains("- Ahmet: sesin tonundan tahmin edilmiş TAKMA ad", input);
            Assert.Contains("- Zeynep Kaya: kullanıcının verdiği GERÇEK ad", input);
            Assert.Contains("[00:08] Zeynep Kaya: Tamam.", input);
        }
    }
}
