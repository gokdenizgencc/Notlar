using System;
using System.Linq;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Sistem geneli (global) kısayolun yaptığı iş.</summary>
public enum GlobalHotkeyAction
{
    /// <summary>Paneli aç / kapat.</summary>
    TogglePanel,
    /// <summary>Paneli Notlar bölümünde açıp not kutusuna odaklan.</summary>
    QuickNote,
    /// <summary>Toplantı kaydını başlat / durdur.</summary>
    ToggleRecording,
}

/// <summary>⚙ Ayarlar → Kısayollar: global kısayollar. Her eylem için aç/kapa ve "Ctrl+Alt+Shift+N" biçiminde
/// kombinasyon saklanır (bkz. <see cref="HotkeyCombo"/>). Varsayılan: hepsi kapalı (kullanıcının başka uygulamalarıyla
/// çakışmasın), önerilen tuşlar dolu. Ctrl+Alt yerine Ctrl+Alt+Shift önerilir: Türkçe klavyede Ctrl+Alt = AltGr
/// (@, €, { … yazmak) olduğundan Ctrl+Alt+harf kısayolları yazmayı bozabilir.</summary>
public sealed partial class AppSettings
{
    public const string DefaultHotkeyTogglePanel = "Ctrl+Alt+Shift+N";
    public const string DefaultHotkeyQuickNote = "Ctrl+Alt+Shift+Y";
    public const string DefaultHotkeyToggleRecording = "Ctrl+Alt+Shift+R";

    public bool HotkeyTogglePanelEnabled { get; set; }
    public string HotkeyTogglePanel { get; set; } = DefaultHotkeyTogglePanel;

    public bool HotkeyQuickNoteEnabled { get; set; }
    public string HotkeyQuickNote { get; set; } = DefaultHotkeyQuickNote;

    public bool HotkeyToggleRecordingEnabled { get; set; }
    public string HotkeyToggleRecording { get; set; } = DefaultHotkeyToggleRecording;

    public static string DefaultHotkey(GlobalHotkeyAction action) => action switch
    {
        GlobalHotkeyAction.QuickNote => DefaultHotkeyQuickNote,
        GlobalHotkeyAction.ToggleRecording => DefaultHotkeyToggleRecording,
        _ => DefaultHotkeyTogglePanel,
    };

    public bool GetHotkeyEnabled(GlobalHotkeyAction action) => action switch
    {
        GlobalHotkeyAction.QuickNote => HotkeyQuickNoteEnabled,
        GlobalHotkeyAction.ToggleRecording => HotkeyToggleRecordingEnabled,
        _ => HotkeyTogglePanelEnabled,
    };

    public string GetHotkey(GlobalHotkeyAction action) => action switch
    {
        GlobalHotkeyAction.QuickNote => HotkeyQuickNote,
        GlobalHotkeyAction.ToggleRecording => HotkeyToggleRecording,
        _ => HotkeyTogglePanel,
    };

    public void SetHotkeyEnabled(GlobalHotkeyAction action, bool enabled)
    {
        switch (action)
        {
            case GlobalHotkeyAction.QuickNote: HotkeyQuickNoteEnabled = enabled; break;
            case GlobalHotkeyAction.ToggleRecording: HotkeyToggleRecordingEnabled = enabled; break;
            default: HotkeyTogglePanelEnabled = enabled; break;
        }
    }

    public void SetHotkey(GlobalHotkeyAction action, string combo)
    {
        switch (action)
        {
            case GlobalHotkeyAction.QuickNote: HotkeyQuickNote = combo; break;
            case GlobalHotkeyAction.ToggleRecording: HotkeyToggleRecording = combo; break;
            default: HotkeyTogglePanel = combo; break;
        }
    }

    /// <summary>Geçersiz kombinasyonları varsayılana çeker, kanonik biçime getirir; aynı kombinasyonla açık iki
    /// eylem varsa (dosya elle düzenlenmişse) sonrakini kapatır.</summary>
    partial void NormalizeShortcuts()
    {
        foreach (var action in Enum.GetValues<GlobalHotkeyAction>())
            SetHotkey(action, HotkeyCombo.Normalize(GetHotkey(action)) ?? DefaultHotkey(action));

        var conflicts = HotkeyCombo.FindConflicts(
            Enum.GetValues<GlobalHotkeyAction>().Select(a => (a, GetHotkeyEnabled(a), (string?)GetHotkey(a))));
        foreach (var (action, others) in conflicts)
        {
            if (others.Any(o => o < action) && GetHotkeyEnabled(action))
                SetHotkeyEnabled(action, false);
        }
    }
}
