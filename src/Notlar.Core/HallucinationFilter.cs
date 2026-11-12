using System.Globalization;

namespace Notlar.Core;

/// <summary>Whisper'ın sessizlikte / müzikte uydurduğu kalıp metinleri (YouTube altyazı imzaları vb.) eler.</summary>
public static class HallucinationFilter
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly string[] Patterns =
    [
        "altyazı m.k",
        "altyazı m k",
        "izlediğiniz için teşekkür",
        "abone ol",
        "abone olmayı unutmayın",
        "thanks for watching",
        "subtitles by",
        "[müzik]",
        "(müzik)",
        "[music]",
        "♪",
    ];

    /// <summary>Metin boşsa, yalnızca noktalama/simgeden oluşuyorsa veya bilinen bir halüsinasyon kalıbı
    /// içeriyorsa true. Karşılaştırma tr-TR küçük harfle, hem ham metinde hem de baştaki/sondaki noktalama
    /// kırpılmış metinde yapılır.</summary>
    public static bool IsHallucination(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!text.Any(char.IsLetterOrDigit))
            return true;

        // tr-TR küçük harf "ALTYAZI" → "altyazı" verir ama "MUSIC" → "musıc" yapar; İngilizce kalıplar için
        // kültürden bağımsız küçük harf de denenir.
        foreach (var lower in new[] { text.ToLower(Tr).Trim(), text.ToLowerInvariant().Trim() })
        {
            var normalized = lower.Trim(TrimChars(lower));
            foreach (var pattern in Patterns)
            {
                if (lower.Contains(pattern, StringComparison.Ordinal) || normalized.Contains(pattern, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Baştaki ve sondaki boşlukları kırpar; null → "".</summary>
    public static string Clean(string text) => text?.Trim() ?? "";

    private static char[] TrimChars(string value) =>
        value.Where(c => char.IsPunctuation(c) || char.IsSymbol(c) || char.IsWhiteSpace(c)).Distinct().ToArray();
}
