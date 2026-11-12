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
    public static bool IsClaudeOnPath() => FindClaude() is not null;

    /// <summary>`claude` komutunun PATH üzerindeki tam yolu (PATHEXT sırasıyla, ör. claude.exe / claude.cmd);
    /// bulunamazsa null.</summary>
    public static string? FindClaude() => FindOnPath("claude");

    /// <summary>`claude` PATH'te bulunamadığında gösterilen Türkçe hata mesajı.</summary>
    internal const string ClaudeNotFoundMessage =
        "'claude' komutu PATH üzerinde bulunamadı. Claude Code'un kurulu ve PATH'e eklenmiş olduğundan emin olun.";

    /// <summary>powershell.exe'nin mutlak yolu (PATH aramasına güvenilmez).</summary>
    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary><paramref name="effectiveFolder"/>: planın çalıştırılacağı klasör (bkz. PlanFolder.Resolve);
    /// null ise plan belirli bir proje klasörüne bağlı değildir ve %USERPROFILE% kullanılır.
    /// İstem yalnızca plan id'si ve sabit Türkçe metin içerir (kullanıcı verisi içermez).</summary>
    public static string BuildPrompt(Plan plan, string? effectiveFolder) => BuildPrompt(plan, effectiveFolder, multiProject: false);

    /// <summary><paramref name="multiProject"/>: true ise, bazı adımların belirli bir projeye bağlı
    /// olabileceğini ve o adımların o projenin klasöründe yapılması gerektiğini belirten ek bir cümle
    /// eklenir (bkz. PlanFolder.ResolveAll).</summary>
    public static string BuildPrompt(Plan plan, string? effectiveFolder, bool multiProject)
    {
        var folderNote = effectiveFolder is null
            ? " Bu plan belirli bir proje klasörüne bağlı değil."
            : "";
        var multiProjectNote = multiProject
            ? " Bazı adımların başında hangi projede yapılacağı yazar; o adımı o projenin klasöründe yap."
            : "";
        return $"Notlar'daki #{plan.Id} numaralı planı uygula.{folderNote} Önce plan_getir ile planı oku, adımları sırayla yap; " +
            "her adıma başlarken plan_adim_durum ile 'yapiliyor', bitince 'bitti' ve kısa sonuç notu yaz; " +
            $"takılırsan 'hata' ile nedenini yaz ve bana sor.{multiProjectNote}";
    }

    public static ProcessStartInfo BuildStart(Plan plan, string? effectiveFolder) =>
        BuildStart(plan, effectiveFolder, extraFolders: null);

    /// <summary><paramref name="mainFolder"/>: WorkingDirectory olarak kullanılır (bkz. tek-klasörlü
    /// overload). <paramref name="extraFolders"/>: her biri doğrulanıp (bkz. ValidateFolder) `claude`
    /// çağrısına ayrı bir `--add-dir "&lt;klasör&gt;"` argümanı olarak eklenir; böylece plan birden fazla
    /// projeye bağlıysa Claude Code hepsine erişebilir. Klasör yolları, ValidateFolder'ın reddettiği
    /// tırnak/noktalı virgül gibi karakterleri içeremeyeceğinden, komut metninin başka hiçbir yerine
    /// gömülmeden tek başlarına güvenle tek tırnaklanabilir.</summary>
    public static ProcessStartInfo BuildStart(Plan plan, string? mainFolder, IReadOnlyList<string>? extraFolders)
    {
        var extras = (extraFolders ?? []).Select(ValidateFolder).ToList();
        var folder = mainFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var sb = new StringBuilder("claude");
        foreach (var extra in extras)
            sb.Append(" --add-dir ").Append(PowerShellQuote(extra));
        sb.Append(' ').Append(PowerShellQuote(BuildPrompt(plan, mainFolder, multiProject: extras.Count > 0)));

        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = true,
            WorkingDirectory = folder,
        };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(EncodeCommand(sb.ToString()));
        return psi;
    }

    /// <summary>Yeni, görünür bir terminal açıp Claude Code'u planı uygulaması için başlatır.</summary>
    public static void Launch(Plan plan, string? effectiveFolder) => Launch(plan, effectiveFolder, extraFolders: null);

    /// <summary>Çoklu proje desteğiyle: ana klasör ve ek klasörler (bkz. BuildStart) ile başlatır.</summary>
    public static void Launch(Plan plan, string? mainFolder, IReadOnlyList<string>? extraFolders)
    {
        if (!IsClaudeOnPath())
            throw new InvalidOperationException(ClaudeNotFoundMessage);

        // Eski kayıtlarda doğrulanmamış bir yol kalmış olabilir; başlatmadan önce yeniden doğrula.
        if (mainFolder is not null)
            ValidateFolder(mainFolder);
        foreach (var extra in extraFolders ?? [])
            ValidateFolder(extra);

        Process.Start(BuildStart(plan, mainFolder, extraFolders));
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
