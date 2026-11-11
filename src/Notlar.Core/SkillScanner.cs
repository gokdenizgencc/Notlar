using System.Text;
using System.Text.Json;

namespace Notlar.Core;

/// <summary>Claude Code'da kurulu bir skill'i (kullanıcı skill'i veya plugin skill'i) tanımlar.</summary>
public sealed record SkillInfo(string Ai, string Id, string Name, string Description, string Source);

/// <summary>Bir skill'in transkriptlerden sayılan kullanım bilgisi.</summary>
public sealed record SkillUsage(int Count, DateTimeOffset? LastUsed);

/// <summary>Kullanıcının %USERPROFILE%\.claude dizinini tarayarak kurulu Claude Code skill'lerini ve
/// geçmiş oturum transkriptlerinden (projects/**/*.jsonl) kullanım sayılarını çıkarır.</summary>
public static class ClaudeSkillScanner
{
    public const string Ai = "Claude";
    public const string UserSource = "kullanıcı";
    public const string ClaudeAiSource = "claude.ai";

    public static string DefaultClaudeDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>claudeDir altındaki skills/*/SKILL.md (kullanıcı skill'leri) ve
    /// plugins/cache/&lt;market&gt;/&lt;plugin&gt;/&lt;version&gt;/skills/*/SKILL.md (plugin skill'leri,
    /// her plugin için yalnızca en yüksek versiyon) dosyalarını tarar.</summary>
    public static IReadOnlyList<SkillInfo> ScanInstalled(string? claudeDir = null)
    {
        claudeDir ??= DefaultClaudeDir;
        var result = new List<SkillInfo>();

        var userSkillsDir = Path.Combine(claudeDir, "skills");
        if (Directory.Exists(userSkillsDir))
        {
            ScanSkillsDirRecursive(userSkillsDir, userSkillsDir, depth: 0, result);
        }

        var pluginsCacheDir = Path.Combine(claudeDir, "plugins", "cache");
        if (Directory.Exists(pluginsCacheDir))
        {
            foreach (var marketplaceDir in TryGetDirectories(pluginsCacheDir))
            foreach (var pluginDir in TryGetDirectories(marketplaceDir))
            {
                var pluginName = Path.GetFileName(pluginDir);
                var latestVersionDir = TryGetDirectories(pluginDir)
                    .OrderByDescending(v => Path.GetFileName(v), Comparer<string>.Create(CompareVersions))
                    .FirstOrDefault();
                if (latestVersionDir is null) continue;

                var skillsDir = Path.Combine(latestVersionDir, "skills");
                if (!Directory.Exists(skillsDir)) continue;

                foreach (var dir in TryGetDirectories(skillsDir))
                {
                    var parsed = TryParseSkillMd(Path.Combine(dir, "SKILL.md"));
                    if (parsed is not { } p) continue;
                    result.Add(new SkillInfo(Ai, $"{pluginName}:{p.Name}", p.Name, p.Description, pluginName));
                }
            }
        }

        return result;
    }

    /// <summary>claudeDir\skills altında SKILL.md dosyalarını en fazla 3 seviye derine kadar özyinelemeli
    /// arar (düz "skills\&lt;ad&gt;\SKILL.md" kullanıcı skill'leri ve claude.ai senkron skill'leri olan
    /// "skills\synced\&lt;id&gt;\&lt;ad&gt;\SKILL.md"). "skills\.trash" içeriği yoksayılır. Bir dizinde
    /// SKILL.md bulununca altına inilmez.</summary>
    private static void ScanSkillsDirRecursive(string dir, string skillsRoot, int depth, List<SkillInfo> result)
    {
        const int maxDepth = 3;
        foreach (var child in TryGetDirectories(dir))
        {
            var childDepth = depth + 1;
            if (childDepth == 1 && string.Equals(Path.GetFileName(child), ".trash", StringComparison.OrdinalIgnoreCase))
                continue;

            var parsed = TryParseSkillMd(Path.Combine(child, "SKILL.md"));
            if (parsed is { } p)
            {
                var source = IsUnderSynced(skillsRoot, child) ? ClaudeAiSource : UserSource;
                result.Add(new SkillInfo(Ai, p.Name, p.Name, p.Description, source));
                continue;
            }

            if (childDepth < maxDepth)
                ScanSkillsDirRecursive(child, skillsRoot, childDepth, result);
        }
    }

    private static bool IsUnderSynced(string skillsRoot, string dir)
    {
        var relative = Path.GetRelativePath(skillsRoot, dir);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return string.Equals(firstSegment, "synced", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>claudeDir\projects altındaki tüm *.jsonl transkriptlerini tarayarak skill kullanım
    /// sayılarını çıkarır (Skill aracı çağrıları + /komut slash çağrıları). Okunamayan dosya/satırlar
    /// sessizce atlanır.</summary>
    public static IReadOnlyDictionary<string, SkillUsage> CountUsage(string? claudeDir = null, DateTimeOffset? since = null)
    {
        claudeDir ??= DefaultClaudeDir;
        var projectsDir = Path.Combine(claudeDir, "projects");
        var counts = new Dictionary<string, int>();
        var lastUsed = new Dictionary<string, DateTimeOffset?>();

        foreach (var file in EnumerateJsonlFiles(projectsDir))
        {
            var partial = ScanTranscriptFile(file, since);
            MergeInto(counts, lastUsed, partial.Counts, partial.LastUsed);
        }

        return BuildUsage(counts, lastUsed);
    }

    internal static IReadOnlyList<string> EnumerateJsonlFiles(string projectsDir)
    {
        try
        {
            if (!Directory.Exists(projectsDir)) return [];
            return Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal readonly record struct FilePartial(Dictionary<string, int> Counts, Dictionary<string, DateTimeOffset?> LastUsed);

    /// <summary>Tek bir .jsonl transkript dosyasını satır satır tarar. Bozuk/okunamayan satırlar ve
    /// dosyalar sessizce atlanır (asla fırlatmaz).</summary>
    internal static FilePartial ScanTranscriptFile(string path, DateTimeOffset? since = null)
    {
        var counts = new Dictionary<string, int>();
        var lastUsed = new Dictionary<string, DateTimeOffset?>();

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    ProcessLine(line, counts, lastUsed, since);
                }
                catch
                {
                    // Bozuk satır; say ama atla.
                }
            }
        }
        catch
        {
            // Okunamayan dosya; say ama atla.
        }

        return new FilePartial(counts, lastUsed);
    }

    private static void ProcessLine(string line, Dictionary<string, int> counts, Dictionary<string, DateTimeOffset?> lastUsed, DateTimeOffset? since)
    {
        if (!line.Contains("\"Skill\"") && !line.Contains("<command-name>"))
            return;

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("message", out var message))
            return;

        var timestamp = TryGetTimestamp(root);
        if (since is { } s && timestamp is { } t && t < s)
            return;

        if (line.Contains("\"Skill\"") &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "tool_use") continue;
                if (!item.TryGetProperty("name", out var nameProp) || nameProp.GetString() != "Skill") continue;
                if (!item.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) continue;
                if (!input.TryGetProperty("skill", out var skillProp) || skillProp.ValueKind != JsonValueKind.String) continue;

                var id = skillProp.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                Record(counts, lastUsed, id, timestamp);
            }
        }

