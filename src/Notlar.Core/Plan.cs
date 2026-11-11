namespace Notlar.Core;

public enum PlanStatus { Taslak, Calisiyor, Bitti, Hata }

public enum StepStatus { Bekliyor, Yapiliyor, Bitti, Hata, Atlandi }

public sealed record Plan(long Id, string Title, string ProjectPath, PlanStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record PlanStep(long Id, long PlanId, int Order, string Text, StepStatus Status, string? Result, DateTimeOffset UpdatedAt);
