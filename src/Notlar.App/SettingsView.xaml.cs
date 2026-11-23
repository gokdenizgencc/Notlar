using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Notlar.Core;
using Notlar.Kayit;

namespace Notlar.App;

/// <summary>Panel içindeki ⚙ Ayarlar görünümü: Görünüm, Genel, Kayıt, Gizlilik, Hakkında. Her değişiklik anında
/// kaydedilir (görünüm kaydırıcıları/renk kodları kısa bir gecikmeyle, yazarken diske boğulmasın diye).
/// Ayarlar her kayıtta diskten taze okunup yazılır (<see cref="AppSettings.Update"/>), böylece panelin kendi
/// kaydettiği alanlar (konum, boyut) eski değerle ezilmez; <see cref="AppSettings.Changed"/> olayı korunur.</summary>
public partial class SettingsView : UserControl
{
    private readonly DispatcherTimer _appearanceSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private AppSettings? _settings;
    private AppFlags? _flags;

    /// <summary>Denetimlere değer yüklenirken true: olay işleyicileri kaydetmez.</summary>
    private bool _syncing;

    /// <summary>İsteğe bağlı: panel bağlarsa "Paneli sabitle" panelin 📌 düğmesiyle aynı yoldan değişir (düğme
    /// görünümü de güncellenir). Bağlı değilse ayar doğrudan <see cref="AppSettings.PanelPinned"/>'e yazılır.</summary>
    public event Action? PinToggleRequested;

    /// <summary>İsteğe bağlı: panel bağlarsa "Büyük mod" anahtarı görünür ve panelin ⤢ düğmesiyle aynı işi yapar.
    /// Pencere boyutu panelin işidir; bağlı değilse anahtar gizlenir (ayarı doğrudan yazmak pencereyle çelişirdi).</summary>
    public event Action? LargeModeToggleRequested;

    public SettingsView()
    {
        InitializeComponent();
        // Sınırları ayarlamak değeri zorlayıp ValueChanged tetikler; bu bir kullanıcı değişikliği değil.
        _syncing = true;
        EditorFontSlider.Minimum = AppSettings.MinEditorFontSize;
        EditorFontSlider.Maximum = AppSettings.MaxEditorFontSize;
        EditorFontSlider.Value = AppSettings.DefaultEditorFontSize;
        _syncing = false;

        Appearance.Edited += ScheduleAppearanceSave;
        _appearanceSaveTimer.Tick += (_, _) => SaveAppearanceNow();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusBar.Visibility = Visibility.Collapsed; };

        // Panelin 📌/⤢ düğmeleri ya da başka pencereler ayarı değiştirirse anahtarlar güncel kalsın.
        Loaded += (_, _) => AppSettings.Changed += OnSettingsChanged;
        Unloaded += (_, _) => AppSettings.Changed -= OnSettingsChanged;

