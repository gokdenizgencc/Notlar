using System.Text.Json.Serialization;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Genel: başlangıç, kapatma, tepsi tıklaması ve pencere davranışı. Varsayılanlar bu ayarlar
/// eklenmeden önceki davranışın aynısıdır (tepside gizli başla, Alt+F4 tepsiye gizler, onay yok, tepsi tıklaması
/// paneli aç/kapatır, panel her zaman üstte).</summary>
public sealed partial class AppSettings
{
    /// <summary>Notlar açılınca (ör. Windows ile başlarken) panel hemen gösterilsin mi? false: tepside gizli başlar.</summary>
    public bool StartPanelOpen { get; set; }

    /// <summary>Panel kapatılınca (Alt+F4): <see cref="WindowBehavior.CloseToTray"/> | <see cref="WindowBehavior.CloseExit"/>.</summary>
    public string CloseAction { get; set; } = WindowBehavior.CloseToTray;

    /// <summary>Uygulamadan çıkarken (tepsi "Çıkış" ya da kapatma davranışı "çık") onay sorulsun mu?</summary>
    public bool ConfirmExit { get; set; }

    /// <summary>Tepsi simgesine sol tıklama: <see cref="WindowBehavior.TrayTogglePanel"/> | <see cref="WindowBehavior.TrayOpenToday"/>.</summary>
    public string TrayClickAction { get; set; } = WindowBehavior.TrayTogglePanel;

    /// <summary>Panel diğer pencerelerin üstünde kalsın mı (Topmost)?</summary>
    public bool PanelAlwaysOnTop { get; set; } = true;

    [JsonIgnore]
    public WindowCloseMode CloseMode => WindowBehavior.ParseCloseMode(CloseAction);

    [JsonIgnore]
    public TrayClickMode TrayClickMode => WindowBehavior.ParseTrayClick(TrayClickAction);

    partial void NormalizeGeneral()
    {
        CloseAction = WindowBehavior.Format(CloseMode);
        TrayClickAction = WindowBehavior.Format(TrayClickMode);
    }
}
