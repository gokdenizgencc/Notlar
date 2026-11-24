using Notlar.Core;

namespace Notlar.Core.Tests;

public class HotkeyComboTests
{
    [Theory]
    [InlineData("Ctrl+Alt+N", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4E)]
    [InlineData("ctrl + shift + space", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x20)]
    [InlineData("Win+Shift+F12", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x7B)]
    [InlineData("Control+Windows+5", HotkeyModifiers.Control | HotkeyModifiers.Win, 0x35)]
    [InlineData("Alt+Num7", HotkeyModifiers.Alt, 0x67)]
    [InlineData("Ctrl+Alt+NumPlus", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x6B)]
    [InlineData("Ctrl+Escape", HotkeyModifiers.Control, 0x1B)]
    [InlineData("Ctrl+Alt+0xBA", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBA)]
    [InlineData("Alt+Ctrl+Shift+i", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x49)]
    public void TryParse_AcceptsValidCombos(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(HotkeyCombo.TryParse(text, out var combo));
        Assert.Equal(new HotkeyCombo(mods, vk), combo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N")]                 // değiştirici yok
    [InlineData("Shift+N")]           // yalnız Shift yazmayı bozar
    [InlineData("Ctrl+Alt")]          // tuş yok
    [InlineData("Ctrl+A+B")]          // iki tuş
    [InlineData("Ctrl++")]            // boş parça
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Bilinmeyen")]
    [InlineData("Ctrl+0x00")]
    public void TryParse_RejectsInvalid(string? text)
    {
        Assert.False(HotkeyCombo.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_IsCultureIndependent_ForTurkishI()
    {
        // Türkçe kültürde "i".ToUpper() == "İ"; ayrıştırma kültürden bağımsız olmalı.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
        try
        {
            Assert.True(HotkeyCombo.TryParse("ctrl+win+i", out var combo));
            Assert.Equal(0x49, combo.VirtualKey);
            Assert.True(HotkeyCombo.TryParse("Ctrl+Insert", out var ins));
            Assert.Equal(0x2D, ins.VirtualKey);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("shift+alt+ctrl+n", "Ctrl+Alt+Shift+N")]
    [InlineData("win+ctrl+f5", "Ctrl+Win+F5")]
    [InlineData("Ctrl+Alt+Del", "Ctrl+Alt+Delete")]
    [InlineData("Alt+pgup", "Alt+PageUp")]
    [InlineData("Ctrl+Alt+0xba", "Ctrl+Alt+0xBA")]
    [InlineData("Ctrl+Num0", "Ctrl+Num0")]
    [InlineData("Shift+N", null)]
    public void Normalize_ProducesCanonicalForm(string text, string? expected)
    {
        Assert.Equal(expected, HotkeyCombo.Normalize(text));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4E)]
    [InlineData(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x20)]
    [InlineData(HotkeyModifiers.Alt, 0x6B)]
    [InlineData(HotkeyModifiers.Control, 0x6F)]
    [InlineData(HotkeyModifiers.Control, 0x87)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0xDE)]
    public void ToString_RoundTrips(HotkeyModifiers mods, int vk)
    {
        var combo = new HotkeyCombo(mods, vk);
        Assert.True(HotkeyCombo.TryParse(combo.ToString(), out var parsed));
        Assert.Equal(combo, parsed);
    }

    [Theory]
    [InlineData(0x11, true)]  // Ctrl
    [InlineData(0xA0, true)]  // sol Shift
    [InlineData(0x5B, true)]  // sol Win
    [InlineData(0x41, false)]
    public void IsModifierKey(int vk, bool expected)
    {
        Assert.Equal(expected, HotkeyCombo.IsModifierKey(vk));
        if (expected) Assert.False(new HotkeyCombo(HotkeyModifiers.Control, vk).IsValid);
    }

    [Fact]
    public void FindConflicts_ReportsOnlyEnabledDuplicates()
    {
        var conflicts = HotkeyCombo.FindConflicts(new (string, bool, string?)[]
        {
            ("panel", true, "Ctrl+Alt+N"),
            ("not", true, "alt+ctrl+n"),      // aynı kombinasyon, farklı yazım
            ("kayit", false, "Ctrl+Alt+N"),   // kapalı: çakışma sayılmaz
            ("diger", true, "Ctrl+Alt+R"),
            ("bozuk", true, "Shift+N"),
        });

        Assert.Equal(2, conflicts.Count);
        Assert.Equal(["not"], conflicts["panel"]);
        Assert.Equal(["panel"], conflicts["not"]);
        Assert.False(conflicts.ContainsKey("kayit"));
        Assert.False(conflicts.ContainsKey("diger"));
    }

    [Fact]
    public void FindConflicts_ThreeWay()
    {
        var conflicts = HotkeyCombo.FindConflicts(new (int, bool, string?)[]
        {
            (1, true, "Win+K"), (2, true, "Win+K"), (3, true, "Win+K"),
        });
        Assert.Equal([2, 3], conflicts[1]);
        Assert.Equal([1, 3], conflicts[2]);
    }

    [Fact]
    public void FindConflicts_NoneWhenDistinct()
    {
        var conflicts = HotkeyCombo.FindConflicts(new (string, bool, string?)[]
        {
            ("a", true, "Ctrl+Alt+Shift+N"), ("b", true, "Ctrl+Alt+Shift+H"), ("c", true, null),
        });
        Assert.Empty(conflicts);
    }
}
