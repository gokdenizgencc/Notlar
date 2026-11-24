using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Otomatik kaydın günleri: Pazartesi'den Pazar'a yedi çip. Varsayılan Pzt–Cum (bugünkü davranış). Her tıklama
/// diskteki ayarlara yazılır; ayar başka yerden değişince ya da görünür olunca yeniden eşitlenir.</summary>
public partial class AutoRecordDaysPicker : UserControl
{
    private readonly Dictionary<DayOfWeek, ToggleButton> _chips = [];
    private bool _syncing;

    /// <summary>Kaydetme hatası (Türkçe mesaj); SettingsView durum çubuğunda gösterir.</summary>
    public event Action<string>? Error;

    public AutoRecordDaysPicker()
    {
        InitializeComponent();
        foreach (var day in RecordingSchedule.WeekOrder)
        {
            var chip = new ToggleButton
            {
                Content = RecordingSchedule.ShortName(day),
                Tag = day,
                Style = (Style)Resources["DayChip"],
            };
            chip.Checked += Chip_Changed;
            chip.Unchecked += Chip_Changed;
            _chips[day] = chip;
            DaysPanel.Children.Add(chip);
        }

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

    /// <summary>Seçili günler (Pazartesi'den başlayan sırayla).</summary>
    public IReadOnlyList<DayOfWeek> SelectedDays =>
        RecordingSchedule.WeekOrder.Where(d => _chips[d].IsChecked == true).ToList();

    private void OnSettingsChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible) Sync();
    });

    private void Sync()
    {
        IReadOnlyCollection<DayOfWeek> days;
        try
        {
            days = AppSettings.Load().AutoRecordDays ?? [.. RecordingSchedule.DefaultDays];
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return;
        }

        _syncing = true;
        try
        {
            foreach (var (day, chip) in _chips)
                chip.IsChecked = days.Contains(day);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Gün değişince "bugün başlatıldı" işareti korunur: aynı gün ikinci kez otomatik kayıt başlamaz.</summary>
    private void Chip_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var days = SelectedDays.ToList();
        try
        {
            AppSettings.Update(s => s.AutoRecordDays = days);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            Error?.Invoke($"Ayar kaydedilemedi: {ex.Message}");
            Sync();
        }
    }
}
