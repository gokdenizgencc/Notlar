using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Notlar.Core;
using Forms = System.Windows.Forms;

namespace Notlar.App;

public partial class PanelWindow : Window
{
    private static readonly StringComparer TrComparer = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);
    private const int RecentDays = 30;

    private enum UndoKind { Note, Task, Memory, Skill, Plan }

    /// <summary>Panelin görünür modu: normal not günlüğü, görünür hafıza kartları sekmesi, görünür
    /// yetenek kataloğu sekmesi, görünür planlar sekmesi ya da üçlü tıklamayla açılan gizli kişisel liste.</summary>
    private enum Mode { Notes, Memory, Skills, Plans, Personal }

    private static readonly CultureInfo TrTr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly NoteStore _store;
    private readonly TaskStore _taskStore;
    private readonly MemoryStore _memoryStore;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillTranslations _skillTranslations;
    private readonly PlanStore _planStore;
    private readonly SkillUsageCache _skillUsageCache = new();
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private AppSettings _settings;
    private string? _tagFilter;
    private string? _authorFilter;
    private long? _undoId;
    private UndoKind _undoKind;
    private bool _reloadPending;
    private bool _showAll;
    private Mode _mode = Mode.Notes;
    private Mode _previousMode = Mode.Notes;
    private DateOnly? _selectedDay;
    private DateTime _lastHiddenAt = DateTime.MinValue;
    private readonly List<DayWindow> _dayWindows = [];

    private long? _selectedPlanId;
    private Plan? _currentPlan;
    private bool _planTitleEditing;
    private bool _planPathEditing;

    private IReadOnlyList<SkillInfo>? _skillInstalledCache;
    private IReadOnlyDictionary<string, SkillUsage>? _skillUsageDataCache;
    private IReadOnlyList<RegistrySkill>? _skillRegistryCache;
    private IReadOnlyDictionary<string, SkillTranslation>? _skillTranslationsCache;
    private IReadOnlyList<MarketplacePlugin>? _marketplaceCache;
    private bool _skillLoading;
    private bool _skillLoadRequested;
    private string _skillSearch = "";
    private bool _skillSortByUsage = true;
    private bool _skillShowDiscover;
    private readonly DispatcherTimer _skillStatusTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    public ObservableCollection<DayListItem> Days { get; } = [];
    public ObservableCollection<NoteItem> DayNotes { get; } = [];
    public ObservableCollection<TagChip> Tags { get; } = [];
    public ObservableCollection<AuthorChip> AuthorFilters { get; } = [];
    public ObservableCollection<TaskItemVm> Tasks { get; } = [];
    public ObservableCollection<MemoryGroupVm> MemoryGroups { get; } = [];
    public ObservableCollection<string> MemoryCategories { get; } = [];
    public ObservableCollection<SkillGroupVm> SkillGroups { get; } = [];
    public ObservableCollection<string> SkillAiOptions { get; } = [];
    public ObservableCollection<MarketplaceGroupVm> MarketplaceGroups { get; } = [];
    public ObservableCollection<PlanListItemVm> Plans { get; } = [];
    public ObservableCollection<PlanStepItemVm> PlanDetailSteps { get; } = [];

    public PanelWindow(NoteStore store, TaskStore taskStore, MemoryStore memoryStore, SkillRegistry skillRegistry,
        SkillTranslations skillTranslations, PlanStore planStore)
    {
        _store = store;
        _taskStore = taskStore;
        _memoryStore = memoryStore;
        _skillRegistry = skillRegistry;
        _skillTranslations = skillTranslations;
        _planStore = planStore;
        _settings = AppSettings.Load();
        InitializeComponent();
        DataContext = this;
        _undoTimer.Tick += (_, _) => HideUndo();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); ErrorBar.Visibility = Visibility.Collapsed; };
        _skillStatusTimer.Tick += (_, _) => { _skillStatusTimer.Stop(); SkillStatusRow.Visibility = Visibility.Collapsed; };
        AppSettings.Changed += OnSettingsChanged;
        UpdatePinButtonVisual();
    }

    private bool AnyEditing => DayNotes.Any(n => n.IsEditing) || Tasks.Any(t => t.IsEditing)
        || MemoryGroups.Any(g => g.Items.Any(m => m.IsEditing))
        || PlanDetailSteps.Any(s => s.IsEditing) || _planTitleEditing || _planPathEditing;

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
        if (_settings.PanelPinned && _settings.PanelLeft is { } left && _settings.PanelTop is { } top
            && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = area.Right - Width - 8;
            Top = area.Bottom - Height - 8;
        }
        Show();
        ReloadCore();
        Activate();
        switch (_mode)
        {
            case Mode.Personal: TaskInputBox.Focus(); break;
            case Mode.Memory: MemorySearchBox.Focus(); break;
            case Mode.Skills: SkillSearchBox.Focus(); break;
            case Mode.Plans: break;
            default: InputBox.Focus(); break;
        }
    }

    /// <summary>Konum ekranın görünür alanı (çoklu monitör dahil) dışında kalmışsa (ör. monitör
    /// çıkarıldıysa) false döner; bu durumda ShowNearTray varsayılan sağ-alt konuma düşer.</summary>
    private static bool IsOnScreen(double left, double top)
    {
        var v = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var width = SystemParameters.VirtualScreenWidth;
        var height = SystemParameters.VirtualScreenHeight;
        return left >= v - 50 && left <= v + width + 50 && top >= vTop - 50 && top <= vTop + height + 50;
    }

    private void UpdatePinButtonVisual()
    {
        PinButton.ToolTip = _settings.PanelPinned ? "Sabitlemeyi kaldır" : "Sabitle";
        PinButton.Background = _settings.PanelPinned ? (Brush)FindResource("Accent") : Brushes.Transparent;
        PinButton.Foreground = _settings.PanelPinned ? (Brush)FindResource("OnAccent") : (Brush)FindResource("Muted");
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PanelPinned = !_settings.PanelPinned;
        _settings.Save();
        UpdatePinButtonVisual();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HidePanel();

    private void HidePanel()
    {
        CancelAllEdits();
        Hide();
        _lastHiddenAt = DateTime.Now;
        _showAll = false;
        _selectedDay = null;
        _authorFilter = null;
        _tagFilter = null;
        _selectedPlanId = null;
        _mode = Mode.Notes;
    }

    /// <summary>Açık düzenlemeleri kaydetmeden kapatır, böylece ardından çağrılan Reload() ertelenmez.</summary>
    private void CancelAllEdits()
    {
        foreach (var note in DayNotes)
            note.IsEditing = false;
        foreach (var task in Tasks)
            task.IsEditing = false;
        foreach (var group in MemoryGroups)
            foreach (var item in group.Items)
                item.IsEditing = false;
        foreach (var step in PlanDetailSteps)
            step.IsEditing = false;
        if (_planTitleEditing) CancelPlanTitleEdit();
        if (_planPathEditing) CancelPlanPathEdit();
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
            HeaderText.Text = _mode switch
            {
                Mode.Personal => "🔒 Kişisel",
                Mode.Memory => "🧠 Hafıza",
                Mode.Skills => "🧩 Yetenekler",
                Mode.Plans => "📋 Planlar",
                _ => "📝 Notlar",
            };
            TabsRow.Visibility = _mode == Mode.Personal ? Visibility.Collapsed : Visibility.Visible;
            SetTabStyle(NotesTabButton, _mode == Mode.Notes);
            SetTabStyle(MemoryTabButton, _mode == Mode.Memory);
            SetTabStyle(SkillsTabButton, _mode == Mode.Skills);
            SetTabStyle(PlansTabButton, _mode == Mode.Plans);

            NotesInputBorder.Visibility = _mode == Mode.Notes ? Visibility.Visible : Visibility.Collapsed;
            TaskInputBorder.Visibility = _mode == Mode.Personal ? Visibility.Visible : Visibility.Collapsed;
            MemoryInputPanel.Visibility = _mode == Mode.Memory ? Visibility.Visible : Visibility.Collapsed;
            SkillsHeaderPanel.Visibility = _mode == Mode.Skills ? Visibility.Visible : Visibility.Collapsed;
            TasksPanel.Visibility = _mode == Mode.Personal ? Visibility.Visible : Visibility.Collapsed;
            MemoryPanel.Visibility = _mode == Mode.Memory ? Visibility.Visible : Visibility.Collapsed;
            SkillsPanel.Visibility = _mode == Mode.Skills ? Visibility.Visible : Visibility.Collapsed;
            PlansPanel.Visibility = _mode == Mode.Plans ? Visibility.Visible : Visibility.Collapsed;

            ReloadTasks();
            ReloadMemory();
            if (_mode == Mode.Skills) EnsureSkillsLoaded(forceRescan: false);
            if (_mode == Mode.Plans) ReloadPlans();

            if (_mode != Mode.Notes)
            {
                AuthorFilterRow.Visibility = Visibility.Collapsed;
                FilterClearButton.Visibility = Visibility.Collapsed;
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
            FilterClearButton.Visibility = _authorFilter is not null || _tagFilter is not null
                ? Visibility.Visible : Visibility.Collapsed;

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

    private void SetTabStyle(Button button, bool selected)
    {
        button.Background = selected ? (Brush)FindResource("Accent") : Brushes.Transparent;
        button.Foreground = selected ? (Brush)FindResource("OnAccent") : (Brush)FindResource("Muted");
    }

    /// <summary>Hafıza kartlarını (arama metni varsa MemoryStore.Search ile süzülmüş) kategoriye göre
    /// gruplayıp yeniden yükler; kategori ComboBox'ının seçeneklerini de günceller.</summary>
    private void ReloadMemory()
    {
        var query = MemorySearchBox.Text.Trim();
        var all = query.Length > 0 ? _memoryStore.Search(query) : _memoryStore.List();

        MemoryGroups.Clear();
        foreach (var group in all.GroupBy(m => m.Category).OrderBy(g => g.Key, TrComparer))
        {
            var items = group.OrderByDescending(m => m.UpdatedAt).Select(m => new MemoryItemVm(m)).ToList();
            MemoryGroups.Add(new MemoryGroupVm(group.Key.ToUpper(TrTr), items));
        }
        MemoryEmptyText.Visibility = MemoryGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var categories = _memoryStore.Categories().ToList();
        if (!categories.Contains("genel", StringComparer.OrdinalIgnoreCase))
            categories.Insert(0, "genel");
        MemoryCategories.Clear();
        foreach (var category in categories.Order(TrComparer))
            MemoryCategories.Add(category);
    }

    public void ExportMarkdown() => Try(() =>
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = Path.Combine(documents, $"Notlar-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md");
        File.WriteAllText(path, MarkdownExporter.Export(_store.List(new NoteFilter())), new UTF8Encoding(false));
        Process.Start("explorer.exe", $"/select,\"{path}\"");
    });

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_settings.PanelPinned) HidePanel();
    }

    private void HeaderText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 3)
        {
            CancelAllEdits();
            if (_mode == Mode.Personal)
            {
                _mode = _previousMode;
            }
            else
            {
                _previousMode = _mode;
                _mode = Mode.Personal;
            }
            Reload();
            return;
        }

        if (e.ClickCount == 1 && _settings.PanelPinned)
        {
            try { DragMove(); } catch (InvalidOperationException) { return; }
            _settings.PanelLeft = Left;
            _settings.PanelTop = Top;
            _settings.Save();
        }
    }

    private void NotesTab_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Personal) return;
        CancelAllEdits();
        _mode = Mode.Notes;
        Reload();
    }

    private void MemoryTab_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Personal) return;
        CancelAllEdits();
        _mode = Mode.Memory;
        Reload();
    }

    private void SkillsTab_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Personal) return;
        CancelAllEdits();
        _mode = Mode.Skills;
        Reload();
    }

    private void PlansTab_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Personal) return;
        CancelAllEdits();
        _mode = Mode.Plans;
        Reload();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AnyEditing) return; // Düzenleme kutusu Enter/Esc'i kendisi işler.
        if (e.Key == Key.Escape)
        {
            if (SkillAddExpander.IsExpanded) { SkillAddExpander.IsExpanded = false; e.Handled = true; return; }
            if (MemoryAddExpander.IsExpanded) { MemoryAddExpander.IsExpanded = false; e.Handled = true; return; }
            if (PlanAddExpander.IsExpanded) { PlanAddExpander.IsExpanded = false; e.Handled = true; return; }
            if (PlanAddStepExpander.IsExpanded) { PlanAddStepExpander.IsExpanded = false; e.Handled = true; return; }
            if (_selectedPlanId is not null)
            {
                _selectedPlanId = null;
                Reload();
            }
            else if (_selectedDay is not null)
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
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is FrameworkElement { DataContext: MemoryItemVm memoryItem })
        {
            DeleteMemory(memoryItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is FrameworkElement { DataContext: PlanStepItemVm stepItem })
        {
            Try(() => { _planStore.DeleteStep(stepItem.Id); Reload(); });
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

    /// <summary>Takvim açılır penceresi dışarı tıklama ya da Esc ile kapandığında (tarih seçilmeden),
    /// DatePicker alanının kendisi de gizlenir.</summary>
    private void OtherDayPicker_CalendarClosed(object sender, RoutedEventArgs e)
    {
        if (OtherDayPicker.SelectedDate is null)
            OtherDayPicker.Visibility = Visibility.Collapsed;
    }

    private void OtherDayClose_Click(object sender, RoutedEventArgs e)
    {
        OtherDayPicker.IsDropDownOpen = false;
        OtherDayPicker.SelectedDate = null;
        OtherDayPicker.Visibility = Visibility.Collapsed;
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

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        CancelAllEdits();
        _authorFilter = null;
        _tagFilter = null;
        Reload();
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

    private void EditNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoteItem item }) return;
        item.EditText = item.Note.Text;
        item.IsEditing = true;
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

    private void CancelNoteEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NoteItem item }) CancelEdit(item);
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

    private void TaskEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskItemVm item }) return;
        item.EditText = item.Task.Title;
        item.IsEditing = true;
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

    private void CancelTaskEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskItemVm item }) CancelTaskEdit(item);
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
            UndoText.Text = "Kişisel kayıt silindi.";
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
            switch (_undoKind)
            {
                case UndoKind.Task: Try(() => { _taskStore.Restore(id); Reload(); }); break;
                case UndoKind.Memory: Try(() => { _memoryStore.Restore(id); Reload(); }); break;
                case UndoKind.Skill: Try(() => { _skillRegistry.Restore(id); RefreshSkillRegistryCache(); }); break;
                case UndoKind.Plan: Try(() => { _planStore.Restore(id); Reload(); }); break;
                default: Try(() => { _store.Restore(id); Reload(); }); break;
            }
        }
        HideUndo();
    }

    private void MemorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_mode == Mode.Memory) Try(ReloadMemory);
    }

    private void MemorySearchClear_Click(object sender, RoutedEventArgs e) => MemorySearchBox.Clear();

    private void MemoryAddExpander_Expanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => MemoryTitleBox.Focus(), DispatcherPriority.Input);

    private void MemoryAddClose_Click(object sender, RoutedEventArgs e) => MemoryAddExpander.IsExpanded = false;

    private void MemoryAdd_Click(object sender, RoutedEventArgs e)
    {
        var title = MemoryTitleBox.Text.Trim();
        var content = MemoryContentBox.Text.Trim();
        if (title.Length == 0 || content.Length == 0) return;
        var category = MemoryCategoryBox.Text;
        Try(() =>
        {
            _memoryStore.Add(title, content, category);
            MemoryTitleBox.Clear();
            MemoryContentBox.Clear();
            MemoryCategoryBox.Text = "genel";
            MemoryAddExpander.IsExpanded = false;
            Reload();
        });
    }

    private void MemoryRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ((UIElement)sender).Focus();

    private void MemoryEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MemoryItemVm item }) return;
        item.EditTitle = item.Title;
        item.EditContent = item.Content;
        item.IsEditing = true;
    }

    private void MemoryTitleEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: MemoryItemVm item }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveMemoryEdit(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelMemoryEdit(item);
        }
    }

    private void MemoryContentEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: MemoryItemVm item }) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveMemoryEdit(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelMemoryEdit(item);
        }
    }

    private void SaveMemoryEdit(MemoryItemVm item)
    {
        var title = item.EditTitle.Trim();
        var content = item.EditContent.Trim();
        item.IsEditing = false;
        if (title.Length > 0 && content.Length > 0 && (title != item.Title || content != item.Content))
            Try(() => _memoryStore.Update(item.Id, title, content));
        Reload();
    }

    private void CancelMemoryEdit(MemoryItemVm item)
    {
        item.IsEditing = false;
        if (_reloadPending) Reload();
    }

    private void CancelMemoryEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemoryItemVm item }) CancelMemoryEdit(item);
    }

    private void MemoryDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemoryItemVm item })
            DeleteMemory(item);
    }

    private void DeleteMemory(MemoryItemVm item)
    {
        CancelAllEdits();
        Try(() =>
        {
            _memoryStore.Delete(item.Id);
            _undoId = item.Id;
            _undoKind = UndoKind.Memory;
            UndoText.Text = "Hafıza kaydı silindi.";
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            Reload();
        });
    }

    private void HideUndo()
    {
        _undoTimer.Stop();
        _undoId = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    private void UndoClose_Click(object sender, RoutedEventArgs e) => HideUndo();

    private void ErrorClose_Click(object sender, RoutedEventArgs e)
    {
        _errorTimer.Stop();
        ErrorBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Yetenek kataloğunu (gerekirse) UI dışı bir iş parçacığında kurar: tarama + kullanım
    /// sayımı + kayıt listesi oluşturma pahalı olabileceğinden (ilk kullanım taraması ~5 sn, sonraki
    /// taramalar dosya önbelleği sayesinde ~0.2 sn) yalnızca ilk açılışta ya da "↻ Yenile" ile
    /// yeniden taranır; aradaki sekme değişimlerinde bellekteki sonuç kullanılır.</summary>
    private void EnsureSkillsLoaded(bool forceRescan)
    {
        if (!forceRescan && (_skillInstalledCache is not null || _skillLoadRequested))
        {
            RenderSkills();
            return;
        }
        _skillLoadRequested = true;
        _skillLoading = true;
        RenderSkills();

        var registry = _skillRegistry;
        var usageCache = _skillUsageCache;
        var translations = _skillTranslations;
        Task.Run(() =>
        {
            var installed = ClaudeSkillScanner.ScanInstalled();
            var usage = usageCache.GetUsage();
            var regList = registry.List();
            var trList = translations.All();
            var marketplace = MarketplaceScanner.ScanAvailable();
            return (installed, usage, regList, trList, marketplace);
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            _skillLoading = false;
            if (t.IsFaulted)
            {
                AppLog.Write(t.Exception);
                RenderSkills();
                return;
            }
            var (installed, usage, regList, trList, marketplace) = t.Result;
            _skillInstalledCache = installed;
            _skillUsageDataCache = usage;
            _skillRegistryCache = regList;
            _skillTranslationsCache = trList;
            _marketplaceCache = marketplace;
            RenderSkills();
        }), TaskScheduler.Default);
    }

    /// <summary>Yalnızca kayıt tablosunu (ucuz bir SQLite sorgusu) yeniden okuyup görünümü tazeler;
    /// kurulu skill taraması ve kullanım sayımı yeniden yapılmaz.</summary>
    private void RefreshSkillRegistryCache()
    {
        _skillRegistryCache = _skillRegistry.List();
        RenderSkills();
    }

    private void RenderSkills()
    {
        SkillsLoadingText.Visibility = _skillLoading ? Visibility.Visible : Visibility.Collapsed;
        if (_skillLoading)
        {
            SkillGroups.Clear();
            MarketplaceGroups.Clear();
            SkillsEmptyText.Visibility = Visibility.Collapsed;
            DiscoverEmptyText.Visibility = Visibility.Collapsed;
            DiscoverHintText.Visibility = Visibility.Collapsed;
            return;
        }

        SetSkillViewToggleStyle();
        InstalledSkillsPanel.Visibility = _skillShowDiscover ? Visibility.Collapsed : Visibility.Visible;
        DiscoverPanel.Visibility = _skillShowDiscover ? Visibility.Visible : Visibility.Collapsed;
        SkillSortRow.Visibility = _skillShowDiscover ? Visibility.Collapsed : Visibility.Visible;

        if (_skillShowDiscover)
        {
            RenderDiscover();
            return;
        }

        var installed = _skillInstalledCache ?? [];
        var usage = _skillUsageDataCache ?? new Dictionary<string, SkillUsage>();
        var registry = _skillRegistryCache ?? [];
        var translations = _skillTranslationsCache ?? new Dictionary<string, SkillTranslation>();
        var catalog = SkillCatalog.Build(installed, usage, registry, translations);

        var query = _skillSearch.Trim();
        var filtered = query.Length == 0
            ? catalog
            : catalog.Where(e => TrContains(e.Name, query) || TrContains(e.Description, query) || TrContains(e.DescriptionTr, query)).ToList();

        SkillGroups.Clear();
        foreach (var group in filtered.GroupBy(e => e.Ai).OrderBy(g => g.Key, TrComparer))
        {
            var orderedItems = _skillSortByUsage
                ? group.OrderByDescending(e => e.UsageCount).ThenBy(e => e.Name, TrComparer)
                : group.OrderBy(e => e.Name, TrComparer);
            var items = orderedItems.Select(e => ToSkillItemVm(e, registry)).ToList();
            var totalUsage = group.Sum(e => e.UsageCount);
            var aiColor = AuthorColors.For(group.Key, _settings) ?? (Brush)FindResource("Fg");
            SkillGroups.Add(new SkillGroupVm(group.Key, aiColor, $"{group.Count()} yetenek · {totalUsage} kullanım", items));
        }
        SkillsEmptyText.Visibility = SkillGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SetSkillSortStyle();
        RebuildSkillAiOptions(registry);
    }

    private static SkillItemVm ToSkillItemVm(SkillEntry e, IReadOnlyList<RegistrySkill> registry)
    {
        var canDelete = e.Source == SkillCatalog.RegisteredSource;
        var registryId = canDelete
            ? registry.FirstOrDefault(r => string.Equals(r.Ai, e.Ai, StringComparison.Ordinal)
                                            && string.Equals(r.Name, e.Name, StringComparison.Ordinal))?.Id
            : null;
        var usageLabel = e.UsageCount > 0 ? $"{e.UsageCount} kez" : "kullanılmadı";
        var lastUsedLabel = e.LastUsed is { } lastUsed ? $"son: {lastUsed.ToString("d MMMM", TrTr)}" : "";
        var displayDescription = string.IsNullOrWhiteSpace(e.DescriptionTr) ? e.Description : e.DescriptionTr!;
        return new SkillItemVm(e.Id, e.Ai, e.Name, displayDescription, e.Description, e.Source, usageLabel, lastUsedLabel,
            e.UsageCount == 0, canDelete, registryId);
    }

    /// <summary>"Keşfet" görünümünü (Kurulu/Keşfet geçişiyle açılan marketplace plugin listesi) kurulu
    /// durum ve Türkçe açıklamalarla birlikte oluşturur.</summary>
    private void RenderDiscover()
    {
        var plugins = _marketplaceCache ?? [];
        var translations = _skillTranslationsCache ?? new Dictionary<string, SkillTranslation>();

        DiscoverHintText.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var query = _skillSearch.Trim();
        var filtered = query.Length == 0
            ? plugins
            : plugins.Where(p => TrContains(p.Name, query) || TrContains(p.Description, query) || TrContains(p.Marketplace, query)).ToList();

        MarketplaceGroups.Clear();
        foreach (var group in filtered.GroupBy(p => p.Marketplace).OrderBy(g => g.Key, TrComparer))
        {
            var items = group.OrderBy(p => p.Installed ? 0 : 1).ThenBy(p => p.Name, TrComparer)
                .Select(p =>
                {
                    translations.TryGetValue($"plugin:{p.Marketplace}/{p.Name}", out var tr);
                    var description = string.IsNullOrWhiteSpace(tr?.DescriptionTr) ? p.Description : tr!.DescriptionTr;
                    var vm = new MarketplacePluginVm(p.Marketplace, p.Name, description, p.Skills) { Installed = p.Installed };
                    return vm;
                }).ToList();
            MarketplaceGroups.Add(new MarketplaceGroupVm(group.Key, items));
        }
        DiscoverEmptyText.Visibility = plugins.Count > 0 && MarketplaceGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>tr-TR, büyük/küçük harfe duyarsız "içerir" araması.</summary>
    private static bool TrContains(string? haystack, string needle) =>
        TrTr.CompareInfo.IndexOf(haystack ?? "", needle, CompareOptions.IgnoreCase) >= 0;

    private void RebuildSkillAiOptions(IReadOnlyList<RegistrySkill> registry)
    {
        var names = new List<string>();
        if (_settings.AuthorColors is not null)
            names.AddRange(_settings.AuthorColors.Keys);
        foreach (var n in new[] { "ChatGPT", "Gemini" })
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        foreach (var r in registry)
            if (!names.Contains(r.Ai, StringComparer.OrdinalIgnoreCase)) names.Add(r.Ai);

        var ordered = names
            .Where(n => !string.Equals(n, "Ben", StringComparison.OrdinalIgnoreCase))
            .Order(TrComparer)
            .ToList();

        if (SkillAiOptions.SequenceEqual(ordered)) return;
        SkillAiOptions.Clear();
        foreach (var n in ordered) SkillAiOptions.Add(n);
    }

    private void SetSkillSortStyle()
    {
        SetTabStyle(SkillSortUsageButton, _skillSortByUsage);
        SetTabStyle(SkillSortNameButton, !_skillSortByUsage);
    }

    private void SkillSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _skillSearch = SkillSearchBox.Text;
        if (_mode == Mode.Skills) RenderSkills();
    }

    private void SkillSearchClear_Click(object sender, RoutedEventArgs e) => SkillSearchBox.Clear();

    private void SkillSortUsage_Click(object sender, RoutedEventArgs e)
    {
        _skillSortByUsage = true;
        RenderSkills();
    }

    private void SkillSortName_Click(object sender, RoutedEventArgs e)
    {
        _skillSortByUsage = false;
        RenderSkills();
    }

    private void SkillRefresh_Click(object sender, RoutedEventArgs e) => EnsureSkillsLoaded(forceRescan: true);

    private void SkillAddClose_Click(object sender, RoutedEventArgs e) => SkillAddExpander.IsExpanded = false;

    private void SkillAddExpander_Expanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => SkillAiBox.Focus(), DispatcherPriority.Input);

    private void SkillStatusClose_Click(object sender, RoutedEventArgs e)
    {
        _skillStatusTimer.Stop();
        SkillStatusRow.Visibility = Visibility.Collapsed;
    }

    private void SetSkillViewToggleStyle()
    {
        SetTabStyle(SkillShowInstalledButton, !_skillShowDiscover);
        SetTabStyle(SkillShowDiscoverButton, _skillShowDiscover);
    }

    private void SkillShowInstalled_Click(object sender, RoutedEventArgs e)
    {
        _skillShowDiscover = false;
        RenderSkills();
    }

    private void SkillShowDiscover_Click(object sender, RoutedEventArgs e)
    {
        _skillShowDiscover = true;
        RenderSkills();
    }

    /// <summary>"Kur"/"Kaldır" düğmesi: kullanıcıdan onay alır, ardından `claude plugin install|uninstall`
    /// komutunu UI dışı bir iş parçacığında çalıştırır. Hiçbir komut, açık onaydan önce çalıştırılmaz.</summary>
    private void MarketplaceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MarketplacePluginVm plugin } || plugin.Busy) return;

        var verb = plugin.Installed ? "kaldırmak" : "kurmak";
        var question = $"\"{plugin.Name}\" eklentisini ({plugin.Marketplace}) {verb} istediğinize emin misiniz?";
        if (MessageBox.Show(question, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        plugin.Busy = true;
        var args = plugin.Installed
            ? $"plugin uninstall \"{plugin.PluginId}\" -s user -y"
            : $"plugin install \"{plugin.PluginId}\" -s user -y";
        var wasInstalled = plugin.Installed;

        Task.Run(() => RunClaudeCli(args)).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            plugin.Busy = false;
            if (t.IsFaulted)
            {
                AppLog.Write(t.Exception);
                ShowError("Komut çalıştırılamadı, ayrıntı: " + AppPaths.LogPath);
                return;
            }
            var (success, stdout, stderr) = t.Result;
            if (success)
            {
                EnsureSkillsLoaded(forceRescan: true);
                ShowSkillStatus(wasInstalled
                    ? "Kaldırıldı."
                    : "Kuruldu. Yeni Claude Code oturumlarında kullanılabilir.");
            }
            else
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                ShowError(string.IsNullOrWhiteSpace(detail) ? "Komut başarısız oldu." : detail.Trim());
            }
        }), TaskScheduler.Default);
    }

    /// <summary>"claude" CLI'yi verilen argümanlarla UI dışı bir iş parçacığında çalıştırır ve
    /// çıkış kodu + yakalanan stdout/stderr ile döner. "claude" PATH'te bulunamazsa başarısız sayılır.</summary>
    private static (bool Success, string Stdout, string Stderr) RunClaudeCli(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return (false, "", "'claude' komutu başlatılamadı.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return (process.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private void ShowSkillStatus(string text)
    {
        SkillStatusText.Text = text;
        SkillStatusRow.Visibility = Visibility.Visible;
        _skillStatusTimer.Stop();
        _skillStatusTimer.Start();
    }

    private void ShowError(string message)
    {
        if (!IsVisible) return;
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    private void SkillAdd_Click(object sender, RoutedEventArgs e)
    {
        var ai = SkillAiBox.Text.Trim();
        var name = SkillNameBox.Text.Trim();
        if (ai.Length == 0 || name.Length == 0) return;
        var description = SkillDescriptionBox.Text.Trim();
        Try(() =>
        {
            _skillRegistry.Upsert(ai, name, description);
            SkillAiBox.Text = "";
            SkillNameBox.Clear();
            SkillDescriptionBox.Clear();
            RefreshSkillRegistryCache();
        });
    }

    private void SkillDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkillItemVm item })
            DeleteSkill(item);
    }

    private void DeleteSkill(SkillItemVm item)
    {
        if (item.RegistryId is not { } id) return;
        Try(() =>
        {
            _skillRegistry.Delete(id);
            _undoId = id;
            _undoKind = UndoKind.Skill;
            UndoText.Text = "Yetenek silindi.";
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            RefreshSkillRegistryCache();
        });
    }

    /// <summary>Planlar listesini (ya da seçili bir plan varsa detay görünümünü) yeniden yükler.</summary>
    private void ReloadPlans()
    {
        if (_selectedPlanId is { } id)
        {
            RenderPlanDetail(id);
            return;
        }

        PlanDetailPanel.Visibility = Visibility.Collapsed;
        PlansListPanel.Visibility = Visibility.Visible;
        _currentPlan = null;

        var plans = _planStore.ListPlans();
        Plans.Clear();
        foreach (var plan in plans)
        {
            var (_, steps) = _planStore.GetPlan(plan.Id);
            var done = steps.Count(s => s.Status is StepStatus.Bitti or StepStatus.Atlandi);
            Plans.Add(new PlanListItemVm(plan.Id, plan.Title, plan.ProjectPath, ShortenPath(plan.ProjectPath),
                PlanStatusLabel(plan.Status), PlanStatusBrush(plan.Status), $"{done}/{steps.Count} adım"));
        }
        PlansEmptyText.Visibility = Plans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Bir plan kartına tıklayınca detay görünümüne geçer, "📁 Başka klasör"/adım düzenleme
    /// gibi satır içi düzenleme durumları koruyarak yeniden çizer (ör. başlık düzenlenirken arka planda
    /// değişiklik sinyali gelirse).</summary>
    private void RenderPlanDetail(long id)
    {
        Plan plan;
        IReadOnlyList<PlanStep> steps;
        try
        {
            (plan, steps) = _planStore.GetPlan(id);
        }
        catch (PlanNotFoundException)
        {
            _selectedPlanId = null;
            ReloadPlans();
            return;
        }

        _currentPlan = plan;
        PlansListPanel.Visibility = Visibility.Collapsed;
        PlanDetailPanel.Visibility = Visibility.Visible;

        if (!_planTitleEditing)
            PlanDetailTitleText.Text = plan.Title;
        if (!_planPathEditing)
        {
            PlanDetailPathText.Text = plan.ProjectPath;
            PlanDetailPathText.ToolTip = plan.ProjectPath;
        }

        PlanDetailTitleText.Visibility = _planTitleEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanTitleEditButton.Visibility = _planTitleEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanTitleEditRow.Visibility = _planTitleEditing ? Visibility.Visible : Visibility.Collapsed;

        PlanDetailPathText.Visibility = _planPathEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanPathEditButton.Visibility = _planPathEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanPathEditRow.Visibility = _planPathEditing ? Visibility.Visible : Visibility.Collapsed;

        PlanDetailSteps.Clear();
        foreach (var step in steps)
            PlanDetailSteps.Add(new PlanStepItemVm(step));
    }

    private static string PlanStatusLabel(PlanStatus status) => status switch
    {
        PlanStatus.Taslak => "Taslak",
        PlanStatus.Calisiyor => "Çalışıyor",
        PlanStatus.Bitti => "Bitti",
        PlanStatus.Hata => "Hata",
        _ => "",
    };

    private Brush PlanStatusBrush(PlanStatus status) => status switch
    {
        PlanStatus.Taslak => (Brush)FindResource("Muted"),
        PlanStatus.Calisiyor => (Brush)FindResource("Accent"),
        PlanStatus.Bitti => (Brush)FindResource("Success"),
        PlanStatus.Hata => (Brush)FindResource("Danger"),
        _ => (Brush)FindResource("Muted"),
    };

    /// <summary>Uzun proje yollarını "…\üstKlasör\klasör" biçiminde kısaltır; tam yol ToolTip'te gösterilir.</summary>
    private static string ShortenPath(string path)
    {
        const int maxLength = 42;
        if (path.Length <= maxLength) return path;
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        var parentName = string.IsNullOrEmpty(parent) ? null : Path.GetFileName(parent);
        return parentName is null ? "…" + name : $"…\\{parentName}\\{name}";
    }

    private void PlanRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlanListItemVm vm }) return;
        CancelAllEdits();
        _selectedPlanId = vm.Id;
        Reload();
    }

    private void PlanBackButton_Click(object sender, RoutedEventArgs e)
    {
        CancelAllEdits();
        _selectedPlanId = null;
        Reload();
    }

    private void PlanAddExpander_Expanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => PlanTitleBox.Focus(), DispatcherPriority.Input);

    private void PlanAddClose_Click(object sender, RoutedEventArgs e) => PlanAddExpander.IsExpanded = false;

    /// <summary>Panel Topmost olduğundan, WinForms klasör seçici için panelin kendi pencere tutamacını
    /// sahip (owner) olarak verir; aksi halde dialog panelin arkasında kalabilir.</summary>
    private sealed class Win32Window(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private Forms.IWin32Window DialogOwner => new Win32Window(new System.Windows.Interop.WindowInteropHelper(this).Handle);

    private void PlanPathBrowseNew_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        var current = PlanPathBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(DialogOwner) == Forms.DialogResult.OK)
            PlanPathBox.Text = dialog.SelectedPath;
    }

    private void PlanCreate_Click(object sender, RoutedEventArgs e)
    {
        var title = PlanTitleBox.Text.Trim();
        var path = PlanPathBox.Text.Trim();
        var steps = PlanStepsBox.Text.Replace("\r\n", "\n").Split('\n')
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (title.Length == 0 || path.Length == 0 || steps.Count == 0) return;
        Try(() =>
        {
            _planStore.CreatePlan(title, path, steps);
            PlanTitleBox.Clear();
            PlanPathBox.Clear();
            PlanStepsBox.Clear();
            PlanAddExpander.IsExpanded = false;
            Reload();
        });
    }

    private void PlanRunRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanListItemVm vm }) RunPlan(vm.Id);
    }

    private void PlanResetRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanListItemVm vm })
            Try(() => { _planStore.ResetPlan(vm.Id); Reload(); });
    }

    private void PlanDeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanListItemVm vm }) DeletePlan(vm.Id);
    }

    private void DeletePlan(long id)
    {
        Try(() =>
        {
            _planStore.Delete(id);
            _undoId = id;
            _undoKind = UndoKind.Plan;
            UndoText.Text = "Plan silindi.";
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            if (_selectedPlanId == id) _selectedPlanId = null;
            Reload();
        });
    }

    private void PlanRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is { } plan) RunPlan(plan.Id);
    }

    /// <summary>▶ Çalıştır: önce 'claude' komutunun PATH'te olup olmadığını kontrol eder, sonra kullanıcıdan
    /// açık onay alır; onaysız hiçbir şey başlatılmaz.</summary>
    private void RunPlan(long id)
    {
        if (!PlanLauncher.IsClaudeOnPath())
        {
            ShowError("'claude' komutu PATH üzerinde bulunamadı. Claude Code'un kurulu ve PATH'e eklenmiş olduğundan emin olun.");
            return;
        }

        Plan plan;
        try
        {
            (plan, _) = _planStore.GetPlan(id);
        }
        catch (PlanNotFoundException)
        {
            Reload();
            return;
        }

        var question = $"Claude Code bu klasörde açılıp planı uygulamaya başlayacak: {plan.ProjectPath}. Devam edilsin mi?";
        if (MessageBox.Show(question, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Try(() => PlanLauncher.Launch(plan));
    }

    private void PlanTitleEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan) return;
        _planTitleEditing = true;
        PlanDetailTitleBox.Text = plan.Title;
        PlanDetailTitleText.Visibility = Visibility.Collapsed;
        PlanTitleEditButton.Visibility = Visibility.Collapsed;
        PlanTitleEditRow.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() => { PlanDetailTitleBox.Focus(); PlanDetailTitleBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void CancelPlanTitleEdit()
    {
        _planTitleEditing = false;
        PlanDetailTitleText.Visibility = Visibility.Visible;
        PlanTitleEditButton.Visibility = Visibility.Visible;
        PlanTitleEditRow.Visibility = Visibility.Collapsed;
        if (_reloadPending) Reload();
    }

    private void PlanTitleEditCancel_Click(object sender, RoutedEventArgs e) => CancelPlanTitleEdit();

    private void PlanTitleEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SavePlanTitleEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelPlanTitleEdit();
        }
    }

    private void SavePlanTitleEdit()
    {
        if (_currentPlan is not { } plan) { CancelPlanTitleEdit(); return; }
        var text = PlanDetailTitleBox.Text.Trim();
        _planTitleEditing = false;
        PlanDetailTitleText.Visibility = Visibility.Visible;
        PlanTitleEditButton.Visibility = Visibility.Visible;
        PlanTitleEditRow.Visibility = Visibility.Collapsed;
        if (text.Length > 0 && text != plan.Title)
            Try(() => _planStore.UpdatePlan(plan.Id, title: text));
        Reload();
    }

    private void PlanPathEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan) return;
        _planPathEditing = true;
        PlanDetailPathBox.Text = plan.ProjectPath;
        PlanDetailPathText.Visibility = Visibility.Collapsed;
        PlanPathEditButton.Visibility = Visibility.Collapsed;
        PlanPathEditRow.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() => { PlanDetailPathBox.Focus(); PlanDetailPathBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void CancelPlanPathEdit()
    {
        _planPathEditing = false;
        PlanDetailPathText.Visibility = Visibility.Visible;
        PlanPathEditButton.Visibility = Visibility.Visible;
        PlanPathEditRow.Visibility = Visibility.Collapsed;
        if (_reloadPending) Reload();
    }

    private void PlanPathEditCancel_Click(object sender, RoutedEventArgs e) => CancelPlanPathEdit();

    private void PlanPathEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SavePlanPathEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelPlanPathEdit();
        }
    }

    private void SavePlanPathEdit()
    {
        if (_currentPlan is not { } plan) { CancelPlanPathEdit(); return; }
        var text = PlanDetailPathBox.Text.Trim();
        _planPathEditing = false;
        PlanDetailPathText.Visibility = Visibility.Visible;
        PlanPathEditButton.Visibility = Visibility.Visible;
        PlanPathEditRow.Visibility = Visibility.Collapsed;
        if (text.Length > 0 && text != plan.ProjectPath)
            Try(() => _planStore.UpdatePlan(plan.Id, projectPath: text));
        Reload();
    }

    private void PlanPathBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan) return;
        using var dialog = new Forms.FolderBrowserDialog();
        if (Directory.Exists(plan.ProjectPath)) dialog.SelectedPath = plan.ProjectPath;
        if (dialog.ShowDialog(DialogOwner) != Forms.DialogResult.OK) return;
        Try(() =>
        {
            _planStore.UpdatePlan(plan.Id, projectPath: dialog.SelectedPath);
            _planPathEditing = false;
            Reload();
        });
    }

    private void PlanStepUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanStepItemVm item })
            Try(() => { _planStore.MoveStep(item.Id, item.Order - 1); Reload(); });
    }

    private void PlanStepDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanStepItemVm item })
            Try(() => { _planStore.MoveStep(item.Id, item.Order + 1); Reload(); });
    }

    private void PlanStepEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlanStepItemVm item }) return;
        item.EditText = item.Text;
        item.IsEditing = true;
    }

    private void PlanStepEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PlanStepItemVm item }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = item.EditText.Trim();
            item.IsEditing = false;
            if (text.Length > 0 && text != item.Text)
                Try(() => _planStore.UpdateStep(item.Id, text));
            Reload();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelPlanStepEdit(item);
        }
    }

    private void CancelPlanStepEdit(PlanStepItemVm item)
    {
        item.IsEditing = false;
        if (_reloadPending) Reload();
    }

    private void PlanStepEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanStepItemVm item }) CancelPlanStepEdit(item);
    }

    private void PlanStepDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanStepItemVm item })
            Try(() => { _planStore.DeleteStep(item.Id); Reload(); });
    }

    private void PlanAddStepExpander_Expanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => PlanNewStepBox.Focus(), DispatcherPriority.Input);

    private void PlanAddStepClose_Click(object sender, RoutedEventArgs e) => PlanAddStepExpander.IsExpanded = false;

    private void PlanAddStep_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan) return;
        var text = PlanNewStepBox.Text.Trim();
        if (text.Length == 0) return;
        Try(() =>
        {
            _planStore.AddStep(plan.Id, text);
            PlanNewStepBox.Clear();
            PlanAddStepExpander.IsExpanded = false;
            Reload();
        });
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
                NoteNotFoundException or TaskNotFoundException or PlanNotFoundException
                    or PlanStepNotFoundException or ArgumentException => ex.Message,
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
