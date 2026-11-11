using System;
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

    private readonly NoteStore _store;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private string? _tagFilter;
    private long? _undoId;
    private bool _reloadPending;
    private DateTime _lastHiddenAt = DateTime.MinValue;

    public ObservableCollection<DayGroupItem> Groups { get; } = [];
    public ObservableCollection<TagChip> Tags { get; } = [];

    public PanelWindow(NoteStore store)
    {
        _store = store;
        InitializeComponent();
        DataContext = this;
        _undoTimer.Tick += (_, _) => HideUndo();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); ErrorBar.Visibility = Visibility.Collapsed; };
    }

    private bool AnyEditing => Groups.SelectMany(g => g.Notes).Any(n => n.IsEditing);

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
        Reload();
        Show();
        Activate();
        InputBox.Focus();
    }

    private void HidePanel()
    {
        foreach (var note in Groups.SelectMany(g => g.Notes))
            note.IsEditing = false;
        Hide();
        _lastHiddenAt = DateTime.Now;
    }

    public void Reload()
    {
        if (AnyEditing)
        {
            _reloadPending = true;
            return;
        }
        _reloadPending = false;
        Try(() =>
        {
            var all = _store.List(new NoteFilter());
            var tags = all.SelectMany(n => n.Tags).Distinct().Order(TrComparer).ToList();
            if (_tagFilter is not null && !tags.Contains(_tagFilter))
                _tagFilter = null;

            Tags.Clear();
            Tags.Add(new TagChip(null, "Tümü", _tagFilter is null));
            foreach (var tag in tags)
                Tags.Add(new TagChip(tag, "#" + tag, tag == _tagFilter));
            TagRow.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var visible = _tagFilter is null ? all : all.Where(n => n.Tags.Contains(_tagFilter)).ToList();
            var today = DateOnly.FromDateTime(DateTime.Now);
            Groups.Clear();
            foreach (var group in DayGrouper.Group(visible))
                Groups.Add(new DayGroupItem(DayGrouper.Header(group.Date, today), group.Notes.Select(n => new NoteItem(n)).ToList()));
            EmptyText.Visibility = Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            HidePanel();
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
            Reload();
        });
    }

    private void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip })
        {
            _tagFilter = chip.Tag;
            Reload();
        }
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ((UIElement)sender).Focus();

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: NoteItem item } box)
            Try(() => { _store.SetDone(item.Id, box.IsChecked == true); Reload(); });
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

    private void DeleteNote(NoteItem item) => Try(() =>
    {
        _store.Delete(item.Id);
        _undoId = item.Id;
        UndoBar.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
        Reload();
    });

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
            ErrorText.Text = ex switch
            {
                NoteNotFoundException or ArgumentException => ex.Message,
                SqliteException => $"Veritabanı hatası. Yedekler: {AppPaths.BackupDir}",
                _ => $"Bir hata oluştu, ayrıntı: {AppPaths.LogPath}",
            };
            ErrorBar.Visibility = Visibility.Visible;
            _errorTimer.Stop();
            _errorTimer.Start();
        }
    }
}
