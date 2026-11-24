using System.Security.Cryptography;

namespace Notlar.Kayit;

/// <summary>İndirilen model dosyalarının bütünlük denetimi: dosyanın SHA-256 özeti koda sabitlenmiş beklenen değerle
/// karşılaştırılır. HTTPS yalnızca aktarımı korur; bu denetim kaynağın kendisinin değişmesine karşıdır.</summary>
internal static class ModelIntegrity
{
    /// <summary>Özet uyuşmazsa <see cref="InvalidDataException"/> fırlatır (çağıran <c>.part</c> dosyasını siler).</summary>
    public static async Task VerifyAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        string actual;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"İndirilen model dosyasının özeti beklenenle uyuşmuyor; dosya kullanılmadı ({Path.GetFileName(path)}).");
    }
}
