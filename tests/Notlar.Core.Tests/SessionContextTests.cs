using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class SessionContextFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public MemoryStore Memory { get; }
    public NoteStore Notes { get; }
    public DateOnly Today => DateOnly.FromDateTime(Time.GetLocalNow().DateTime);

    public SessionContextFixture()
    {
        Memory = new MemoryStore(DbPath, Time, Signal);
        Notes = new NoteStore(DbPath, Time, Signal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class SessionContextTests
{
    [Fact]
    public void Build_includes_header_with_turkish_date()
    {
        using var f = new SessionContextFixture();
        var result = SessionContext.Build(f.Memory, f.Notes, f.Today);
        Assert.StartsWith("# Notlar bağlamı (11 Kasım 2026, Çarşamba)", result);
    }

    [Fact]
    public void Build_prints_yok_for_all_empty_sections()
    {
        using var f = new SessionContextFixture();
        var result = SessionContext.Build(f.Memory, f.Notes, f.Today);

        Assert.Contains("## Hafıza\n(yok)", result);
        Assert.Contains("## Son notlar\n(yok)", result);
    }

    [Fact]
    public void Build_groups_memory_by_category_with_title_colon_content()
    {
        using var f = new SessionContextFixture();
        f.Memory.Add("Ahmet Bey", "ClickUp/Asana aktarma işleri", "kişiler");
        f.Memory.Add("Notlar projesi", "Claude hafızası ekleniyor", "projeler");

        var result = SessionContext.Build(f.Memory, f.Notes, f.Today);

        Assert.Contains("### kişiler\n- Ahmet Bey: ClickUp/Asana aktarma işleri", result);
        Assert.Contains("### projeler\n- Notlar projesi: Claude hafızası ekleniyor", result);
    }

    [Fact]
    public void Build_truncates_long_memory_content_to_about_200_chars()
    {
        using var f = new SessionContextFixture();
        var longContent = new string('a', 300);
        f.Memory.Add("başlık", longContent);

        var result = SessionContext.Build(f.Memory, f.Notes, f.Today);

        Assert.Contains("…", result);
        Assert.DoesNotContain(new string('a', 300), result);
    }

    [Fact]
    public void Build_includes_today_and_yesterday_notes_but_not_older()
    {
        using var f = new SessionContextFixture();
        f.Notes.AddAt("iki gün önceki not", f.Time.GetLocalNow().AddDays(-2));
        f.Notes.AddAt("dünkü not", f.Time.GetLocalNow().AddDays(-1));
        f.Notes.Add("bugünkü not");

        var result = SessionContext.Build(f.Memory, f.Notes, f.Today);

        Assert.Contains("BUGÜN", result);
        Assert.Contains("bugünkü not", result);
        Assert.Contains("DÜN", result);
        Assert.Contains("dünkü not", result);
        Assert.DoesNotContain("iki gün önceki not", result);
    }

    [Fact]
    public void Build_truncates_notes_section_first_when_over_budget()
    {
        using var f = new SessionContextFixture();
        for (var i = 0; i < 400; i++)
            f.Notes.Add($"not numara {i} biraz daha uzun metin doldurmak icin");

        var result = SessionContext.Build(f.Memory, f.Notes, f.Today);

        Assert.True(result.Length <= 6000, $"Uzunluk {result.Length}");
        Assert.Contains("daha fazlası için notlari_listele", result);
    }
}
