namespace Notlar.Core;

public sealed record Note(long Id, string Text, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, bool Done, string Author = NoteAuthor.Ben)
{
    // Tags/DisplayText tembel hesaplanır ve önbelleğe alınır. Önbellek hesaplandığı Text referansını taşır;
    // `with { Text = ... }` kopyası önbelleği devralsa bile referans uyuşmadığından yeniden hesaplanır.
    // Önbellek eşitlik/hash'e dahil değildir (bkz. Equals/GetHashCode).
    private sealed class Derived(string text)
    {
        public readonly string Text = text;
        public readonly IReadOnlyList<string> Tags = TagParser.Parse(text);
        public readonly string DisplayText = TagParser.Strip(text);
    }

    private Derived? _derived;

    private Derived Cache
    {
        get
        {
            var d = _derived;
            if (d is null || !ReferenceEquals(d.Text, Text))
                _derived = d = new Derived(Text);
            return d;
        }
    }

    public IReadOnlyList<string> Tags => Cache.Tags;
    public string DisplayText => Cache.DisplayText;
    public DateOnly Day => DateOnly.FromDateTime(CreatedAt.DateTime);

    public bool Equals(Note? other) =>
        other is not null &&
        Id == other.Id &&
        Text == other.Text &&
        CreatedAt.Equals(other.CreatedAt) &&
        Nullable.Equals(UpdatedAt, other.UpdatedAt) &&
        Done == other.Done &&
        Author == other.Author;

    public override int GetHashCode() => HashCode.Combine(Id, Text, CreatedAt, UpdatedAt, Done, Author);
}
