using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class FakeClaudeDir : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-claude-" + Guid.NewGuid().ToString("N"));

    public string UserSkillsDir => Path.Combine(Dir, "skills");
    public string PluginsCacheDir => Path.Combine(Dir, "plugins", "cache");
    public string ProjectsDir => Path.Combine(Dir, "projects");

    public void AddUserSkill(string name, string frontmatter)
    {
        var dir = Path.Combine(UserSkillsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), frontmatter);
    }

    public void AddPluginSkill(string marketplace, string plugin, string version, string skillDir, string frontmatter)
    {
        var dir = Path.Combine(PluginsCacheDir, marketplace, plugin, version, "skills", skillDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), frontmatter);
    }

    public void AddSyncedSkill(string syncId, string name, string frontmatter)
    {
        var dir = Path.Combine(UserSkillsDir, "synced", syncId, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), frontmatter);
    }

    public string AddTranscript(string project, string fileName, string content)
    {
        var dir = Path.Combine(ProjectsDir, project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class ClaudeSkillScannerTests
{
    private const string SimpleFrontmatter = """
        ---
        name: brainstorming
        description: "Explore ideas before building anything."
        ---

        # Body
        """;

    [Fact]
    public void ScanInstalled_finds_user_skill()
    {
        using var dir = new FakeClaudeDir();
        dir.AddUserSkill("brainstorming", SimpleFrontmatter);

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);

        var skill = Assert.Single(skills);
        Assert.Equal("Claude", skill.Ai);
        Assert.Equal("brainstorming", skill.Id);
        Assert.Equal("brainstorming", skill.Name);
        Assert.Equal("Explore ideas before building anything.", skill.Description);
        Assert.Equal("kullanıcı", skill.Source);
    }

    [Fact]
    public void ScanInstalled_finds_plugin_skill_with_plugin_prefixed_id()
    {
        using var dir = new FakeClaudeDir();
        dir.AddPluginSkill("claude-plugins-official", "superpowers", "6.0.3", "brainstorming", SimpleFrontmatter);

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);

        var skill = Assert.Single(skills);
        Assert.Equal("superpowers:brainstorming", skill.Id);
        Assert.Equal("brainstorming", skill.Name);
        Assert.Equal("superpowers", skill.Source);
    }

    [Fact]
    public void ScanInstalled_uses_only_highest_plugin_version()
    {
        using var dir = new FakeClaudeDir();
        dir.AddPluginSkill("mp", "superpowers", "1.2.0", "a", "---\nname: a\ndescription: old\n---\n");
        dir.AddPluginSkill("mp", "superpowers", "6.0.3", "a", "---\nname: a\ndescription: new\n---\n");
        dir.AddPluginSkill("mp", "superpowers", "10.0.0", "a", "---\nname: a\ndescription: newest\n---\n");

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);

        var skill = Assert.Single(skills);
        Assert.Equal("newest", skill.Description);
    }

    [Fact]
    public void ScanInstalled_parses_single_quoted_description()
    {
        using var dir = new FakeClaudeDir();
        dir.AddUserSkill("docs", """
            ---
            name: docs
            description: 'a doc''s default, quoted with single quotes'
            ---
            """);

        var skill = Assert.Single(ClaudeSkillScanner.ScanInstalled(dir.Dir));
        Assert.Equal("a doc's default, quoted with single quotes", skill.Description);
    }

    [Fact]
    public void ScanInstalled_parses_block_scalar_description()
    {
        using var dir = new FakeClaudeDir();
        dir.AddUserSkill("multi", """
            ---
            name: multi
            description: |
              First line of description.
              Second line continues it.
            ---
            """);

        var skill = Assert.Single(ClaudeSkillScanner.ScanInstalled(dir.Dir));
        Assert.Equal("First line of description. Second line continues it.", skill.Description);
    }

    [Fact]
    public void ScanInstalled_skips_malformed_skill_md_silently()
    {
        using var dir = new FakeClaudeDir();
        dir.AddUserSkill("broken", "no frontmatter here");
        dir.AddUserSkill("ok", SimpleFrontmatter);

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);
        Assert.Single(skills);
    }

    [Fact]
    public void ScanInstalled_missing_claude_dir_returns_empty()
    {
        var skills = ClaudeSkillScanner.ScanInstalled(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));
        Assert.Empty(skills);
    }

    [Fact]
    public void ScanInstalled_finds_synced_skill_nested_three_levels_deep()
    {
        using var dir = new FakeClaudeDir();
        dir.AddSyncedSkill("abc123", "brainstorming", SimpleFrontmatter);

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);

        var skill = Assert.Single(skills);
        Assert.Equal("Claude", skill.Ai);
        Assert.Equal("brainstorming", skill.Id);
        Assert.Equal("brainstorming", skill.Name);
        Assert.Equal("claude.ai", skill.Source);
    }

    [Fact]
    public void ScanInstalled_finds_both_direct_user_skill_and_synced_skill()
    {
        using var dir = new FakeClaudeDir();
        dir.AddUserSkill("brainstorming", SimpleFrontmatter);
        dir.AddSyncedSkill("abc123", "writing", """
            ---
            name: writing
            description: Write things.
            ---
            """);

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);

        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Name == "brainstorming" && s.Source == "kullanıcı");
        Assert.Contains(skills, s => s.Name == "writing" && s.Source == "claude.ai");
    }

    [Fact]
    public void ScanInstalled_ignores_trash_directory()
    {
        using var dir = new FakeClaudeDir();
        dir.AddUserSkill(".trash/deleted-skill".Replace('/', Path.DirectorySeparatorChar), SimpleFrontmatter);

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);

        Assert.Empty(skills);
    }

    [Fact]
    public void ScanInstalled_does_not_recurse_past_three_levels()
    {
        using var dir = new FakeClaudeDir();
        // 4 levels deep under skills\ -- beyond the supported depth, should be ignored.
        var dirPath = Path.Combine(dir.UserSkillsDir, "a", "b", "c", "toodeep");
        Directory.CreateDirectory(dirPath);
        File.WriteAllText(Path.Combine(dirPath, "SKILL.md"), SimpleFrontmatter);

        var skills = ClaudeSkillScanner.ScanInstalled(dir.Dir);

        Assert.Empty(skills);
    }

    private static string ToolUseLine(string skill, string timestamp) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"x\",\"name\":\"Skill\",\"input\":{\"skill\":\"" + skill + "\"}}]}}";

    private static string CommandNameLine(string command, string timestamp) =>
        "{\"type\":\"user\",\"timestamp\":\"" + timestamp + "\",\"message\":{\"role\":\"user\",\"content\":\"<command-name>" + command + "</command-name>\\n<command-message>x</command-message>\"}}";

    [Fact]
    public void CountUsage_counts_Skill_tool_use_entries()
    {
        using var dir = new FakeClaudeDir();
        dir.AddTranscript("proj", "a.jsonl", string.Join('\n', [
            ToolUseLine("superpowers:brainstorming", "2026-11-01T10:00:00.000Z"),
            ToolUseLine("superpowers:brainstorming", "2026-11-02T10:00:00.000Z"),
            ToolUseLine("claude-in-chrome", "2026-11-03T10:00:00.000Z"),
        ]));

        var usage = ClaudeSkillScanner.CountUsage(dir.Dir);

        Assert.Equal(2, usage["superpowers:brainstorming"].Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), usage["superpowers:brainstorming"].LastUsed);
        Assert.Equal(1, usage["claude-in-chrome"].Count);
    }

    [Fact]
    public void CountUsage_counts_slash_command_invocations()
    {
        using var dir = new FakeClaudeDir();
        dir.AddTranscript("proj", "a.jsonl",
            CommandNameLine("/superpowers:brainstorming", "2026-11-05T10:00:00.000Z"));

        var usage = ClaudeSkillScanner.CountUsage(dir.Dir);

        Assert.Equal(1, usage["superpowers:brainstorming"].Count);
    }

    [Fact]
    public void CountUsage_combines_tool_use_and_slash_command_for_same_id()
    {
        using var dir = new FakeClaudeDir();
        dir.AddTranscript("proj", "a.jsonl", string.Join('\n', [
            ToolUseLine("superpowers:brainstorming", "2026-11-01T10:00:00.000Z"),
            CommandNameLine("/superpowers:brainstorming", "2026-11-05T10:00:00.000Z"),
        ]));

        var usage = ClaudeSkillScanner.CountUsage(dir.Dir);

        Assert.Equal(2, usage["superpowers:brainstorming"].Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), usage["superpowers:brainstorming"].LastUsed);
    }

    [Fact]
    public void CountUsage_skips_garbage_lines_and_files_without_throwing()
    {
        using var dir = new FakeClaudeDir();
        dir.AddTranscript("proj", "a.jsonl", string.Join('\n', [
            "not even json {{{",
            "{\"name\":\"Skill\" this is broken json",
            ToolUseLine("ok-skill", "2026-11-01T10:00:00.000Z"),
        ]));
        // Unreadable binary "jsonl" file alongside.
        File.WriteAllBytes(Path.Combine(dir.ProjectsDir, "proj", "bad.jsonl"), [0, 1, 2, 255, 254]);

        var usage = ClaudeSkillScanner.CountUsage(dir.Dir);

        Assert.Equal(1, usage["ok-skill"].Count);
    }

    [Fact]
    public void CountUsage_missing_projects_dir_returns_empty()
    {
        using var dir = new FakeClaudeDir();
        var usage = ClaudeSkillScanner.CountUsage(dir.Dir);
        Assert.Empty(usage);
    }
}

