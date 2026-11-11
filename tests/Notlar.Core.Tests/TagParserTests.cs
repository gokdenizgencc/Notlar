using Notlar.Core;

namespace Notlar.Core.Tests;

public class TagParserTests
{
    [Fact]
    public void Parse_returns_lowercase_distinct_tags_with_turkish_rules()
    {
        Assert.Equal(["ev", "iş", "istanbul", "ışık"], TagParser.Parse("Faturayı öde #ev #İş #EV #İstanbul #IŞIK"));
    }

    [Fact]
    public void Parse_ignores_hash_not_at_word_start()
    {
        Assert.Empty(TagParser.Parse("C# öğren, mail@x#y"));
    }

    [Fact]
    public void Parse_allows_digits_dash_underscore()
    {
        Assert.Equal(["proje-2", "a_b"], TagParser.Parse("#proje-2 ve #a_b"));
    }

    [Fact]
    public void Strip_removes_tags_and_collapses_spaces()
    {
        Assert.Equal("Faturayı öde yarın", TagParser.Strip("Faturayı #ev öde  yarın #iş"));
    }

    [Fact]
    public void Normalize_trims_hash_and_lowercases()
    {
        Assert.Equal("iş", TagParser.Normalize(" #İş "));
    }

    [Fact]
    public void Note_exposes_tags_display_text_and_local_day()
    {
        var note = new Note(1, "Sunumu bitir #iş", new DateTimeOffset(2026, 11, 11, 0, 30, 0, TimeSpan.FromHours(3)), null, false);
        Assert.Equal(["iş"], note.Tags);
        Assert.Equal("Sunumu bitir", note.DisplayText);
        Assert.Equal(new DateOnly(2026, 11, 11), note.Day);
    }
}
