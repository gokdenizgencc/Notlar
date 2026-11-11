using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Notlar.Core;

namespace Notlar.Mcp;

/// <summary>"Planlar": kullanıcının Notlar panelinde yazdığı, sıralı adımlardan oluşan iş planları.
/// Kullanıcı panelde ▶ Çalıştır'a bastığında proje klasöründe bu MCP sunucusuna bağlı bir Claude Code
/// oturumu açılır; o oturum plan_getir ile planı okur, adımları sırayla uygular ve her adımda
/// plan_adim_durum ile durumunu/sonucunu günceller. Panel ilerlemeyi canlı gösterir.</summary>
[McpServerToolType]
public sealed class PlanTools(PlanStore store)
{
    [McpServerTool(Name = "plan_listele")]
    [Description("Tüm planları özetler: id, başlık, durum, proje klasörü ve adım sayıları. Hangi planın uygulanacağını bulmak için önce burayı kullan.")]
    public string PlanListele() => Run(() =>
    {
        var plans = store.ListPlans();
        if (plans.Count == 0)
            return "Plan yok.";

        var sb = new StringBuilder();
        foreach (var plan in plans)
        {
            var (_, steps) = store.GetPlan(plan.Id);
            var done = steps.Count(s => s.Status is StepStatus.Bitti or StepStatus.Atlandi);
            sb.Append($"[#{plan.Id} · {StatusText(plan.Status)}] {plan.Title} ({plan.ProjectPath}) — {done}/{steps.Count} adım\n");
        }
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "plan_getir")]
    [Description("""
        Bir planın tüm ayrıntılarını getirir: başlık, proje klasörü ve sırayla tüm adımlar (durum ikonu ve
        varsa sonuç notuyla). Bir planı uygularken şu sırayı izle: 1) burayı çağırıp planı oku,
        2) adımları yazıldığı sırayla, birini bitirmeden diğerine geçmeden uygula, 3) her adıma başlarken
        plan_adim_durum ile 'yapiliyor' işaretle, bitirince 'bitti' ve kısa bir sonuç notu yaz,
        4) bir adımda takılırsan 'hata' ile nedenini yaz ve kullanıcıya sor; plan tamamını bitirmeden durma.
        """)]
    public string PlanGetir([Description("Planın id'si.")] long id) => Run(() =>
    {
        var (plan, steps) = store.GetPlan(id);
        var sb = new StringBuilder();
        sb.Append($"Plan #{plan.Id}: {plan.Title}\n");
        sb.Append($"Durum: {StatusText(plan.Status)}\n");
        sb.Append($"Proje klasörü: {plan.ProjectPath}\n");
        sb.Append("Adımlar:\n");
        foreach (var step in steps)
        {
            sb.Append($"{step.Order}. {StepIcon(step.Status)} {step.Text}");
            if (!string.IsNullOrWhiteSpace(step.Result))
                sb.Append($" — {step.Result}");
            sb.Append('\n');
        }
        sb.Append("\nAdımları sırayla uygula; her adıma başlarken plan_adim_durum ile 'yapiliyor', " +
                   "bitince 'bitti' ve kısa sonuç notuyla işaretle; takılırsan 'hata' ile nedenini yaz ve kullanıcıya sor.");
        return sb.ToString();
    });

    [McpServerTool(Name = "plan_adim_durum")]
    [Description("""
        Bir plan adımının durumunu günceller. durum şunlardan biri olmalı: bekliyor, yapiliyor, bitti, hata,
        atlandi. Bir adıma başlarken 'yapiliyor', bitirince 'bitti' (ve sonuc ile kısa bir not) yaz. Bir
        adımda takılırsan 'hata' yaz, sonuc'a nedenini açıkla ve devam etmeden önce kullanıcıya sor.
        """)]
    public string PlanAdimDurum(
        [Description("Adımın id'si (plan_getir çıktısındaki sıra değil, adımın kendi id'si).")] long adim_id,
        [Description("Yeni durum: bekliyor, yapiliyor, bitti, hata veya atlandi.")] string durum,
        [Description("Kısa sonuç notu (opsiyonel), ör. ne yapıldığı veya hata nedeni.")] string? sonuc = null) =>
        Run(() =>
        {
            var status = PlanStore.ParseStepStatus(durum);
            var step = store.SetStepStatus(adim_id, status, sonuc);
            return $"Adım #{step.Id} güncellendi: {StepIcon(step.Status)} {step.Text}";
        });

    [McpServerTool(Name = "plan_olustur")]
    [Description("Yeni bir plan oluşturur: başlık, proje klasörü (var olan bir klasör olmalı) ve satır satır adımlar. Kullanıcı 'şu planı oluştur' dediğinde burayı kullan.")]
    public string PlanOlustur(
        [Description("Planın başlığı.")] string baslik,
        [Description("Projenin bulunduğu klasörün tam yolu; var olmalı.")] string proje_klasoru,
        [Description("Adımlar, her satırda bir adım olacak şekilde (satır sonlarıyla ayrılmış).")] string adimlar) =>
        Run(() =>
        {
            var steps = adimlar.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            var plan = store.CreatePlan(baslik, proje_klasoru, steps);
            return $"Plan oluşturuldu: #{plan.Id} {plan.Title} ({steps.Count} adım).";
        });

    [McpServerTool(Name = "plan_adim_ekle")]
    [Description("Var olan bir plana yeni bir adım ekler; sira verilmezse sona eklenir, verilirse o konuma eklenip sonrakiler kaydırılır.")]
    public string PlanAdimEkle(
        [Description("Planın id'si.")] long plan_id,
        [Description("Yeni adımın metni.")] string metin,
        [Description("Eklenecek konum (1 tabanlı); boşsa sona eklenir.")] int? sira = null) =>
        Run(() =>
        {
            var step = store.AddStep(plan_id, metin, sira);
            return $"Adım eklendi: {step.Order}. {step.Text} (id: {step.Id}).";
        });

    private static string StatusText(PlanStatus status) => status switch
    {
        PlanStatus.Taslak => "taslak",
        PlanStatus.Calisiyor => "çalışıyor",
        PlanStatus.Bitti => "bitti",
        PlanStatus.Hata => "hata",
        _ => status.ToString(),
    };

    private static string StepIcon(StepStatus status) => status switch
    {
        StepStatus.Bekliyor => "⏳",
        StepStatus.Yapiliyor => "🔄",
        StepStatus.Bitti => "✅",
        StepStatus.Hata => "❌",
        StepStatus.Atlandi => "⏭",
        _ => "",
    };

    private static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is PlanNotFoundException or PlanStepNotFoundException or ArgumentException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (SqliteException ex)
        {
            throw new McpException($"Veritabanına erişilemedi ({ex.SqliteErrorCode}). Biraz sonra tekrar dene.", ex);
        }
    }
}
