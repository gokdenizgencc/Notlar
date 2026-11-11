using System.Globalization;
using System.Text;

namespace Notlar.Core;

/// <summary>Claude Code oturum başlangıcında (hook: `NotlarMcp.exe --baglam`) stdout'a yazılan, Claude'un
/// bağlamına eklenecek kompakt Türkçe özet: hafıza kartları, açık görevler ve son iki günün notları.</summary>
public static class SessionContext
{
    private const int MaxLength = 6000;
    private const int MaxContentLength = 200;
    private const string TruncationMarker = "… (daha fazlası için notlari_listele)";
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Build(MemoryStore memory, NoteStore notes, DateOnly today)
    {
        var header = $"# Notlar bağlamı ({DayGrouper.FormatDate(today)})";

        var memorySection = BuildMemorySection(memory);
        var noteLines = BuildNoteLines(notes, today);

        var result = Join(header, memorySection, FormatNotesSection(noteLines));

        if (result.Length > MaxLength)
        {
            // Önce notlar bölümünü kısaltarak boşluk aç.
            while (noteLines.Count > 0 &&
                   Join(header, memorySection, FormatNotesSection(noteLines, truncated: true)).Length > MaxLength)
                noteLines.RemoveAt(noteLines.Count - 1);
            result = Join(header, memorySection, FormatNotesSection(noteLines, truncated: true));
        }

        return result;
    }

    private static string Join(string header, string memorySection, string notesSection) =>
        string.Join("\n\n", header, memorySection, notesSection);

    private static string BuildMemorySection(MemoryStore memory)
    {
        var sb = new StringBuilder("## Hafıza");
        var items = memory.List();
        if (items.Count == 0)
        {
            sb.Append('\n').Append("(yok)");
            return sb.ToString();
        }

        foreach (var group in items.GroupBy(m => m.Category))
        {
            sb.Append('\n').Append("### ").Append(group.Key);
            foreach (var item in group)
                sb.Append('\n').Append("- ").Append(item.Title).Append(": ").Append(TruncateContent(item.Content));
        }
        return sb.ToString();
    }

    private static string TruncateContent(string content)
    {
        var flat = content.ReplaceLineEndings(" ");
        return flat.Length <= MaxContentLength ? flat : flat[..MaxContentLength] + "…";
    }

    private static List<(DateOnly Day, string Line)> BuildNoteLines(NoteStore notes, DateOnly today)
    {
        var list = notes.List(new NoteFilter(From: today.AddDays(-1), To: today));
        var lines = new List<(DateOnly, string)>();
        foreach (var group in DayGrouper.Group(list))
        {
            lines.Add((group.Date, $"### {DayGrouper.Header(group.Date, today)}"));
            foreach (var n in group.Notes)
                lines.Add((group.Date, $"- {n.CreatedAt:HH:mm} {n.Text.ReplaceLineEndings(" ")} ({n.Author})"));
        }
        return lines;
    }

    private static string FormatNotesSection(List<(DateOnly Day, string Line)> noteLines, bool truncated = false)
    {
        var sb = new StringBuilder("## Son notlar");
        if (noteLines.Count == 0 && !truncated)
        {
            sb.Append('\n').Append("(yok)");
            return sb.ToString();
        }

        foreach (var (_, line) in noteLines)
            sb.Append('\n').Append(line);
        if (truncated)
            sb.Append('\n').Append(TruncationMarker);
        return sb.ToString();
    }
}
