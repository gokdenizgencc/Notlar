using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class ProjectFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public string ProjectDir { get; }
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public ProjectStore Store { get; }

    public ProjectFixture()
    {
        ProjectDir = Path.Combine(Dir, "proje");
        Directory.CreateDirectory(ProjectDir);
        Store = new ProjectStore(DbPath, Time, Signal);
    }

    public ProjectStore Reopen() => new(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class ProjectStoreTests
{
    [Fact]
    public void Add_stores_name_and_path_and_signals()
    {
        using var f = new ProjectFixture();

        var project = f.Store.Add("  Harika Proje  ", f.ProjectDir);

        Assert.Equal("Harika Proje", project.Name);
        Assert.Equal(f.ProjectDir, project.Path);
        Assert.Equal(1, f.Signal.Count);
    }

    [Fact]
    public void Add_rejects_empty_name()
    {
        using var f = new ProjectFixture();
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Add("  ", f.ProjectDir));
        Assert.Equal("Proje adı boş olamaz.", ex.Message);
    }

    [Fact]
    public void Add_rejects_missing_folder()
    {
        using var f = new ProjectFixture();
        var missing = Path.Combine(f.Dir, "yok-boyle-bir-klasor");
        var ex = Assert.Throws<ArgumentException>(() => f.Store.Add("P", missing));
        Assert.Equal($"Proje klasörü bulunamadı: {missing}", ex.Message);
    }

    [Fact]
    public void Add_rejects_duplicate_name_case_insensitive_turkish()
    {
        using var f = new ProjectFixture();
        f.Store.Add("İşler", f.ProjectDir);

        var ex = Assert.Throws<ArgumentException>(() => f.Store.Add("işler", f.ProjectDir));
        Assert.Contains("İşler", ex.Message);
    }

    [Fact]
    public void Update_renames_and_rejects_duplicate_with_another_live_project()
    {
        using var f = new ProjectFixture();
        var a = f.Store.Add("A", f.ProjectDir);
        var otherDir = Path.Combine(f.Dir, "b");
        Directory.CreateDirectory(otherDir);
        f.Store.Add("B", otherDir);

        var ex = Assert.Throws<ArgumentException>(() => f.Store.Update(a.Id, name: "b"));
        Assert.Contains("zaten var", ex.Message);

        var renamed = f.Store.Update(a.Id, name: "A2");
        Assert.Equal("A2", renamed.Name);
    }

    [Fact]
    public void Update_unknown_id_throws()
    {
        using var f = new ProjectFixture();
        Assert.Throws<ProjectNotFoundException>(() => f.Store.Update(999, name: "x"));
    }

    [Fact]
    public void Delete_hides_project_and_Restore_brings_it_back()
    {
        using var f = new ProjectFixture();
        var project = f.Store.Add("P", f.ProjectDir);

        f.Store.Delete(project.Id);
        Assert.Empty(f.Store.List());
        Assert.Throws<ProjectNotFoundException>(() => f.Store.Delete(project.Id));

        var restored = f.Store.Restore(project.Id);
        Assert.Equal("P", restored.Name);
        Assert.Single(f.Store.List());
    }

    [Fact]
    public void List_orders_by_name()
    {
        using var f = new ProjectFixture();
        var dirB = Path.Combine(f.Dir, "b"); Directory.CreateDirectory(dirB);
        var dirA = Path.Combine(f.Dir, "a"); Directory.CreateDirectory(dirA);
        f.Store.Add("Zeta", dirB);
        f.Store.Add("Alfa", dirA);

        Assert.Equal(["Alfa", "Zeta"], f.Store.List().Select(p => p.Name));
    }

    [Fact]
    public void Get_unknown_id_throws()
    {
        using var f = new ProjectFixture();
        Assert.Throws<ProjectNotFoundException>(() => f.Store.Get(999));
    }

    [Fact]
    public void FindByName_matches_exact_case_insensitive_turkish()
    {
        using var f = new ProjectFixture();
        var project = f.Store.Add("İşler Projesi", f.ProjectDir);

        var found = f.Store.FindByName("işler projesi");
        Assert.Equal(project.Id, found.Id);
    }

    [Fact]
    public void FindByName_matches_unique_prefix_or_contains()
    {
        using var f = new ProjectFixture();
        var project = f.Store.Add("Notlar Uygulaması", f.ProjectDir);

        Assert.Equal(project.Id, f.Store.FindByName("notlar").Id);
        Assert.Equal(project.Id, f.Store.FindByName("uygulaması").Id);
    }

    [Fact]
    public void FindByName_ambiguous_match_throws_listing_candidates()
    {
        using var f = new ProjectFixture();
        var dirB = Path.Combine(f.Dir, "b"); Directory.CreateDirectory(dirB);
        f.Store.Add("Notlar App", f.ProjectDir);
        f.Store.Add("Notlar Core", dirB);

        var ex = Assert.Throws<ArgumentException>(() => f.Store.FindByName("notlar"));
        Assert.Contains("Notlar App", ex.Message);
        Assert.Contains("Notlar Core", ex.Message);
    }

    [Fact]
    public void FindByName_no_match_throws_ProjectNotFoundException()
    {
        using var f = new ProjectFixture();
        f.Store.Add("Bir Şey", f.ProjectDir);

        Assert.Throws<ProjectNotFoundException>(() => f.Store.FindByName("alakasız"));
    }

    [Fact]
    public void Data_persists_across_instances()
    {
        using var f = new ProjectFixture();
        var project = f.Store.Add("kalıcı", f.ProjectDir);
        Assert.Equal("kalıcı", f.Reopen().List().Single(p => p.Id == project.Id).Name);
    }
}
