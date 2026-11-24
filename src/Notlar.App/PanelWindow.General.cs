using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Genel davranışlarının panel tarafı: Alt+F4 (tepsiye gizle / çık), her zaman üstte, bugünün
/// gün penceresi (tepsi tıklaması seçeneği) ve konum sıfırlama.</summary>
public partial class PanelWindow
{
    /// <summary>Kapatma davranışı "Uygulamadan çık" iken Alt+F4'e basıldı; App onay sorup çıkar.</summary>
    public event Action? ExitRequested;

    private void InitializeGeneralBehavior()
    {
        Topmost = _settings.PanelAlwaysOnTop;
        Closing += PanelWindow_Closing;
        AppSettings.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var onTop = AppSettings.Load().PanelAlwaysOnTop;
                if (Topmost != onTop) Topmost = onTop;
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        });
    }

    /// <summary>Panel kapatılmaya çalışıldığında (Alt+F4) pencere asla yok edilmez (uygulama ömrü boyunca tek panel
    /// yeniden kullanılır): ayara göre gizlenir ya da uygulamadan çıkış istenir. Uygulama kapanırken karışılmaz.</summary>
    private void PanelWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;
        e.Cancel = true;

        WindowCloseMode mode;
        try
        {
            mode = AppSettings.Load().CloseMode;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            mode = WindowCloseMode.HideToTray;
        }

        if (mode == WindowCloseMode.Exit && ExitRequested is { } exit)
        {
            // Closing içinde Shutdown çağrılmaz; olay bittikten sonra (onay penceresiyle birlikte) çıkılır.
            Dispatcher.InvokeAsync(exit);
            return;
        }
        if (IsVisible) HidePanel();
    }

    /// <summary>Verilen günün gün penceresini açar; zaten açıksa öne getirir (paneldeki ⤢ Genişlet ile aynı pencere).</summary>
    public void OpenDayWindow(DateOnly day)
    {
        var existing = _dayWindows.FirstOrDefault(w => w.Day == day);
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new DayWindow(_store, day);
        window.Closed += (_, _) => _dayWindows.Remove(window);
        _dayWindows.Add(window);
        window.Show();
        window.Activate();
    }

    /// <summary>Tepsi tıklaması "Bugünün gün penceresini aç" seçeneği.</summary>
    public void OpenTodayWindow() => OpenDayWindow(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>⚙ Ayarlar → "Panel konumunu sıfırla": hatırlanan kompakt/büyük mod konumlarını siler; panel açıksa
    /// hemen varsayılan yere (kompakt: sağ alt, büyük: ekranın ortası) taşınır. Büyük moddaki boyut korunur.</summary>
    public void ResetPosition()
    {
        _settings = AppSettings.Update(GeneralSettings.ClearPanelPosition);
        if (!IsVisible || _fullScreen) return;
        ApplyWindowMode();
        if (!_settings.PanelLarge) RepositionCompact();
    }
}
