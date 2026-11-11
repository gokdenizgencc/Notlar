namespace Notlar.Core;

public sealed class NoteNotFoundException(long id) : Exception($"Not bulunamadı: {id}")
{
    public long Id { get; } = id;
}
