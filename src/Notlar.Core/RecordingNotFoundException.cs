namespace Notlar.Core;

public sealed class RecordingNotFoundException(long id) : Exception($"Kayıt bulunamadı: {id}")
{
    public long Id { get; } = id;
}