        VersionText.Text = "Notlar " + GetVersion();
        DataDirText.Text = AppPaths.DataDir;
    }

    /// <summary>Bir kez, panel kurulurken çağrılır.</summary>
    public void Initialize(AppSettings settings, AppFlags flags)
    {
        _settings = settings;
        _flags = flags;
        Reload();
    }

    /// <summary>Görünüm her açıldığında / veri değiştiğinde güncel değerleri yükler.</summary>
    public void Reload()
    {
        // Bekleyen görünüm kaydı önce yazılır; yoksa aşağıdaki yükleme kullanıcının son değişikliğini silerdi.
        if (_appearanceSaveTimer.IsEnabled) SaveAppearanceNow();

        var settings = AppSettings.Load();
        _syncing = true;
        try
        {
            // Kullanıcı bir renk kodu yazarken (ör. veri değişikliği sinyaliyle gelen Reload) satırları yeniden kurma.
            if (!(Keyboard.FocusedElement is TextBox focused && Appearance.IsAncestorOf(focused)))
                Appearance.Load(settings);
            EditorFontSlider.Value = settings.EditorFontSize;
            UpdateEditorFontText();

            SyncPanelToggles(settings);
            SyncLanguage(settings);
            SyncAutoRecord(settings);
            SyncAutoStart();
            SyncKisiselOkuma();
            SyncBackend();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSettingsChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) return;
            var settings = AppSettings.Load();
            _syncing = true;
            try
            {
                SyncPanelToggles(settings);
                SyncLanguage(settings);
                SyncAutoRecord(settings);
            }
            finally
            {
                _syncing = false;
            }
        });
    }

    // ── Görünüm ────────────────────────────────────────────────────────────

    private void ScheduleAppearanceSave()
    {
        _appearanceSaveTimer.Stop();
        _appearanceSaveTimer.Start();
    }

    /// <summary>Gün penceresi yazı boyutu, yazar renkleri ve editör yazı boyutunu kaydeder. Geçersiz renk kodu
    /// kaydedilmez (kutunun kenarı kırmızı kalır); diğer değerler yine de kaydedilir.</summary>
    private void SaveAppearanceNow()
    {
        _appearanceSaveTimer.Stop();
        var editorFontSize = Math.Round(EditorFontSlider.Value);
        SaveSetting(s =>
        {
            Appearance.ApplyTo(s);
            s.EditorFontSize = editorFontSize;
        });
    }

    private void EditorFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateEditorFontText();
        if (!_syncing && IsInitialized) ScheduleAppearanceSave();
    }

    private void UpdateEditorFontText()
    {
        if (EditorFontValueText is null) return;
        EditorFontValueText.Text = Math.Round(EditorFontSlider.Value).ToString("0");
    }

    private void AppearanceReset_Click(object sender, RoutedEventArgs e)
    {
        Appearance.ResetToDefaults();
        EditorFontSlider.Value = AppSettings.DefaultEditorFontSize;
        SaveAppearanceNow();
    }

    // ── Genel ──────────────────────────────────────────────────────────────

    private void SyncAutoStart()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            AutoStartCheck.IsChecked = AutoStart.IsEnabled();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    /// <summary>Tepsi menüsündeki "Windows ile başlat" ile aynı kayıt defteri değerini değiştirir; tepsi kutucuğu
    /// menü her açıldığında bu değeri okuduğundan ikisi tutarlı kalır.</summary>
    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        try
        {
            AutoStart.SetEnabled(AutoStartCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError($"\"Windows ile başlat\" değiştirilemedi: {ex.Message}");
        }
        SyncAutoStart();
    }

    private void SyncPanelToggles(AppSettings settings)
    {
        PinnedCheck.IsChecked = settings.PanelPinned;
        LargeModeCheck.IsChecked = settings.PanelLarge;
        LargeModeRow.Visibility = LargeModeToggleRequested is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PinnedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var pinned = PinnedCheck.IsChecked == true;
        if (PinToggleRequested is { } toggle)
        {
            if (AppSettings.Load().PanelPinned != pinned) Run(toggle);
        }
        else
        {
            SaveSetting(s => s.PanelPinned = pinned);
        }
        ResyncPanelToggles();
    }

    private void LargeModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (LargeModeToggleRequested is { } toggle && AppSettings.Load().PanelLarge != (LargeModeCheck.IsChecked == true))
            Run(toggle);
        ResyncPanelToggles();
    }

    private void ResyncPanelToggles()
    {
        _syncing = true;
        try
        {
            SyncPanelToggles(AppSettings.Load());
        }
        finally
        {
            _syncing = false;
        }
    }

    // ── Kayıt ──────────────────────────────────────────────────────────────

    private void SyncLanguage(AppSettings settings)
    {
        string code;
        try
        {
            code = RecordingStore.ValidateLanguage(settings.RecordingLanguage);
        }
        catch (ArgumentException)
        {
            code = "tr";
        }
        LangTr.IsChecked = code == "tr";
        LangEn.IsChecked = code == "en";
        LangAuto.IsChecked = code == "auto";
    }

    /// <summary>Yalnızca yeni kayıtların dilini değiştirir (RecordingController kayıt başlarken okur).</summary>
    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string code }) return;
        SaveSetting(s => s.RecordingLanguage = code);
    }

    private void SyncAutoRecord(AppSettings settings)
    {
        AutoRecordCheck.IsChecked = settings.AutoRecordEnabled;
        // Kullanıcı saat yazarken (başka bir ayar kaydedilince gelen eşitleme) kutunun metni ezilmez.
        if (!AutoRecordStartBox.IsKeyboardFocusWithin) AutoRecordStartBox.Text = settings.AutoRecordStart;
        if (!AutoRecordEndBox.IsKeyboardFocusWithin) AutoRecordEndBox.Text = settings.AutoRecordEnd;
    }

    private void AutoRecordCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var enabled = AutoRecordCheck.IsChecked == true;
        SaveSetting(s => s.AutoRecordEnabled = enabled);
    }

    private void AutoRecordTime_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        e.Handled = true;
        CommitAutoRecordTime(box);
    }

    private void AutoRecordTime_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitAutoRecordTime(box);
    }

    /// <summary>Saat kutusunu doğrulayıp kaydeder; geçersizse eski değere döner. Saat değişince "bugün başlatıldı"
    /// işareti silinir (yeni pencere bugün de geçerli olsun).</summary>
    private void CommitAutoRecordTime(TextBox box)
    {
        if (_syncing) return;
        var isStart = ReferenceEquals(box, AutoRecordStartBox);
        var current = AppSettings.Load();
        var previous = isStart ? current.AutoRecordStart : current.AutoRecordEnd;

        if (!RecordingSchedule.TryParseTime(box.Text, out var time))
        {
            box.Text = previous;
            ShowError("Saat SS:dd biçiminde olmalı (ör. 09:30).");
            return;
        }

        var text = RecordingSchedule.FormatTime(time);
        box.Text = text;
        if (text == previous) return;

        SaveSetting(s =>
        {
            if (isStart) s.AutoRecordStart = text;
            else s.AutoRecordEnd = text;
            s.AutoRecordLastDate = null;
        });

        var saved = AppSettings.Load();
        if (RecordingSchedule.TryParseTime(saved.AutoRecordStart, out var start)
            && RecordingSchedule.TryParseTime(saved.AutoRecordEnd, out var end) && end <= start)
            ShowError("Bitiş saati başlangıçtan sonra olmalı; bu hâliyle otomatik kayıt başlamaz.");
    }

    private void SyncBackend()
    {
        var backend = WhisperRuntime.LastBackend;
        BackendText.Text = backend switch
        {
            null => "Çeviri donanımı: bu oturumda henüz çeviri yapılmadı.",
            "CPU" => "Çeviri donanımı: CPU",
            _ => $"Çeviri donanımı: GPU ({backend})",
        };

        var gpuOff = WhisperRuntime.GpuDisabledReason;
        GpuOffText.Text = gpuOff is null ? "" : $"GPU kapalı: {gpuOff}";
        GpuOffText.Visibility = gpuOff is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenRecordingsDir_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.RecordingsDir);

    // ── Gizlilik ───────────────────────────────────────────────────────────

    private void SyncKisiselOkuma()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            KisiselOkumaCheck.IsEnabled = _flags is not null;
            KisiselOkumaCheck.IsChecked = _flags?.GetBool(AppFlags.KisiselMcpOkuma) ?? false;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            KisiselOkumaCheck.IsEnabled = false;
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    /// <summary>Kişisel bölümdeki "Claude okuyabilsin" kutusuyla aynı anahtar: AppFlags.KisiselMcpOkuma. SetBool
    /// değişiklik sinyalini gönderir; MCP sunucusu her çağrıda güncel değeri okur, panel de kendi kutusunu tazeler.</summary>
    private void KisiselOkumaCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _flags is null) return;
        try
        {
            _flags.SetBool(AppFlags.KisiselMcpOkuma, KisiselOkumaCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError($"Ayar kaydedilemedi: {ex.Message}");
            SyncKisiselOkuma();
        }
    }

    // ── Hakkında ───────────────────────────────────────────────────────────

    private void OpenDataDir_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDir);

    /// <summary>"1.0.0" ya da derleme commit'i biliniyorsa "1.0.0 (f110eb8)".</summary>
    private static string GetVersion()
    {
        var assembly = typeof(SettingsView).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return assembly.GetName().Version?.ToString(3) ?? "?";

        var plus = info.IndexOf('+');
        if (plus < 0) return info;
        var hash = info[(plus + 1)..];
        return $"{info[..plus]} ({(hash.Length > 7 ? hash[..7] : hash)})";
    }

    // ── Yardımcılar ────────────────────────────────────────────────────────

    /// <summary>Diskteki güncel ayarlara uygular ve kaydeder; Initialize'da verilen örneği de aynı değerle günceller.</summary>
    private void SaveSetting(Action<AppSettings> mutate)
    {
        try
        {
            AppSettings.Update(mutate);
            if (_settings is not null) mutate(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError($"Ayar kaydedilemedi: {ex.Message}");
        }
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError(ex.Message);
        }
    }

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo { FileName = explorer, UseShellExecute = false };
            psi.ArgumentList.Add(dir);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError($"Klasör açılamadı: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusBar.Visibility = Visibility.Visible;
        StatusBar.BringIntoView();
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
