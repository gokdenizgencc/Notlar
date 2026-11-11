namespace Notlar.Core;

public enum NoteAuthor
{
    Ben,
    Claude
}

public static class NoteAuthorExtensions
{
    public static string ToDbValue(this NoteAuthor author) => author switch
    {
        NoteAuthor.Ben => "ben",
        NoteAuthor.Claude => "claude",
        _ => throw new ArgumentOutOfRangeException(nameof(author), author, null)
    };

    public static string DisplayName(this NoteAuthor author) => author switch
    {
        NoteAuthor.Ben => "Ben",
        NoteAuthor.Claude => "Claude",
        _ => throw new ArgumentOutOfRangeException(nameof(author), author, null)
    };

    public static bool TryParse(string? value, out NoteAuthor author)
    {
        if (string.Equals(value, "ben", StringComparison.OrdinalIgnoreCase))
        {
            author = NoteAuthor.Ben;
            return true;
        }
        if (string.Equals(value, "claude", StringComparison.OrdinalIgnoreCase))
        {
            author = NoteAuthor.Claude;
            return true;
        }
        author = default;
        return false;
    }
}
