using System.Text.Json;

namespace Notlar.Core;

/// <summary>Bir marketplace'teki tek bir plugin kaydı: kurulu olup olmadığı ve (yerel bir kaynaktan
/// geliyorsa) içerdiği skill adları.</summary>
public sealed record MarketplacePlugin(
    string Marketplace, string Name, string Description, bool Installed, IReadOnlyList<string> Skills);

/// <summary>%USERPROFILE%\.claude\plugins\marketplaces altındaki yerel marketplace klonlarını ve
/// %USERPROFILE%\.claude\plugins\installed_plugins.json içindeki kurulu plugin listesini tarayarak
/// "Keşfet" sekmesi için kullanılabilir plugin kataloğunu çıkarır.</summary>
public static class MarketplaceScanner
{
    public static string DefaultClaudeDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>claudeDir\plugins\marketplaces altındaki her marketplace klasöründeki
    /// .claude-plugin\marketplace.json dosyasını okur ve içindeki plugin'leri, kurulu olup olmadıklarıyla
    /// birlikte döndürür.</summary>
    public static IReadOnlyList<MarketplacePlugin> ScanAvailable(string? claudeDir = null)
    {
        claudeDir ??= DefaultClaudeDir;
        var marketplacesDir = Path.Combine(claudeDir, "plugins", "marketplaces");
        var installed = ScanInstalledNames(claudeDir);

        var result = new List<MarketplacePlugin>();
        if (!Directory.Exists(marketplacesDir)) return result;

        foreach (var marketDir in TryGetDirectories(marketplacesDir))
        {
            var marketName = Path.GetFileName(marketDir);
            var manifestPath = Path.Combine(marketDir, ".claude-plugin", "marketplace.json");
            if (!File.Exists(manifestPath)) continue;

            foreach (var plugin in ParseManifest(manifestPath, marketDir))
            {
                var isInstalled = installed.Contains($"{plugin.Name}@{marketName}");
                result.Add(plugin with { Marketplace = marketName, Installed = isInstalled });
            }
        }

        return result;
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
            if (string.IsNullOrWhiteSpace(name)) continue;

            var description = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";

            var localPath = TryGetLocalSourcePath(el);
            var skills = localPath is null ? [] : ScanLocalSkills(Path.Combine(marketDir, localPath));

            yield return new MarketplacePlugin("", name, description, false, skills);
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
