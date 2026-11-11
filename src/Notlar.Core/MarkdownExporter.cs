using System.Globalization;
using System.Text;

namespace Notlar.Core;

public static class MarkdownExporter
{
    public static string Export(IEnumerable<Note> notes)
    {
        var sb = new StringBuilder();
        foreach (var group in DayGrouper.Group(notes))
        {
            sb.Append("## ").Append(DayGrouper.FormatDate(group.Date)).Append('\n');
            foreach (var note in group.Notes)
            {
                sb.Append(note.Done ? "- [x] " : "- [ ] ")
                  .Append(note.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(note.Text.ReplaceLineEndings(" "));
                if (note.Author == NoteAuthor.Claude)
                    sb.Append(" _(Claude)_");
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
