using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notlar.Core;

/// <summary>İçe aktarmanın sonucu: birleştirilmiş ayarlar ve uygulanan / yok sayılan alan adları.</summary>
public sealed record SettingsImportResult(JsonObject Merged, IReadOnlyList<string> Applied, IReadOnlyList<string> Ignored);

/// <summary>Ayarları JSON dosyasına dışa aktarma / dosyadan içe aktarma (saf mantık). İçe aktarırken yalnızca mevcut
/// ayarlarda bulunan, dışlanmamış ve türü uyan alanlar alınır; bilinmeyen ya da geçersiz alanlar yok sayılır.</summary>
public static class SettingsTransfer
{
    /// <summary>İçe aktarılacak dosyanın üst sınırı (ayarlar birkaç KB'dir).</summary>
    public const int MaxImportChars = 1_000_000;

    /// <summary>Dışa aktarım için kopya: <paramref name="excluded"/> alanları çıkarılır.</summary>
    public static JsonObject StripExcluded(JsonObject settings, IReadOnlySet<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = new JsonObject();
        foreach (var (key, value) in settings)
        {
            if (!IsExcluded(key, excluded))
                copy[key] = value?.DeepClone();
        }
        return copy;
    }

    /// <summary><paramref name="importedJson"/>'daki geçerli alanları <paramref name="current"/>'in bir kopyasına uygular.
    /// Alan adları büyük/küçük harfe duyarsız eşleşir (mevcut adla yazılır). <paramref name="validate"/> verilirse her aday
    /// alan için ayrıca sorulur (ör. gerçek tipe çözülebiliyor mu). JSON bir nesne değilse <see cref="FormatException"/>.</summary>
    public static SettingsImportResult Merge(JsonObject current, string importedJson, IReadOnlySet<string> excluded,
        Func<string, JsonNode?, bool>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(importedJson))
            throw new FormatException("Dosya boş.");
        if (importedJson.Length > MaxImportChars)
            throw new FormatException("Dosya bir ayarlar dosyası olamayacak kadar büyük.");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(importedJson, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException("Dosya geçerli bir JSON değil: " + ex.Message, ex);
        }
        if (root is not JsonObject imported)
            throw new FormatException("Dosya bir ayarlar nesnesi içermiyor.");

        var merged = (JsonObject)current.DeepClone();
        var canonical = current.Select(kv => kv.Key).ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
        var applied = new List<string>();
        var ignored = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, value) in imported)
        {
            if (!canonical.TryGetValue(rawKey, out var key) || IsExcluded(key, excluded) || !seen.Add(key)
                || !IsCompatible(current[key], value) || (validate is not null && !validate(key, value)))
            {
                ignored.Add(rawKey);
                continue;
            }
            merged[key] = value?.DeepClone();
            applied.Add(key);
        }
        return new SettingsImportResult(merged, applied, ignored);
    }

    /// <summary>İçe aktarılan değerin türü mevcut değerinkiyle uyuşuyor mu? Mevcut değer null ise (boş bırakılabilen alan)
    /// null ya da basit değer (metin/sayı/mantıksal) kabul edilir; dolu alana null yazılmaz.</summary>
    internal static bool IsCompatible(JsonNode? current, JsonNode? imported)
    {
        var currentKind = current?.GetValueKind() ?? JsonValueKind.Null;
        var importedKind = imported?.GetValueKind() ?? JsonValueKind.Null;
        return currentKind switch
        {
            JsonValueKind.Null => importedKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False,
            JsonValueKind.True or JsonValueKind.False => importedKind is JsonValueKind.True or JsonValueKind.False,
            _ => importedKind == currentKind,
        };
    }

    private static bool IsExcluded(string key, IReadOnlySet<string> excluded) =>
        excluded.Contains(key) || excluded.Any(e => string.Equals(e, key, StringComparison.OrdinalIgnoreCase));
}
