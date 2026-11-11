using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notlar.Core;

/// <summary>Bir marketplace'teki tek bir plugin kaydı: kurulu olup olmadığı, (yerel bir kaynaktan
/// geliyorsa) içerdiği skill adları ve plugin-catalog-cache.json'dan gelen popülerlik verisi.</summary>
public sealed record MarketplacePlugin(
    string Marketplace, string Name, string Description, bool Installed, IReadOnlyList<string> Skills,
    int? UniqueInstalls = null, DateTimeOffset? LastUpdated = null, int? AlwaysOnTokens = null)
{
    /// <summary>AlwaysOnTokens'a göre kabaca bağlam yükü: "hafif" (&lt;1000), "orta" (&lt;4000),
    /// "ağır" (≥4000). Veri yoksa null.</summary>
    public string? ContextWeight => AlwaysOnTokens switch
    {
        null => null,
        < 1000 => "hafif",
        < 4000 => "orta",
        _ => "ağır",
    };
}

/// <summary>Keşfet listesi için sıralama seçenekleri.</summary>
public enum PluginSort
{
    /// <summary>Kurulum sayısına göre (azalan, null'lar sonda), sonra isme göre.</summary>
    Popular,
    /// <summary>Son güncellenme zamanına göre (azalan, null'lar sonda).</summary>
    RecentlyUpdated,
    /// <summary>tr-TR kurallarına göre isim sırası.</summary>
    Name,
}

