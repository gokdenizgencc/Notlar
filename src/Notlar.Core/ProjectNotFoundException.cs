namespace Notlar.Core;

public sealed class ProjectNotFoundException : Exception
{
    public ProjectNotFoundException(long id) : base($"Proje bulunamadı: {id}") { }

    public ProjectNotFoundException(string key) : base($"Proje bulunamadı: {key}") { }
}
