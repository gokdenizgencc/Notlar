using System.Text.Json.Nodes;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class SettingsTransferTests
{
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "DataDirSecured", "PanelLeft" };

    private static JsonObject Current() => JsonNode.Parse("""
        {
          "DayFontSize": 16,
          "PanelPinned": true,
          "RecordingLanguage": "tr",
          "AutoRecordLastDate": null,
          "AuthorColors": { "Ben": "" },
          "DataDirSecured": true,
          "PanelLeft": 10
        }
        """)!.AsObject();

    [Fact]
    public void Merge_AppliesKnownCompatibleFields_IgnoresRest()
    {
        var result = SettingsTransfer.Merge(Current(), """
            {
              "dayFontSize": 20,
              "PanelPinned": "evet",
              "RecordingLanguage": "en",
              "AutoRecordLastDate": "2026-11-24",
              "AuthorColors": { "Claude": "#000000" },
              "DataDirSecured": false,
              "PanelLeft": 500,
              "Bilinmeyen": 1,
            }
            """, Excluded);

        Assert.Equal(["DayFontSize", "RecordingLanguage", "AutoRecordLastDate", "AuthorColors"], result.Applied);
        Assert.Equal(["PanelPinned", "DataDirSecured", "PanelLeft", "Bilinmeyen"], result.Ignored);

        var m = result.Merged;
        Assert.Equal(20, m["DayFontSize"]!.GetValue<int>());
        Assert.False(m.ContainsKey("dayFontSize"));
        Assert.True(m["PanelPinned"]!.GetValue<bool>());
        Assert.Equal("en", m["RecordingLanguage"]!.GetValue<string>());
        Assert.True(m["DataDirSecured"]!.GetValue<bool>());
        Assert.Equal(10, m["PanelLeft"]!.GetValue<int>());
        Assert.False(m.ContainsKey("Bilinmeyen"));
    }

    [Fact]
    public void Merge_DoesNotMutateCurrent_AndRejectsNullForNonNullable()
    {
        var current = Current();
        var result = SettingsTransfer.Merge(current, """{"DayFontSize": null, "PanelPinned": false}""", Excluded);
        Assert.Equal(["PanelPinned"], result.Applied);
        Assert.Equal(["DayFontSize"], result.Ignored);
        Assert.True(current["PanelPinned"]!.GetValue<bool>());
    }

    [Fact]
    public void Merge_ValidatorCanRejectField()
    {
        var result = SettingsTransfer.Merge(Current(), """{"DayFontSize": 20, "RecordingLanguage": "xx"}""", Excluded,
            (key, value) => key != "RecordingLanguage");
        Assert.Equal(["DayFontSize"], result.Applied);
        Assert.Equal(["RecordingLanguage"], result.Ignored);
    }

    [Fact]
    public void Merge_DuplicateKeysDifferingInCase_FirstWins()
    {
        var result = SettingsTransfer.Merge(Current(), """{"DayFontSize": 20, "DAYFONTSIZE": 30}""", Excluded);
        Assert.Equal(20, result.Merged["DayFontSize"]!.GetValue<int>());
        Assert.Equal(["DAYFONTSIZE"], result.Ignored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("{ bozuk")]
    public void Merge_RejectsNonObjectOrInvalidJson(string json) =>
        Assert.Throws<FormatException>(() => SettingsTransfer.Merge(Current(), json, Excluded));

    [Fact]
    public void Merge_RejectsOversizedInput() =>
        Assert.Throws<FormatException>(() =>
            SettingsTransfer.Merge(Current(), "{\"a\":\"" + new string('x', SettingsTransfer.MaxImportChars) + "\"}", Excluded));

    [Fact]
    public void StripExcluded_RemovesExcludedKeysOnly()
    {
        var stripped = SettingsTransfer.StripExcluded(Current(), Excluded);
        Assert.False(stripped.ContainsKey("DataDirSecured"));
        Assert.False(stripped.ContainsKey("PanelLeft"));
        Assert.Equal(5, stripped.Count);
    }
}
