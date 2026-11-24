using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Notlar.Core;
using Notlar.Kayit;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Kayıt → Ses ve metne çevirme. Her değişiklik diskteki güncel ayarlara anında yazılır
/// (<see cref="AppSettings.Update"/>); kayıt aygıtları bir sonraki kayıtta, işlemci ve kişi ayırma bir sonraki çeviride
/// geçerli olur (RecordingController <see cref="AppSettings.Changed"/>'i dinler).
/// <para>Mikrofon testi yalnızca kullanıcı "Test et"e basınca açılır; denetim görünmez olunca (ayarlardan çıkıldı,
/// panel gizlendi) ya da kayıt başlayınca kendiliğinden durur. Kayıt sürerken test devre dışıdır.</para></summary>
public partial class AudioSettings : UserControl
{
    /// <summary>Açılır listedeki "Varsayılan" satırının kimliği.</summary>
    private const string DefaultId = "";

    private readonly MicrophoneLevelMonitor _monitor = new();
    private RecordingController? _controller;
    private double _level;

    /// <summary>Denetimlere değer yüklenirken true: olay işleyicileri kaydetmez.</summary>
    private bool _syncing;

    private sealed record DeviceItem(string Id, string Name);

    public AudioSettings()
    {
        InitializeComponent();
        _monitor.LevelChanged += OnLevel;
        _monitor.Error += ex => Dispatcher.InvokeAsync(() =>
        {
            AppLog.Write(ex);
            StopMicTest("Mikrofon testi durdu: " + ex.Message);
        });

        Loaded += (_, _) =>
        {
            AppSettings.Changed += OnSettingsChanged;
            AttachController();
            LoadDevices();
            Sync();
        };
        Unloaded += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            DetachController();
            StopMicTest();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AttachController();
                Sync();
            }
            else
            {
                StopMicTest();
            }
        };
    }

    // ── Eşitleme ──────────────────────────────────────────────────────────

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
            SelectDevice(MicrophoneCombo, settings.MicrophoneDeviceId);
            SelectDevice(OutputCombo, settings.LoopbackDeviceId);

            var processor = settings.Processor;
            ProcessorAuto.IsChecked = processor == TranscriptionProcessor.Auto;
            ProcessorGpu.IsChecked = processor == TranscriptionProcessor.Gpu;
            ProcessorCpu.IsChecked = processor == TranscriptionProcessor.Cpu;
            UpdateProcessorHint(processor);

            SpeakerLabelingCheck.IsChecked = settings.SpeakerLabelingEnabled;
            AutoTranscribeCheck.IsChecked = settings.AutoTranscribeAfterRecording;
        }
        finally
        {
            _syncing = false;
        }
        UpdateMicTestState();
    }

    // ── Aygıtlar ──────────────────────────────────────────────────────────

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        LoadDevices();
        Sync();
    }

    /// <summary>Etkin aygıtları listeler; ilk satır her zaman Windows'un varsayılanıdır.</summary>
    private void LoadDevices()
    {
        IReadOnlyList<AudioDeviceInfo> microphones = [];
        IReadOnlyList<AudioDeviceInfo> outputs = [];
        try
        {
            microphones = AudioDevices.Microphones();
            outputs = AudioDevices.Outputs();
            HideError();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowError($"Ses aygıtları listelenemedi: {ex.Message}");
        }

        _syncing = true;
        try
        {
            MicrophoneCombo.ItemsSource = BuildItems("Varsayılan mikrofon (Windows)", microphones);
            OutputCombo.ItemsSource = BuildItems("Varsayılan çıkış aygıtı (Windows)", outputs);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static List<DeviceItem> BuildItems(string defaultName, IReadOnlyList<AudioDeviceInfo> devices)
    {
        var items = new List<DeviceItem> { new(DefaultId, defaultName) };
        items.AddRange(devices.Select(d => new DeviceItem(d.Id, d.Name)));
        return items;
    }

    /// <summary>Kayıtlı aygıtı seçer. Listede yoksa (çıkarılmış) seçim kaybolmasın diye "bağlı değil" satırı eklenir;
    /// kayıt o aygıt bağlanana kadar varsayılanla yapılır.</summary>
    private static void SelectDevice(ComboBox combo, string? savedId)
    {
        if (combo.ItemsSource is not List<DeviceItem> items) return;
        var id = string.IsNullOrWhiteSpace(savedId) ? DefaultId : savedId;
        var match = items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            match = new DeviceItem(id, "Seçili aygıt bağlı değil — varsayılan kullanılıyor");
            items.Add(match);
            combo.Items.Refresh();
        }
        combo.SelectedItem = match;
    }

    private void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || MicrophoneCombo.SelectedItem is not DeviceItem item) return;
        var id = item.Id.Length == 0 ? null : item.Id;
        Save(s => s.MicrophoneDeviceId = id);
        if (_monitor.IsRunning) StartMicTest();   // test yeni mikrofonla sürsün
    }

    private void OutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || OutputCombo.SelectedItem is not DeviceItem item) return;
        var id = item.Id.Length == 0 ? null : item.Id;
        Save(s => s.LoopbackDeviceId = id);
    }

    // ── Mikrofon testi ────────────────────────────────────────────────────

    private void AttachController()
    {
        var current = RecordingController.Current;
        if (ReferenceEquals(current, _controller)) return;
        DetachController();
        _controller = current;
        if (_controller is not null) _controller.RecordingStateChanged += OnRecordingStateChanged;
    }

    private void DetachController()
    {
        if (_controller is not null) _controller.RecordingStateChanged -= OnRecordingStateChanged;
        _controller = null;
    }

    /// <summary>Kayıt başlarsa test durur (mikrofon kayıtta); bitince düğme yeniden açılır.</summary>
    private void OnRecordingStateChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (_controller?.IsRecording == true) StopMicTest();
        UpdateMicTestState();
    });

    private bool IsRecording => (_controller ?? RecordingController.Current)?.IsRecording == true;

    private void MicTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor.IsRunning) StopMicTest();
        else StartMicTest();
    }

    private void StartMicTest()
    {
        if (IsRecording)
        {
            UpdateMicTestState();
            return;
        }
        try
        {
            var id = AppSettings.Load().MicrophoneDeviceId;
            _monitor.Start(id, out var fallbackNote);
            if (fallbackNote is not null) AppLog.Info("Mikrofon testi: " + fallbackNote);
            MicTestButton.Content = "⏹ Durdur";
            MicTestHint.Text = fallbackNote ?? "Konuşun; çubuk sesinizle birlikte hareket etmeli. Hiçbir şey kaydedilmez.";
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            StopMicTest(ex.Message);
        }
    }

    /// <summary>Testi durdurur; <paramref name="message"/> verilirse açıklama satırında gösterilir.</summary>
    private void StopMicTest(string? message = null)
    {
        try
        {
            _monitor.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        _level = 0;
        LevelFill.Width = 0;
        MicTestButton.Content = "🎤 Test et";
        UpdateMicTestState(message);
    }

    private void UpdateMicTestState(string? message = null)
    {
        var recording = IsRecording;
        MicTestButton.IsEnabled = !recording;
        if (recording)
            MicTestHint.Text = "Kayıt sürerken mikrofon testi kapalı (mikrofon kayıtta). Kayıt bitince yeniden deneyebilirsiniz.";
        else if (message is not null)
            MicTestHint.Text = message;
        else if (!_monitor.IsRunning)
            MicTestHint.Text = "Seçili mikrofonu dinler; hiçbir şey kaydedilmez. Ayarlardan çıkınca kendiliğinden durur.";
    }

    /// <summary>Ses thread'inden gelir; UI'ye aktarılıp yumuşatılarak çubuğa yansıtılır.</summary>
    private void OnLevel(float peak)
    {
        var fraction = AudioPreferences.MeterFraction(peak);
        Dispatcher.InvokeAsync(() =>
        {
            if (!_monitor.IsRunning) return;
            _level = AudioPreferences.Smooth(_level, fraction);
            if (LevelFill.Parent is FrameworkElement track)
                LevelFill.Width = Math.Max(0, (track.ActualWidth - 2) * _level);
        });
    }

    // ── İşlemci ve çeviri seçenekleri ─────────────────────────────────────

    private void Processor_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var processor = ReferenceEquals(sender, ProcessorGpu) ? TranscriptionProcessor.Gpu
            : ReferenceEquals(sender, ProcessorCpu) ? TranscriptionProcessor.Cpu
            : TranscriptionProcessor.Auto;
        Save(s => s.TranscriptionHardware = AudioPreferences.FormatProcessor(processor));
        UpdateProcessorHint(processor);
    }

    private void UpdateProcessorHint(TranscriptionProcessor processor)
    {
        var text = processor switch
        {
            TranscriptionProcessor.Gpu => "GPU tercih edilir; ayrık ekran kartı yoksa tümleşik GPU denenir.",
            TranscriptionProcessor.Cpu => "Ekran kartı kullanılmaz; çeviri daha uzun sürer.",
            _ => "Ayrık ekran kartı varsa GPU (çok daha hızlı), yoksa CPU kullanılır.",
        };
        var off = WhisperRuntime.GpuDisabledReason;
        if (off is not null && processor != TranscriptionProcessor.Cpu)
            text += $" Şu an GPU kapalı: {off}.";
        ProcessorHint.Text = text + " Bir sonraki çeviride geçerli olur; GPU hata verirse CPU ile devam edilir.";
    }

    private void SpeakerLabelingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var enabled = SpeakerLabelingCheck.IsChecked == true;
        Save(s => s.SpeakerLabelingEnabled = enabled);
    }

    private void AutoTranscribeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var enabled = AutoTranscribeCheck.IsChecked == true;
        Save(s => s.AutoTranscribeAfterRecording = enabled);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────

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
