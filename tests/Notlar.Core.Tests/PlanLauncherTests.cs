using System.Text;
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
    public void BuildStart_uses_absolute_powershell_and_argument_list()
    {
        var psi = PlanLauncher.BuildStart(MakePlan(@"C:\projeler\x"), @"C:\projeler\x");

        Assert.True(Path.IsPathFullyQualified(psi.FileName));
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", psi.Arguments);
        Assert.Equal(["-NoExit", "-EncodedCommand"], psi.ArgumentList.Take(2));
        Assert.Equal(3, psi.ArgumentList.Count);
    }

    [Fact]
    public void BuildStart_never_puts_folder_text_into_arguments()
    {
        const string folder = @"C:\Users\kullanici\projeler\Ahmet’s proje; calc";
        var psi = PlanLauncher.BuildStart(MakePlan(folder), folder);

        var combined = string.Join(' ', psi.ArgumentList) + psi.Arguments;
        Assert.DoesNotContain("Ahmet", combined);
        Assert.DoesNotContain("calc", combined);
        Assert.DoesNotContain("Set-Location", combined);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(psi.ArgumentList[2]));
        Assert.DoesNotContain("Ahmet", decoded);
        Assert.Equal(folder, psi.WorkingDirectory);
    }

    [Fact]
    public void EncodedCommand_round_trips_claude_invocation_with_quoted_prompt()
    {
        var plan = MakePlan(null);
        var psi = PlanLauncher.BuildStart(plan, null);

        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(psi.ArgumentList[2]));
        var expectedPrompt = PlanLauncher.BuildPrompt(plan, null).Replace("'", "''");
        Assert.Equal($"claude '{expectedPrompt}'", decoded);
        Assert.Equal(decoded, Encoding.Unicode.GetString(Convert.FromBase64String(PlanLauncher.EncodeCommand(decoded))));
    }

    [Theory]
    [InlineData("Ahmet’s proje")]
    [InlineData("a;b")]
    [InlineData("a'b")]
    [InlineData("a`b")]
    [InlineData("a‘b")]
    [InlineData("a‚b")]
    [InlineData("a‛b")]
    public void ValidateFolder_rejects_dangerous_characters_even_if_dir_exists(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "notlar-launch-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => PlanLauncher.ValidateFolder(dir));
            Assert.StartsWith("Klasör yolu desteklenmeyen karakter içeriyor:", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("C:\\x\u202Ey")]
    [InlineData("C:\\x\u2066y")]
    [InlineData("C:\\x\ny")]
    [InlineData("C:\\x\"y")]
    public void ValidateFolder_rejects_control_bidi_and_quote_chars(string path)
    {
        var ex = Assert.Throws<ArgumentException>(() => PlanLauncher.ValidateFolder(path));
        Assert.StartsWith("Klasör yolu desteklenmeyen karakter içeriyor:", ex.Message);
    }

    [Fact]
    public void ValidateFolder_requires_fully_qualified_existing_dir()
    {
        Assert.Throws<ArgumentException>(() => PlanLauncher.ValidateFolder(@"relative\dir"));
        Assert.Throws<ArgumentException>(() => PlanLauncher.ValidateFolder(@"C:\yok-" + Guid.NewGuid().ToString("N")));
        Assert.Throws<ArgumentException>(() => PlanLauncher.ValidateFolder("  "));
        var temp = Path.GetTempPath().TrimEnd('\\');
        Assert.Equal(temp, PlanLauncher.ValidateFolder("  " + temp + " "));
    }
}
