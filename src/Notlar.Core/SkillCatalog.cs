namespace Notlar.Core;

/// <summary>Panel'de gösterilecek, birleştirilmiş tek bir yetenek satırı: kurulu/elle kaydedilmiş bilgi
/// + kullanım sayısı. DescriptionTr varsa panelde Description yerine gösterilir (orijinal her zaman
/// ToolTip'te kalır); yoksa null'dır.</summary>
public sealed record SkillEntry(string Ai, string Id, string Name, string Description, string Source, int UsageCount, DateTimeOffset? LastUsed, string? DescriptionTr = null);

/// <summary>Claude'un taranan kurulu skill'lerini + kullanım sayılarını ve diğer AI'ların elle kaydedilen
/// yeteneklerini tek bir listede birleştirir.</summary>
public static class SkillCatalog
{
    public const string RemovedSource = "kaldırılmış";
    public const string RegisteredSource = "kayıtlı";

    public static IReadOnlyList<SkillEntry> Build(
        IReadOnlyList<SkillInfo> claudeInstalled,
        IReadOnlyDictionary<string, SkillUsage> claudeUsage,
        IReadOnlyList<RegistrySkill> registry,
        IReadOnlyDictionary<string, SkillTranslation>? translations = null)
    {
        translations ??= new Dictionary<string, SkillTranslation>();
        var entries = new List<SkillEntry>();
        var seenClaudeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var skill in claudeInstalled)
        {
            claudeUsage.TryGetValue(skill.Id, out var usage);
            translations.TryGetValue(skill.Id, out var tr);
            entries.Add(new SkillEntry(skill.Ai, skill.Id, skill.Name, skill.Description, skill.Source, usage?.Count ?? 0, usage?.LastUsed, tr?.DescriptionTr));
            seenClaudeIds.Add(skill.Id);
        }

        foreach (var kv in claudeUsage)
        {
            if (seenClaudeIds.Contains(kv.Key)) continue;
            entries.Add(new SkillEntry(ClaudeSkillScanner.Ai, kv.Key, kv.Key, "", RemovedSource, kv.Value.Count, kv.Value.LastUsed));
        }

        foreach (var reg in registry)
        {
            if (string.Equals(reg.Ai, ClaudeSkillScanner.Ai, StringComparison.Ordinal))
            {
                var existingIndex = entries.FindIndex(e =>
                    e.Ai == ClaudeSkillScanner.Ai && string.Equals(e.Name, reg.Name, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    var existing = entries[existingIndex];
                    entries[existingIndex] = existing with
                    {
                        UsageCount = existing.UsageCount + reg.UsageCount,
                        LastUsed = ClaudeSkillScanner.MaxOf(existing.LastUsed, reg.LastUsed),
                        Description = string.IsNullOrEmpty(existing.Description) ? reg.Description : existing.Description,
                    };
                    continue;
                }
            }

            entries.Add(new SkillEntry(reg.Ai, reg.Name, reg.Name, reg.Description, RegisteredSource, reg.UsageCount, reg.LastUsed));
        }

        return entries
            .OrderBy(e => e.Ai, StringComparer.Ordinal)
            .ThenByDescending(e => e.UsageCount)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }
}
