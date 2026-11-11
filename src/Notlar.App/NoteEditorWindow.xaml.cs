using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Bir günün notlarını Notepad++ tarzı tam ekran, satır satır düzenlenebilen bir pencerede
/// gösterir. Her açık gün bir sekmedir; pencere tekildir (<see cref="OpenDay"/> yeniden kullanır).</summary>
public partial class NoteEditorWindow : Window
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static NoteEditorWindow? _instance;

    private readonly NoteStore _store;
    private readonly List<EditorTab> _tabs = new();
    private AppSettings _settings;
    private double _fontSize;
    private EditorTab? _active;
    private bool _suppressTextChanged;

    private NoteEditorWindow(NoteStore store)
    {
        _store = store;
        _settings = AppSettings.Load();
        _fontSize = _settings.EditorFontSize;
        InitializeComponent();
        EditorBox.FontSize = _fontSize;
        GutterText.FontSize = _fontSize;
        AppSettings.Changed += OnSettingsChanged;
    }

    /// <summary>Tekil editör penceresini açar (yoksa oluşturur) ve verilen günü bir sekmede gösterir.
    /// Gün zaten açıksa o sekmeyi etkinleştirir.</summary>
    public static void OpenDay(NoteStore store, DateOnly day)
    {
        if (_instance is null)
        {
            _instance = new NoteEditorWindow(store);
            _instance.Show();
        }

        _instance.OpenOrActivate(day);

        if (_instance.WindowState == WindowState.Minimized)
            _instance.WindowState = WindowState.Maximized;
        _instance.Activate();
    }

    /// <summary>Not deposu dışarıdan (başka bir süreçten veya bu süreçte başka bir pencereden)
    /// değiştiğinde çağrılır. Açık, kirli olmayan sekmeleri sessizce tazeler; kirli sekmeler için
    /// "dışarıda değişti" çubuğunu gösterir.</summary>
    public static void ReloadAll() => _instance?.ReloadAllTabsInternal();

    private void OpenOrActivate(DateOnly day)
    {
        var existing = _tabs.FirstOrDefault(t => t.Day == day);
        if (existing is not null)
        {
            ActivateTab(existing);
            return;
        }

        var tab = new EditorTab { Day = day };
        LoadTab(tab);
        _tabs.Add(tab);
        ActivateTab(tab);
        RebuildTabStrip();
    }

    private void ReloadAllTabsInternal()
    {
        foreach (var tab in _tabs)
        {
            if (tab.Dirty)
            {
                tab.ExternalChangeNotice = true;
                continue;
            }

            LoadTab(tab);
            if (tab == _active)
                RefreshActiveFromTab();
        }

        UpdateExternalChangeBar();
        RebuildTabStrip();
    }

    /// <summary>Verilen sekmeyi depodaki güncel notlarla (yeniden) doldurur; var olan düzenlemeleri atar.
    /// Yalnızca ilk açılışta, kayıttan sonra veya "yeniden yükle" ile çağrılmalıdır.</summary>
    private void LoadTab(EditorTab tab)
    {
        var notes = _store.List(new NoteFilter(From: tab.Day, To: tab.Day));
        var ordered = DayGrouper.Group(notes).FirstOrDefault(g => g.Date == tab.Day)?.Notes ?? Array.Empty<Note>();
        tab.Notes = ordered.ToList();
        tab.OriginalText = string.Join("\n", tab.Notes.Select(n => n.Text));
        tab.Text = tab.OriginalText;
        tab.ExternalChangeNotice = false;
    }

    private void ActivateTab(EditorTab tab)
    {
        _active = tab;
        Title = $"Notlar — Editör — {DayGrouper.FormatDate(tab.Day)}";
        RefreshActiveFromTab();
        UpdateExternalChangeBar();
        RebuildTabStrip();
    }

    private void RefreshActiveFromTab()
    {
        if (_active is null) return;

        _suppressTextChanged = true;
        EditorBox.Text = _active.Text;
        EditorBox.CaretIndex = 0;
        _suppressTextChanged = false;

        RebuildGutter();
        UpdateStatusBar();
    }

    private void RebuildGutter()
    {
        if (_active is null)
        {
            GutterText.Text = "";
            return;
        }

        var oldLines = SplitLines(_active.OriginalText);
        var newLines = SplitLines(_active.Text);
        var ops = LineDiff.Map(oldLines, newLines);

        var timeByNewIndex = new Dictionary<int, string>();
        foreach (var op in ops)
        {
            if ((op.Kind == LineOpKind.Keep || op.Kind == LineOpKind.Update) && op.OldIndex >= 0 && op.OldIndex < _active.Notes.Count)
                timeByNewIndex[op.NewIndex] = _active.Notes[op.OldIndex].CreatedAt.ToString("HH:mm", Tr);
        }

        var width = Math.Max(2, newLines.Length.ToString(Tr).Length);
        var sb = new StringBuilder();
        for (var i = 0; i < newLines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append((i + 1).ToString(Tr).PadLeft(width));
            if (timeByNewIndex.TryGetValue(i, out var t))
                sb.Append("  ").Append(t);
        }

        GutterText.Text = sb.ToString();
    }

    private void UpdateStatusBar()
    {
        if (_active is null)
        {
            StatusText.Text = "";
            DirtyText.Visibility = Visibility.Collapsed;
            return;
        }

        var text = EditorBox.Text;
        var lineCount = SplitLines(text).Length;
        var line = EditorBox.GetLineIndexFromCharacterIndex(EditorBox.CaretIndex);
        var lineStart = EditorBox.GetCharacterIndexFromLineIndex(line);
        var col = EditorBox.CaretIndex - lineStart + 1;

        StatusText.Text = $"length: {text.Length}   lines: {lineCount}   Ln: {line + 1}   Col: {col}   UTF-8";
        DirtyText.Visibility = _active.Dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateExternalChangeBar()
    {
        ExternalChangeBar.Visibility = _active is { ExternalChangeNotice: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildTabStrip()
    {
        TabStrip.Children.Clear();
        foreach (var tab in _tabs)
            TabStrip.Children.Add(BuildTabElement(tab));
    }

    private UIElement BuildTabElement(EditorTab tab)
    {
        var isActive = tab == _active;
        var border = new Border
        {
            Background = isActive ? (Brush)FindResource("Surface") : (Brush)FindResource("Bg"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Margin = new Thickness(0, 0, 2, 0),
            Padding = new Thickness(10, 5, 6, 5),
            Cursor = Cursors.Hand,
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var label = tab.Day.ToString("d MMMM yyyy", Tr) + (tab.Dirty ? " ●" : "");
        stack.Children.Add(new TextBlock
        {
            Text = label,
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

    private void ReloadBar_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        LoadTab(_active);
        RefreshActiveFromTab();
        UpdateExternalChangeBar();
        RebuildTabStrip();
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || _active is null) return;

        _active.Text = EditorBox.Text;
        RebuildGutter();
        UpdateStatusBar();
        RebuildTabStrip();
    }

    private void EditorBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextChanged) return;
        UpdateStatusBar();
    }

    private void EditorBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        GutterScroll.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void CloseTab(EditorTab tab)
    {
        if (!ConfirmAndResolve(tab)) return;

        _tabs.Remove(tab);
        if (_active == tab)
        {
            _active = _tabs.FirstOrDefault();
            if (_active is not null)
                ActivateTab(_active);
        }
        RebuildTabStrip();

        if (_tabs.Count == 0)
            Close();
    }

    /// <summary>Sekme kirliyse kullanıcıya sorar. Evet: kaydeder; Hayır: değişiklikleri atar;
    /// İptal: false döner (çağıran işlemi iptal etmelidir).</summary>
    private bool ConfirmAndResolve(EditorTab tab)
    {
        if (!tab.Dirty) return true;

        var result = MessageBox.Show(
            this,
            $"{tab.Day.ToString("d MMMM yyyy", Tr)} günü için kaydedilmemiş değişiklikler var. Kaydedilsin mi?",
            "Notlar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) SaveTab(tab);
        return true;
    }

    private void SaveTab(EditorTab tab)
    {
        try
        {
            var oldLines = SplitLines(tab.OriginalText);
            var newLines = SplitLines(tab.Text);
            var ops = LineDiff.Map(oldLines, newLines);

            for (var k = 0; k < ops.Count; k++)
            {
                var op = ops[k];
                switch (op.Kind)
                {
                    case LineOpKind.Update:
                        _store.Update(tab.Notes[op.OldIndex].Id, newLines[op.NewIndex]);
                        break;
                    case LineOpKind.Delete:
                        _store.Delete(tab.Notes[op.OldIndex].Id);
                        break;
                    case LineOpKind.Insert:
                        var prev = FindNeighborNote(ops, tab, k, backwards: true);
                        var next = FindNeighborNote(ops, tab, k, backwards: false);
                        var time = ComputeInsertTime(prev, next, tab.Day);
                        _store.AddAt(newLines[op.NewIndex], time, NoteAuthor.Ben);
                        break;
                }
            }

            LoadTab(tab);
            if (tab == _active)
                RefreshActiveFromTab();
            RebuildTabStrip();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(this, $"Kaydetme başarısız: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Note? FindNeighborNote(IReadOnlyList<LineOp> ops, EditorTab tab, int index, bool backwards)
    {
        if (backwards)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (ops[i].Kind is LineOpKind.Keep or LineOpKind.Update)
                    return tab.Notes[ops[i].OldIndex];
            }
        }
        else
        {
            for (var i = index + 1; i < ops.Count; i++)
            {
                if (ops[i].Kind is LineOpKind.Keep or LineOpKind.Update)
                    return tab.Notes[ops[i].OldIndex];
            }
        }
        return null;
    }

    private static DateTimeOffset ComputeInsertTime(Note? prev, Note? next, DateOnly day)
    {
        DateTimeOffset time;
        if (prev is not null)
        {
            time = prev.CreatedAt.AddMinutes(1);
        }
        else
        {
            var now = DateTimeOffset.Now;
            time = day == DateOnly.FromDateTime(now.DateTime)
                ? now
                : new DateTimeOffset(day.ToDateTime(TimeOnly.FromDateTime(now.DateTime)), now.Offset);
        }

        if (next is not null && time >= next.CreatedAt)
            time = next.CreatedAt.AddMinutes(-1);

        return time;
    }

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');

    private void OnSettingsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _settings = AppSettings.Load();
            _fontSize = _settings.EditorFontSize;
            EditorBox.FontSize = _fontSize;
            GutterText.FontSize = _fontSize;
            RebuildGutter();
        });
    }

    private void SetFontSize(double size)
    {
        var clamped = Math.Clamp(size, AppSettings.MinEditorFontSize, AppSettings.MaxEditorFontSize);
        if (Math.Abs(clamped - _fontSize) < 0.01) return;

        _fontSize = clamped;
        EditorBox.FontSize = _fontSize;
        GutterText.FontSize = _fontSize;
        RebuildGutter();

        try
        {
            _settings.EditorFontSize = _fontSize;
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (!ctrl) return;

        if (e.Key == Key.S)
        {
            if (_active is not null) SaveTab(_active);
            e.Handled = true;
        }
        else if (e.Key == Key.W)
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
        foreach (var tab in _tabs.ToList())
        {
            if (!ConfirmAndResolve(tab))
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

    private sealed class EditorTab
    {
        public DateOnly Day;
        public List<Note> Notes = new();
        public string OriginalText = "";
        public string Text = "";
        public bool ExternalChangeNotice;

        public bool Dirty => Text != OriginalText;
    }
}
