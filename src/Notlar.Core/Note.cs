namespace Notlar.Core;

public sealed record Note(long Id, string Text, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, bool Done)
{
    public IReadOnlyList<string> Tags => TagParser.Parse(Text);
    public string DisplayText => TagParser.Strip(Text);
    public DateOnly Day => DateOnly.FromDateTime(CreatedAt.DateTime);
}
