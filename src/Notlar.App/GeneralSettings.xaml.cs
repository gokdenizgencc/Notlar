using System;
using System.Windows;
using System.Windows.Controls;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Genel'in ek ayarları. Her değişiklik diskteki güncel ayarlara anında yazılır
/// (<see cref="AppSettings.Update"/>); panel ve App değerleri <see cref="AppSettings.Changed"/> / her kullanımda
/// diskten okuyarak uygular. Görünür olunca ve ayarlar başka yerden değişince denetimler yeniden eşitlenir.</summary>
public partial class GeneralSettings : UserControl
{
    /// <summary>Denetimlere değer yüklenirken true: olay işleyicileri kaydetmez.</summary>
    private bool _syncing;

    public GeneralSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppSettings.Changed += OnSettingsChanged;
            Sync();
        };
        Unloaded += (_, _) => AppSettings.Changed -= OnSettingsChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Sync();
        };
    }

    private void OnSettingsChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible) Sync();
    });

    private void Sync()
    {
        AppSettings settings;
        try
        {
            settings = AppSettings.Load();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return;
        }

        _syncing = true;
        try
        {
            StartOpen.IsChecked = settings.StartPanelOpen;
            StartHidden.IsChecked = !settings.StartPanelOpen;
            CloseExit.IsChecked = settings.CloseMode == WindowCloseMode.Exit;
            CloseToTray.IsChecked = settings.CloseMode == WindowCloseMode.HideToTray;
            ConfirmExitCheck.IsChecked = settings.ConfirmExit;
            TrayOpenToday.IsChecked = settings.TrayClickMode == TrayClickMode.OpenTodayWindow;
            TrayTogglePanel.IsChecked = settings.TrayClickMode == TrayClickMode.TogglePanel;
            AlwaysOnTopCheck.IsChecked = settings.PanelAlwaysOnTop;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Startup_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var open = ReferenceEquals(sender, StartOpen);
        Save(s => s.StartPanelOpen = open);
    }

    private void Close_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var mode = ReferenceEquals(sender, CloseExit) ? WindowCloseMode.Exit : WindowCloseMode.HideToTray;
        Save(s => s.CloseAction = WindowBehavior.Format(mode));
    }

    private void ConfirmExitCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var confirm = ConfirmExitCheck.IsChecked == true;
        Save(s => s.ConfirmExit = confirm);
    }

    private void TrayClick_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var mode = ReferenceEquals(sender, TrayOpenToday) ? TrayClickMode.OpenTodayWindow : TrayClickMode.TogglePanel;
        Save(s => s.TrayClickAction = WindowBehavior.Format(mode));
    }

    private void AlwaysOnTopCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var onTop = AlwaysOnTopCheck.IsChecked == true;
        Save(s => s.PanelAlwaysOnTop = onTop);
    }

    /// <summary>Panelin içindeyse panel konumu hemen sıfırlar (açıkken yerine taşınır); değilse yalnızca kayıtlı
    /// konumlar silinir, bir sonraki açılışta varsayılan konum kullanılır.</summary>
    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Window.GetWindow(this) is PanelWindow panel)
                panel.ResetPosition();
            else
                AppSettings.Update(ClearPanelPosition);
            HideError();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError($"Konum sıfırlanamadı: {ex.Message}");
        }
    }

    /// <summary>Kompakt ve büyük moddaki hatırlanan konumları siler (büyük moddaki boyut korunur).</summary>
    public static void ClearPanelPosition(AppSettings s)
    {
        s.PanelLeft = null;
        s.PanelTop = null;
        s.PanelLargeLeft = null;
        s.PanelLargeTop = null;
    }

    private void Save(Action<AppSettings> mutate)
    {
        try
        {
            AppSettings.Update(mutate);
            HideError();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError($"Ayar kaydedilemedi: {ex.Message}");
            Sync();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
