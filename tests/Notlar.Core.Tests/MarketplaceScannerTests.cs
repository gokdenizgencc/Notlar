using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class FakeMarketplaceDir : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-market-" + Guid.NewGuid().ToString("N"));

    public string MarketplacesDir => Path.Combine(Dir, "plugins", "marketplaces");

    public void AddMarketplace(string marketName, string manifestJson)
    {
        var dir = Path.Combine(MarketplacesDir, marketName, ".claude-plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marketplace.json"), manifestJson);
    }

    public void AddLocalSkill(string marketName, string relativePluginPath, string skillName)
    {
        var dir = Path.Combine(MarketplacesDir, marketName, relativePluginPath, "skills", skillName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: " + skillName + "\ndescription: test\n---\nBody");
    }

    public void SetInstalled(string json)
    {
        var dir = Path.Combine(Dir, "plugins");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "installed_plugins.json"), json);
    }

    public void SetCatalogCache(string json)
    {
        var dir = Path.Combine(Dir, "plugins");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin-catalog-cache.json"), json);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class MarketplaceScannerTests
{
    [Fact]
    public void ScanAvailable_returns_empty_when_no_marketplaces_dir()
    {
        using var f = new FakeMarketplaceDir();
        var result = MarketplaceScanner.ScanAvailable(f.Dir);
        Assert.Empty(result);
    }

    [Fact]
    public void ScanAvailable_parses_plugins_from_manifest_with_string_source()
    {
        using var f = new FakeMarketplaceDir();
        f.AddMarketplace("ponytail", """
            {
              "name": "ponytail",
              "plugins": [
                { "name": "ponytail", "description": "Lazy dev mode.", "source": "./" }
              ]
            }
            """);
        f.AddLocalSkill("ponytail", "", "ponytail");
        f.AddLocalSkill("ponytail", "", "ponytail-audit");

        var result = MarketplaceScanner.ScanAvailable(f.Dir);

        var plugin = Assert.Single(result);
        Assert.Equal("ponytail", plugin.Marketplace);
        Assert.Equal("ponytail", plugin.Name);
        Assert.Equal("Lazy dev mode.", plugin.Description);
        Assert.False(plugin.Installed);
        Assert.Equal(["ponytail", "ponytail-audit"], plugin.Skills.OrderBy(s => s));
    }

    [Fact]
    public void ScanAvailable_treats_object_source_as_remote_without_skills()
    {
        using var f = new FakeMarketplaceDir();
        f.AddMarketplace("official", """
            {
              "name": "official",
              "plugins": [
                { "name": "foo", "description": "Remote plugin.",
                  "source": { "source": "git-subdir", "url": "https://example.com/x.git", "path": "plugins/foo" } }
              ]
            }
            """);

        var result = MarketplaceScanner.ScanAvailable(f.Dir);

        var plugin = Assert.Single(result);
        Assert.Empty(plugin.Skills);
    }

    [Fact]
    public void ScanAvailable_marks_installed_plugins()
    {
        using var f = new FakeMarketplaceDir();
        f.AddMarketplace("ponytail", """
            { "name": "ponytail", "plugins": [ { "name": "ponytail", "description": "d", "source": "./" } ] }
            """);
        f.SetInstalled("""
            { "version": 2, "plugins": { "ponytail@ponytail": [ { "scope": "user" } ] } }
            """);

        var result = MarketplaceScanner.ScanAvailable(f.Dir);

        var plugin = Assert.Single(result);
        Assert.True(plugin.Installed);
    }

    [Fact]
    public void ScanAvailable_ignores_marketplace_without_manifest()
    {
        using var f = new FakeMarketplaceDir();
        Directory.CreateDirectory(Path.Combine(f.MarketplacesDir, "broken"));

        var result = MarketplaceScanner.ScanAvailable(f.Dir);

        Assert.Empty(result);
    }

    [Fact]
    public void ScanAvailable_joins_popularity_data_from_catalog_cache()
    {
        using var f = new FakeMarketplaceDir();
        f.AddMarketplace("ponytail", """
            { "name": "ponytail", "plugins": [ { "name": "ponytail", "description": "d", "source": "./" } ] }
            """);
        f.SetCatalogCache("""
            {
              "version": 1,
              "catalog": {
                "plugins": {
                  "ponytail@ponytail": {
                    "tokens": { "a": { "always_on": 500 }, "b": { "always_on": 1500 } },
                    "unique_installs": 1636,
                    "last_updated": "2026-07-03T20:55:36+01:00"
                  }
                }
              }
            }
            """);

        var result = MarketplaceScanner.ScanAvailable(f.Dir);

        var plugin = Assert.Single(result);
        Assert.Equal(1636, plugin.UniqueInstalls);
        Assert.Equal(1500, plugin.AlwaysOnTokens);
        Assert.Equal("orta", plugin.ContextWeight);
        Assert.Equal(DateTimeOffset.Parse("2026-07-03T20:55:36+01:00"), plugin.LastUpdated);
    }

    [Fact]
    public void ScanAvailable_leaves_popularity_data_null_when_not_in_catalog_cache()
    {
        using var f = new FakeMarketplaceDir();
        f.AddMarketplace("ponytail", """
            { "name": "ponytail", "plugins": [ { "name": "ponytail", "description": "d", "source": "./" } ] }
            """);

        var result = MarketplaceScanner.ScanAvailable(f.Dir);

        var plugin = Assert.Single(result);
        Assert.Null(plugin.UniqueInstalls);
        Assert.Null(plugin.LastUpdated);
        Assert.Null(plugin.AlwaysOnTokens);
        Assert.Null(plugin.ContextWeight);
    }
}

public class PluginCatalogCacheTests
{
    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        using var f = new FakeMarketplaceDir();
        var result = PluginCatalogCache.Load(f.Dir);
        Assert.Empty(result);
    }

    [Fact]
    public void Load_returns_empty_when_claudeDir_null()
    {
        var result = PluginCatalogCache.Load(null);
        Assert.Empty(result);
    }

    [Fact]
    public void Load_returns_empty_on_malformed_json()
    {
        using var f = new FakeMarketplaceDir();
        f.SetCatalogCache("{ not valid json");
        var result = PluginCatalogCache.Load(f.Dir);
        Assert.Empty(result);
    }

    [Fact]
    public void Load_parses_installs_last_updated_and_max_always_on()
    {
        using var f = new FakeMarketplaceDir();
        f.SetCatalogCache("""
            {
              "catalog": {
                "plugins": {
                  "foo@bar": {
                    "tokens": {
                      "model-a": { "always_on": 899, "on_invoke": 17693 },
                      "model-b": { "always_on": 1216, "on_invoke": 23758 }
                    },
                    "unique_installs": 1636,
                    "last_updated": "2026-07-03T20:55:36+01:00"
                  }
                }
              }
            }
            """);

        var result = PluginCatalogCache.Load(f.Dir);

        var entry = Assert.Single(result).Value;
        Assert.Equal(1636, entry.UniqueInstalls);
        Assert.Equal(1216, entry.AlwaysOnTokens);
        Assert.Equal(DateTimeOffset.Parse("2026-07-03T20:55:36+01:00"), entry.LastUpdated);
    }

    [Fact]
    public void Load_tolerates_missing_fields()
    {
        using var f = new FakeMarketplaceDir();
        f.SetCatalogCache("""{ "catalog": { "plugins": { "foo@bar": { } } } }""");

        var result = PluginCatalogCache.Load(f.Dir);

        var entry = Assert.Single(result).Value;
        Assert.Null(entry.UniqueInstalls);
        Assert.Null(entry.LastUpdated);
        Assert.Null(entry.AlwaysOnTokens);
    }
}

