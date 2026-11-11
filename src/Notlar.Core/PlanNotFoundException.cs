namespace Notlar.Core;

public sealed class PlanNotFoundException(long id) : Exception($"Plan bulunamadı: {id}")
{
    public long Id { get; } = id;
}

public sealed class PlanStepNotFoundException(long id) : Exception($"Adım bulunamadı: {id}")
{
    public long Id { get; } = id;
}
