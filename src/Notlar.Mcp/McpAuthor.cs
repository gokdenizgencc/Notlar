using Notlar.Core;

namespace Notlar.Mcp;

/// <summary>Bu MCP sunucusu örneğinin varsayılan yazarı (CLI'den <c>--yazar</c> ile verilir, yoksa "Claude").</summary>
public sealed record McpAuthor(string Name);

/// <summary>Komut satırı argümanlarından <c>--yazar &lt;ad&gt;</c> seçeneğini ayrıştırır.</summary>
public static class McpAuthorParser
{
    public static string Parse(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--yazar")
                return NoteAuthor.Normalize(args[i + 1]);
        }
        return NoteAuthor.Claude;
    }
}
