namespace Notlar.Core;

public sealed record MemoryItem(long Id, string Title, string Content, string Category, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
