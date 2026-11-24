using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Veri ve yedekleme: ses saklama süresi, yedekleme (elle / otomatik / adet), geri yükleme,
/// ayarları dışa/içe aktarma ve sıfırlama, depolama kullanımı, log klasörü. Disk işleri arka planda yürür.</summary>
public partial class DataSettings : UserControl
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    /// <summary>Denetimlere değer yüklenirken true: olay işleyicileri kaydetmez.</summary>
    private bool _syncing;

    /// <summary>Yedek alma / geri yükleme sürerken true (düğmeler kilitli).</summary>
    private bool _busy;

    private bool _usageRunning;

    private sealed record BackupRow(BackupEntry Entry, string Display);

    public DataSettings()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusBar.Visibility = Visibility.Collapsed; };

        Loaded += (_, _) =>
        {
            AppSettings.Changed += OnSettingsChanged;
            Reload();
        };
        Unloaded += (_, _) => AppSettings.Changed -= OnSettingsChanged;
        // Panelde başka bölümden ⚙ Ayarlar'a dönülünce yedek listesi ve kullanım tazelensin.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && IsLoaded) Reload();
        };
    }

    public void Reload()
    {
        SyncSettings(AppSettings.Load());
        _ = RefreshBackupsAsync();
        _ = RefreshUsageAsync();
    }

    private void OnSettingsChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible) SyncSettings(AppSettings.Load());
    });

    private void SyncSettings(AppSettings settings)
    {
        _syncing = true;
        try
        {
            CheckByTag(RetentionPanel, AudioRetentionPolicy.Normalize(settings.AudioRetentionDays).ToString(CultureInfo.InvariantCulture));
            FreqOff.IsChecked = settings.AutoBackupFrequency == BackupFrequency.Off;
            FreqDaily.IsChecked = settings.AutoBackupFrequency == BackupFrequency.Daily;
            FreqWeekly.IsChecked = settings.AutoBackupFrequency == BackupFrequency.Weekly;
            var keep = DataBackup.NormalizeKeep(settings.BackupKeepCount).ToString(CultureInfo.InvariantCulture);
            if (!CheckByTag(KeepPanel, keep))
            {
                // Elle yazılmış, çiplerde olmayan bir sayı: hiçbir çip seçili görünmez ama değer korunur.
                foreach (var chip in KeepPanel.Children.OfType<RadioButton>()) chip.IsChecked = false;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private static bool CheckByTag(Panel panel, string tag)
    {
        var found = false;
        foreach (var chip in panel.Children.OfType<RadioButton>())
        {
            var match = chip.Tag as string == tag;
            chip.IsChecked = match;
            found |= match;
        }
        return found;
    }

    // ── Ayar çipleri ───────────────────────────────────────────────────────

    private void Retention_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var days)) return;
        SaveSetting(s => s.AudioRetentionDays = AudioRetentionPolicy.Normalize(days));
    }

    private void Frequency_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<BackupFrequency>(tag, out var freq)) return;
        SaveSetting(s => s.AutoBackupFrequency = freq);
        // Kapalıdan açığa geçince yedek hiç yoksa / zamanı geldiyse hemen alınsın (saatlik denetimi beklemesin).
        if (freq != BackupFrequency.Off) _ = RunInBackground(DataBackupService.RunAutomaticIfDue, refresh: true);
    }

    private void Keep_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var keep)) return;
        SaveSetting(s => s.BackupKeepCount = DataBackup.NormalizeKeep(keep));
    }

    // ── Yedekleme ──────────────────────────────────────────────────────────

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        BackupEntry? created = null;
        if (await RunInBackground(() => created = DataBackupService.CreateNow(), refresh: true) && created is not null)
            ShowStatus($"Yedek alındı: {Path.GetFileName(created.DbPath)}");
    }

    private void OpenBackupDir_Click(object sender, RoutedEventArgs e) => OpenInExplorer(AppPaths.BackupDir, null);

    private async Task RefreshBackupsAsync()
    {
        IReadOnlyList<BackupEntry> entries;
        try
        {
            entries = await Task.Run(DataBackupService.List);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus($"Yedekler listelenemedi: {ex.Message}", error: true);
            return;
        }

        var selected = (BackupList.SelectedItem as BackupRow)?.Entry.DbPath;
        var rows = entries.Select(entry => new BackupRow(entry, Describe(entry))).ToList();
        BackupList.ItemsSource = rows;
        BackupList.SelectedItem = rows.FirstOrDefault(r => r.Entry.DbPath == selected);
        BackupList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoBackupsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.IsEnabled = !_busy && BackupList.SelectedItem is not null;

        LastBackupText.Text = entries.Count == 0
            ? "Henüz yedek alınmadı."
            : $"Son yedek: {FormatTime(entries[0].CreatedAt)} · toplam {entries.Count} yedek";
    }

    private static string Describe(BackupEntry entry)
    {
        var text = new StringBuilder(FormatTime(entry.CreatedAt));
        try
        {
            text.Append(" · ").Append(StorageUsage.FormatMegabytes(new FileInfo(entry.DbPath).Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (entry.IsBeforeRestore) text.Append(" · geri yükleme öncesi");
        if (entry.SettingsPath is null) text.Append(" · ayarsız");
        return text.ToString();
    }

    /// <summary>Eski sürümün günlük yedeklerinde saat yoktur (00:00): yalnızca tarih gösterilir.</summary>
    private static string FormatTime(DateTime at) =>
        at.TimeOfDay == TimeSpan.Zero ? at.ToString("d MMMM yyyy", Tr) : at.ToString("d MMMM yyyy HH:mm", Tr);

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = !_busy && BackupList.SelectedItem is not null;

    /// <summary>Geri yükleme geri alınamaz: kullanıcıdan açık onay alınır; öncesinde mevcut durum yedeklenir;
    /// sonunda yeniden başlatma istenir (uygulama kendini yeniden başlatmaz).</summary>
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || BackupList.SelectedItem is not BackupRow row) return;

        if (RecordingController.Current?.IsRecording == true)
        {
            ShowStatus("Kayıt sürerken geri yükleme yapılamaz. Önce kaydı durdurun.", error: true);
            return;
        }

        var answer = MessageBox.Show(Window.GetWindow(this),
            $"\"{FormatTime(row.Entry.CreatedAt)}\" tarihli yedek geri yüklenecek.\n\n" +
            "Şu anki notlar, görevler, planlar, projeler ve kayıt metinleri bu yedektekilerle DEĞİŞTİRİLECEK" +
            (row.Entry.SettingsPath is null ? "." : "; ayarlar da yedektekilere döner.") +
            " Bu işlem geri alınamaz.\n\n" +
            "Güvenlik için önce mevcut durumun yedeği alınır (listede \"geri yükleme öncesi\" olarak görünür).\n\n" +
            "Devam edilsin mi?",
            "Yedekten geri yükle", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        BackupEntry? before = null;
        if (!await RunInBackground(() => before = DataBackupService.RestoreDatabase(row.Entry), refresh: true)) return;

        // Ayarlar UI thread'inde (Changed dinleyicileri için) geri yüklenir.
        var settingsRestored = DataBackupService.RestoreSettings(row.Entry);
        if (settingsRestored) ReloadSettingsPage();

        MessageBox.Show(Window.GetWindow(this),
            "Geri yükleme tamamlandı" + (settingsRestored ? " (veriler ve ayarlar)." : ".") + "\n\n" +
            "Değişikliklerin her yerde doğru görünmesi için Notlar'ı yeniden başlatın: tepsi simgesi → Çıkış, sonra " +
            "Notlar'ı yeniden açın. Açık Claude oturumları da yeniden başlatılmalıdır.\n\n" +
            $"Önceki durumun yedeği: {Path.GetFileName(before?.DbPath)}",
            "Notlar", MessageBoxButton.OK, MessageBoxImage.Information);
        ShowStatus("Geri yükleme tamamlandı. Notlar'ı yeniden başlatın.");
    }

    // ── Ayarları dışa / içe aktar, sıfırla ─────────────────────────────────

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Ayarları dışa aktar",
            Filter = "Notlar ayarları (*.json)|*.json",
            FileName = $"notlar-ayarlar-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, AppSettings.ExportJson(), new UTF8Encoding(false));
            ShowStatus($"Ayarlar dışa aktarıldı: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus($"Ayarlar dışa aktarılamadı: {ex.Message}", error: true);
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ayarları içe aktar",
            Filter = "Notlar ayarları (*.json)|*.json|Tüm dosyalar (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > SettingsTransfer.MaxImportChars * 4L)
                throw new FormatException("Dosya bir ayarlar dosyası olamayacak kadar büyük.");
            var result = AppSettings.ImportJson(File.ReadAllText(dialog.FileName));
            ReloadSettingsPage();
            if (result.Applied.Count == 0)
                ShowStatus("Dosyada uygulanabilir ayar bulunamadı; hiçbir şey değişmedi.", error: true);
            else
                ShowStatus($"{result.Applied.Count} ayar içe aktarıldı" +
                           (result.Ignored.Count > 0 ? $"; {result.Ignored.Count} bilinmeyen ya da geçersiz alan yok sayıldı." : "."));
        }
        catch (FormatException ex)
        {
            ShowStatus($"Ayarlar içe aktarılamadı: {ex.Message}", error: true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus($"Ayarlar içe aktarılamadı: {ex.Message}", error: true);
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Tüm ayarlar (görünüm, kısayollar, bildirimler, kayıt, yedekleme…) varsayılan değerlerine dönecek.\n\n" +
            "Notlara, görevlere ve kayıtlara dokunulmaz. Devam edilsin mi?",
            "Ayarları sıfırla", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            AppSettings.ResetToDefaults();
            ReloadSettingsPage();
            ShowStatus("Ayarlar varsayılana döndürüldü.");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus($"Ayarlar sıfırlanamadı: {ex.Message}", error: true);
        }
    }

    /// <summary>İçe aktarma / sıfırlama sonrası tüm ⚙ Ayarlar sayfası yeni değerleri göstersin.</summary>
    private void ReloadSettingsPage()
    {
        DependencyObject? node = this;
        while (node is not null and not SettingsView)
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        if (node is SettingsView view) view.Reload();
        Reload();
    }

    // ── Depolama ───────────────────────────────────────────────────────────

    private void UsageRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshUsageAsync();

    private async Task RefreshUsageAsync()
    {
        if (_usageRunning) return;
        _usageRunning = true;
        UsageRefreshButton.IsEnabled = false;
        try
        {
            var (notes, recordings, models, backups) = await Task.Run(() => (
                StorageUsage.DatabaseSize(AppPaths.DbPath),
                StorageUsage.SizeOf(AppPaths.RecordingsDir),
                StorageUsage.SizeOf(AppPaths.ModelsDir),
                StorageUsage.SizeOf(AppPaths.BackupDir)));
            UsageNotes.Text = StorageUsage.FormatMegabytes(notes);
            UsageRecordings.Text = StorageUsage.FormatMegabytes(recordings);
            UsageModels.Text = StorageUsage.FormatMegabytes(models);
            UsageBackups.Text = StorageUsage.FormatMegabytes(backups);
            UsageTotal.Text = StorageUsage.FormatMegabytes(notes + recordings + models + backups);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            UsageTotal.Text = "?";
        }
        finally
        {
            _usageRunning = false;
            UsageRefreshButton.IsEnabled = true;
        }
    }

    // ── Log ────────────────────────────────────────────────────────────────

    private void OpenLogDir_Click(object sender, RoutedEventArgs e) =>
        OpenInExplorer(Path.GetDirectoryName(AppPaths.LogPath)!, File.Exists(AppPaths.LogPath) ? AppPaths.LogPath : null);

    // ── Yardımcılar ────────────────────────────────────────────────────────

    /// <summary>İşi arka planda yürütür (düğmeler kilitli); başarılıysa true. Hata durum satırında gösterilir.</summary>
    private async Task<bool> RunInBackground(Action work, bool refresh)
    {
        if (_busy) return false;
        _busy = true;
        BackupNowButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        try
        {
            await Task.Run(work);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus(ex is InvalidDataException or FileNotFoundException ? ex.Message : $"İşlem başarısız: {ex.Message}",
                error: true);
            return false;
        }
        finally
        {
            _busy = false;
            BackupNowButton.IsEnabled = true;
            if (refresh)
            {
                await RefreshBackupsAsync();
                _ = RefreshUsageAsync();
            }
            else
            {
                RestoreButton.IsEnabled = BackupList.SelectedItem is not null;
            }
        }
    }

    private void SaveSetting(Action<AppSettings> mutate)
    {
        try
        {
            AppSettings.Update(mutate);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus($"Ayar kaydedilemedi: {ex.Message}", error: true);
        }
    }

    /// <summary>Klasörü Gezgin'de açar; <paramref name="selectFile"/> verilirse o dosya seçili gelir.</summary>
    private void OpenInExplorer(string dir, string? selectFile)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo { FileName = explorer, UseShellExecute = false };
            if (selectFile is null)
            {
                psi.ArgumentList.Add(dir);
            }
            else
            {
                // explorer "/select,<yol>" biçimini tek argüman olarak bekler.
                psi.Arguments = $"/select,\"{selectFile}\"";
            }
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowStatus($"Klasör açılamadı: {ex.Message}", error: true);
        }
    }

    private void ShowStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerText" : "Fg");
        StatusBar.SetResourceReference(Border.BorderBrushProperty, error ? "DangerText" : "Accent");
        StatusBar.Visibility = Visibility.Visible;
        StatusBar.BringIntoView();
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