        if (line.Contains("<command-name>") &&
            message.TryGetProperty("role", out var roleProp) &&
            roleProp.GetString() == "user" &&
            message.TryGetProperty("content", out var userContent))
        {
            foreach (var text in ExtractUserTexts(userContent))
            foreach (var id in ExtractCommandNames(text))
                Record(counts, lastUsed, id, timestamp);
        }
    }

    private static IEnumerable<string> ExtractUserTexts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            yield return content.GetString() ?? "";
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    item.TryGetProperty("text", out var textProp))
                    yield return textProp.GetString() ?? "";
            }
        }
    }

    private static IEnumerable<string> ExtractCommandNames(string text)
    {
        const string open = "<command-name>";
        const string close = "</command-name>";
        var pos = 0;
        while (true)
        {
            var start = text.IndexOf(open, pos, StringComparison.Ordinal);
            if (start < 0) yield break;
            start += open.Length;
            var end = text.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0) yield break;

            var raw = text[start..end].Trim();
            if (raw.StartsWith('/')) raw = raw[1..];
            if (!string.IsNullOrEmpty(raw)) yield return raw;
            pos = end + close.Length;
        }
    }

    private static void Record(Dictionary<string, int> counts, Dictionary<string, DateTimeOffset?> lastUsed, string id, DateTimeOffset? timestamp)
    {
        counts[id] = counts.GetValueOrDefault(id) + 1;
        var current = lastUsed.GetValueOrDefault(id);
        lastUsed[id] = MaxOf(current, timestamp);
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(ts.GetString(), out var value) ? value : null;
    }

    internal static DateTimeOffset? MaxOf(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }

    internal static void MergeInto(
        Dictionary<string, int> counts, Dictionary<string, DateTimeOffset?> lastUsed,
        Dictionary<string, int> addCounts, Dictionary<string, DateTimeOffset?> addLastUsed)
    {
        foreach (var kv in addCounts)
            counts[kv.Key] = counts.GetValueOrDefault(kv.Key) + kv.Value;
        foreach (var kv in addLastUsed)
            lastUsed[kv.Key] = MaxOf(lastUsed.GetValueOrDefault(kv.Key), kv.Value);
    }

    private static Dictionary<string, SkillUsage> BuildUsage(Dictionary<string, int> counts, Dictionary<string, DateTimeOffset?> lastUsed)
    {
        var result = new Dictionary<string, SkillUsage>();
        foreach (var kv in counts)
            result[kv.Key] = new SkillUsage(kv.Value, lastUsed.GetValueOrDefault(kv.Key));
        return result;
    }

    private static IReadOnlyList<string> TryGetDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            if (int.TryParse(sa, out var na) && int.TryParse(sb, out var nb))
            {
                var c = na.CompareTo(nb);
                if (c != 0) return c;
            }
            else
            {
                var c = string.CompareOrdinal(sa, sb);
                if (c != 0) return c;
            }
        }
        return 0;
    }

    private readonly record struct ParsedSkillMd(string Name, string Description);

    private static ParsedSkillMd? TryParseSkillMd(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "---") return null;

            var end = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { end = i; break; }
            }
            if (end == -1) return null;

            string? name = null;
            string? description = null;

            for (var i = 1; i < end; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = trimmed[..colonIdx].Trim();
                var rest = trimmed[(colonIdx + 1)..].TrimStart();

                if (key == "name")
                {
                    name = Unquote(rest);
                }
                else if (key == "description")
                {
                    if (rest is "|" or ">" or "|-" or ">-" or "|+" or ">+")
                    {
                        var (text, nextIndex) = ReadBlockScalar(lines, i + 1, end);
                        description = text;
                        i = nextIndex - 1;
                    }
                    else
                    {
                        description = Unquote(rest);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(name)) return null;
            return new ParsedSkillMd(name.Trim(), (description ?? "").Trim());
        }
        catch
        {
            return null;
        }
    }

    private static (string Text, int NextIndex) ReadBlockScalar(string[] lines, int start, int end)
    {
        var sb = new StringBuilder();
        var blockIndent = -1;
        var i = start;
        for (; i < end; i++)
        {
            var l = lines[i];
            if (string.IsNullOrWhiteSpace(l))
            {
                if (sb.Length > 0) sb.Append(' ');
                continue;
            }

            var lt = l.TrimStart();
            var indent = l.Length - lt.Length;
            if (blockIndent == -1) blockIndent = indent;
            if (indent < blockIndent) break;

            if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append(lt.TrimEnd());
        }
        return (sb.ToString().Trim(), i);
    }

    private static string Unquote(string value)
    {
        var s = value.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1].Replace("\\\"", "\"");
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
            return s[1..^1].Replace("''", "'");
        return s;
    }
}

