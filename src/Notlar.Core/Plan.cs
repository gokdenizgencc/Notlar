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

public sealed record PlanStep(long Id, long PlanId, int Order, string Text, StepStatus Status, string? Result, DateTimeOffset UpdatedAt);

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
}
