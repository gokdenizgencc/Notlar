namespace Notlar.Core;

/// <summary>Kullanıcının kayıtlı bir projesi: bir ad ve o projenin bulunduğu klasörün yolu. Planlar
/// isteğe bağlı olarak bir projeye bağlanabilir; proje taşınır/yeniden adlandırılırsa bağlı planlar
/// güncel yolu otomatik kullanır.</summary>
public sealed record Project(long Id, string Name, string Path, DateTimeOffset CreatedAt);
