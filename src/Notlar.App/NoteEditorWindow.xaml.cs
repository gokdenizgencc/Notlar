using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Bir ya da birkaç günün notlarını Notepad++ tarzı tam ekran, sekmeli bir pencerede satır satır
/// düzenler. Her sekme bir <see cref="DayLineEditor"/>'dur (düzenlemeler kendiliğinden kaydedilir; Ctrl+S
/// hemen kaydeder). Pencere tekildir (<see cref="OpenDay"/> yeniden kullanır).</summary>
public partial class NoteEditorWindow : Window
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static NoteEditorWindow? _instance;

    private readonly NoteStore _store;
    private readonly List<DayLineEditor> _tabs = new();
    private AppSettings _settings;
    private double _fontSize;
    private DayLineEditor? _active;

    private NoteEditorWindow(NoteStore store)
    {
        _store = store;
        _settings = AppSettings.Load();
        _fontSize = _settings.EditorFontSize;
        InitializeComponent();
        AppSettings.Changed += OnSettingsChanged;
    }

    /// <summary>Tekil editör penceresini açar (yoksa oluşturur) ve verilen günü bir sekmede gösterir.
    /// <paramref name="noteId"/> verilirse imleç o notun satırının sonuna konur.</summary>
    public static void OpenDay(NoteStore store, DateOnly day, long? noteId = null)
    {
        if (_instance is null)
        {
            _instance = new NoteEditorWindow(store);
            _instance.Show();
        }

        var instance = _instance;
        instance.OpenOrActivate(day);
        if (noteId is { } id)
            instance._active?.MoveCaretToNote(id);

        if (instance.WindowState == WindowState.Minimized)
            instance.WindowState = WindowState.Maximized;
        instance.Activate();
        instance._active?.FocusEditor();
    }

    /// <summary>Not deposu dışarıdan değiştiğinde çağrılır; her sekme kendini tazeler (kaydedilmemiş
    /// düzenlemeler varsa önce birleştirerek kaydeder).</summary>
    public static void ReloadAll()
    {
        if (_instance is null) return;
        foreach (var tab in _instance._tabs)
            tab.Reload();
        _instance.RebuildTabStrip();
        _instance.UpdateStatusBar();
    }

    private void OpenOrActivate(DateOnly day)
    {
        var existing = _tabs.FirstOrDefault(t => t.Day == day);
        if (existing is not null)
        {
            ActivateTab(existing);
            return;
        }

        var editor = new DayLineEditor { FontSize = _fontSize, Visibility = Visibility.Collapsed };
        editor.StateChanged += () =>
        {
            if (editor == _active) UpdateStatusBar();
            RebuildTabStrip();
        };
        EditorHost.Children.Add(editor);
        editor.Load(_store, day);
        _tabs.Add(editor);
        ActivateTab(editor);
    }

    private void ActivateTab(DayLineEditor tab)
    {
        if (_active is not null && _active != tab)
        {
            _active.Flush();
            _active.Visibility = Visibility.Collapsed;
        }
        _active = tab;
        tab.Visibility = Visibility.Visible;
        Title = $"Notlar — Editör — {DayGrouper.FormatDate(tab.Day!.Value)}";
        tab.FocusEditor();
        UpdateStatusBar();
        RebuildTabStrip();
    }

    private void UpdateStatusBar()
    {
        if (_active is null)
        {
            StatusText.Text = "";
            DirtyText.Visibility = Visibility.Collapsed;
            return;
        }

        var (line, col) = _active.CaretPosition();
        StatusText.Text = $"length: {_active.Text.Length}   lines: {_active.LineCount}   Ln: {line}   Col: {col}   UTF-8" +
                          "      Satırın üstüne gel: saat · Ctrl+D: tamamlandı · Alt+Z: satır kaydırma";
        DirtyText.Text = "Kaydediliyor…";
        DirtyText.Visibility = _active.Dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildTabStrip()
    {
        TabStrip.Children.Clear();
        foreach (var tab in _tabs)
            TabStrip.Children.Add(BuildTabElement(tab));
    }

    private UIElement BuildTabElement(DayLineEditor tab)
    {
        var isActive = tab == _active;
        var border = new Border
        {
            Background = isActive ? (Brush)FindResource("Surface") : (Brush)FindResource("Bg"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1, 1, 1, 0),
            Margin = new Thickness(0, 0, 2, 0),
            Padding = new Thickness(10, 5, 6, 5),
            Cursor = Cursors.Hand,
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new TextBlock
        {
            Text = tab.Day!.Value.ToString("d MMMM yyyy", Tr) + (tab.Dirty ? " ●" : ""),
            Foreground = isActive ? (Brush)FindResource("Fg") : (Brush)FindResource("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var closeText = new TextBlock
        {
            Text = " ✕",
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = (Brush)FindResource("Muted"),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeText.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseTab(tab);
        };
        stack.Children.Add(closeText);

        border.Child = stack;
        border.MouseLeftButtonUp += (_, _) => ActivateTab(tab);
        return border;
    }

    private void AddTabButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDayPicker.SelectedDate = (_active?.Day ?? DateOnly.FromDateTime(DateTime.Now)).ToDateTime(TimeOnly.MinValue);
        DatePickerPopup.IsOpen = true;
    }

    private void OpenDayPicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenDayPicker.SelectedDate is not { } date) return;
        DatePickerPopup.IsOpen = false;
        OpenOrActivate(DateOnly.FromDateTime(date));
    }

    private void CloseTab(DayLineEditor tab)
    {
        if (!tab.Flush()) return;

        _tabs.Remove(tab);
        EditorHost.Children.Remove(tab);
        if (_active == tab)
        {
            _active = null;
            if (_tabs.FirstOrDefault() is { } next)
                ActivateTab(next);
        }
        RebuildTabStrip();

        if (_tabs.Count == 0)
            Close();
    }

    private void OnSettingsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _settings = AppSettings.Load();
            ApplyFontSize(_settings.EditorFontSize);
        });
    }

    private void ApplyFontSize(double size)
    {
        _fontSize = size;
        foreach (var tab in _tabs)
            tab.FontSize = size;
    }

    private void SetFontSize(double size)
    {
        var clamped = Math.Clamp(size, AppSettings.MinEditorFontSize, AppSettings.MaxEditorFontSize);
        if (Math.Abs(clamped - _fontSize) < 0.01) return;
        ApplyFontSize(clamped);

        try
        {
            _settings.EditorFontSize = clamped;
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        if (e.Key == Key.W)
        {
            if (_active is not null) CloseTab(_active);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && _tabs.Count > 1 && _active is not null)
        {
            var index = _tabs.IndexOf(_active);
            ActivateTab(_tabs[(index + 1) % _tabs.Count]);
            e.Handled = true;
        }
        else if (e.Key is Key.OemPlus or Key.Add)
        {
            SetFontSize(_fontSize + 1);
            e.Handled = true;
        }
        else if (e.Key is Key.OemMinus or Key.Subtract)
        {
            SetFontSize(_fontSize - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
        {
            SetFontSize(AppSettings.DefaultEditorFontSize);
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        SetFontSize(_fontSize + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        foreach (var tab in _tabs)
        {
            if (!tab.Flush())
            {
                e.Cancel = true;
                return;
            }
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        AppSettings.Changed -= OnSettingsChanged;
        _instance = null;
    }
}
