using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Notlar.Core;

/// <summary>Transkripti "[mm:ss] Ben: metin" / "[mm:ss] Karşı taraf: metin" satırlarına çevirir.</summary>
public static partial class TranscriptFormatter
{
    /// <summary>Transkriptin kullanıcı verisi olarak sarıldığı etiket adı (Claude istemleri ve MCP çıktısı).</summary>
    public const string DataTag = "transkript_verisi";

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
            sb.Append('[').Append(Stamp(s.StartMs, withHours)).Append("] ")
                .Append(Label(s.Speaker)).Append(": ")
                .Append(TextSafety.Flatten(s.Text));
        }
        return sb.ToString();
    }

    /// <summary>Biçimli transkripti &lt;transkript_verisi&gt; … &lt;/transkript_verisi&gt; etiketleri arasına
    /// koyar. Metin içinde geçen etiket benzeri ifadeler ('&lt;' → '‹') etkisizleştirilir; böylece transkript
    /// etiketi erkenden kapatıp kendi "talimatını" veri dışına taşıyamaz.</summary>
    public static string WrapAsData(IReadOnlyList<TranscriptSegment> segments)
    {
        var body = TagLike().Replace(Format(segments), m => "‹" + m.Value[1..]);
        return $"<{DataTag}>\n{body}\n</{DataTag}>";
    }

    /// <summary>Konuşmacının Türkçe etiketi: "Ben" / "Karşı taraf".</summary>
    public static string Label(Speaker speaker) => speaker == Speaker.Ben ? "Ben" : "Karşı taraf";

    private static string Stamp(long ms, bool withHours)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return withHours
            ? string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(Inv, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
    }

    [GeneratedRegex(@"<\s*/?\s*transkript_verisi", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagLike();
}
