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
using System.Windows.Threading;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Bir günün notlarını Notepad++ tarzı tam ekran, tek parça metin olarak düzenlenebilen bir pencerede
/// gösterir (her satır = bir not; Enter yeni satır, ↑/↓ satırlar arası, Backspace satır başında birleştirir).
/// Ctrl+S kaydeder; satırlar <see cref="DayTextSync"/> ile notlara eşlenir. Her açık gün bir sekmedir;
/// pencere tekildir (<see cref="OpenDay"/> yeniden kullanır).</summary>
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
    /// Gün zaten açıksa o sekmeyi etkinleştirir. <paramref name="noteId"/> verilirse imleç o notun
    /// satırının sonuna konur; her durumda klavye odağı doğrudan metne verilir (hemen yazılabilir).</summary>
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
            instance.MoveCaretToNote(id);

        if (instance.WindowState == WindowState.Minimized)
            instance.WindowState = WindowState.Maximized;
        instance.Activate();
        instance.FocusEditor();
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

    /// <summary>Değişiklik sinyalinde (başka süreç, panel ya da bu pencerenin kendi kaydı) çağrılır.
    /// Günün notları sekmenin bildiğiyle aynıysa hiçbir şey yapılmaz (kendi kaydımızın yankısı yazmayı
    /// kesmez). Kirli olmayan sekme sessizce tazelenir (imleç aynı notta kalır); kirli sekme için
    /// "dışarıda değişti" çubuğu gösterilir.</summary>
    private void ReloadAllTabsInternal()
    {
        foreach (var tab in _tabs)
        {
            List<Note> fresh;
            try
            {
                fresh = ReadNotes(tab.Day);
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
                continue;
            }

            if (fresh.SequenceEqual(tab.Notes)) continue;

            if (tab.Dirty)
            {
                tab.ExternalChangeNotice = true;
                continue;
            }

            SetNotes(tab, fresh);
            SyncTextToNotes(tab);
        }

        RebuildGutter();
        UpdateStatusBar();
        UpdateExternalChangeBar();
        RebuildTabStrip();
    }

    /// <summary>Verilen sekmeyi depodaki güncel notlarla ilk kez doldurur (yeni sekme).</summary>
    private void LoadTab(EditorTab tab)
    {
        SetNotes(tab, ReadNotes(tab.Day));
        tab.Text = tab.OriginalText;
        tab.CaretIndex = 0;
    }

    private List<Note> ReadNotes(DateOnly day)
    {
        var notes = _store.List(new NoteFilter(From: day, To: day));
        var ordered = DayGrouper.Group(notes).FirstOrDefault(g => g.Date == day)?.Notes ?? Array.Empty<Note>();
        return ordered.ToList();
    }

    /// <summary>Sekmenin bildiği notları (kaydetme/eşleme tabanı) günceller; editör metnine dokunmaz.</summary>
    private static void SetNotes(EditorTab tab, List<Note> notes)
    {
        tab.Notes = notes;
        tab.OriginalText = DayTextSync.ToText(notes);
        tab.ExternalChangeNotice = false;
    }

    /// <summary>Editör metni notlarla zaten örtüşüyorsa (boş satırlar/kenar boşlukları hariç) metne
    /// dokunmaz — imleç, kaydırma ve geri al (Ctrl+Z) geçmişi korunur. Örtüşmüyorsa metni notlardan
    /// yeniden kurar ve imleci aynı nota taşır.</summary>
    private void SyncTextToNotes(EditorTab tab)
    {
        if (!DayTextSync.HasChanges(tab.Notes, tab.Text)) return;
        if (tab == _active)
        {
            ReplaceActiveText(tab.OriginalText);
        }
        else
        {
            var (line, col) = DayTextSync.LineColumnOf(tab.Text, tab.CaretIndex);
            var newLine = DayTextSync.MapLine(tab.Text, tab.OriginalText, line);
            tab.CaretIndex = DayTextSync.IndexOf(tab.OriginalText, newLine, col);
            tab.Text = tab.OriginalText;
        }
    }

    /// <summary>Etkin sekmenin metnini değiştirir; imleci aynı satıra/nota, kaydırmayı eski yerine koyar.</summary>
    private void ReplaceActiveText(string newText)
    {
        if (_active is null) return;

        var oldText = EditorBox.Text;
        var (line, col) = DayTextSync.LineColumnOf(oldText, EditorBox.CaretIndex);
        var newLine = DayTextSync.MapLine(oldText, newText, line);
        var vertical = EditorBox.VerticalOffset;
        var horizontal = EditorBox.HorizontalOffset;

        _suppressTextChanged = true;
        EditorBox.Text = newText;
        EditorBox.CaretIndex = DayTextSync.IndexOf(newText, newLine, col);
        _suppressTextChanged = false;
        _active.Text = newText;

        Dispatcher.InvokeAsync(() =>
        {
            EditorBox.ScrollToVerticalOffset(vertical);
            EditorBox.ScrollToHorizontalOffset(horizontal);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>İmleci etkin sekmede verilen notun satırının sonuna koyar ve satırı görünür yapar.</summary>
    private void MoveCaretToNote(long noteId)
    {
        if (_active is null) return;
        var line = DayTextSync.LineOfNote(_active.Notes, _active.Text, noteId);
        if (line < 0) return;

        EditorBox.CaretIndex = DayTextSync.IndexOf(EditorBox.Text, line, int.MaxValue);
        Dispatcher.InvokeAsync(() => EditorBox.ScrollToLine(line), DispatcherPriority.Loaded);
    }

    /// <summary>Klavye odağını metin kutusuna verir (pencere ilk açılışta henüz yüklenmemiş olabilir).</summary>
    private void FocusEditor()
    {
        Dispatcher.InvokeAsync(() =>
        {
            EditorBox.Focus();
            Keyboard.Focus(EditorBox);
        }, DispatcherPriority.Loaded);
    }

    private void ActivateTab(EditorTab tab)
    {
        if (_active is not null && _active != tab)
            _active.CaretIndex = EditorBox.CaretIndex;
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
        EditorBox.CaretIndex = Math.Clamp(_active.CaretIndex, 0, _active.Text.Length);
        _suppressTextChanged = false;

        RebuildGutter();
        UpdateStatusBar();
        FocusEditor();
    }

    private void RebuildGutter()
    {
        if (_active is null)
        {
            GutterText.Text = "";
            return;
        }

        var oldLines = DayTextSync.SplitLines(_active.OriginalText);
        var newLines = DayTextSync.SplitLines(_active.Text);
        var ops = LineDiff.Map(oldLines, newLines);

        var timeByNewIndex = new Dictionary<int, string>();
        foreach (var op in ops)
        {
            if ((op.Kind == LineOpKind.Keep || op.Kind == LineOpKind.Update) && op.OldIndex >= 0 && op.OldIndex < _active.Notes.Count)
            {
                var note = _active.Notes[op.OldIndex];
                timeByNewIndex[op.NewIndex] = note.CreatedAt.ToString("HH:mm", Tr) + (note.Done ? " ✓" : "");
            }
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
        var lineCount = DayTextSync.SplitLines(text).Length;
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

    /// <summary>"Yeniden yükle": etkin sekmedeki kaydedilmemiş düzenlemeleri atıp depodaki güncel hali gösterir.</summary>
    private void ReloadBar_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        try
        {
            SetNotes(_active, ReadNotes(_active.Day));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(this, $"Yeniden yükleme başarısız: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ReplaceActiveText(_active.OriginalText);
        RebuildGutter();
        UpdateStatusBar();
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
        // Kaydetme başarısız olursa sekme/pencere kapanmaz; düzenlemeler kaybolmasın.
        if (result == MessageBoxResult.Yes) return SaveTab(tab);
        return true;
    }

    /// <summary>Sekmenin metnini depoya yazar. İşlemler sekmenin bildiği notlara göre hesaplanır ve
    /// yalnızca kullanıcının değiştirdiği satırlar depoya dokunur; bu yüzden bu arada başka bir süreçten
    /// (ör. MCP) eklenen/değişen notlar korunur ve kayıttan sonra metne katılır. Metin kaydedilen
    /// hâlle örtüşüyorsa editöre dokunulmaz (imleç ve Ctrl+Z geçmişi kalır). Başarılıysa true döner.</summary>
    private bool SaveTab(EditorTab tab)
    {
        try
        {
            var plan = DayTextSync.Plan(tab.Notes, tab.Text, tab.Day, DateTimeOffset.Now);
            var byId = tab.Notes.ToDictionary(n => n.Id);

            foreach (var edit in plan)
            {
                switch (edit.Kind)
                {
                    case DayEditKind.Update:
                        try
                        {
                            _store.Update(edit.NoteId, edit.Text);
                        }
                        catch (NoteNotFoundException)
                        {
                            // Not bu arada dışarıda silinmiş: düzenleme kaybolmasın, aynı saat ve yazarla yeniden ekle.
                            var original = byId[edit.NoteId];
                            _store.AddAt(edit.Text, original.CreatedAt, original.Author);
                        }
                        break;
                    case DayEditKind.Delete:
                        try
                        {
                            _store.Delete(edit.NoteId);
                        }
                        catch (NoteNotFoundException)
                        {
                            // Zaten silinmiş; yapılacak bir şey yok.
                        }
                        break;
                    case DayEditKind.Insert:
                        _store.AddAt(edit.Text, edit.CreatedAt, NoteAuthor.Ben);
                        break;
                }
            }

            SetNotes(tab, ReadNotes(tab.Day));
            SyncTextToNotes(tab);
            if (tab == _active)
            {
                RebuildGutter();
                UpdateStatusBar();
                UpdateExternalChangeBar();
            }
            RebuildTabStrip();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(this, $"Kaydetme başarısız: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

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
        public int CaretIndex;
        public bool ExternalChangeNotice;

        /// <summary>Kaydedildiğinde notlarda değişiklik yaratacak bir düzenleme var mı? Boş satırlar ve
        /// satır kenarındaki boşluklar sayılmaz (zaten kaydedilmezler).</summary>
        public bool Dirty => DayTextSync.HasChanges(Notes, Text);
    }
}
