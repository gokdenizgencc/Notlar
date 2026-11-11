using System.Globalization;
using System.Text.Json;

namespace Notlar.Core;

/// <summary>plugin-catalog-cache.json içindeki tek bir plugin kaydının popülerlik verisi:
/// kurulum sayısı, son güncellenme zamanı ve en yüksek "always_on" bağlam token sayısı.</summary>
public sealed record PluginCatalogEntry(int? UniqueInstalls, DateTimeOffset? LastUpdated, int? AlwaysOnTokens);

/// <summary>%USERPROFILE%\.claude\plugins\plugin-catalog-cache.json dosyasını okuyarak "Keşfet"
/// sekmesindeki marketplace plugin'leri için popülerlik/bağlam verisini sağlar. Dosya yoksa veya
/// bozuksa (eksik alanlar, geçersiz JSON) hiçbir şey fırlatmaz; boş sonuç döner.</summary>
public static class PluginCatalogCache
{
    /// <summary>claudeDir\plugins\plugin-catalog-cache.json dosyasını okur ve "plugin@marketplace"
    /// anahtarına göre eşlenmiş girişleri döndürür.</summary>
    public static IReadOnlyDictionary<string, PluginCatalogEntry> Load(string? claudeDir)
    {
        var result = new Dictionary<string, PluginCatalogEntry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(claudeDir)) return result;

        var path = Path.Combine(claudeDir, "plugins", "plugin-catalog-cache.json");
        try
        {
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("catalog", out var catalog) || catalog.ValueKind != JsonValueKind.Object)
                return result;
            if (!catalog.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var prop in plugins.EnumerateObject())
            {
                var key = prop.Name;
                var value = prop.Value;
                if (value.ValueKind != JsonValueKind.Object) continue;

                int? uniqueInstalls = TryGetInt(value, "unique_installs");
                DateTimeOffset? lastUpdated = TryGetDateTimeOffset(value, "last_updated");
                int? alwaysOnTokens = TryGetMaxAlwaysOn(value);

                result[key] = new PluginCatalogEntry(uniqueInstalls, lastUpdated, alwaysOnTokens);
            }
        }
        catch
        {
            return new Dictionary<string, PluginCatalogEntry>(StringComparer.Ordinal);
        }

        return result;
    }

    private static int? TryGetInt(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.Number) return null;
        return prop.TryGetInt32(out var i) ? i : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        var s = prop.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto
            : null;
    }

    private static int? TryGetMaxAlwaysOn(JsonElement plugin)
    {
        if (!plugin.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            return null;

        int? max = null;
        foreach (var modelProp in tokens.EnumerateObject())
        {
            if (modelProp.Value.ValueKind != JsonValueKind.Object) continue;
            var alwaysOn = TryGetInt(modelProp.Value, "always_on");
            if (alwaysOn is null) continue;
            if (max is null || alwaysOn > max) max = alwaysOn;
        }
        return max;
    }
}
