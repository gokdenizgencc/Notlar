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
}