/// <summary>Skill kullanım sayımını dosya bazında önbellekler (boyut + son değişiklik zamanına göre);
/// yalnızca değişen/yeni transkript dosyaları yeniden okunur.</summary>
public sealed class SkillUsageCache(string? cachePath = null)
{
    private readonly string _cachePath = cachePath ?? Path.Combine(AppPaths.DataDir, "skill-usage-cache.json");

    public IReadOnlyDictionary<string, SkillUsage> GetUsage(string? claudeDir = null)
    {
        claudeDir ??= ClaudeSkillScanner.DefaultClaudeDir;
        var projectsDir = Path.Combine(claudeDir, "projects");
        var files = ClaudeSkillScanner.EnumerateJsonlFiles(projectsDir);
        var persisted = LoadCache();

        var totalCounts = new Dictionary<string, int>();
        var totalLastUsed = new Dictionary<string, DateTimeOffset?>();
        var newCache = new Dictionary<string, CachedFileEntry>();

        foreach (var file in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists) continue;
            }
            catch
            {
                continue;
            }

            CachedFileEntry entry;
            if (persisted.TryGetValue(file, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                entry = cached;
            }
            else
            {
                var partial = ClaudeSkillScanner.ScanTranscriptFile(file);
                entry = new CachedFileEntry(info.Length, info.LastWriteTimeUtc, partial.Counts, partial.LastUsed);
            }

            newCache[file] = entry;
            ClaudeSkillScanner.MergeInto(totalCounts, totalLastUsed, entry.Counts, entry.LastUsed);
        }

        SaveCache(newCache);

        var result = new Dictionary<string, SkillUsage>();
        foreach (var kv in totalCounts)
            result[kv.Key] = new SkillUsage(kv.Value, totalLastUsed.GetValueOrDefault(kv.Key));
        return result;
    }

    private Dictionary<string, CachedFileEntry> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return [];
            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<Dictionary<string, CachedFileEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveCache(Dictionary<string, CachedFileEntry> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
            var json = JsonSerializer.Serialize(cache);
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // Önbellek yazılamazsa sorun değil; bir sonraki taramada her şey yeniden okunur.
        }
    }

    private sealed record CachedFileEntry(long Length, DateTime LastWriteUtc, Dictionary<string, int> Counts, Dictionary<string, DateTimeOffset?> LastUsed);
}
