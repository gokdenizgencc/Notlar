using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class SkillRegistryFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public SkillRegistry Store { get; }

    public SkillRegistryFixture() => Store = new SkillRegistry(DbPath, Time, Signal);

    public SkillRegistry Reopen() => new(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class SkillRegistryTests
{
    [Fact]
    public void Upsert_creates_new_skill_with_normalized_ai_name()
    {
        using var f = new SkillRegistryFixture();

        var skill = f.Store.Upsert("chatgpt", "  web-search  ", "Arama yapar.");

        Assert.Equal("ChatGPT", skill.Ai);
        Assert.Equal("web-search", skill.Name);
        Assert.Equal("Arama yapar.", skill.Description);
        Assert.Equal(0, skill.UsageCount);
        Assert.Equal(1, f.Signal.Count);
    }

    [Fact]
    public void Upsert_updates_description_on_existing_ai_and_name()
    {
        using var f = new SkillRegistryFixture();
        f.Store.Upsert("Gemini", "search", "ilk açıklama");

        var updated = f.Store.Upsert("gemini", "search", "yeni açıklama");

        Assert.Equal("yeni açıklama", updated.Description);
        Assert.Single(f.Store.List());
    }

    [Fact]
    public void RecordUse_creates_if_missing_and_increments_usage_count()
    {
        using var f = new SkillRegistryFixture();

        var first = f.Store.RecordUse("chatgpt", "web-search");
        Assert.Equal(1, first.UsageCount);
        Assert.NotNull(first.LastUsed);

        f.Time.Advance(TimeSpan.FromMinutes(5));
        var second = f.Store.RecordUse("ChatGPT", "web-search");
        Assert.Equal(2, second.UsageCount);
        Assert.True(second.LastUsed > first.LastUsed);
    }

    [Fact]
    public void List_filters_by_ai_and_orders_by_usage_count_desc()
    {
        using var f = new SkillRegistryFixture();
        f.Store.Upsert("Gemini", "a");
        f.Store.RecordUse("ChatGPT", "low");
        f.Store.RecordUse("ChatGPT", "high");
        f.Store.RecordUse("ChatGPT", "high");

        var chatgpt = f.Store.List("chatgpt");

        Assert.Equal(["high", "low"], chatgpt.Select(s => s.Name));
    }

    [Fact]
    public void Delete_hides_and_Restore_brings_back()
    {
        using var f = new SkillRegistryFixture();
        var skill = f.Store.Upsert("Claude", "yetenek-kataloğu");

        f.Store.Delete(skill.Id);
        Assert.Empty(f.Store.List());
        Assert.Throws<SkillNotFoundException>(() => f.Store.Delete(skill.Id));

        var restored = f.Store.Restore(skill.Id);
        Assert.Equal("yetenek-kataloğu", restored.Name);
        Assert.Single(f.Store.List());
    }

    [Fact]
    public void Upsert_rejects_empty_ai_or_name()
    {
        using var f = new SkillRegistryFixture();
        Assert.Throws<ArgumentException>(() => f.Store.Upsert("  ", "x"));
        Assert.Throws<ArgumentException>(() => f.Store.Upsert("ChatGPT", "  "));
    }

    [Fact]
    public void Data_persists_across_instances()
    {
        using var f = new SkillRegistryFixture();
        f.Store.Upsert("ChatGPT", "kalıcı", "açıklama");

        Assert.Equal("kalıcı", f.Reopen().List().Single().Name);
    }

    [Fact]
    public void Coexists_with_other_stores_on_same_db_file()
    {
        using var f = new SkillRegistryFixture();
        var memoryStore = new MemoryStore(f.DbPath, f.Time, f.Signal);

        memoryStore.Add("bir hafıza", "içerik");
        f.Store.Upsert("ChatGPT", "bir-yetenek");

        Assert.Single(memoryStore.List());
        Assert.Single(f.Store.List());
    }
}