public class SkillUsageCacheTests
{
    [Fact]
    public void GetUsage_aggregates_across_files()
    {
        using var dir = new FakeClaudeDir();
        var cachePath = Path.Combine(dir.Dir, "cache.json");
        dir.AddTranscript("p1", "a.jsonl", ToolUseLineFor("skill-a"));
        dir.AddTranscript("p2", "b.jsonl", ToolUseLineFor("skill-a"));

        var cache = new SkillUsageCache(cachePath);
        var usage = cache.GetUsage(dir.Dir);

        Assert.Equal(2, usage["skill-a"].Count);
    }

    [Fact]
    public void GetUsage_does_not_reread_unchanged_file()
    {
        using var dir = new FakeClaudeDir();
        var cachePath = Path.Combine(dir.Dir, "cache.json");
        var path = dir.AddTranscript("p1", "a.jsonl", ToolUseLineFor("skill-a"));
        var originalWriteTime = File.GetLastWriteTimeUtc(path);
        var originalLength = new FileInfo(path).Length;

        var cache = new SkillUsageCache(cachePath);
        var first = cache.GetUsage(dir.Dir);
        Assert.Equal(1, first["skill-a"].Count);

        // Overwrite content with a DIFFERENT skill id, but pad to the same length and restore the
        // original mtime, so the cache key (length + mtime) looks unchanged. If the cache correctly
        // avoids re-reading this file, the second call must still report "skill-a" (from cache), not
        // the new id written below.
        var replacement = ToolUseLineFor("skill-b");
        var padded = replacement.PadRight((int)originalLength);
        File.WriteAllText(path, padded);
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        Assert.Equal(originalLength, new FileInfo(path).Length);

        var second = cache.GetUsage(dir.Dir);
        Assert.True(second.ContainsKey("skill-a"), "Expected cached result for unchanged file, meaning the file was not re-read.");
        Assert.False(second.ContainsKey("skill-b"), "File should not have been re-read since length+mtime matched the cache.");
    }

