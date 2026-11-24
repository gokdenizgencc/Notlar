namespace Notlar.Core;

public enum PlanStatus { Taslak, Calisiyor, Bitti, Hata }

public enum StepStatus { Bekliyor, Yapiliyor, Bitti, Hata, Atlandi }

public sealed record Plan(
    long Id,
    string Title,
    string? ProjectPath,
    PlanStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? ProjectId = null);

/// <summary><paramref name="Author"/>: adımı ekleyen ("Ben" = kullanıcı, aksi halde MCP yazarı, ör. "Claude").
/// <paramref name="ProjectId"/>: adımın isteğe bağlı olarak bağlı olduğu proje (planın bağlı olduğu
/// projelerden biri olmalı). <paramref name="Assignee"/>: işin verildiği kişi ("Ben" = kullanıcı; null =
/// sorumlusu belirsiz). <paramref name="DueText"/>: toplantıda söylendiği hâliyle son tarih (ör. "Cuma").</summary>
public sealed record PlanStep(
    long Id, long PlanId, int Order, string Text, StepStatus Status, string? Result, DateTimeOffset UpdatedAt,
    string Author = NoteAuthor.Ben, long? ProjectId = null, string? Assignee = null, string? DueText = null);

/// <summary>Bir planın çalıştırılacağı klasörü belirler: plan bir projeye bağlıysa ve o proje hâlâ canlıysa
/// projenin güncel yolu, aksi halde planın kendi kayıtlı proje_klasoru'u, o da yoksa null (klasöre bağlı
/// değil).</summary>
public static class PlanFolder
{
    public static string? Resolve(Plan plan, ProjectStore projects)
    {
        if (plan.ProjectId is { } id)
        {
            try { return projects.Get(id).Path; }
            catch (ProjectNotFoundException) { /* proje silinmiş; plana kayıtlı yola düş */ }
        }
        return plan.ProjectPath;
    }

    /// <summary>Çoklu proje desteğiyle: planın bağlı olduğu tüm (canlı) projelerin klasörlerini döndürür.
    /// İlki (position 0) ana klasördür; geri kalanı ekstra klasörlerdir (ör. Claude'a --add-dir ile verilir).
    /// Planın hiç projesi yoksa, geriye dönük uyumluluk için <see cref="Plan.ProjectPath"/> ana klasör olarak
    /// kullanılır. Ana proje silinmişse de aynı şekilde <see cref="Plan.ProjectPath"/>'e düşülür.</summary>
    public static (string? MainFolder, IReadOnlyList<string> ExtraFolders) ResolveAll(
        Plan plan, ProjectStore projects, PlanStore planStore)
    {
        var projectIds = planStore.GetPlanProjects(plan.Id);
        if (projectIds.Count == 0)
            return (plan.ProjectPath, Array.Empty<string>());

        string? mainFolder = null;
        var extra = new List<string>();
        for (var i = 0; i < projectIds.Count; i++)
        {
            string path;
            try { path = projects.Get(projectIds[i]).Path; }
            catch (ProjectNotFoundException) { continue; } // proje silinmiş; atla

            if (i == 0) mainFolder = path;
            else extra.Add(path);
        }
        mainFolder ??= plan.ProjectPath;
        return (mainFolder, extra);
    }
}
