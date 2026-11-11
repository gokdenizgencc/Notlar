namespace Notlar.Core;

public sealed record TaskItem(long Id, string Title, DateTimeOffset CreatedAt, bool Done, string Author);
