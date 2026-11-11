using System.Globalization;
using System.Text.RegularExpressions;

namespace Notlar.Core;

public static partial class TagParser
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    [GeneratedRegex(@"(?<=^|\s)#([\p{L}\p{N}_-]+)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();

    public static IReadOnlyList<string> Parse(string text) =>
        TagRegex().Matches(text).Select(m => Normalize(m.Groups[1].Value)).Distinct().ToList();

    public static string Strip(string text) =>
        MultiSpace().Replace(TagRegex().Replace(text.ReplaceLineEndings(" "), ""), " ").Trim();

    public static string Normalize(string tag) => tag.Trim().TrimStart('#').ToLower(Tr);
}
