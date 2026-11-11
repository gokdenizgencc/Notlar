using System.Globalization;
using Notlar.Core;

namespace Notlar.Mcp;

internal static class NoteFormatter
{
    public static string Line(Note note) =>
        $"{(note.Done ? "[x]" : "[ ]")} {note.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture)} {note.Text.ReplaceLineEndings(" ")}  (id: {note.Id})";
}
