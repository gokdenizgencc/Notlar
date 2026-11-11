namespace Notlar.Core;

public sealed class MemoryNotFoundException(long id) : Exception($"Hafıza kaydı bulunamadı: {id}")
{
    public long Id { get; } = id;
}
