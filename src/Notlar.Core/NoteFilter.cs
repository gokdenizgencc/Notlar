namespace Notlar.Core;

public sealed record NoteFilter(DateOnly? From = null, DateOnly? To = null, string? Tag = null, bool OnlyOpen = false, NoteAuthor? Author = null);
