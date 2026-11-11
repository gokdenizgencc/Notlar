using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.App;

public partial class PanelWindow : Window
{
    private static readonly StringComparer TrComparer = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);
    private const int RecentDays = 30;

    private readonly NoteStore _store;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private string? _tagFilter;
    private NoteAuthor? _authorFilter;
    private long? _undoId;
    private bool _reloadPending;
    private bool _showAll;
    private DateOnly? _selectedDay;
    private DateTime _lastHiddenAt = DateTime.MinValue;
    private readonly List<DayWindow> _dayWindows = [];

    public ObservableCollection<DayListItem> Days { get; } = [];
    public ObservableCollection<NoteItem> DayNotes { get; } = [];
    public ObservableCollection<TagChip> Tags { get; } = [];
    public ObservableCollection<AuthorChip> AuthorFilters { get; } = [];

    public PanelWindow(NoteStore store)
    {
        _store = store;
        InitializeComponent();
        DataContext = this;
        _undoTimer.Tick += (_, _) => HideUndo();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); ErrorBar.Visibility = Visibility.Collapsed; };
    }

    private bool AnyEditing => DayNotes.Any(n => n.IsEditing);

    /// <summary>Tray sol tık. Panel açıkken tıklama önce Deactivated'ı tetikler; hemen yeniden açmayı engelle.</summary>
    public void Toggle()
    {
        if (IsVisible) HidePanel();
        else if ((DateTime.Now - _lastHiddenAt).TotalMilliseconds > 300) ShowNearTray();
    }

    public void ShowNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 8;
        Top = area.Bottom - Height - 8;
        Show();
        ReloadCore();
        Activate();
        InputBox.Focus();
    }

    private void HidePanel()
    {
        CancelAllEdits();
        Hide();
        _lastHiddenAt = DateTime.Now;
        _showAll = false;
        _selectedDay = null;
        _authorFilter = null;
    }

    /// <summary>Açık düzenlemeleri kaydetmeden kapatır, böylece ardından çağrılan Reload() ertelenmez.</summary>
    private void CancelAllEdits()
    {
        foreach (var note in DayNotes)
            note.IsEditing = false;
    }

    /// <summary>Panel gizliyken gerçek yeniden yükleme işini atlar (DB okuma + UI güncelleme).
    /// ShowNearTray açılışta zaten koşulsuz yeniden yükler.</summary>
    public void Reload()
    {
        ReloadDayWindows();
        if (!IsVisible) return;
        ReloadCore();
    }

    /// <summary>Açık DayWindow'ları (varsa) güncel veriyle tazeler. Panel gizliyken de çalışır.</summary>
    public void ReloadDayWindows()
    {
        foreach (var window in _dayWindows.ToList())
            window.Reload();
    }

    private void ReloadCore()
    {
        if (AnyEditing)
        {
            _reloadPending = true;
            return;
        }
        _reloadPending = false;
        Try(() =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var since = today.AddDays(-(RecentDays - 1));
            var all = _showAll ? _store.List(new NoteFilter()) : _store.ListRecentAndOpen(since);

            AuthorFilters.Clear();
            AuthorFilters.Add(new AuthorChip(null, "Tümü", _authorFilter is null));
            AuthorFilters.Add(new AuthorChip(NoteAuthor.Ben, "Ben", _authorFilter == NoteAuthor.Ben));
            AuthorFilters.Add(new AuthorChip(NoteAuthor.Claude, "Claude", _authorFilter == NoteAuthor.Claude));

            var byAuthor = _authorFilter is null ? all : all.Where(n => n.Author == _authorFilter).ToList();

            var tags = byAuthor.SelectMany(n => n.Tags).Distinct().Order(TrComparer).ToList();
            if (_tagFilter is not null && !tags.Contains(_tagFilter))
                _tagFilter = null;

            Tags.Clear();
            Tags.Add(new TagChip(null, "Tümü", _tagFilter is null));
            foreach (var tag in tags)
                Tags.Add(new TagChip(tag, "#" + tag, tag == _tagFilter));
            TagRow.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var visible = _tagFilter is null ? byAuthor : byAuthor.Where(n => n.Tags.Contains(_tagFilter)).ToList();
            var groups = DayGrouper.Group(visible);

            Days.Clear();
            foreach (var group in groups)
                Days.Add(new DayListItem(group.Date, DayGrouper.Header(group.Date, today), group.Notes.Count));

            var selectedGroup = _selectedDay is { } day ? groups.FirstOrDefault(g => g.Date == day) : null;
            if (_selectedDay is not null && selectedGroup is null)
                _selectedDay = null;

            DayNotes.Clear();
            if (selectedGroup is not null)
            {
                foreach (var note in selectedGroup.Notes)
                    DayNotes.Add(new NoteItem(note));
                DayViewHeader.Text = DayGrouper.Header(selectedGroup.Date, today);
            }

            DayListPanel.Visibility = _selectedDay is null ? Visibility.Visible : Visibility.Collapsed;
            DayViewPanel.Visibility = _selectedDay is null ? Visibility.Collapsed : Visibility.Visible;

            var hasOlderDone = !_showAll && _store.HasDoneBefore(since);
            OlderToggle.Visibility = _showAll || hasOlderDone ? Visibility.Visible : Visibility.Collapsed;
            OlderToggle.Content = _showAll ? "Sadece son 30 günü göster" : "Daha eski notları göster";

            EmptyText.Visibility = Days.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = Days.Count == 0 && !_showAll && hasOlderDone
                ? "Son 30 günde not yok."
                : "Henüz not yok.";
        });
    }

    public void ExportMarkdown() => Try(() =>
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = Path.Combine(documents, $"Notlar-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md");
        File.WriteAllText(path, MarkdownExporter.Export(_store.List(new NoteFilter())), new UTF8Encoding(false));
        Process.Start("explorer.exe", $"/select,\"{path}\"");
    });

    private void Window_Deactivated(object? sender, EventArgs e) => HidePanel();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AnyEditing) return; // Düzenleme kutusu Enter/Esc'i kendisi işler.
        if (e.Key == Key.Escape)
        {
            if (_selectedDay is not null)
            {
                _selectedDay = null;
                Reload();
            }
            else
            {
                HidePanel();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is FrameworkElement { DataContext: NoteItem item })
        {
            DeleteNote(item);
            e.Handled = true;
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        Try(() =>
        {
            _store.Add(InputBox.Text);
            InputBox.Clear();
            _selectedDay = DateOnly.FromDateTime(DateTime.Now);
            Reload();
        });
    }

    private void DayRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DayListItem day })
        {
            CancelAllEdits();
            _selectedDay = day.Date;
            Reload();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        CancelAllEdits();
        _selectedDay = null;
        Reload();
    }

    private void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip })
        {
            CancelAllEdits();
            _tagFilter = chip.Tag;
            Reload();
        }
    }

    private void AuthorChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AuthorChip chip })
        {
            CancelAllEdits();
            _authorFilter = chip.Author;
            Reload();
        }
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDay is not { } day) return;
        var existing = _dayWindows.FirstOrDefault(w => w.Day == day);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        var window = new DayWindow(_store, day);
        window.Closed += (_, _) => _dayWindows.Remove(window);
        _dayWindows.Add(window);
        window.Show();
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ((UIElement)sender).Focus();

    private void OlderToggle_Click(object sender, RoutedEventArgs e)
    {
        CancelAllEdits();
        _showAll = !_showAll;
        Reload();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: NoteItem item } box)
        {
            CancelAllEdits();
            Try(() => { _store.SetDone(item.Id, box.IsChecked == true); Reload(); });
        }
    }

    private void NoteText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: NoteItem item }) return;
        item.EditText = item.Note.Text;
        item.IsEditing = true;
        e.Handled = true;
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.InvokeAsync(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: NoteItem item }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = item.EditText.Trim();
            item.IsEditing = false;
            if (text.Length > 0 && text != item.Note.Text)
                Try(() => _store.Update(item.Id, text));
            Reload();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelEdit(item);
        }
    }

    private void EditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: NoteItem { IsEditing: true } item })
            CancelEdit(item);
    }

    private void CancelEdit(NoteItem item)
    {
        item.IsEditing = false;
        if (_reloadPending) Reload();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NoteItem item })
            DeleteNote(item);
    }

    private void DeleteNote(NoteItem item)
    {
        CancelAllEdits();
        Try(() =>
        {
            _store.Delete(item.Id);
            _undoId = item.Id;
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            Reload();
        });
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoId is { } id)
            Try(() => { _store.Restore(id); Reload(); });
        HideUndo();
    }

    private void HideUndo()
    {
        _undoTimer.Stop();
        _undoId = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            var message = ex switch
            {
                NoteNotFoundException or ArgumentException => ex.Message,
                SqliteException => $"Veritabanı hatası. Yedekler: {AppPaths.BackupDir}",
                _ => $"Bir hata oluştu, ayrıntı: {AppPaths.LogPath}",
            };
            // Panel gizliyken ErrorBar görünmez; tepsi menüsünden tetiklenen (ör. dışa aktarma)
            // hatalar kaybolmasın diye bu durumda mesaj kutusu gösterilir.
            if (IsVisible)
            {
                ErrorText.Text = message;
                ErrorBar.Visibility = Visibility.Visible;
                _errorTimer.Stop();
                _errorTimer.Start();
            }
            else
            {
                MessageBox.Show(message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