/// <summary>%USERPROFILE%\.claude\plugins\marketplaces altındaki yerel marketplace klonlarını ve
/// %USERPROFILE%\.claude\plugins\installed_plugins.json içindeki kurulu plugin listesini tarayarak
/// "Keşfet" sekmesi için kullanılabilir plugin kataloğunu çıkarır.</summary>
public static partial class MarketplaceScanner
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex PluginNameRegex();

    /// <summary>Plugin/marketplace adı güvenli mi: harf/rakamla başlar, yalnızca ASCII harf, rakam, '.', '_', '-'
    /// içerir ve en fazla 64 karakterdir. Komut satırına (ör. <c>claude plugin install ad@market</c>)
    /// verilmeden önce mutlaka bununla doğrulanmalıdır.</summary>
    public static bool IsValidPluginName(string? name) => name is not null && PluginNameRegex().IsMatch(name);

    public static string DefaultClaudeDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>claudeDir\plugins\marketplaces altındaki her marketplace klasöründeki
    /// .claude-plugin\marketplace.json dosyasını okur, içindeki plugin'leri kurulu olup olmadıklarıyla
    /// birlikte, plugin-catalog-cache.json'dan gelen popülerlik verisiyle eşleyerek döndürür.
    /// Sonuç varsayılan olarak <see cref="PluginSort.Popular"/> sırasındadır.</summary>
    public static IReadOnlyList<MarketplacePlugin> ScanAvailable(string? claudeDir = null)
    {
        claudeDir ??= DefaultClaudeDir;
        var marketplacesDir = Path.Combine(claudeDir, "plugins", "marketplaces");
        var installed = ScanInstalledNames(claudeDir);
        var catalog = PluginCatalogCache.Load(claudeDir);

        var result = new List<MarketplacePlugin>();
        if (!Directory.Exists(marketplacesDir)) return result;

        foreach (var marketDir in TryGetDirectories(marketplacesDir))
        {
            var marketName = Path.GetFileName(marketDir);
            if (!IsValidPluginName(marketName)) continue;
            var manifestPath = Path.Combine(marketDir, ".claude-plugin", "marketplace.json");
            if (!File.Exists(manifestPath)) continue;

            foreach (var plugin in ParseManifest(manifestPath, marketDir))
            {
                var key = $"{plugin.Name}@{marketName}";
                var isInstalled = installed.Contains(key);
                var withMarket = plugin with { Marketplace = marketName, Installed = isInstalled };

                if (catalog.TryGetValue(key, out var entry))
                {
                    withMarket = withMarket with
                    {
                        UniqueInstalls = entry.UniqueInstalls,
                        LastUpdated = entry.LastUpdated,
                        AlwaysOnTokens = entry.AlwaysOnTokens,
                    };
                }

                result.Add(withMarket);
            }
        }

        return Sort(result, PluginSort.Popular);
    }

    /// <summary>Plugin listesini verilen kritere göre sıralar; orijinal listeyi değiştirmez.</summary>
    public static IReadOnlyList<MarketplacePlugin> Sort(IReadOnlyList<MarketplacePlugin> plugins, PluginSort sort)
    {
        var trTr = CultureInfo.GetCultureInfo("tr-TR");
        IEnumerable<MarketplacePlugin> ordered = sort switch
        {
            PluginSort.Popular => plugins
                .OrderByDescending(p => p.UniqueInstalls.HasValue)
                .ThenByDescending(p => p.UniqueInstalls ?? int.MinValue)
                .ThenBy(p => p.Name, StringComparer.Create(trTr, ignoreCase: false)),
            PluginSort.RecentlyUpdated => plugins
                .OrderByDescending(p => p.LastUpdated.HasValue)
                .ThenByDescending(p => p.LastUpdated ?? DateTimeOffset.MinValue),
            PluginSort.Name => plugins
                .OrderBy(p => p.Name, StringComparer.Create(trTr, ignoreCase: false)),
            _ => plugins,
        };
        return ordered.ToList();
    }

    /// <summary>Kurulum sayısını tr-TR kurallarına göre okunabilir biçimde döndürür:
    /// 950 → "950 kurulum", 1636 → "1,6 bin kurulum", 23400 → "23,4 bin kurulum",
    /// 1250000 → "1,3 milyon kurulum".</summary>
    public static string FormatInstalls(int n)
    {
        var trTr = CultureInfo.GetCultureInfo("tr-TR");

        if (n < 1000) return $"{n.ToString(trTr)} kurulum";

        if (n < 1_000_000)
        {
            var thousands = n / 1000.0;
            return $"{FormatOneDecimal(thousands, trTr)} bin kurulum";
        }

        var millions = n / 1_000_000.0;
        return $"{FormatOneDecimal(millions, trTr)} milyon kurulum";
    }

    private static string FormatOneDecimal(double value, CultureInfo culture)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("0.0", culture);
        if (text.EndsWith(",0", StringComparison.Ordinal))
            text = text[..^2];
        return text;
    }

    /// <summary>claudeDir\plugins\installed_plugins.json içindeki "plugin@marketplace" anahtarlarını
    /// döndürür. Dosya yoksa/okunamazsa boş küme döner.</summary>
    public static IReadOnlySet<string> ScanInstalledNames(string? claudeDir = null)
    {
        claudeDir ??= DefaultClaudeDir;
        var path = Path.Combine(claudeDir, "plugins", "installed_plugins.json");
        try
        {
            if (!File.Exists(path)) return new HashSet<string>();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
                return new HashSet<string>();

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in plugins.EnumerateObject())
                set.Add(prop.Name);
            return set;
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private static IEnumerable<MarketplacePlugin> ParseManifest(string manifestPath, string marketDir)
    {
        List<JsonElement> pluginElements;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                yield break;
            pluginElements = plugins.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var el in pluginElements)
        {
            if (!el.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String) continue;
            var name = nameProp.GetString();
            if (!IsValidPluginName(name)) continue;

            var description = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";

            var localPath = TryGetLocalSourcePath(el);
            var skills = localPath is null ? [] : ScanLocalSkills(Path.Combine(marketDir, localPath));

            yield return new MarketplacePlugin("", name!, description, false, skills);
        }
    }

    /// <summary>"source" alanı doğrudan bir string ise (ör. "./" veya "./claude-plugin") yerel bir yol
    /// kabul edilir; "source" bir obje ise (ör. {"source":"git-subdir", ...}) uzak bir kaynaktır, null
    /// döner.</summary>
    private static string? TryGetLocalSourcePath(JsonElement plugin)
    {
        if (!plugin.TryGetProperty("source", out var source)) return null;
        if (source.ValueKind != JsonValueKind.String) return null;

        var value = source.GetString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("://", StringComparison.Ordinal)) return null;
        // Marketplace klasörünün dışına çıkmaya izin verme: köklü yollar ve '..' segmentleri reddedilir.
        if (Path.IsPathRooted(value) || value.Contains(':')) return null;
        if (value.Split('/', '\\').Any(seg => seg == "..")) return null;
        return value;
    }

    private static IReadOnlyList<string> ScanLocalSkills(string pluginDir)
    {
        var skillsDir = Path.Combine(pluginDir, "skills");
        if (!Directory.Exists(skillsDir)) return [];

        var result = new List<string>();
        foreach (var dir in TryGetDirectories(skillsDir))
        {
            var skillMd = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillMd))
                result.Add(Path.GetFileName(dir));
        }
        return result;
    }

    private static IReadOnlyList<string> TryGetDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
