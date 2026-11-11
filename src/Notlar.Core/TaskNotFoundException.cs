namespace Notlar.Core;

public sealed class TaskNotFoundException(long id) : Exception($"Kişisel kayıt bulunamadı: {id}")
{
    public long Id { get; } = id;
}
