using System.Globalization;
using System.Text;

namespace Notlar.Core;

/// <summary>Claude'un toplantı transkriptinden çıkardığı (henüz kaydedilmemiş) bir plan adımı: iş metni,
/// varsa sorumlusu ("Ben" = kullanıcı) ve toplantıda söylendiği hâliyle son tarihi.</summary>
public sealed record PlanDraftStep(string Text, string? Assignee = null, string? DueText = null);

/// <summary>Aynı sorumluya verilmiş adımlar (<see cref="Assignee"/> null: sorumlusu belirsiz).</summary>
public sealed record AssigneeGroup<T>(string? Assignee, IReadOnlyList<T> Steps);

/// <summary>Plan adımlarının sorumlu (kime verildi) bilgisi: ad normalleştirme, sorumluya göre gruplama ve
/// "kime ne verildi" özet metni.</summary>
public static class PlanAssignees
{
    /// <summary>Sorumlusu belli olmayan adımların gruplardaki / özet metnindeki başlığı.</summary>
    public const string UnassignedLabel = "Sorumlusu belirsiz";

    /// <summary>Sorumlu adı ve son tarih metni en çok bu kadar karakter tutulur.</summary>
    public const int MaxLength = 60;

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Sorumlu adları büyük/küçük harf duyarsız (Türkçe kurallarıyla) karşılaştırılır.</summary>
    public static readonly StringComparer NameComparer = StringComparer.Create(Tr, ignoreCase: true);

    /// <summary>Claude'un "bilinmiyor" anlamında yazabileceği değerler; sorumlu/son tarih boş sayılır.</summary>
    private static readonly HashSet<string> EmptyMarkers = new(StringComparer.Create(Tr, ignoreCase: true))
    {
        "null", "none", "-", "—", "?", "yok", "belirsiz", "bilinmiyor", "belli değil", "belirtilmedi",
        "belirtilmemiş", "sorumlu yok", UnassignedLabel,
    };

    /// <summary>Sorumlu adını normalleştirir: tek satıra indirir, sondaki ':' ve noktalamayı atar, "ben"
    /// (kullanıcının kendisi) → "Ben"; boş ya da "yok/belirsiz/null" gibi değerler → null; en çok
    /// <see cref="MaxLength"/> karakter.</summary>
    public static string? Normalize(string? name)
    {
        var flat = TextSafety.Flatten(name).Trim().TrimEnd(':', '.', ',', ';').Trim();
        if (flat.Length == 0 || EmptyMarkers.Contains(flat)) return null;
        if (NameComparer.Equals(flat, NoteAuthor.Ben)) return NoteAuthor.Ben;
        return flat.Length <= MaxLength ? flat : TextSafety.Truncate(flat, MaxLength);
    }

    /// <summary>Son tarih metnini normalleştirir (tek satır, en çok <see cref="MaxLength"/> karakter); boş ya da
    /// "yok/belirsiz/null" gibi değerler → null. Tarih yorumlanmaz; toplantıda söylendiği gibi saklanır.</summary>
    public static string? NormalizeDue(string? due)
    {
        var flat = TextSafety.Flatten(due).Trim().TrimEnd('.', ',', ';').Trim();
        if (flat.Length == 0 || EmptyMarkers.Contains(flat)) return null;
        return flat.Length <= MaxLength ? flat : TextSafety.Truncate(flat, MaxLength);
    }

    /// <summary>Adımlardaki farklı sorumluları ilk geçtikleri sırayla döndürür (sorumlusuz adımlar sayılmaz).
    /// Arayüzde her sorumluya bu sıraya göre ayrı bir renk verilir.</summary>
    public static IReadOnlyList<string> Order<T>(IEnumerable<T> steps, Func<T, string?> assignee)
    {
        var seen = new HashSet<string>(NameComparer);
        var order = new List<string>();
        foreach (var step in steps)
        {
            if (Normalize(assignee(step)) is { } name && seen.Add(name))
                order.Add(name);
        }
        return order;
    }

    public static IReadOnlyList<string> Order(IEnumerable<PlanStep> steps) => Order(steps, s => s.Assignee);

    /// <summary>Bir sorumlunun <see cref="Order{T}"/> listesindeki 1 tabanlı sırası (renk numarası); sorumlu
    /// yoksa ya da listede değilse 0.</summary>
    public static int ColorSlot(IReadOnlyList<string> order, string? assignee)
    {
        if (Normalize(assignee) is not { } name) return 0;
        for (var i = 0; i < order.Count; i++)
        {
            if (NameComparer.Equals(order[i], name)) return i + 1;
        }
        return 0;
    }

    /// <summary>Adımları sorumluya göre gruplar: gruplar sorumlunun ilk geçtiği sırada, grup içindeki adımlar
    /// kendi sıralarında; sorumlusuz adımlar (varsa) en sonda <c>Assignee = null</c> grubunda. Ad yazımı
    /// (büyük/küçük harf) farklı olsa da aynı kişi tek grupta toplanır; grup adı ilk yazımdır.</summary>
    public static IReadOnlyList<AssigneeGroup<T>> Group<T>(IEnumerable<T> steps, Func<T, string?> assignee)
    {
        var groups = new List<(string Name, List<T> Steps)>();
        var index = new Dictionary<string, int>(NameComparer);
        var unassigned = new List<T>();
        foreach (var step in steps)
        {
            if (Normalize(assignee(step)) is not { } name)
            {
                unassigned.Add(step);
                continue;
            }
            if (!index.TryGetValue(name, out var i))
            {
                i = groups.Count;
                index[name] = i;
                groups.Add((name, []));
            }
            groups[i].Steps.Add(step);
        }

        var result = groups.Select(g => new AssigneeGroup<T>(g.Name, g.Steps)).ToList();
        if (unassigned.Count > 0)
            result.Add(new AssigneeGroup<T>(null, unassigned));
        return result;
    }

    public static IReadOnlyList<AssigneeGroup<PlanStep>> Group(IEnumerable<PlanStep> steps) =>
        Group(steps, s => s.Assignee);

    /// <summary>"Kime ne verildi" özeti (panoya kopyalamak için), ör. "Ahmet: Sunumu hazırla, Bütçeyi gönder /
    /// Ayşe: Teklifi yaz (son tarih: Cuma) / Sorumlusu belirsiz: Toplantı notlarını paylaş". Adım sonundaki
    /// nokta atılır. Adım yoksa boş metin.</summary>
    public static string BuildSummary<T>(IEnumerable<T> steps, Func<T, string> text, Func<T, string?> assignee,
        Func<T, string?> due)
    {
        var sb = new StringBuilder();
        foreach (var group in Group(steps, assignee))
        {
            var items = group.Steps
                .Select(s => (Text: TextSafety.Flatten(text(s)).TrimEnd('.', ' '), Due: NormalizeDue(due(s))))
                .Where(s => s.Text.Length > 0)
                .Select(s => s.Due is { } d ? $"{s.Text} (son tarih: {d})" : s.Text)
                .ToList();
            if (items.Count == 0) continue;
            if (sb.Length > 0) sb.Append(" / ");
            sb.Append(group.Assignee ?? UnassignedLabel).Append(": ").Append(string.Join(", ", items));
        }
        return sb.ToString();
    }

    public static string BuildSummary(IEnumerable<PlanStep> steps) =>
        BuildSummary(steps, s => s.Text, s => s.Assignee, s => s.DueText);

    /// <summary>Adımlardan en az birinin sorumlusu var mı.</summary>
    public static bool AnyAssigned(IEnumerable<PlanStep> steps) => steps.Any(s => Normalize(s.Assignee) is not null);
}
