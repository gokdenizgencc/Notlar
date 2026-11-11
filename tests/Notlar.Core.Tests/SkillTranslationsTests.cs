using Microsoft.Data.Sqlite;

namespace Notlar.Core.Tests;

public sealed class SkillTranslationsFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, TimeSpan.FromHours(3)));
    public CountingSignal Signal { get; } = new();
    public SkillTranslations Store { get; }

    public SkillTranslationsFixture() => Store = new SkillTranslations(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class SkillTranslationsTests
{
    [Fact]
    public void Set_then_Get_returns_translation()
    {
        using var f = new SkillTranslationsFixture();

        f.Store.Set("superpowers:brainstorming", "Yaratıcı fikirleri keşfeder.");
        var result = f.Store.Get("superpowers:brainstorming");

        Assert.NotNull(result);
        Assert.Equal("Yaratıcı fikirleri keşfeder.", result!.DescriptionTr);
        Assert.Equal(1, f.Signal.Count);
    }

    [Fact]
    public void Get_returns_null_when_missing()
    {
        using var f = new SkillTranslationsFixture();
        Assert.Null(f.Store.Get("nope"));
    }

    [Fact]
    public void Set_is_idempotent_upsert()
    {
        using var f = new SkillTranslationsFixture();

        f.Store.Set("x", "ilk");
        f.Store.Set("x", "güncel");

        Assert.Equal("güncel", f.Store.Get("x")!.DescriptionTr);
        Assert.Single(f.Store.All());
    }

    [Fact]
    public void Set_rejects_empty_description()
    {
        using var f = new SkillTranslationsFixture();
        Assert.Throws<ArgumentException>(() => f.Store.Set("x", "  "));
    }

    [Fact]
    public void All_returns_every_translation()
    {
        using var f = new SkillTranslationsFixture();
        f.Store.Set("a", "açıklama a");
        f.Store.Set("b", "açıklama b");

        var all = f.Store.All();

        Assert.Equal(2, all.Count);
        Assert.Equal("açıklama a", all["a"].DescriptionTr);
    }
}
