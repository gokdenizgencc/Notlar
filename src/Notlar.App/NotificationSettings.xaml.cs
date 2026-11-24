using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Bildirimler: ana anahtar, olay bazlı anahtarlar, ses, rahatsız etmeyin ve örnek bildirim.
/// Her değişiklik anında <see cref="AppSettings.Update"/> ile kaydedilir; görünür olduğunda diskten tazelenir.</summary>
public partial class NotificationSettings : UserControl
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>Denetimlere değer yüklenirken true: olay işleyicileri kaydetmez.</summary>
    private bool _syncing;

    public NotificationSettings()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Visibility = Visibility.Collapsed; };
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
        Loaded += (_, _) => AppSettings.Changed += OnSettingsChanged;
        Unloaded += (_, _) => AppSettings.Changed -= OnSettingsChanged;
        Reload();
    }

    private void OnSettingsChanged() => Dispatcher.InvokeAsync(() => { if (IsVisible) Reload(); });

    private void Reload()
    {
        var s = AppSettings.Load();
        _syncing = true;
        try
        {
            EnabledCheck.IsChecked = s.NotificationsEnabled;
            ManualCheck.IsChecked = s.NotifyRecordingManual;
            ScheduledCheck.IsChecked = s.NotifyRecordingScheduled;
            TeamsCheck.IsChecked = s.NotifyRecordingTeams;
            TranscriptCheck.IsChecked = s.NotifyTranscriptReady;
            SummaryCheck.IsChecked = s.NotifySummaryReady;
            ErrorsCheck.IsChecked = s.NotifyErrors;
            SoundCheck.IsChecked = s.NotificationSound;
            QuietCheck.IsChecked = s.QuietHoursEnabled;
            // Kullanıcı saat yazarken kutunun metni ezilmez.
            if (!QuietStartBox.IsKeyboardFocusWithin) QuietStartBox.Text = s.QuietHoursStart;
            if (!QuietEndBox.IsKeyboardFocusWithin) QuietEndBox.Text = s.QuietHoursEnd;
            UpdateEnabledState();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateEnabledState()
    {
        DetailPanel.IsEnabled = EnabledCheck.IsChecked == true;
        DetailPanel.Opacity = DetailPanel.IsEnabled ? 1 : 0.5;
        QuietStartBox.IsEnabled = QuietEndBox.IsEnabled = QuietCheck.IsChecked == true;
    }

    private void Check_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsInitialized) return;
        UpdateEnabledState();
        var enabled = EnabledCheck.IsChecked == true;
        var manual = ManualCheck.IsChecked == true;
        var scheduled = ScheduledCheck.IsChecked == true;
        var teams = TeamsCheck.IsChecked == true;
        var transcript = TranscriptCheck.IsChecked == true;
        var summary = SummaryCheck.IsChecked == true;
        var errors = ErrorsCheck.IsChecked == true;
        var sound = SoundCheck.IsChecked == true;
        var quiet = QuietCheck.IsChecked == true;
        Save(s =>
        {
            s.NotificationsEnabled = enabled;
            s.NotifyRecordingManual = manual;
            s.NotifyRecordingScheduled = scheduled;
            s.NotifyRecordingTeams = teams;
            s.NotifyTranscriptReady = transcript;
            s.NotifySummaryReady = summary;
            s.NotifyErrors = errors;
            s.NotificationSound = sound;
            s.QuietHoursEnabled = quiet;
        });
    }

    private void QuietTime_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        e.Handled = true;
        CommitQuietTime(box);
    }

    private void QuietTime_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitQuietTime(box);
    }

    /// <summary>Saat kutusunu doğrulayıp kaydeder; geçersizse eski değere döner.</summary>
    private void CommitQuietTime(TextBox box)
    {
        if (_syncing) return;
        var isStart = ReferenceEquals(box, QuietStartBox);
        var current = AppSettings.Load();
        var previous = isStart ? current.QuietHoursStart : current.QuietHoursEnd;

        if (!RecordingSchedule.TryParseTime(box.Text, out var time))
        {
            box.Text = previous;
            ShowStatus("Saat SS:dd biçiminde olmalı (ör. 22:00).");
            return;
        }

        var text = RecordingSchedule.FormatTime(time);
        box.Text = text;
        if (text == previous) return;
        Save(s =>
        {
            if (isStart) s.QuietHoursStart = text;
            else s.QuietHoursEnd = text;
        });

        var saved = AppSettings.Load();
        if (saved.QuietHoursStart == saved.QuietHoursEnd)
            ShowStatus("Başlangıç ve bitiş aynı; bu hâliyle hiçbir bildirim susturulmaz.");
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Load();
        var shown = Notifier.Show(NotificationKind.Test, "Notlar — örnek bildirim",
            s.NotificationSound ? "Bildirimler böyle görünecek." : "Bildirimler böyle görünecek (sessiz).");
        if (!shown)
        {
            ShowStatus("Bildirim gösterilemedi (tepsi simgesi hazır değil).");
            return;
        }

        var prefs = s.ToNotificationPreferences();
        if (!s.NotificationsEnabled)
            ShowStatus("Örnek gönderildi; ancak bildirimler kapalı olduğu için diğer bildirimler gösterilmez.");
        else if (prefs.QuietHoursEnabled && NotificationPolicy.IsInQuietHours(TimeOnly.FromDateTime(DateTime.Now),
                     prefs.QuietStart, prefs.QuietEnd))
            ShowStatus("Örnek gönderildi; şu an rahatsız etmeyin saatlerindesiniz, hata dışındaki bildirimler susturulur.");
        else
            ShowStatus("Örnek bildirim gönderildi. Görmediyseniz Windows bildirim/odak ayarlarını kontrol edin.");
    }

    private void Save(Action<AppSettings> mutate)
    {
        try
        {
            AppSettings.Update(mutate);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus("Ayar kaydedilemedi: " + ex.Message);
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
