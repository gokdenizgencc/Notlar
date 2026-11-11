using Notlar.Core;

namespace Notlar.Core.Tests;

public class MarkdownExporterTests
{
    private static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    [Fact]
    public void Export_writes_day_headings_checkboxes_and_times()
    {
        Note[] notes =
        [
            new(1, "Kitap önerisi: Dune", new DateTimeOffset(2026, 11, 10, 22, 5, 0, Tr), null, false),
            new(2, "Faturayı öde #ev", new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr), null, true),
            new(3, "Sunumu bitir #iş", new DateTimeOffset(2026, 11, 11, 9, 12, 0, Tr), null, false),
        ];

        var expected =
            "## 11 Kasım 2026, Çarşamba\n" +
            "- [ ] 09:12 Sunumu bitir #iş\n" +
            "- [x] 14:32 Faturayı öde #ev\n" +
            "\n" +
            "## 10 Kasım 2026, Salı\n" +
            "- [ ] 22:05 Kitap önerisi: Dune\n" +
            "\n";

        Assert.Equal(expected, MarkdownExporter.Export(notes));
    }

    [Fact]
    public void Export_flattens_multiline_text()
    {
        Note[] notes = [new(1, "satır1\nsatır2", new DateTimeOffset(2026, 11, 11, 8, 0, 0, Tr), null, false)];
        Assert.Contains("- [ ] 08:00 satır1 satır2\n", MarkdownExporter.Export(notes));
    }

    [Fact]
    public void Export_marks_Claude_notes_with_suffix()
    {
        Note[] notes =
        [
            new(1, "ben notu", new DateTimeOffset(2026, 11, 11, 8, 0, 0, Tr), null, false, NoteAuthor.Ben),
            new(2, "claude notu", new DateTimeOffset(2026, 11, 11, 9, 0, 0, Tr), null, false, NoteAuthor.Claude),
        ];

        var result = MarkdownExporter.Export(notes);

        Assert.Contains("- [ ] 08:00 ben notu\n", result);
        Assert.Contains("- [ ] 09:00 claude notu _(Claude)_\n", result);
    }
}
