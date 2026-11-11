namespace Notlar.Core;

public sealed class TaskNotFoundException(long id) : Exception($"Görev bulunamadı: {id}")
{
    public long Id { get; } = id;
}
