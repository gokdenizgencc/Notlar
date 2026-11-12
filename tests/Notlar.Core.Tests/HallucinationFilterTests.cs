using Notlar.Core;

namespace Notlar.Core.Tests;

public class HallucinationFilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData(" ?! ")]
    [InlineData("♪ ♪")]
    [InlineData("Altyazı M.K.")]
    [InlineData("ALTYAZI M.K.")]
    [InlineData("altyazı m k")]
    [InlineData("İzlediğiniz için teşekkür ederim.")]
    [InlineData("Abone ol!")]
    [InlineData("Kanalıma abone olmayı unutmayın")]
    [InlineData("Thanks for watching!")]
    [InlineData("THANKS FOR WATCHING")]
    [InlineData("Subtitles by the Amara.org community")]
    [InlineData("[Müzik]")]
    [InlineData("(müzik)")]
    [InlineData("[MUSIC]")]
    [InlineData("♪ la la la ♪")]
    public void Detects_hallucinations(string text)
    {
        Assert.True(HallucinationFilter.IsHallucination(text));
    }

    [Theory]
    [InlineData("Toplantıya başlayalım.")]
    [InlineData("Bu sprintte üç iş var.")]
    [InlineData("Teşekkür ederim, görüşürüz.")]
    [InlineData("Let's review the backlog.")]
    [InlineData("42")]
    public void Keeps_real_speech(string text)
    {
        Assert.False(HallucinationFilter.IsHallucination(text));
    }

    [Fact]
    public void Null_is_hallucination()
    {
        Assert.True(HallucinationFilter.IsHallucination(null!));
    }

    [Theory]
    [InlineData("  merhaba  ", "merhaba")]
    [InlineData("\tselam\n", "selam")]
    [InlineData("", "")]
    public void Clean_trims_whitespace(string input, string expected)
    {
        Assert.Equal(expected, HallucinationFilter.Clean(input));
    }

    [Fact]
    public void Clean_null_returns_empty()
    {
        Assert.Equal("", HallucinationFilter.Clean(null!));
    }
}
