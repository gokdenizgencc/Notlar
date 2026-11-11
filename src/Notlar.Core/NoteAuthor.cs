using System.Globalization;

namespace Notlar.Core;

/// <summary>Not/görev yazarları için açık uçlu isim sabitleri ve normalize yardımcıları.
/// Yazar artık sabit bir enum değil, serbest bir metin; gelecekteki yapay zeka modelleri
/// (ör. ChatGPT, Gemini) kendi adlarıyla not/görev ekleyebilir.</summary>
public static class NoteAuthor
{
    public const string Ben = "Ben";
    public const string Claude = "Claude";

    private static readonly CultureInfo TrTR = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Yazar adını normalize eder: kırpılır; bilinen adlar ("ben", "claude", "chatgpt", "gemini")
    /// büyük/küçük harfe duyarsız şekilde kanonik adlarına çevrilir; diğerleri kırpılıp ilk harfi
    /// tr-TR kurallarına göre büyütülür. Boş/null girdi "Ben" olur.</summary>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Ben;

        return trimmed.ToLowerInvariant() switch
        {
            "ben" => Ben,
            "claude" => Claude,
            "chatgpt" => "ChatGPT",
            "gemini" => "Gemini",
            _ => CapitalizeFirst(trimmed),
        };
    }

    private static string CapitalizeFirst(string value) =>
        value.Length == 0 ? value : string.Concat(char.ToUpper(value[0], TrTR), value[1..]);
}
