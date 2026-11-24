using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Notlar.Core;

/// <summary>Global kısayol değiştiricileri. Değerler Win32 RegisterHotKey'in MOD_* sabitleriyle birebir aynıdır.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>Bir global kısayol kombinasyonu: değiştiriciler + tek bir sanal tuş kodu (Win32 VK_*). WPF/WinForms'a
/// bağımlı değildir; ayar dosyasındaki "Ctrl+Alt+N" biçimini ayrıştırır ve biçimlendirir.</summary>
public readonly record struct HotkeyCombo(HotkeyModifiers Modifiers, int VirtualKey)
{
    // VK kodu ↔ görünen ad. Harfler (A–Z), rakamlar (0–9) ve F1–F24 aşağıda hesaplanır; burada yalnızca adlandırılmış tuşlar var.
    private static readonly (int Vk, string Name)[] NamedKeys =
    [
        (0x08, "Backspace"), (0x09, "Tab"), (0x0D, "Enter"), (0x13, "Pause"), (0x1B, "Esc"), (0x20, "Space"),
        (0x21, "PageUp"), (0x22, "PageDown"), (0x23, "End"), (0x24, "Home"),
        (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"),
        (0x2C, "PrintScreen"), (0x2D, "Insert"), (0x2E, "Delete"),
        (0x6A, "Num*"), (0x6B, "NumPlus"), (0x6D, "Num-"), (0x6E, "Num."), (0x6F, "Num/"),
        (0xBB, "Plus"), (0xBC, "Comma"), (0xBD, "Minus"), (0xBE, "Period"),
    ];

    // Ayrıştırmada kabul edilen ek adlar.
    private static readonly (int Vk, string Name)[] KeyAliases =
    [
        (0x1B, "Escape"), (0x0D, "Return"), (0x2E, "Del"), (0x2D, "Ins"), (0x21, "PgUp"), (0x22, "PgDn"),
        (0x2C, "PrtSc"),
    ];

    /// <summary>Tek başına "tuş" sayılmayan değiştirici tuşların VK kodları (Shift, Ctrl, Alt, sol/sağ türevleri, Win).</summary>
    private static readonly HashSet<int> ModifierVks = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    /// <summary>Global kısayol olarak kullanılabilir mi? En az bir Ctrl/Alt/Win gerekir (yalnız Shift + tuş yazmayı
    /// bozar) ve tuş bir değiştirici olmamalıdır.</summary>
    public bool IsValid =>
        VirtualKey is > 0 and < 0xFF
        && !ModifierVks.Contains(VirtualKey)
        && (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;

    /// <summary>Tuş tek başına bir değiştirici mi (kaydedici kutuda yalnız Ctrl'e basılmışken olduğu gibi)?</summary>
    public static bool IsModifierKey(int virtualKey) => ModifierVks.Contains(virtualKey);

    /// <summary>Kanonik biçim: "Ctrl+Alt+Shift+Win+Tuş".</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>VK kodunun görünen adı; bilinmeyen kodlar "0xBA" gibi onaltılık yazılır (yine ayrıştırılabilir).</summary>
    public static string KeyName(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A || virtualKey is >= 0x30 and <= 0x39)
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87)
            return "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture);
        if (virtualKey is >= 0x60 and <= 0x69)
            return "Num" + (virtualKey - 0x60).ToString(CultureInfo.InvariantCulture);
        foreach (var (vk, name) in NamedKeys)
            if (vk == virtualKey) return name;
        return "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>"Ctrl+Alt+N" biçimini ayrıştırır (büyük/küçük harf ve boşluk önemsizdir; "Control", "Windows" da
    /// kabul edilir). Tam olarak bir tuş olmalı ve sonuç <see cref="IsValid"/> olmalıdır.</summary>
    public static bool TryParse(string? text, out HotkeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0) return false; // "Ctrl++" ya da sondaki '+' (artı tuşları "Plus"/"NumPlus" diye yazılır).
            var mod = ParseModifier(token);
            if (mod != HotkeyModifiers.None)
            {
                modifiers |= mod;
                continue;
            }
            if (key is not null) return false; // İkinci tuş.
            if (!TryParseKey(token, out var vk)) return false;
            key = vk;
        }

        if (key is null) return false;
        combo = new HotkeyCombo(modifiers, key.Value);
        return combo.IsValid;
    }

    private static HotkeyModifiers ParseModifier(string token) => token.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" or "CTL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" or "META" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static bool TryParseKey(string token, out int vk)
    {
        vk = 0;
        var upper = token.ToUpperInvariant();
        if (upper.Length == 1 && (upper[0] is >= 'A' and <= 'Z' || upper[0] is >= '0' and <= '9'))
        {
            vk = upper[0];
            return true;
        }
        if (upper.Length >= 2 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            vk = 0x6F + f;
            return true;
        }
        if (upper.Length == 4 && upper.StartsWith("NUM", StringComparison.Ordinal) && upper[3] is >= '0' and <= '9')
        {
            vk = 0x60 + (upper[3] - '0');
            return true;
        }
        if (upper.StartsWith("0X", StringComparison.Ordinal)
            && int.TryParse(upper.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            && hex is > 0 and < 0xFF)
        {
            vk = hex;
            return true;
        }
        foreach (var (code, name) in NamedKeys.Concat(KeyAliases))
        {
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
            {
                vk = code;
                return true;
            }
        }
        return false;
    }

    /// <summary>Metni kanonik biçime çevirir; geçersizse null.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var combo) ? combo.ToString() : null;

    /// <summary>Açık (etkin) atamalar arasında aynı kombinasyonu paylaşanları bulur. Dönüş: çakışan her eylemin
    /// anahtarı → onunla çakışan diğer eylemlerin anahtarları. Kapalı ya da geçersiz atamalar çakışma sayılmaz.</summary>
    public static IReadOnlyDictionary<TKey, IReadOnlyList<TKey>> FindConflicts<TKey>(
        IEnumerable<(TKey Action, bool Enabled, string? Combo)> bindings) where TKey : notnull
    {
        var byCombo = new Dictionary<HotkeyCombo, List<TKey>>();
        foreach (var (action, enabled, text) in bindings)
        {
            if (!enabled || !TryParse(text, out var combo)) continue;
            if (!byCombo.TryGetValue(combo, out var list)) byCombo[combo] = list = [];
            list.Add(action);
        }

        var result = new Dictionary<TKey, IReadOnlyList<TKey>>();
        foreach (var list in byCombo.Values.Where(l => l.Count > 1))
            foreach (var action in list)
                result[action] = list.Where(a => !EqualityComparer<TKey>.Default.Equals(a, action)).ToList();
        return result;
    }
}
