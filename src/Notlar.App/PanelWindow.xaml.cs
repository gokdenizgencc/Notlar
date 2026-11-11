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
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.App;

public partial class PanelWindow : Window
{
    private static readonly StringComparer TrComparer = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);
    private const int RecentDays = 30;

    private enum UndoKind { Note, Task }

    private readonly NoteStore _store;
    private readonly TaskStore _taskStore;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private AppSettings _settings;
    private string? _tagFilter;
    private string? _authorFilter;
    private long? _undoId;
    private UndoKind _undoKind;
    private bool _reloadPending;
    private bool _showAll;
    private bool _tasksMode;
    private DateOnly? _selectedDay;
    private DateTime _lastHiddenAt = DateTime.MinValue;
    private readonly List<DayWindow> _dayWindows = [];

    public ObservableCollection<DayListItem> Days { get; } = [];
    public ObservableCollection<NoteItem> DayNotes { get; } = [];
    public ObservableCollection<TagChip> Tags { get; } = [];
    public ObservableCollection<AuthorChip> AuthorFilters { get; } = [];
    public ObservableCollection<TaskItemVm> Tasks { get; } = [];

    public PanelWindow(NoteStore store, TaskStore taskStore)
    {
        _store = store;
        _taskStore = taskStore;
        _settings = AppSettings.Load();
        InitializeComponent();
        DataContext = this;
        _undoTimer.Tick += (_, _) => HideUndo();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); ErrorBar.Visibility = Visibility.Collapsed; };
        AppSettings.Changed += OnSettingsChanged;
    }

    private bool AnyEditing => DayNotes.Any(n => n.IsEditing) || Tasks.Any(t => t.IsEditing);

    private void OnSettingsChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _settings = AppSettings.Load();
            if (IsVisible) ReloadCore();
        });
    }

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
        if (_tasksMode) TaskInputBox.Focus(); else InputBox.Focus();
    }

    private void HidePanel()
    {
        CancelAllEdits();
        Hide();
        _lastHiddenAt = DateTime.Now;
        _showAll = false;
        _selectedDay = null;
        _authorFilter = null;
        _tasksMode = false;
    }

    /// <summary>Açık düzenlemeleri kaydetmeden kapatır, böylece ardından çağrılan Reload() ertelenmez.</summary>
    private void CancelAllEdits()
    {
        foreach (var note in DayNotes)
            note.IsEditing = false;
        foreach (var task in Tasks)
            task.IsEditing = false;
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
            HeaderText.Text = _tasksMode ? "✅ Görevler" : "📝 Notlar";
            NotesInputBorder.Visibility = _tasksMode ? Visibility.Collapsed : Visibility.Visible;
            TaskInputBorder.Visibility = _tasksMode ? Visibility.Visible : Visibility.Collapsed;
            TasksPanel.Visibility = _tasksMode ? Visibility.Visible : Visibility.Collapsed;

            ReloadTasks();

            if (_tasksMode)
            {
                AuthorFilterRow.Visibility = Visibility.Collapsed;
                TagRow.Visibility = Visibility.Collapsed;
                DayListPanel.Visibility = Visibility.Collapsed;
                DayViewPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var today = DateOnly.FromDateTime(DateTime.Now);
            var since = today.AddDays(-(RecentDays - 1));
            var all = _showAll ? _store.List(new NoteFilter()) : _store.ListRecentAndOpen(since);

            var distinctAuthors = all.Select(n => n.Author).Distinct(StringComparer.OrdinalIgnoreCase).Order(TrComparer).ToList();
            if (_authorFilter is not null && !distinctAuthors.Contains(_authorFilter, StringComparer.OrdinalIgnoreCase))
                _authorFilter = null;

            AuthorFilters.Clear();
            AuthorFilters.Add(new AuthorChip(null, "Tümü", _authorFilter is null, (Brush)FindResource("Fg")));
            foreach (var author in distinctAuthors)
            {
                var color = AuthorColors.For(author, _settings) ?? (Brush)FindResource("Fg");
                AuthorFilters.Add(new AuthorChip(author, author, string.Equals(author, _authorFilter, StringComparison.OrdinalIgnoreCase), color));
            }

            var byAuthor = _authorFilter is null ? all : all.Where(n => string.Equals(n.Author, _authorFilter, StringComparison.OrdinalIgnoreCase)).ToList();

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

            // Not: açık bir gün görünümünde gösterilecek not kalmasa bile (ör. hepsi silindi, ya da
            // "Başka gün" ile boş bir gün açıldı) otomatik olarak gün listesine dönülmez; kullanıcı
            // yalnızca Geri/Esc ile çıkar.
            var selectedGroup = _selectedDay is { } selDay ? groups.FirstOrDefault(g => g.Date == selDay) : null;

            DayNotes.Clear();
            if (_selectedDay is { } day)
            {
                foreach (var note in selectedGroup?.Notes ?? [])
                {
                    var color = AuthorColors.For(note.Author, _settings) ?? (Brush)FindResource("Fg");
                    DayNotes.Add(new NoteItem(note, color, AuthorColors.Tint(color)));
                }
                DayViewHeader.Text = DayGrouper.Header(day, today);
            }
            DayViewEmptyText.Visibility = _selectedDay is not null && DayNotes.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;

            DayListPanel.Visibility = _selectedDay is null ? Visibility.Visible : Visibility.Collapsed;
            DayViewPanel.Visibility = _selectedDay is null ? Visibility.Collapsed : Visibility.Visible;
            AuthorFilterRow.Visibility = Visibility.Visible;

            InputPlaceholder.Text = _selectedDay is { } selectedOther && selectedOther != today
                ? $"{DayGrouper.FormatShort(selectedOther)}'{DativeSuffix(selectedOther)} not yaz... (#etiket)"
                : "Bugüne not yaz... (#etiket)";

            var hasOlderDone = !_showAll && _store.HasDoneBefore(since);
            OlderToggle.Visibility = _showAll || hasOlderDone ? Visibility.Visible : Visibility.Collapsed;
            OlderToggle.Content = _showAll ? "Sadece son 30 günü göster" : "Daha eski notları göster";

            EmptyText.Visibility = Days.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = Days.Count == 0 && !_showAll && hasOlderDone
                ? "Son 30 günde not yok."
                : "Henüz not yok.";
        });
    }

    private void ReloadTasks()
    {
        var tasks = _taskStore.List();
        Tasks.Clear();
        foreach (var task in tasks)
        {
            var color = AuthorColors.For(task.Author, _settings) ?? (Brush)FindResource("Fg");
            Tasks.Add(new TaskItemVm(task, color, AuthorColors.Tint(color)));
        }
        TasksEmptyText.Visibility = Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ExportMarkdown() => Try(() =>
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = Path.Combine(documents, $"Notlar-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md");
        File.WriteAllText(path, MarkdownExporter.Export(_store.List(new NoteFilter())), new UTF8Encoding(false));
        Process.Start("explorer.exe", $"/select,\"{path}\"");
    });

    private void Window_Deactivated(object? sender, EventArgs e) => HidePanel();

    private void HeaderText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 3) return;
        CancelAllEdits();
        _tasksMode = !_tasksMode;
        Reload();
    }

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
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is FrameworkElement { DataContext: TaskItemVm taskItem })
        {
            DeleteTask(taskItem);
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
            if (_selectedDay is { } day)
            {
                // Seçili gün görünümünde: notu o güne, günün saati olarak "şu an"ın saatiyle ekle
                // (seçili gün bugünse bu zaten normal "şimdi" ile aynı anlama gelir).
                var now = DateTimeOffset.Now;
                var createdAt = new DateTimeOffset(day.Year, day.Month, day.Day, now.Hour, now.Minute, now.Second, now.Offset);
                _store.AddAt(InputBox.Text, createdAt);
            }
            else
            {
                _store.Add(InputBox.Text);
                _selectedDay = DateOnly.FromDateTime(DateTime.Now);
            }
            InputBox.Clear();
            Reload();
        });
    }

    private void TaskInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(TaskInputBox.Text)) return;
        Try(() =>
        {
            _taskStore.Add(TaskInputBox.Text, NoteAuthor.Ben);
            TaskInputBox.Clear();
            Reload();
        });
    }

    private void OtherDayButton_Click(object sender, RoutedEventArgs e)
    {
        OtherDayPicker.DisplayDateEnd = DateTime.Today;
        OtherDayPicker.Visibility = Visibility.Visible;
        OtherDayPicker.IsDropDownOpen = true;
        OtherDayPicker.Focus();
    }

    private void OtherDayPicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        OtherDayPicker.Visibility = Visibility.Collapsed;
        if (OtherDayPicker.SelectedDate is not { } date) return;
        CancelAllEdits();
        _selectedDay = DateOnly.FromDateTime(date);
        OtherDayPicker.SelectedDate = null;
        Reload();
    }

    /// <summary>"gün ay" biçimindeki ifadeye eklenecek -e hali eki ("'a" / "'e"), son sesli harfe göre
    /// Türkçe büyük ünlü uyumuyla belirlenir (ör. "10 Kasım" → "a", "3 Ekim" → "e").</summary>
    private static string DativeSuffix(DateOnly date)
    {
        const string back = "aıouAIOU";
        const string front = "eiöüEİÖÜ";
        var phrase = DayGrouper.FormatShort(date);
        for (var i = phrase.Length - 1; i >= 0; i--)
        {
            if (back.IndexOf(phrase[i]) >= 0) return "a";
            if (front.IndexOf(phrase[i]) >= 0) return "e";
        }
        return "e";
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
            _undoKind = UndoKind.Note;
            UndoText.Text = "Not silindi.";
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            Reload();
        });
    }

    private void TaskDone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TaskItemVm item } box)
        {
            CancelAllEdits();
            Try(() => { _taskStore.SetDone(item.Id, box.IsChecked == true); Reload(); });
        }
    }

    private void TaskTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: TaskItemVm item }) return;
        item.EditText = item.Task.Title;
        item.IsEditing = true;
        e.Handled = true;
    }

    private void TaskEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TaskItemVm item }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = item.EditText.Trim();
            item.IsEditing = false;
            if (text.Length > 0 && text != item.Task.Title)
                Try(() => _taskStore.Rename(item.Id, text));
            Reload();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTaskEdit(item);
        }
    }

    private void TaskEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TaskItemVm { IsEditing: true } item })
            CancelTaskEdit(item);
    }

    private void CancelTaskEdit(TaskItemVm item)
    {
        item.IsEditing = false;
        if (_reloadPending) Reload();
    }

    private void TaskDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskItemVm item })
            DeleteTask(item);
    }

    private void DeleteTask(TaskItemVm item)
    {
        CancelAllEdits();
        Try(() =>
        {
            _taskStore.Delete(item.Id);
            _undoId = item.Id;
            _undoKind = UndoKind.Task;
            UndoText.Text = "Görev silindi.";
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            Reload();
        });
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoId is { } id)
        {
            if (_undoKind == UndoKind.Task)
                Try(() => { _taskStore.Restore(id); Reload(); });
            else
                Try(() => { _store.Restore(id); Reload(); });
        }
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
                NoteNotFoundException or TaskNotFoundException or ArgumentException => ex.Message,
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
