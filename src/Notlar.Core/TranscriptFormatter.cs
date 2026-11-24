using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Notlar.Core;

/// <summary>Transkripti "[mm:ss] Ad: metin" satırlarına çevirir: her satır ayrı bir konuşmadır; ad sese göre bulunan
/// konuşmacının adıdır (aynı ses = aynı ad): sesin tonundan tahmin edilmiş takma ad (ör. "Ahmet") ya da kullanıcının
/// verdiği gerçek ad; adı olmayan kişiler (eski kayıtlar) "Kişi N". Kişi bilgisi olmayan satırlar (eski kayıtlar,
/// kişi ayrımı yapılamayanlar) etiketsiz "[mm:ss] metin" olarak yazılır.</summary>
public static partial class TranscriptFormatter
{
    /// <summary>Transkriptin kullanıcı verisi olarak sarıldığı etiket adı (Claude istemleri ve MCP çıktısı).</summary>
    public const string DataTag = "transkript_verisi";

    /// <summary>Kayıt özetinin kullanıcı verisi olarak sarıldığı etiket adı (MCP çıktısı).</summary>
    public const string SummaryTag = "ozet_verisi";

    private const long HourMs = 3_600_000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Satır başına bir segment; kayıt 1 saat veya daha uzunsa tüm zaman damgaları [h:mm:ss] olur.
    /// Metindeki satır sonları/kontrol karakterleri boşluğa çevrilir (her segment tek satır kalır).</summary>
    public static string Format(IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0)
            return "";

        var withHours = segments.Max(s => Math.Max(s.StartMs, s.EndMs)) >= HourMs;
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append('[').Append(Stamp(s.StartMs, withHours)).Append("] ");
            if (DisplayName(s) is { } name) sb.Append(name).Append(": ");
            sb.Append(TextSafety.Flatten(s.Text));
        }
        return sb.ToString();
    }

    /// <summary>Kişi numarasının görünen adı: "Kişi 1", "Kişi 2"…</summary>
    public static string PersonLabel(int person) => string.Create(Inv, $"Kişi {person}");

    /// <summary>Satırın konuşmacı etiketi: kişinin adı (tek satıra indirilmiş), adı yoksa "Kişi N"; kişi yoksa null.</summary>
    public static string? DisplayName(TranscriptSegment segment)
    {
        if (segment.Person is not { } person) return null;
        var name = TextSafety.Flatten(segment.PersonName);
        return name.Length > 0 ? name : PersonLabel(person);
    }

    /// <summary>Transkriptteki konuşmacıların açıklaması (Claude istemleri ve MCP çıktısı için), ilk konuşma sırasıyla:
    /// her ad için kullanıcının verdiği gerçek ad mı, hitaptan çıkan ad mı, sesten tahmin edilmiş takma ad mı yoksa yalnızca numara mı
    /// olduğu. Kişi etiketi yoksa boş metin. Adlar veri etiketlerini taklit edemez ('&lt;' → '‹').</summary>
    public static string DescribePersons(IReadOnlyList<TranscriptSegment> segments)
    {
        var seen = new HashSet<int>();
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (s.Person is not { } person || !seen.Add(person)) continue;
            var name = DisplayName(s)!;
            var kind = s.PersonNameSource switch
            {
                PersonNameSource.Kullanici => "kullanıcının verdiği GERÇEK ad (bu kişi gerçekten bu addadır)",
                PersonNameSource.Hitap => "toplantıda bu kişiye bu adla hitap edildiği için konuşmadan çıkarılan ad "
                                          + "(büyük olasılıkla gerçek adı, ama kesin değil)",
                _ when name == PersonLabel(person) || TextSafety.Flatten(s.PersonName).Length == 0
                    => "yalnızca numara (adı bilinmiyor)",
                _ => "sesin tonundan tahmin edilmiş TAKMA ad (gerçek kimlik değil; kişinin gerçek adı bilinmiyor)",
            };
            sb.Append("- ").Append(TagLike().Replace(name, m => "‹" + m.Value[1..])).Append(": ").Append(kind).Append('\n');
        }
        return sb.Length == 0 ? "" : "Konuşmacılar:\n" + sb.ToString().TrimEnd('\n');
    }

    /// <summary>Biçimli transkripti &lt;transkript_verisi&gt; … &lt;/transkript_verisi&gt; etiketleri arasına
    /// koyar. Metin içinde geçen etiket benzeri ifadeler ('&lt;' → '‹') etkisizleştirilir; böylece transkript
    /// etiketi erkenden kapatıp kendi "talimatını" veri dışına taşıyamaz.</summary>
    public static string WrapAsData(IReadOnlyList<TranscriptSegment> segments)
    {
        var body = TagLike().Replace(Format(segments), m => "‹" + m.Value[1..]);
        return $"<{DataTag}>\n{body}\n</{DataTag}>";
    }

    /// <summary>Özeti (çok satırlı, Markdown olabilir) &lt;ozet_verisi&gt; etiketleri arasına koyar. Özet, transkriptten
    /// üretildiği ya da MCP ile yazıldığı için güvenilmez veridir: veri etiketlerini taklit eden ifadeler
    /// etkisizleştirilir, görünmez/bidi karakterler atılır; satır yapısı korunur.</summary>
    public static string WrapSummaryAsData(string summary)
    {
        var lines = summary.ReplaceLineEndings("\n").Split('\n').Select(TextSafety.Flatten);
        var body = TagLike().Replace(string.Join('\n', lines), m => "‹" + m.Value[1..]);
        return $"<{SummaryTag}>\n{body}\n</{SummaryTag}>";
    }

    private static string Stamp(long ms, bool withHours)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return withHours
            ? string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(Inv, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
    }

    [GeneratedRegex(@"<\s*/?\s*(?:transkript_verisi|ozet_verisi)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagLike();
}
