using System.Globalization;

namespace Notlar.Core;

/// <summary>Transkriptteki bir konuşmacının özeti (kişi çipi): görünen adı, adın kaynağı ve konuşma satırı sayısı.
/// <paramref name="Numbered"/>: adı bilinmiyor, "Kişi N" gösteriliyor.</summary>
public sealed record TranscriptPersonCount(int Person, string Name, PersonNameSource Source, bool Numbered, int Lines)
{
    /// <summary>Ad sesten tahmin edilmiş takma ad mı (gerçek kişi değil)?</summary>
    public bool IsGuess => Source == PersonNameSource.Tahmin && !Numbered;

    /// <summary>Çipte ve bilgi satırında görünen ad: takma adın başında "~".</summary>
    public string Label => (IsGuess ? "~" : "") + Name;
}

/// <summary>Transkripti kişiye göre süzme: kayıttaki kişileri satır sayılarıyla listeler, seçili kişilerin satırlarını
/// ayıklar ve "Ahmet — 23 satır gösteriliyor" bilgi satırını üretir. Seçim boşsa filtre kapalıdır (tüm satırlar).</summary>
public static class TranscriptPersonFilter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Bilgi satırında adı yazılan en fazla kişi; fazlası "+N kişi" olur.</summary>
    public const int MaxNamesInDescription = 3;

    /// <summary>Transkriptteki kişiler, çok konuşandan aza (eşitlikte ilk konuşma sırasıyla). Ad ve kaynağı kişinin ilk
    /// satırından alınır. Kişi bilgisi olmayan satırlar sayılmaz; hiç kişi yoksa boş liste (eski transkriptler).</summary>
    public static IReadOnlyList<TranscriptPersonCount> Count(IReadOnlyList<TranscriptSegment> segments)
    {
        var order = new List<int>();
        var first = new Dictionary<int, TranscriptSegment>();
        var lines = new Dictionary<int, int>();
        foreach (var s in segments)
        {
            if (s.Person is not { } person) continue;
            if (first.TryAdd(person, s)) order.Add(person);
            lines[person] = lines.GetValueOrDefault(person) + 1;
        }

        return order
            .Select((person, index) => (person, index))
            .OrderByDescending(x => lines[x.person])
            .ThenBy(x => x.index)
            .Select(x =>
            {
                var s = first[x.person];
                var name = TranscriptFormatter.DisplayName(s)!;
                var numbered = name == TranscriptFormatter.PersonLabel(x.person);
                return new TranscriptPersonCount(x.person, name, s.PersonNameSource, numbered, lines[x.person]);
            })
            .ToList();
    }

    /// <summary>Filtre açık mı (en az bir kişi seçili)?</summary>
    public static bool IsActive(IReadOnlyCollection<int>? selected) => selected is { Count: > 0 };

    /// <summary>Seçili kişilerin satırları (sıra korunur); seçim boşsa tüm satırlar. Filtre açıkken kişi bilgisi olmayan
    /// satırlar gizlenir.</summary>
    public static IReadOnlyList<TranscriptSegment> Apply(IReadOnlyList<TranscriptSegment> segments, IReadOnlyCollection<int>? selected)
    {
        if (!IsActive(selected)) return segments;
        return segments.Where(s => s.Person is { } person && selected!.Contains(person)).ToList();
    }

    /// <summary>Seçimden transkriptte artık olmayan kişileri atar (ör. yeniden çeviriden sonra).</summary>
    public static IReadOnlyList<int> Prune(IEnumerable<int> selected, IReadOnlyList<TranscriptPersonCount> persons)
    {
        var present = persons.Select(p => p.Person).ToHashSet();
        return selected.Where(present.Contains).Distinct().ToList();
    }

    /// <summary>Çipe tıklama: kişi seçiliyse çıkarır, değilse ekler (çipler aç/kapa gibi davranır).</summary>
    public static IReadOnlyList<int> Toggle(IReadOnlyCollection<int> selected, int person) =>
        selected.Contains(person) ? selected.Where(p => p != person).ToList() : [.. selected, person];

    /// <summary>Filtre açıkken gösterilen bilgi satırı: "Ahmet — 23 satır gösteriliyor", çoklu seçimde
    /// "Ahmet, Ayşe — 40 satır gösteriliyor". Filtre kapalıysa null.</summary>
    public static string? Describe(IReadOnlyList<TranscriptPersonCount> persons, IReadOnlyCollection<int>? selected) =>
        Summarize(persons, selected) is { } summary ? summary + " gösteriliyor" : null;

    /// <summary>Seçimin kısa özeti: "Ahmet — 23 satır", "Ahmet, Ayşe — 40 satır" (en çok
    /// <see cref="MaxNamesInDescription"/> ad, fazlası "+N kişi"). Adlar <see cref="Count"/> sırasıyla yazılır.
    /// Filtre kapalıysa (ya da seçili kişilerin hiçbiri transkriptte yoksa) null.</summary>
    public static string? Summarize(IReadOnlyList<TranscriptPersonCount> persons, IReadOnlyCollection<int>? selected)
    {
        if (!IsActive(selected)) return null;
        var chosen = persons.Where(p => selected!.Contains(p.Person)).ToList();
        if (chosen.Count == 0) return null;

        var names = string.Join(", ", chosen.Take(MaxNamesInDescription).Select(p => p.Label));
        if (chosen.Count > MaxNamesInDescription)
            names += string.Create(Inv, $" +{chosen.Count - MaxNamesInDescription} kişi");
        var lines = chosen.Sum(p => p.Lines);
        return string.Create(Inv, $"{names} — {lines} satır");
    }

    /// <summary>Gösterilecek çipler: kişi sayısı <paramref name="maxVisible"/>'ı aşıyorsa (ve açılmadıysa) ilk
    /// <paramref name="maxVisible"/> kişi ile listede daha aşağıda kalan seçili kişiler gösterilir, kalanların sayısı
    /// <c>Hidden</c> olarak döner ("+N" çipi). Tek kişi gizlenecekse daraltılmaz (onun yerine "+1" göstermek anlamsız).</summary>
    public static (IReadOnlyList<TranscriptPersonCount> Visible, int Hidden) Chips(
        IReadOnlyList<TranscriptPersonCount> persons, IReadOnlyCollection<int>? selected, int maxVisible, bool expanded)
    {
        if (expanded || persons.Count <= maxVisible + 1) return (persons, 0);
        var visible = persons
            .Where((p, index) => index < maxVisible || (selected?.Contains(p.Person) ?? false))
            .ToList();
        return (visible, persons.Count - visible.Count);
    }
}
