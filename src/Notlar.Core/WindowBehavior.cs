namespace Notlar.Core;

/// <summary>Panel kapatılınca (Alt+F4) ne olsun?</summary>
public enum WindowCloseMode { HideToTray, Exit }

/// <summary>Tepsi simgesine sol tıklayınca ne olsun?</summary>
public enum TrayClickMode { TogglePanel, OpenTodayWindow }

/// <summary>⚙ Ayarlar → Genel'deki pencere/tepsi davranışlarının saf karar mantığı. Ayarlar JSON'da kısa metin
/// kodlarıyla saklanır; bilinmeyen ya da boş değer bugünkü varsayılana düşer (eski ayar dosyaları sorunsuz açılır).</summary>
public static class WindowBehavior
{
    public const string CloseToTray = "tray";
    public const string CloseExit = "exit";

    public const string TrayTogglePanel = "panel";
    public const string TrayOpenToday = "day";

    public static WindowCloseMode ParseCloseMode(string? value) =>
        string.Equals(value?.Trim(), CloseExit, StringComparison.OrdinalIgnoreCase) ? WindowCloseMode.Exit : WindowCloseMode.HideToTray;

    public static string Format(WindowCloseMode mode) => mode == WindowCloseMode.Exit ? CloseExit : CloseToTray;

    public static TrayClickMode ParseTrayClick(string? value) =>
        string.Equals(value?.Trim(), TrayOpenToday, StringComparison.OrdinalIgnoreCase) ? TrayClickMode.OpenTodayWindow : TrayClickMode.TogglePanel;

    public static string Format(TrayClickMode mode) => mode == TrayClickMode.OpenTodayWindow ? TrayOpenToday : TrayTogglePanel;

    /// <summary>Çıkış onay iletişim kutusunun metni (⚙ Ayarlar → "Çıkarken onay sor" açıkken).</summary>
    public static string ExitConfirmMessage(bool isRecording) => isRecording
        ? "Kayıt sürüyor; çıkılırsa kayıt durdurulup kaydedilir.\n\nNotlar'dan çıkılsın mı?"
        : "Notlar'dan çıkılsın mı?\n\nÇıkınca otomatik kayıt ve Teams araması kaydı çalışmaz.";
}
