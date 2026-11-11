using Notlar.Mcp;

namespace Notlar.Core.Tests;

public class McpAuthorParserTests
{
    [Fact]
    public void Parse_defaults_to_Claude_when_no_args()
    {
        Assert.Equal("Claude", McpAuthorParser.Parse([]));
    }

    [Fact]
    public void Parse_defaults_to_Claude_when_flag_absent()
    {
        Assert.Equal("Claude", McpAuthorParser.Parse(["--baska", "deger"]));
    }

    [Fact]
    public void Parse_reads_value_after_yazar_flag()
    {
        Assert.Equal("ChatGPT", McpAuthorParser.Parse(["--yazar", "ChatGPT"]));
    }

    [Fact]
    public void Parse_normalizes_the_parsed_value()
    {
        Assert.Equal("Gemini", McpAuthorParser.Parse(["--yazar", "gemini"]));
    }

    [Fact]
    public void Parse_ignores_trailing_flag_with_no_value()
    {
        Assert.Equal("Claude", McpAuthorParser.Parse(["--yazar"]));
    }
}