public class PluginSortAndFormatTests
{
    private static MarketplacePlugin Plugin(string name, int? installs = null, DateTimeOffset? updated = null) =>
        new("mp", name, "", false, [], installs, updated, null);

    [Fact]
    public void Sort_Popular_orders_by_installs_descending_then_name_nulls_last()
    {
        var plugins = new[]
        {
            Plugin("c", installs: null),
            Plugin("a", installs: 100),
            Plugin("b", installs: 500),
        };

        var sorted = MarketplaceScanner.Sort(plugins, PluginSort.Popular);

        Assert.Equal(["b", "a", "c"], sorted.Select(p => p.Name));
    }

    [Fact]
    public void Sort_RecentlyUpdated_orders_by_last_updated_descending_nulls_last()
    {
        var now = DateTimeOffset.Parse("2026-07-03T00:00:00Z");
        var plugins = new[]
        {
            Plugin("old", updated: now.AddDays(-10)),
            Plugin("none", updated: null),
            Plugin("new", updated: now),
        };

        var sorted = MarketplaceScanner.Sort(plugins, PluginSort.RecentlyUpdated);

        Assert.Equal(["new", "old", "none"], sorted.Select(p => p.Name));
    }

    [Fact]
    public void Sort_Name_orders_with_turkish_culture()
    {
        var plugins = new[] { Plugin("ç"), Plugin("a"), Plugin("b") };

        var sorted = MarketplaceScanner.Sort(plugins, PluginSort.Name);

        Assert.Equal(["a", "b", "ç"], sorted.Select(p => p.Name));
    }

    [Theory]
    [InlineData(950, "950 kurulum")]
    [InlineData(1636, "1,6 bin kurulum")]
    [InlineData(23400, "23,4 bin kurulum")]
    [InlineData(1250000, "1,3 milyon kurulum")]
    public void FormatInstalls_formats_in_turkish(int n, string expected)
    {
        Assert.Equal(expected, MarketplaceScanner.FormatInstalls(n));
    }
}
