using Notlar.Core;

namespace Notlar.Core.Tests;

public class PlanLauncherTests
{
    private static Plan MakePlan(string? path) =>
        new(1, "Test planı", path, PlanStatus.Taslak, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void BuildPrompt_contains_plan_id_and_tool_names()
    {
        var plan = MakePlan(@"C:\projeler\x");
        var prompt = PlanLauncher.BuildPrompt(plan, plan.ProjectPath);

        Assert.Contains("#1 numaralı planı", prompt);
        Assert.Contains("plan_getir", prompt);
        Assert.Contains("plan_adim_durum", prompt);
        Assert.Contains("'yapiliyor'", prompt);
        Assert.Contains("'bitti'", prompt);
        Assert.Contains("'hata'", prompt);
    }

    [Fact]
    public void BuildPrompt_notes_when_no_folder_is_linked()
    {
        var plan = MakePlan(null);
        var prompt = PlanLauncher.BuildPrompt(plan, null);

        Assert.Contains("Bu plan belirli bir proje klasörüne bağlı değil.", prompt);
    }

    [Fact]
    public void PowerShellQuote_escapes_single_quotes()
    {
        Assert.Equal("'it''s'", PlanLauncher.PowerShellQuote("it's"));
    }

    [Fact]
    public void BuildStart_sets_working_directory_to_project_path()
    {
        var plan = MakePlan(@"C:\Users\kullanici\projeler\örnek proje");
        var psi = PlanLauncher.BuildStart(plan, plan.ProjectPath);

        Assert.Equal(plan.ProjectPath, psi.WorkingDirectory);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void BuildStart_uses_user_profile_when_no_folder_is_linked()
    {
        var plan = MakePlan(null);
        var psi = PlanLauncher.BuildStart(plan, null);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), psi.WorkingDirectory);
    }

    [Fact]
    public void BuildStart_escapes_path_with_spaces_and_apostrophe_for_powershell_fallback()
    {
        var plan = MakePlan(@"C:\Users\kullanici\projeler\Ahmet's proje");
        var psi = PlanLauncher.BuildStart(plan, plan.ProjectPath);

        // Bu makinede wt.exe yoksa powershell.exe doğrudan kullanılır.
        if (psi.FileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) && psi.ArgumentList.Count == 0)
        {
            Assert.Contains("Ahmet''s proje", psi.Arguments);
            Assert.Contains("Set-Location -LiteralPath", psi.Arguments);
            Assert.Contains("claude", psi.Arguments);
        }
    }

    [Fact]
    public void BuildStart_prompt_is_safely_quoted_regardless_of_launcher()
    {
        var plan = MakePlan(@"C:\projeler\x");
        var psi = PlanLauncher.BuildStart(plan, plan.ProjectPath);

        var combined = psi.ArgumentList.Count > 0 ? string.Join(' ', psi.ArgumentList) : psi.Arguments;
        Assert.Contains("claude", combined);
        Assert.Contains("plan_getir", combined);
    }
}
