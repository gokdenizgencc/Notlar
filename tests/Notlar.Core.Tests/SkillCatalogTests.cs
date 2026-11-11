using Notlar.Core;

namespace Notlar.Core.Tests;

public class SkillCatalogTests
{
    [Fact]
    public void Build_joins_installed_skills_with_usage()
    {
        var installed = new List<SkillInfo> { new("Claude", "superpowers:brainstorming", "brainstorming", "desc", "superpowers") };
        var usage = new Dictionary<string, SkillUsage> { ["superpowers:brainstorming"] = new(5, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)) };

        var entries = SkillCatalog.Build(installed, usage, []);

        var entry = Assert.Single(entries);
        Assert.Equal(5, entry.UsageCount);
        Assert.Equal("superpowers", entry.Source);
        Assert.Null(entry.DescriptionTr);
    }

    [Fact]
    public void Build_attaches_turkish_translation_when_present()
    {
        var installed = new List<SkillInfo> { new("Claude", "superpowers:brainstorming", "brainstorming", "desc", "superpowers") };
        var translations = new Dictionary<string, SkillTranslation>
        {
            ["superpowers:brainstorming"] = new("superpowers:brainstorming", "Fikir üretir.", DateTimeOffset.UnixEpoch),
        };

        var entries = SkillCatalog.Build(installed, new Dictionary<string, SkillUsage>(), [], translations);

        var entry = Assert.Single(entries);
        Assert.Equal("Fikir üretir.", entry.DescriptionTr);
        Assert.Equal("desc", entry.Description);
    }

    [Fact]
    public void Build_includes_used_but_no_longer_installed_skills_as_removed()
    {
        var usage = new Dictionary<string, SkillUsage> { ["old:skill"] = new(3, null) };

        var entries = SkillCatalog.Build([], usage, []);

        var entry = Assert.Single(entries);
        Assert.Equal("kaldırılmış", entry.Source);
        Assert.Equal(3, entry.UsageCount);
    }

    [Fact]
    public void Build_includes_registry_entries_for_other_ais()
    {
        var registry = new List<RegistrySkill> { new(1, "ChatGPT", "web-search", "arar", 4, null, DateTimeOffset.UnixEpoch) };

        var entries = SkillCatalog.Build([], new Dictionary<string, SkillUsage>(), registry);

        var entry = Assert.Single(entries);
        Assert.Equal("ChatGPT", entry.Ai);
        Assert.Equal(4, entry.UsageCount);
        Assert.Equal("kayıtlı", entry.Source);
    }

    [Fact]
    public void Build_merges_claude_registry_entry_into_matching_installed_skill_by_name()
    {
        var installed = new List<SkillInfo> { new("Claude", "superpowers:brainstorming", "brainstorming", "desc", "superpowers") };
        var usage = new Dictionary<string, SkillUsage> { ["superpowers:brainstorming"] = new(2, null) };
        var registry = new List<RegistrySkill> { new(1, "Claude", "brainstorming", "", 3, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UnixEpoch) };

        var entries = SkillCatalog.Build(installed, usage, registry);

        var entry = Assert.Single(entries);
        Assert.Equal(5, entry.UsageCount);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), entry.LastUsed);
    }

    [Fact]
    public void Build_sorts_by_ai_then_usage_desc_then_name()
    {
        var installed = new List<SkillInfo>
        {
            new("Claude", "b", "b", "", "kullanıcı"),
            new("Claude", "a", "a", "", "kullanıcı"),
        };
        var usage = new Dictionary<string, SkillUsage>
        {
            ["a"] = new(1, null),
            ["b"] = new(9, null),
        };
        var registry = new List<RegistrySkill> { new(1, "ChatGPT", "x", "", 1, null, DateTimeOffset.UnixEpoch) };

        var entries = SkillCatalog.Build(installed, usage, registry);

        Assert.Equal(
            [("ChatGPT", "x"), ("Claude", "b"), ("Claude", "a")],
            entries.Select(e => (e.Ai, e.Name)));
    }
}
