using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Notlar.Core;

namespace Notlar.Mcp;

/// <summary>Yapay zekâ "yetenek kataloğu": Claude Code'un kurulu skill'lerini + kullanım sayılarını tarar
/// ve başka AI asistanlarının (ChatGPT, Gemini, vb.) kendi yeteneklerini elle kaydetmesine izin verir.
/// Panel'deki bir sekme (başka bir ekip tarafından) bu bilgiyi kullanıcıya gösterecek.</summary>
[McpServerToolType]
public sealed class SkillTools(SkillRegistry registry, SkillTranslations translations)
{
    private static readonly CultureInfo TrTR = CultureInfo.GetCultureInfo("tr-TR");
    private const int DescriptionPreviewLength = 100;
    private const string NoTranslationMarker = "[TR yok]";

    [McpServerTool(Name = "yetenek_kaydet")]
    [Description("""
        Bir yapay zekânın yeteneğini (skill/araç) Notlar'daki yetenek kataloğuna kaydeder/günceller.
        Başka AI asistanları kendi yeteneklerini buraya yazabilir.
        """)]
    public string YetenekKaydet(
        [Description("Yapay zekâ adı, örn. ChatGPT, Gemini, Claude.")] string ai,
        [Description("Yeteneğin adı.")] string ad,
        [Description("Yeteneğin ne işe yaradığının kısa açıklaması.")] string? aciklama = null) =>
        Run(() => $"Yetenek kaydedildi: {Line(registry.Upsert(ai, ad, aciklama))}");

    [McpServerTool(Name = "yetenek_kullanildi")]
    [Description("Bir yeteneğin kullanıldığını kaydeder; kullanım sayacını bir artırır (kayıt yoksa oluşturur).")]
    public string YetenekKullanildi(
        [Description("Yapay zekâ adı.")] string ai,
        [Description("Yeteneğin adı.")] string ad) =>
        Run(() => $"Kullanım kaydedildi: {Line(registry.RecordUse(ai, ad))}");

    [McpServerTool(Name = "yetenek_aciklama_kaydet")]
    [Description("""
        Bir yeteneğin Türkçe açıklamasını kaydeder (panelde İngilizce yerine gösterilir). Kısa, 1–2
        cümle, ne işe yaradığını ve ne zaman kullanıldığını anlat.
        """)]
    public string YetenekAciklamaKaydet(
        [Description("Yeteneğin id'si (yetenekleri_listele çıktısındaki id, ör. superpowers:brainstorming).")] string id,
        [Description("1-2 cümlelik Türkçe açıklama.")] string aciklama_tr) =>
        Run(() => $"Türkçe açıklama kaydedildi: {id} — {translations.Set(id, aciklama_tr).DescriptionTr}");

    [McpServerTool(Name = "yetenekleri_listele")]
    [Description("""
        Tüm yapay zekâların yeteneklerini listeler: Claude'un bilgisayarda kurulu skill'leri (kullanım
        sayılarıyla birlikte) + başka AI'ların elle kaydettiği yetenekler. Türkçe açıklaması olmayan
        Claude skill'leri "[TR yok]" ile işaretlenir; bunlar için yetenek_aciklama_kaydet ile Türkçe
        açıklama eklenebilir.
        """)]
    public string YetenekleriListele(
        [Description("Yapay zekâ adına göre filtrelemek için, örn. Claude, ChatGPT. Boşsa tümü.")] string? ai = null) => Run(() =>
    {
        var installed = ClaudeSkillScanner.ScanInstalled();
        var usage = new SkillUsageCache().GetUsage();
        var registryItems = registry.List();
        var allTranslations = translations.All();
        var catalog = SkillCatalog.Build(installed, usage, registryItems, allTranslations);

        if (ai is { } filter)
        {
            var normalized = NoteAuthor.Normalize(filter);
            catalog = catalog.Where(e => e.Ai == normalized).ToList();
        }

        if (catalog.Count == 0)
            return "Kayıtlı yetenek yok.";

        var sb = new StringBuilder();
        foreach (var entry in catalog)
            sb.Append(Line(entry)).Append('\n');
        return sb.ToString().TrimEnd();
    });

    private static string Line(SkillEntry e)
    {
        var hasTr = !string.IsNullOrWhiteSpace(e.DescriptionTr);
        var desc = hasTr ? e.DescriptionTr! : e.Description;
        if (desc.Length > DescriptionPreviewLength) desc = desc[..DescriptionPreviewLength];
        var trMarker = !hasTr && string.Equals(e.Ai, ClaudeSkillScanner.Ai, StringComparison.Ordinal) ? $" {NoTranslationMarker}" : "";
        var used = e.LastUsed is { } lu ? $", son: {lu.ToString("d MMMM yyyy", TrTR)}" : "";
        return $"{e.Ai} · {e.Id} — {e.UsageCount} kez{used} — {desc}{trMarker}";
    }

    private static string Line(RegistrySkill r)
    {
        var used = r.LastUsed is { } lu ? $", son: {lu.ToString("d MMMM yyyy", TrTR)}" : "";
        return $"[{r.Ai}] {r.Name} — {r.UsageCount} kez{used} — {r.Description}";
    }

    private static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is SkillNotFoundException or ArgumentException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (SqliteException ex)
        {
            throw new McpException($"Veritabanına erişilemedi ({ex.SqliteErrorCode}). Biraz sonra tekrar dene.", ex);
        }
    }
}
