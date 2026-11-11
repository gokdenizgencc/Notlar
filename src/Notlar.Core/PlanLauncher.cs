using System.Diagnostics;
using System.Text;

namespace Notlar.Core;

/// <summary>Bir planı yürütmek için görünür bir PowerShell penceresi açar: proje klasöründe Claude Code'u, planı
/// MCP üzerinden okuyup adım adım uygulamasını isteyen bir başlangıç istemiyle başlatır.
/// Güvenlik: klasör yolu hiçbir komut metnine gömülmez, yalnızca <see cref="ProcessStartInfo.WorkingDirectory"/>
/// ile verilir. Komut (<c>claude '&lt;istem&gt;'</c>) -EncodedCommand ile Base64 (UTF-16LE) olarak geçirilir ve
/// powershell.exe mutlak sistem yolundan çalıştırılır. Windows Terminal (wt.exe) bilinçli olarak kullanılmaz:
/// wt kendi argümanlarında ';' karakterini komut ayırıcı olarak yorumladığı için ek bir enjeksiyon yüzeyi
/// oluşturur.</summary>
public static class PlanLauncher
{
    /// <summary>`claude` komutunun PATH üzerinde bulunup bulunmadığını kontrol eder.</summary>
    public static bool IsClaudeOnPath() => FindOnPath("claude") is not null;

    /// <summary>powershell.exe'nin mutlak yolu (PATH aramasına güvenilmez).</summary>
    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary><paramref name="effectiveFolder"/>: planın çalıştırılacağı klasör (bkz. PlanFolder.Resolve);
    /// null ise plan belirli bir proje klasörüne bağlı değildir ve %USERPROFILE% kullanılır.
    /// İstem yalnızca plan id'si ve sabit Türkçe metin içerir (kullanıcı verisi içermez).</summary>
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
        var command = $"claude {PowerShellQuote(BuildPrompt(plan, effectiveFolder))}";

        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = true,
            WorkingDirectory = folder,
        };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(EncodeCommand(command));
        return psi;
    }

    /// <summary>Yeni, görünür bir terminal açıp Claude Code'u planı uygulaması için başlatır.</summary>
    public static void Launch(Plan plan, string? effectiveFolder)
    {
        if (!IsClaudeOnPath())
            throw new InvalidOperationException(
                "'claude' komutu PATH üzerinde bulunamadı. Claude Code'un kurulu ve PATH'e eklenmiş olduğundan emin olun.");

        // Eski kayıtlarda doğrulanmamış bir yol kalmış olabilir; başlatmadan önce yeniden doğrula.
        if (effectiveFolder is not null)
            ValidateFolder(effectiveFolder);

        Process.Start(BuildStart(plan, effectiveFolder));
    }

    /// <summary>Bir proje/plan klasör yolunu doğrular ve kırpılmış hâlini döndürür: tam (fully qualified) bir
    /// yol olmalı, var olmalı ve kabuk/terminal tarafından yorumlanabilecek karakterler (';', tırnaklar,
    /// ters tırnak, kontrol ve bidi karakterleri) içermemelidir. Geçersizse Türkçe mesajlı
    /// <see cref="ArgumentException"/> fırlatır.</summary>
    public static string ValidateFolder(string? path)
    {
        var clean = path?.Trim();
        if (string.IsNullOrEmpty(clean))
            throw new ArgumentException("Proje klasörü boş olamaz.");

        foreach (var c in clean)
        {
            if (IsForbiddenFolderChar(c))
                throw new ArgumentException($"Klasör yolu desteklenmeyen karakter içeriyor: {Describe(c)}");
        }

        if (!Path.IsPathFullyQualified(clean))
            throw new ArgumentException($"Proje klasörü tam bir yol olmalı: {clean}");
        if (!Directory.Exists(clean))
            throw new ArgumentException($"Proje klasörü bulunamadı: {clean}");
        return clean;
    }

    private static bool IsForbiddenFolderChar(char c) =>
        c is ';' or '\'' or '‘' or '’' or '‚' or '‛' or '"' or '`'
        || char.IsControl(c)
        || IsBidi(c);

    private static bool IsBidi(char c) => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    private static string Describe(char c) =>
        char.IsControl(c) || IsBidi(c) ? $"U+{(int)c:X4} (görünmez karakter)" : $"'{c}' (U+{(int)c:X4})";

    /// <summary>PowerShell -EncodedCommand biçimi: UTF-16LE baytlarının Base64'ü.</summary>
    internal static string EncodeCommand(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

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
