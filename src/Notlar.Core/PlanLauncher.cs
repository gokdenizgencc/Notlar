using System.Diagnostics;
using System.Text;

namespace Notlar.Core;

/// <summary>Bir planı yürütmek için görünür bir terminal açar: proje klasöründe Claude Code'u, planı MCP
/// üzerinden okuyup adım adım uygulamasını isteyen bir başlangıç istemiyle başlatır. Windows Terminal
/// (wt.exe) varsa onu, yoksa doğrudan powershell.exe'yi kullanır.</summary>
public static class PlanLauncher
{
    /// <summary>`claude` komutunun PATH üzerinde bulunup bulunmadığını kontrol eder.</summary>
    public static bool IsClaudeOnPath() => FindOnPath("claude") is not null;

    /// <summary><paramref name="effectiveFolder"/>: planın çalıştırılacağı klasör (bkz. PlanFolder.Resolve);
    /// null ise plan belirli bir proje klasörüne bağlı değildir ve %USERPROFILE% kullanılır.</summary>
    public static string BuildPrompt(Plan plan, string? effectiveFolder)
    {
        var folderNote = effectiveFolder is null
            ? " Bu plan belirli bir proje klasörüne bağlı değil."
            : "";
        return $"Notlar'daki #{plan.Id} numaralı planı uygula.{folderNote} Önce plan_getir ile planı oku, adımları sırayla yap; " +
            "her adıma başlarken plan_adim_durum ile 'yapiliyor', bitince 'bitti' ve kısa sonuç notu yaz; " +
            "takılırsan 'hata' ile nedenini yaz ve bana sor.";
    }

    public static ProcessStartInfo BuildStart(Plan plan, string? effectiveFolder)
    {
        var folder = effectiveFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var prompt = BuildPrompt(plan, effectiveFolder);
        var wt = FindOnPath("wt.exe") ?? FindOnPath("wt");

        if (wt is not null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wt.exe",
                UseShellExecute = true,
                WorkingDirectory = folder,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(folder);
            psi.ArgumentList.Add("powershell");
            psi.ArgumentList.Add("-NoExit");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"claude {PowerShellQuote(prompt)}");
            return psi;
        }

        var command = $"Set-Location -LiteralPath {PowerShellQuote(folder)}; claude {PowerShellQuote(prompt)}";
        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute = true,
            WorkingDirectory = folder,
        };
    }

    /// <summary>Yeni, görünür bir terminal açıp Claude Code'u planı uygulaması için başlatır.</summary>
    public static void Launch(Plan plan, string? effectiveFolder)
    {
        if (!IsClaudeOnPath())
            throw new InvalidOperationException(
                "'claude' komutu PATH üzerinde bulunamadı. Claude Code'un kurulu ve PATH'e eklenmiş olduğundan emin olun.");

        var psi = BuildStart(plan, effectiveFolder);
        Process.Start(psi);
    }

    /// <summary>PowerShell tek tırnaklı string literali: içindeki tek tırnakları ikileyerek escape eder.</summary>
    internal static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hasExtension = Path.HasExtension(fileName);

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (hasExtension)
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                else
                {
                    foreach (var ext in pathExt)
                    {
                        var candidate = Path.Combine(dir, fileName + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                    var noExt = Path.Combine(dir, fileName);
                    if (File.Exists(noExt)) return noExt;
                }
            }
            catch (ArgumentException)
            {
                // Bozuk PATH girdisini yok say.
            }
        }
        return null;
    }
}
