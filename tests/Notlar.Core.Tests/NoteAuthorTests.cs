using Notlar.Core;

namespace Notlar.Core.Tests;

public class NoteAuthorTests
{
    [Theory]
    [InlineData(null, "Ben")]
    [InlineData("", "Ben")]
    [InlineData("   ", "Ben")]
    [InlineData("ben", "Ben")]
    [InlineData("BEN", "Ben")]
    [InlineData("claude", "Claude")]
    [InlineData("CLAUDE", "Claude")]
    [InlineData("chatgpt", "ChatGPT")]
    [InlineData("ChatGPT", "ChatGPT")]
    [InlineData("gemini", "Gemini")]
    [InlineData("GEMINI", "Gemini")]
    public void Normalize_canonicalizes_known_names(string? input, string expected) =>
        Assert.Equal(expected, NoteAuthor.Normalize(input));

    [Fact]
    public void Normalize_trims_and_capitalizes_unknown_names()
    {
        Assert.Equal("Mistral", NoteAuthor.Normalize("  mistral  "));
        Assert.Equal("Llama", NoteAuthor.Normalize("llama"));
    }
}