    [Fact]
    public void GetUsage_rereads_file_when_content_changes()
    {
        using var dir = new FakeClaudeDir();
        var cachePath = Path.Combine(dir.Dir, "cache.json");
        var path = dir.AddTranscript("p1", "a.jsonl", ToolUseLineFor("skill-a"));

        var cache = new SkillUsageCache(cachePath);
        cache.GetUsage(dir.Dir);

        File.AppendAllText(path, "\n" + ToolUseLineFor("skill-a"));
        var second = cache.GetUsage(dir.Dir);

        Assert.Equal(2, second["skill-a"].Count);
    }

    [Fact]
    public void GetUsage_persists_cache_to_disk_and_new_instance_reuses_it()
    {
        using var dir = new FakeClaudeDir();
        var cachePath = Path.Combine(dir.Dir, "cache.json");
        dir.AddTranscript("p1", "a.jsonl", ToolUseLineFor("skill-a"));

        new SkillUsageCache(cachePath).GetUsage(dir.Dir);
        Assert.True(File.Exists(cachePath));

        var usage = new SkillUsageCache(cachePath).GetUsage(dir.Dir);
        Assert.Equal(1, usage["skill-a"].Count);
    }

    private static string ToolUseLineFor(string skill) =>
        "{\"type\":\"assistant\",\"timestamp\":\"2026-11-01T10:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"x\",\"name\":\"Skill\",\"input\":{\"skill\":\"" + skill + "\"}}]}}";
}
