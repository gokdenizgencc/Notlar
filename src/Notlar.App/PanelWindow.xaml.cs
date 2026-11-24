using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    private enum UndoKind { Note, Task, Memory, Skill, Plan, Project }

    /// <summary>Panelin görünür modu: normal not günlüğü, hafıza kartları, yetenek kataloğu, planlar, panel içi
    /// Kayıtlar ve ⚙ Ayarlar görünümleri ya da üst çubuktaki bölüm adına üçlü tıklamayla açılan gizli kişisel liste.</summary>
    private enum Mode { Notes, Memory, Skills, Plans, Recordings, Settings, Personal }

    private static readonly CultureInfo TrTr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly NoteStore _store;
    private readonly TaskStore _taskStore;
    private readonly MemoryStore _memoryStore;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillTranslations _skillTranslations;
    private readonly PlanStore _planStore;
    private readonly ProjectStore _projectStore;
    private readonly AppFlags? _appFlags;
    private readonly RecordingController _recordings;
    private bool _kisiselOkumaSyncing;
    /// <summary>Panelin açtığı bir onay/uyarı penceresi sürerken (ör. kayıt başlatma) odak kaybında gizlenmeyi engeller.</summary>
    private bool _suppressAutoHide;
    /// <summary>Ayar değişikliği büyük olasılıkla Ayarlar görünümünün kendisinden geldiğinde, görünümü yeniden yükleyip
    /// kullanıcının o an etkileştiği denetimi sıfırlamamak için.</summary>
    private bool _skipSettingsViewReload;
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
    private bool _dragging;
    private bool _fullScreen;
    private readonly List<DayWindow> _dayWindows = [];

    private bool _selectMode;
    private readonly HashSet<long> _selectedNoteIds = [];

    private long? _selectedPlanId;
    private Plan? _currentPlan;
    private bool _planTitleEditing;
    private bool _planProjectEditing;
    private bool _plansShowProjects;
    private string? _planNewFolderOverride;
    private readonly List<long> _planNewProjectIds = [];

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
    private PluginSort _discoverSort = PluginSort.Popular;
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
    public ObservableCollection<ProjectItemVm> Projects { get; } = [];
    public ObservableCollection<ProjectComboItem> PlanProjectOptions { get; } = [];
    public ObservableCollection<PlanProjectChipVm> PlanNewProjectChips { get; } = [];
    public ObservableCollection<PlanProjectChipVm> PlanDetailProjectChips { get; } = [];
    public ObservableCollection<PlanStepProjectOption> PlanStepProjectOptions { get; } = [];

    public PanelWindow(NoteStore store, TaskStore taskStore, MemoryStore memoryStore, SkillRegistry skillRegistry,
        SkillTranslations skillTranslations, PlanStore planStore, ProjectStore projectStore, AppFlags appFlags,
        RecordingController recordings)
    {
        _recordings = recordings;
        _store = store;
        _taskStore = taskStore;
        _memoryStore = memoryStore;
        _skillRegistry = skillRegistry;
        _skillTranslations = skillTranslations;
        _planStore = planStore;
        _projectStore = projectStore;
        _appFlags = appFlags;
        _settings = AppSettings.Load();
        _discoverSort = _settings.DiscoverSort;
        InitializeComponent();
        DataContext = this;
        _undoTimer.Tick += (_, _) => HideUndo();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); ErrorBar.Visibility = Visibility.Collapsed; };
        _skillStatusTimer.Tick += (_, _) => { _skillStatusTimer.Stop(); SkillStatusRow.Visibility = Visibility.Collapsed; };
        AppSettings.Changed += OnSettingsChanged;

        Rail.SectionRequested += NavigateTo;
        RecordingsViewHost.Initialize(recordings, planStore, projectStore);
        RecordingsViewHost.Wide = _settings.PanelLarge;
        // Olaylar Initialize'dan önce bağlanır: Ayarlar görünümü "Büyük mod" anahtarını yalnızca bağlıysa gösterir.
        SettingsViewHost.PinToggleRequested += TogglePinned;
        SettingsViewHost.LargeModeToggleRequested += ToggleSize;
        SettingsViewHost.Initialize(_settings, appFlags);
        RecordButton.Attach(recordings);
        RecordButton.ToggleRequested += ToggleRecording;

        UpdatePinButtonVisual();
        UpdateFullScreenButtonVisual();
        InitializeGeneralBehavior();
    }

    /// <summary>Paneli gösterir, Kayıtlar bölümüne geçer ve <paramref name="selectId"/> verilmişse o kaydı seçer
    /// (tepsi "Kayıtlar…" menüsü ve kayıt balonu tıklamaları buradan gelir).</summary>
    public void ShowRecordings(long? selectId)
    {
        CancelAllEdits();
        ResetSelectMode();
        _mode = Mode.Recordings;
        if (IsVisible)
        {
            ReloadCore();
            Activate();
        }
        else
        {
            ShowNearTray();
        }
        if (selectId is { } id) RecordingsViewHost.Select(id);
    }

    /// <summary>Global kısayol "Paneli aç/kapat": panel önde ve etkinse gizler; açık ama arkada kaldıysa öne getirir
    /// (sabitlenmiş panel başka pencerelerin altında kalabilir); kapalıysa açar.</summary>
    public void ToggleFromHotkey()
    {
        if (IsVisible && IsActive) HidePanel();
        else if (IsVisible) Activate();
        else ShowNearTray();
    }

    /// <summary>Global kısayol "Hızlı not": paneli Notlar bölümünde açar ve not kutusuna odaklanır.</summary>
    public void ShowQuickNote()
    {
        if (_mode != Mode.Notes)
        {
            CancelAllEdits();
            ResetSelectMode();
            _mode = Mode.Notes;
            if (IsVisible) ReloadCore();
        }
        if (IsVisible) Activate();
        else ShowNearTray();
        InputBox.Focus();
    }

    /// <summary>Sağ alttaki kayıt düğmesi: kayıt sürüyorsa durdurur, yoksa başlatır. İlk kayıtta gösterilen onay
    /// penceresi panelin odağını alacağından, bu sürede panel kendiliğinden gizlenmez.</summary>
    private void ToggleRecording()
    {
        if (_recordings.IsRecording)
        {
            _recordings.Stop();
            return;
        }
        _suppressAutoHide = true;
        try
        {
            _recordings.Start(owner: this);
        }
        finally
        {
            _suppressAutoHide = false;
        }
    }

    /// <summary>Sol şerit / Ctrl+1…5: ilgili bölüme geçer. Kişisel bölümdeyken (şerit gizli) yok sayılır.</summary>
    private void NavigateTo(PanelSection section)
    {
        if (_mode == Mode.Personal) return;
        var target = section switch
        {
            PanelSection.Memory => Mode.Memory,
            PanelSection.Plans => Mode.Plans,
            PanelSection.Recordings => Mode.Recordings,
            PanelSection.Skills => Mode.Skills,
            PanelSection.Settings => Mode.Settings,
            _ => Mode.Notes,
        };
        CancelAllEdits();
        _mode = target;
        Reload();
        switch (_mode)
        {
            case Mode.Notes: InputBox.Focus(); break;
            case Mode.Memory: MemorySearchBox.Focus(); break;
            case Mode.Skills: SkillSearchBox.Focus(); break;
        }
    }

    private static PanelSection? SectionOf(Mode mode) => mode switch
    {
        Mode.Notes => PanelSection.Notes,
        Mode.Memory => PanelSection.Memory,
        Mode.Plans => PanelSection.Plans,
        Mode.Recordings => PanelSection.Recordings,
        Mode.Skills => PanelSection.Skills,
        Mode.Settings => PanelSection.Settings,
        _ => null,
    };

    private bool AnyEditing => DayNotes.Any(n => n.IsEditing) || Tasks.Any(t => t.IsEditing)
        || MemoryGroups.Any(g => g.Items.Any(m => m.IsEditing))
        || PlanDetailSteps.Any(s => s.IsEditing) || _planTitleEditing || _planProjectEditing
        || Projects.Any(p => p.IsEditing);

    private void OnSettingsChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _settings = AppSettings.Load();
            UpdatePinButtonVisual();
            if (!IsVisible) return;
            _skipSettingsViewReload = true;
            try
            {
                ReloadCore();
            }
            finally
            {
                _skipSettingsViewReload = false;
            }
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
        ApplyWindowMode();
        if (!_settings.PanelLarge)
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
        }
        Show();
        ReloadCore();
        Activate();
        switch (_mode)
        {
            case Mode.Personal: TaskInputBox.Focus(); break;
            case Mode.Memory: MemorySearchBox.Focus(); break;
            case Mode.Skills: SkillSearchBox.Focus(); break;
            case Mode.Plans or Mode.Recordings or Mode.Settings: break;
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

    private void PinButton_Click(object sender, RoutedEventArgs e) => TogglePinned();

    /// <summary>📌 düğmesi ve Ayarlar'daki "Paneli sabitle" anahtarı aynı yoldan geçer.</summary>
    private void TogglePinned()
    {
        _settings.PanelPinned = !_settings.PanelPinned;
        _settings.Save();
        UpdatePinButtonVisual();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HidePanel();

    private void HidePanel()
    {
        if (_settings.PanelLarge && !_fullScreen)
        {
            _settings.PanelLargeWidth = Width;
            _settings.PanelLargeHeight = Height;
            _settings.PanelLargeLeft = Left;
            _settings.PanelLargeTop = Top;
            _settings.Save();
        }
        CancelAllEdits();
        Hide();
        _lastHiddenAt = DateTime.Now;
        _showAll = false;
        _selectedDay = null;
        _authorFilter = null;
        _tagFilter = null;
        _selectedPlanId = null;
        _mode = Mode.Notes;
        // Tam ekran kalıcı değildir: panel her gizlendiğinde (tepsi tıklaması, Esc, vb.) sıfırlanır,
        // böylece bir sonraki açılış her zaman kompakt/büyük moddan başlar.
        _fullScreen = false;
        UpdateFullScreenButtonVisual();

        // Bellek ayak izini küçültmek için panel kapalıyken ağır önbellekleri bırak; bir sonraki açılışta
        // yeniden kurulur (tarama kendi dosya önbelleğine sahip olduğundan hızlı kalır).
        _skillInstalledCache = null;
        _skillUsageDataCache = null;
        _skillRegistryCache = null;
        _skillTranslationsCache = null;
        _marketplaceCache = null;
        _skillLoadRequested = false;
    }

    /// <summary>Kompakt (sabit 360×520, NoResize) ve büyük (serbest boyutlandırılabilir, ~1000×720,
    /// geçerli ekranın görünür alanına ortalanmış/kenetlenmiş) mod arasında geçiş yapar; boyut/konum
    /// ve mod <see cref="AppSettings"/> içinde kalıcı tutulur.</summary>
    /// <summary>Beyaz sistem çerçevesi yerine WindowChrome kenarı; büyük modda kenardan boyutlandırma.</summary>
    private void SetResizeBorder(double thickness)
    {
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.ResizeBorderThickness = new Thickness(thickness);
    }

    private void ApplyWindowMode()
    {
        // Kayıtlar görünümü büyük/tam ekranda liste + detayı yan yana, kompakt panelde sırayla gösterir.
        RecordingsViewHost.Wide = _fullScreen || _settings.PanelLarge;
        if (_fullScreen)
        {
            ResizeMode = ResizeMode.NoResize;
            SetResizeBorder(0);
            MinWidth = 0;
            MinHeight = 0;
            var fsArea = GetCurrentScreenWorkArea();
            Width = fsArea.Width;
            Height = fsArea.Height;
            Left = fsArea.Left;
            Top = fsArea.Top;
            SizeToggleButton.Content = "⤡";
            SizeToggleButton.ToolTip = "Tam ekrandan çık (büyük mod)";
            return;
        }
        if (_settings.PanelLarge)
        {
            ResizeMode = ResizeMode.CanResizeWithGrip;
            SetResizeBorder(6);
            MinWidth = 500;
            MinHeight = 400;
            var area = GetCurrentScreenWorkArea();
            var width = Math.Clamp(_settings.PanelLargeWidth ?? 1000, MinWidth, Math.Max(MinWidth, area.Width));
            var height = Math.Clamp(_settings.PanelLargeHeight ?? 720, MinHeight, Math.Max(MinHeight, area.Height));
            Width = width;
            Height = height;
            var left = _settings.PanelLargeLeft ?? area.Left + (area.Width - width) / 2;
            var top = _settings.PanelLargeTop ?? area.Top + (area.Height - height) / 2;
            Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - width));
            Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - height));
            SizeToggleButton.Content = "⤡";
            SizeToggleButton.ToolTip = "Kompakt moda dön";
        }
        else
        {
            ResizeMode = ResizeMode.NoResize;
            SetResizeBorder(0);
            MinWidth = 0;
            MinHeight = 0;
            Width = 360;
            Height = 520;
            SizeToggleButton.Content = "⤢";
            SizeToggleButton.ToolTip = "Büyük moda geç";
        }
    }

    /// <summary>Panelin şu anki konumunun bulunduğu ekranın görünür (görev çubuğu hariç) alanını WPF
    /// cihazdan bağımsız birimlerde döner; birden fazla monitörde doğru ekranı bulmak için kullanılır.</summary>
    private Rect GetCurrentScreenWorkArea()
    {
        try
        {
            var bounds = new System.Drawing.Rectangle((int)Left, (int)Top, Math.Max(1, (int)Width), Math.Max(1, (int)Height));
            var workingArea = Forms.Screen.FromRectangle(bounds).WorkingArea;
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                var topLeft = target.TransformFromDevice.Transform(new Point(workingArea.Left, workingArea.Top));
                var bottomRight = target.TransformFromDevice.Transform(new Point(workingArea.Right, workingArea.Bottom));
                return new Rect(topLeft, bottomRight);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        var fallback = SystemParameters.WorkArea;
        return new Rect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    private void SizeToggleButton_Click(object sender, RoutedEventArgs e) => ToggleSize();

    private void ToggleSize()
    {
        if (_fullScreen)
        {
            // Tam ekrandayken ⤢/⤡ düğmesi her zaman tam ekrandan çıkıp büyük moda geçer.
            _fullScreen = false;
            _settings.PanelLarge = true;
            _settings.Save();
            ApplyWindowMode();
            UpdateFullScreenButtonVisual();
            return;
        }
        if (_settings.PanelLarge)
        {
            _settings.PanelLargeWidth = Width;
            _settings.PanelLargeHeight = Height;
            _settings.PanelLargeLeft = Left;
            _settings.PanelLargeTop = Top;
            _settings.PanelLarge = false;
        }
        else
        {
            _settings.PanelLarge = true;
        }
        _settings.Save();
        ApplyWindowMode();
        if (!_settings.PanelLarge)
            RepositionCompact();
    }

    /// <summary>Kompakt moda geçişte (büyük/tam ekrandan çıkışta) konumu, sabitlenmiş konum varsa ona,
    /// yoksa ekranın sağ altına yerleştirir.</summary>
    private void RepositionCompact()
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
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>Panelin, geçerli monitörün görev çubuğu hariç tüm görünür alanını kaplayan tam ekran
    /// moduna girip çıkar. Tam ekran kalıcı değildir (ayarlara yazılmaz): çıkışta önceki kompakt/büyük
    /// moda (boyut/konumuyla birlikte) dönülür.</summary>
    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        ApplyWindowMode();
        if (!_fullScreen && !_settings.PanelLarge)
            RepositionCompact();
        UpdateFullScreenButtonVisual();
    }

    private void UpdateFullScreenButtonVisual()
    {
        FullScreenButton.ToolTip = _fullScreen ? "Tam ekrandan çık" : "Tam ekran";
        FullScreenButton.Background = _fullScreen ? (Brush)FindResource("Accent") : Brushes.Transparent;
        FullScreenButton.Foreground = _fullScreen ? (Brush)FindResource("OnAccent") : (Brush)FindResource("Muted");
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
        foreach (var project in Projects)
            project.IsEditing = false;
        if (_planTitleEditing) CancelPlanTitleEdit();
        if (_planProjectEditing) CancelPlanProjectEdit();
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
                Mode.Memory => "Hafıza",
                Mode.Skills => "Yetenekler",
                Mode.Plans => "Planlar",
                Mode.Recordings => "Kayıtlar",
                Mode.Settings => "Ayarlar",
                _ => "Notlar",
            };
            // Kişisel bölümde (eski sekme satırı gibi) gezinme şeridi gizlenir; çıkış yine üçlü tıklamayla.
            Rail.Visibility = _mode == Mode.Personal ? Visibility.Collapsed : Visibility.Visible;
            Rail.SetActive(SectionOf(_mode));

            var hostedView = _mode is Mode.Recordings or Mode.Settings;
            MainScroll.Visibility = hostedView ? Visibility.Collapsed : Visibility.Visible;
            RecordingsViewHost.Visibility = _mode == Mode.Recordings ? Visibility.Visible : Visibility.Collapsed;
            SettingsViewHost.Visibility = _mode == Mode.Settings ? Visibility.Visible : Visibility.Collapsed;
            // Kayıtlar bölümünde listenin üstündeki büyük kayıt düğmesi yeterli; sabit düğme gizlenir.
            RecordButton.Visibility = _mode == Mode.Recordings ? Visibility.Collapsed : Visibility.Visible;
            if (_mode == Mode.Recordings) RecordingsViewHost.Reload();
            if (_mode == Mode.Settings && !_skipSettingsViewReload) SettingsViewHost.Reload();

            NotesInputBorder.Visibility = _mode == Mode.Notes ? Visibility.Visible : Visibility.Collapsed;
            TaskInputBorder.Visibility = _mode == Mode.Personal ? Visibility.Visible : Visibility.Collapsed;
            MemoryInputPanel.Visibility = _mode == Mode.Memory ? Visibility.Visible : Visibility.Collapsed;
            SkillsHeaderPanel.Visibility = _mode == Mode.Skills ? Visibility.Visible : Visibility.Collapsed;
            TasksPanel.Visibility = _mode == Mode.Personal ? Visibility.Visible : Visibility.Collapsed;
            if (_mode == Mode.Personal) SyncKisiselOkumaCheckBox();
            MemoryPanel.Visibility = _mode == Mode.Memory ? Visibility.Visible : Visibility.Collapsed;
            SkillsPanel.Visibility = _mode == Mode.Skills ? Visibility.Visible : Visibility.Collapsed;
            PlansPanel.Visibility = _mode == Mode.Plans ? Visibility.Visible : Visibility.Collapsed;

            ReloadTasks();
            ReloadMemory();
            if (_mode == Mode.Skills) EnsureSkillsLoaded(forceRescan: false);
            if (_mode == Mode.Plans) ReloadPlans();

            if (_mode != Mode.Notes)
            {
                NotesFilterRow.Visibility = Visibility.Collapsed;
                DayListPanel.Visibility = Visibility.Collapsed;
                DayViewPanel.Visibility = Visibility.Collapsed;
                ResetSelectMode();
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
            TagFilterButton.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TagFilterButton.Content = _tagFilter is null ? "▾ #etiket" : $"▾ #{_tagFilter}";
            TagFilterButton.Background = _tagFilter is null ? Brushes.Transparent : (Brush)FindResource("Accent");
            TagFilterButton.Foreground = _tagFilter is null ? (Brush)FindResource("Muted") : (Brush)FindResource("OnAccent");

            var visible = _tagFilter is null ? byAuthor : byAuthor.Where(n => n.Tags.Contains(_tagFilter)).ToList();
            var groups = DayGrouper.Group(visible);

            Days.Clear();
            foreach (var group in groups)
                Days.Add(new DayListItem(group.Date, DayGrouper.Header(group.Date, today), group.Notes.Count));

            // Not: açık bir gün görünümünde gösterilecek not kalmasa bile (ör. hepsi silindi, ya da
            // "Başka gün" ile boş bir gün açıldı) otomatik olarak gün listesine dönülmez; kullanıcı
            // yalnızca Geri/Esc ile çıkar.
            var selectedGroup = _selectedDay is { } selDay ? groups.FirstOrDefault(g => g.Date == selDay) : null;

            if (_selectedDay is null) ResetSelectMode();

            DayNotes.Clear();
            if (_selectedDay is { } day)
            {
                foreach (var note in selectedGroup?.Notes ?? [])
                {
                    var color = AuthorColors.For(note.Author, _settings) ?? (Brush)FindResource("Fg");
                    DayNotes.Add(new NoteItem(note, color, AuthorColors.Tint(color))
                    {
                        SelectModeActive = _selectMode,
                        IsSelected = _selectedNoteIds.Contains(note.Id),
                    });
                }
                DayViewHeader.Text = DayGrouper.Header(day, today);
            }
            // Gün görünümü varsayılan olarak satır editörüdür; "Seç" modunda çoklu seçim için eski not listesi.
            var lineEditor = _selectedDay is not null && !_selectMode;
            DayEditor.Visibility = lineEditor ? Visibility.Visible : Visibility.Collapsed;
            DayNotesList.Visibility = lineEditor ? Visibility.Collapsed : Visibility.Visible;
            if (lineEditor) DayEditor.Load(_store, _selectedDay!.Value);
            DayViewEmptyText.Visibility = _selectedDay is not null && !lineEditor && DayNotes.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            RefreshSelectModeUi();

            DayListPanel.Visibility = _selectedDay is null ? Visibility.Visible : Visibility.Collapsed;
            DayViewPanel.Visibility = _selectedDay is null ? Visibility.Collapsed : Visibility.Visible;
            NotesFilterRow.Visibility = Visibility.Visible;
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
        var explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var psi = new ProcessStartInfo { FileName = explorerPath, UseShellExecute = false };
        psi.ArgumentList.Add($"/select,{path}");
        Process.Start(psi);
    });

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_dragging || _fullScreen || _suppressAutoHide) return;
        if (!_settings.PanelPinned && !_settings.PanelLarge) HidePanel();
    }

    /// <summary>Panelin en üstündeki her zaman görünür sürükleme çubuğu: moda ya da sabitlenme
    /// durumuna bakmaksızın her zaman DragMove ile taşımaya izin verir. Sürükleme sırasında pencere
    /// odağı kaybedip Deactivated tetiklense bile (ör. alt-tab) <see cref="_dragging"/> bayrağı sayesinde
    /// sabitlenmemiş panel gizlenmez.</summary>
    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_fullScreen) return; // Tam ekranda sürükleme çubuğu hiçbir şey yapmaz.
        if (e.ClickCount == 2)
        {
            ToggleSize();
            return;
        }
        if (e.ClickCount != 1) return;

        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* mouse zaten bırakılmış olabilir */ }
        finally { _dragging = false; }
        SavePanelPosition();
    }

    /// <summary>Sürükleme sonrası güncel konumu, geçerli moda (kompakt/büyük) göre uygun alanlara
    /// kaydeder.</summary>
    private void SavePanelPosition()
    {
        if (_settings.PanelLarge)
        {
            _settings.PanelLargeLeft = Left;
            _settings.PanelLargeTop = Top;
        }
        else
        {
            _settings.PanelLeft = Left;
            _settings.PanelTop = Top;
        }
        _settings.Save();
    }

    /// <summary>Üst çubuktaki bölüm adı: üçlü tıklama gizli kişisel bölümü açar/kapatır (eski başlık satırındaki
    /// erişim yolunun aynısı). Sabitliyken tek tıklama sürükler. Olay üst çubuğa kabarmaz; böylece üçlü tıklamanın
    /// ikinci tıklaması büyük/kompakt mod geçişi yapmaz.</summary>
    private void HeaderText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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

        if (e.ClickCount == 1 && _settings.PanelPinned && !_fullScreen)
        {
            _dragging = true;
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            finally { _dragging = false; }
            SavePanelPosition();
        }
    }

    /// <summary>Ctrl+1…5 → sol şeridin ilk beş bölümü (Notlar, Hafıza, Planlar, Kayıtlar, Yetenekler).</summary>
    private static PanelSection? SectionForShortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return null;
        return e.Key switch
        {
            Key.D1 or Key.NumPad1 => PanelSection.Notes,
            Key.D2 or Key.NumPad2 => PanelSection.Memory,
            Key.D3 or Key.NumPad3 => PanelSection.Plans,
            Key.D4 or Key.NumPad4 => PanelSection.Recordings,
            Key.D5 or Key.NumPad5 => PanelSection.Skills,
            _ => null,
        };
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AnyEditing) return; // Düzenleme kutusu Enter/Esc'i kendisi işler.
        if (SectionForShortcut(e) is { } section)
        {
            if (_mode != Mode.Personal) NavigateTo(section);
            e.Handled = true;
            return;
        }
        // Panel içi Kayıtlar/Ayarlar görünümlerindeki metin kutuları (ör. başlık düzenleme) Esc'i kendileri işler.
        if (_mode is Mode.Recordings or Mode.Settings && e.Key == Key.Escape && e.OriginalSource is TextBox)
            return;
        if (e.Key == Key.Escape)
        {
            if (OtherDayPopup.IsOpen) { OtherDayPopup.IsOpen = false; e.Handled = true; return; }
            if (SkillAddExpander.IsExpanded) { SkillAddExpander.IsExpanded = false; e.Handled = true; return; }
            if (MemoryAddExpander.IsExpanded) { MemoryAddExpander.IsExpanded = false; e.Handled = true; return; }
            if (PlanAddExpander.IsExpanded) { PlanAddExpander.IsExpanded = false; e.Handled = true; return; }
            if (PlanAddStepExpander.IsExpanded) { PlanAddStepExpander.IsExpanded = false; e.Handled = true; return; }
            if (_fullScreen) { ToggleFullScreen(); e.Handled = true; return; }
            if (_selectMode)
            {
                ResetSelectMode();
                Reload();
                e.Handled = true;
                return;
            }
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

    /// <summary>AppFlags.KisiselMcpOkuma anahtarının geçerli değerini onay kutusuna yansıtır (olay tetiklemeden).</summary>
    private void SyncKisiselOkumaCheckBox()
    {
        _kisiselOkumaSyncing = true;
        KisiselOkumaCheckBox.IsChecked = _appFlags?.GetBool(AppFlags.KisiselMcpOkuma) ?? false;
        _kisiselOkumaSyncing = false;
    }

    /// <summary>"Claude okuyabilsin" anahtarını kaydeder; kapalıyken MCP kişisel listeyi okuyamaz,
    /// yalnızca ekleme yapabilir (TaskTools.kisisel_listele). Varsayılan kapalı.</summary>
    private void KisiselOkumaCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_kisiselOkumaSyncing || _appFlags is null) return;
        Try(() => _appFlags.SetBool(AppFlags.KisiselMcpOkuma, KisiselOkumaCheckBox.IsChecked == true));
    }

    /// <summary>📅 (Başka gün): bugüne kadar olan bir günü seçtiren takvimi açar; dışarı tıklama ya da Esc kapatır.</summary>
    private void OtherDayButton_Click(object sender, RoutedEventArgs e)
    {
        OtherDayCalendar.SelectedDate = null;
        OtherDayCalendar.DisplayDateEnd = DateTime.Today;
        OtherDayCalendar.DisplayDate = DateTime.Today;
        OtherDayPopup.IsOpen = true;
        Dispatcher.InvokeAsync(() => OtherDayCalendar.Focus(), DispatcherPriority.Input);
    }

    private void OtherDayCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OtherDayCalendar.SelectedDate is not { } date) return;
        OtherDayPopup.IsOpen = false;
        // Calendar tıklamadan sonra fareyi yakalı tutar; bırakılmazsa paneldeki ilk tıklama yutulur.
        Mouse.Capture(null);
        CancelAllEdits();
        _selectedDay = DateOnly.FromDateTime(date);
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

    /// <summary>"▾ #etiket": görünür notların etiketlerini (Tümü + her etiket) bir açılır menüde listeler; seçilen
    /// etiket süzgeç olur (eski etiket çipi satırıyla aynı davranış).</summary>
    private void TagFilterButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var chip in Tags)
        {
            var item = new MenuItem { Header = chip.Label, IsCheckable = false, IsChecked = chip.IsSelected };
            var tag = chip.Tag;
            item.Click += (_, _) =>
            {
                CancelAllEdits();
                _tagFilter = tag;
                Reload();
            };
            menu.Items.Add(item);
        }
        TagFilterButton.ContextMenu = menu;
        menu.PlacementTarget = TagFilterButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
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

    /// <summary>Tek tık satırı seçer; çift tık notun gününü editörde açar, imleç o notun satırında.</summary>
    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: NoteItem item })
        {
            e.Handled = true;
            CancelAllEdits();
            NoteEditorWindow.OpenDay(_store, item.Note.Day, item.Id);
            return;
        }
        ((UIElement)sender).Focus();
    }

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

    // --- Notları plana adım olarak ekleme (tekli ve çoklu seçim) ---

    private long? _planAddedId;

    private void SelectModeToggle_Click(object sender, RoutedEventArgs e)
    {
        CancelAllEdits();
        _selectMode = !_selectMode;
        if (!_selectMode) _selectedNoteIds.Clear();
        Reload();
    }

    private void SelectModeExit_Click(object sender, RoutedEventArgs e)
    {
        ResetSelectMode();
        Reload();
    }

    /// <summary>Çoklu seçim modunu kapatır ve ilgili alt barı gizler; Reload() çağrılmadan da
    /// güvenle kullanılabilir (ör. başka bir sekmeye/güne geçişte).</summary>
    private void ResetSelectMode()
    {
        _selectMode = false;
        _selectedNoteIds.Clear();
        RefreshSelectModeUi();
    }

    private void RefreshSelectModeUi()
    {
        SetTabStyle(SelectModeToggleButton, _selectMode);
        SelectModeToggleButton.Content = _selectMode ? "✕ Seçimi bitir" : "Seç";
        SelectionBar.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
        SelectionCountText.Text = $"{_selectedNoteIds.Count} not seçildi";
    }

    private void NoteSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: NoteItem item } box) return;
        if (box.IsChecked == true) _selectedNoteIds.Add(item.Id);
        else _selectedNoteIds.Remove(item.Id);
        RefreshSelectModeUi();
    }

    private void AddNoteToPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoteItem item }) return;
        OpenAddToPlanDialog([FlattenNoteText(item.Note)]);
    }

    private void SelectionAddToPlan_Click(object sender, RoutedEventArgs e)
    {
        var texts = DayNotes.Where(n => _selectedNoteIds.Contains(n.Id)).Select(n => FlattenNoteText(n.Note)).ToList();
        if (texts.Count == 0) return;
        OpenAddToPlanDialog(texts);
    }

    /// <summary>Not metnini (etiketler dahil) tek satıra düzleştirir; adım metni olarak kullanılır.</summary>
    private static string FlattenNoteText(Note note) =>
        string.Join(' ', note.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void OpenAddToPlanDialog(IReadOnlyList<string> texts)
    {
        var dialog = new AddToPlanWindow(_planStore, _projectStore, texts) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (_selectMode) ResetSelectMode();
        Reload();

        _planAddedId = dialog.ResultPlanId;
        PlanAddedText.Text = $"{dialog.ResultAddedCount} adım '{dialog.ResultPlanTitle}' planına eklendi.";
        PlanAddedBar.Visibility = Visibility.Visible;
    }

    private void PlanAddedOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_planAddedId is not { } id) return;
        PlanAddedBar.Visibility = Visibility.Collapsed;
        CancelAllEdits();
        _mode = Mode.Plans;
        _plansShowProjects = false;
        _selectedPlanId = id;
        Reload();
    }

    private void PlanAddedClose_Click(object sender, RoutedEventArgs e) => PlanAddedBar.Visibility = Visibility.Collapsed;

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
                case UndoKind.Project: Try(() => { _projectStore.Restore(id); Reload(); }); break;
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
        UpdateSkillSortButton();

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
    /// durum, Türkçe açıklamalar, seçili sıralama kriteri ve popülerlik/güncellenme/bağlam yükü
    /// meta verisiyle birlikte oluşturur. Ada göre sıralamada marketplace'e göre gruplanır; Popüler ve
    /// Yeni güncellenen sıralamalarında düz bir liste olarak gösterilir (her satırda marketplace rozeti
    /// ile birlikte).</summary>
    private void RenderDiscover()
    {
        var plugins = _marketplaceCache ?? [];
        var translations = _skillTranslationsCache ?? new Dictionary<string, SkillTranslation>();

        DiscoverHintText.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetDiscoverSortStyle();

        var topPopularIds = plugins
            .Where(p => p.UniqueInstalls.HasValue)
            .OrderByDescending(p => p.UniqueInstalls!.Value)
            .Take(10)
            .Select(p => $"{p.Name}@{p.Marketplace}")
            .ToHashSet(StringComparer.Ordinal);

        var query = _skillSearch.Trim();
        var filtered = query.Length == 0
            ? plugins
            : plugins.Where(p => TrContains(p.Name, query) || TrContains(p.Description, query) || TrContains(p.Marketplace, query)).ToList();

        var sorted = MarketplaceScanner.Sort(filtered, _discoverSort);

        MarketplaceGroups.Clear();
        if (_discoverSort == PluginSort.Name)
        {
            foreach (var group in sorted.GroupBy(p => p.Marketplace).OrderBy(g => g.Key, TrComparer))
            {
                var items = group.OrderBy(p => p.Installed ? 0 : 1).ThenBy(p => p.Name, TrComparer)
                    .Select(p => ToMarketplacePluginVm(p, translations, topPopularIds, showMarketplaceChip: false))
                    .ToList();
                MarketplaceGroups.Add(new MarketplaceGroupVm(group.Key, items));
            }
        }
        else if (sorted.Count > 0)
        {
            var items = sorted.Select(p => ToMarketplacePluginVm(p, translations, topPopularIds, showMarketplaceChip: true)).ToList();
            MarketplaceGroups.Add(new MarketplaceGroupVm("", items));
        }
        DiscoverEmptyText.Visibility = plugins.Count > 0 && MarketplaceGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private MarketplacePluginVm ToMarketplacePluginVm(
        MarketplacePlugin p, IReadOnlyDictionary<string, SkillTranslation> translations,
        IReadOnlySet<string> topPopularIds, bool showMarketplaceChip)
    {
        translations.TryGetValue($"plugin:{p.Marketplace}/{p.Name}", out var tr);
        var description = string.IsNullOrWhiteSpace(tr?.DescriptionTr) ? p.Description : tr!.DescriptionTr;

        var metaParts = new List<string>();
        if (p.UniqueInstalls is { } installs) metaParts.Add(MarketplaceScanner.FormatInstalls(installs));
        if (p.LastUpdated is { } updated) metaParts.Add($"güncellendi: {updated.ToString("d MMMM yyyy", TrTr)}");
        var metaLine = metaParts.Count == 0 ? null : string.Join(" · ", metaParts);

        var contextBrush = p.ContextWeight switch
        {
            "hafif" => (Brush)FindResource("Success"),
            "orta" => (Brush)FindResource("Warning"),
            "ağır" => (Brush)FindResource("Danger"),
            _ => null,
        };

        var isTopPopular = topPopularIds.Contains($"{p.Name}@{p.Marketplace}");

        return new MarketplacePluginVm(p.Marketplace, p.Name, description, p.Skills,
            metaLine, p.ContextWeight, contextBrush, isTopPopular, showMarketplaceChip)
        { Installed = p.Installed };
    }

    private void SetDiscoverSortStyle() => UpdateSkillSortButton();

    private void SetDiscoverSort(PluginSort sort)
    {
        if (_discoverSort == sort) return;
        _discoverSort = sort;
        _settings.DiscoverSort = sort;
        _settings.Save();
        RenderDiscover();
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

    private void SetSkillSortStyle() => UpdateSkillSortButton();

    private void SkillSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _skillSearch = SkillSearchBox.Text;
        if (_mode == Mode.Skills) RenderSkills();
    }

    private void SkillSearchClear_Click(object sender, RoutedEventArgs e) => SkillSearchBox.Clear();

    /// <summary>Sıralama açılır menüsü: Kurulu görünümünde Kullanıma göre / Ada göre, Keşfet görünümünde
    /// Popüler / Yeni güncellenen / Ada göre (eski sıralama düğmeleriyle aynı seçenekler ve davranış).</summary>
    private void SkillSortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string header, bool selected, Action onPick)
        {
            var item = new MenuItem { Header = header, IsChecked = selected };
            item.Click += (_, _) => onPick();
            menu.Items.Add(item);
        }

        if (_skillShowDiscover)
        {
            Add("Popüler", _discoverSort == PluginSort.Popular, () => SetDiscoverSort(PluginSort.Popular));
            Add("Yeni güncellenen", _discoverSort == PluginSort.RecentlyUpdated, () => SetDiscoverSort(PluginSort.RecentlyUpdated));
            Add("Ada göre", _discoverSort == PluginSort.Name, () => SetDiscoverSort(PluginSort.Name));
        }
        else
        {
            Add("Kullanıma göre", _skillSortByUsage, () => { _skillSortByUsage = true; RenderSkills(); });
            Add("Ada göre", !_skillSortByUsage, () => { _skillSortByUsage = false; RenderSkills(); });
        }
        SkillSortButton.ContextMenu = menu;
        menu.PlacementTarget = SkillSortButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Sıralama düğmesinin etiketini geçerli görünümün (Kurulu/Keşfet) seçili sıralamasına göre günceller.</summary>
    private void UpdateSkillSortButton()
    {
        var label = _skillShowDiscover
            ? _discoverSort switch
            {
                PluginSort.RecentlyUpdated => "Yeni güncellenen",
                PluginSort.Name => "Ada göre",
                _ => "Popüler",
            }
            : _skillSortByUsage ? "Kullanıma göre" : "Ada göre";
        SkillSortButton.Content = label + " ▾";
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
        var question = $"\"{SanitizeForDisplay(plugin.Name)}\" eklentisini ({SanitizeForDisplay(plugin.Marketplace)}) {verb} istediğinize emin misiniz?";
        if (MessageBox.Show(question, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (!IsValidPluginName(plugin.Name) || !IsValidPluginName(plugin.Marketplace))
        {
            ShowError("Eklenti veya pazar yeri adı geçersiz karakterler içeriyor; işlem yapılmadı.");
            return;
        }

        plugin.Busy = true;
        var verbArg = plugin.Installed ? "uninstall" : "install";
        var argsList = new[] { "plugin", verbArg, plugin.PluginId, "-s", "user", "-y" };
        var wasInstalled = plugin.Installed;

        Task.Run(() => RunClaudeCli(argsList)).ContinueWith(t => Dispatcher.InvokeAsync(() =>
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

    private static readonly Regex PluginNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    /// <summary>Eklenti/pazar yeri adının komut satırına güvenle geçirilebilmesi için izin verilen
    /// karakter kümesiyle sınırlı olup olmadığını kontrol eder (shell/argüman enjeksiyonunu önler).</summary>
    private static bool IsValidPluginName(string? name) => name is not null && PluginNamePattern.IsMatch(name);

    /// <summary>Kullanıcıya gösterilen ad içinden çift yönlü (bidi) kontrol karakterlerini ve satır
    /// sonlarını temizler; onay kutusunda sahte görünüm/gizli metin enjeksiyonunu önler.</summary>
    private static string SanitizeForDisplay(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n') continue;
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Control) continue;
            // Bidi/format kontrol karakterleri (ör. RLO, LRO, PDF, LRM/RLM) Cf kategorisindedir.
            if (category == UnicodeCategory.Format) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? _claudeExePathCache;

    /// <summary>"claude" komutunun PATH üzerindeki tam yolunu bulur: önce claude.exe, sonra claude.cmd
    /// aranır. UseShellExecute=false + FileName="claude" bu makinede claude.cmd (npm kurulumu) için
    /// çalışmadığından tam yol gereklidir.</summary>
    private static string? ResolveClaudeExePath()
    {
        if (_claudeExePathCache is not null) return _claudeExePathCache;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var candidateName in new[] { "claude.exe", "claude.cmd" })
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, candidateName);
                    if (File.Exists(candidate)) return _claudeExePathCache = candidate;
                }
                catch (ArgumentException)
                {
                    // Bozuk PATH girdisini yok say.
                }
            }
        }
        return null;
    }

    /// <summary>"claude" CLI'yi verilen argümanlarla (ArgumentList; birleştirilmiş Arguments string'i yok,
    /// böylece argüman enjeksiyonu önlenir) UI dışı bir iş parçacığında çalıştırır ve çıkış kodu +
    /// yakalanan stdout/stderr ile döner. "claude" PATH'te bulunamazsa başarısız sayılır.</summary>
    private static (bool Success, string Stdout, string Stderr) RunClaudeCli(IReadOnlyList<string> arguments)
    {
        try
        {
            var claudePath = ResolveClaudeExePath();
            if (claudePath is null) return (false, "", "'claude' komutu bulunamadı.");

            ProcessStartInfo psi;
            if (string.Equals(Path.GetExtension(claudePath), ".cmd", StringComparison.OrdinalIgnoreCase))
            {
                // .cmd doğrudan CreateProcess ile çalıştırılamaz; cmd.exe üzerinden başlatılmalı.
                psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add('"' + claudePath + '"');
                foreach (var arg in arguments) psi.ArgumentList.Add(arg);
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = claudePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in arguments) psi.ArgumentList.Add(arg);
            }

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

    /// <summary>Planlar listesini (ya da seçili bir plan varsa detay görünümünü, ya da "Projeler" alt
    /// görünümü seçiliyse proje listesini) yeniden yükler.</summary>
    private void ReloadPlans()
    {
        SetTabStyle(PlansShowPlansButton, !_plansShowProjects);
        SetTabStyle(PlansShowProjectsButton, _plansShowProjects);

        if (_selectedPlanId is { } id)
        {
            PlansListPanel.Visibility = Visibility.Collapsed;
            PlansProjectsPanel.Visibility = Visibility.Collapsed;
            RenderPlanDetail(id);
            return;
        }

        PlanDetailPanel.Visibility = Visibility.Collapsed;
        _currentPlan = null;

        var projects = _projectStore.List();
        RebuildPlanProjectOptions(projects);

        if (_plansShowProjects)
        {
            PlansListPanel.Visibility = Visibility.Collapsed;
            PlansProjectsPanel.Visibility = Visibility.Visible;
            RenderProjects(projects);
            return;
        }

        PlansListPanel.Visibility = Visibility.Visible;
        PlansProjectsPanel.Visibility = Visibility.Collapsed;

        var plans = _planStore.ListPlans();
        Plans.Clear();
        foreach (var plan in plans)
        {
            var (_, steps) = _planStore.GetPlan(plan.Id);
            var done = steps.Count(s => s.Status is StepStatus.Bitti or StepStatus.Atlandi);
            var (full, shortLabel) = DescribePlanLocation(plan, projects);
            Plans.Add(new PlanListItemVm(plan.Id, plan.Title, full, shortLabel,
                PlanStatusLabel(plan.Status), PlanStatusBrush(plan.Status), $"{done}/{steps.Count} adım"));
        }
        PlansEmptyText.Visibility = Plans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Bir planın konumunu (tam metin, kısa/görünen metin) açıklar: bağlı projesi varsa adı,
    /// yoksa kendi kayıtlı klasörü, o da yoksa "(klasör yok)".</summary>
    private static (string Full, string Short) DescribePlanLocation(Plan plan, IReadOnlyList<Project> projects)
    {
        if (plan.ProjectId is { } projectId)
        {
            var project = projects.FirstOrDefault(p => p.Id == projectId);
            if (project is not null)
                return ($"{project.Name} ({project.Path})", $"📁 {project.Name}");
        }
        if (plan.ProjectPath is { } path)
            return (path, ShortenPath(path));
        return ("(klasör yok)", "(klasör yok)");
    }

    private void RebuildPlanProjectOptions(IReadOnlyList<Project> projects)
    {
        PlanProjectOptions.Clear();
        PlanProjectOptions.Add(new ProjectComboItem(null, "(Proje yok)"));
        foreach (var project in projects)
            PlanProjectOptions.Add(new ProjectComboItem(project.Id, project.Name));
    }

    /// <summary>"+ Yeni plan" formundaki proje çiplerini (<see cref="_planNewProjectIds"/>'den, ilki ana
    /// proje) ve adım düzenleyicisindeki proje seçeneklerini yeniden kurar. Hiç proje seçili değilse tek
    /// seferlik klasör seçimi tekrar gösterilir (ikisi birbirini dışlar).</summary>
    private void RebuildPlanNewProjectChips()
    {
        var projects = _projectStore.List();
        PlanNewProjectChips.Clear();
        var resolved = new List<(long Id, string Name)>();
        for (var i = 0; i < _planNewProjectIds.Count; i++)
        {
            var id = _planNewProjectIds[i];
            var project = projects.FirstOrDefault(p => p.Id == id);
            if (project is null) continue;
            PlanNewProjectChips.Add(new PlanProjectChipVm(project.Id, project.Name, IsMain: i == 0));
            resolved.Add((project.Id, project.Name));
        }

        PlanNewFolderRow.Visibility = resolved.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (resolved.Count > 0) SetPlanNewFolderOverride(null);

        PlanStepsEditorControl.SetAvailableProjects(resolved);
    }

    /// <summary>"+ Proje ekle ▾" tıklamasında, henüz seçilmemiş canlı projeleri listeleyen bir ContextMenu
    /// açar; seçilen proje <paramref name="onPick"/> ile eklenir.</summary>
    private void ShowAddProjectMenu(FrameworkElement anchor, IReadOnlyList<long> alreadySelected, Action<Project> onPick)
    {
        var candidates = _projectStore.List().Where(p => !alreadySelected.Contains(p.Id)).ToList();
        var menu = new ContextMenu();
        if (candidates.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(eklenecek başka proje yok)", IsEnabled = false });
        }
        else
        {
            foreach (var project in candidates)
            {
                var item = new MenuItem { Header = project.Name };
                item.Click += (_, _) => onPick(project);
                menu.Items.Add(item);
            }
        }
        anchor.ContextMenu = menu;
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void RenderProjects(IReadOnlyList<Project> projects)
    {
        Projects.Clear();
        foreach (var project in projects)
            Projects.Add(new ProjectItemVm(project));
        ProjectsEmptyText.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlansShowPlans_Click(object sender, RoutedEventArgs e)
    {
        CancelAllEdits();
        _plansShowProjects = false;
        Reload();
    }

    private void PlansShowProjects_Click(object sender, RoutedEventArgs e)
    {
        CancelAllEdits();
        _plansShowProjects = true;
        Reload();
    }

    private void ProjectAddExpander_Expanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => ProjectNameBox.Focus(), DispatcherPriority.Input);

    private void ProjectAddClose_Click(object sender, RoutedEventArgs e) => ProjectAddExpander.IsExpanded = false;

    private void ProjectPathBrowseNew_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        var current = ProjectPathBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(DialogOwner) == Forms.DialogResult.OK)
            ProjectPathBox.Text = dialog.SelectedPath;
    }

    private void ProjectAdd_Click(object sender, RoutedEventArgs e)
    {
        var name = ProjectNameBox.Text.Trim();
        var path = ProjectPathBox.Text.Trim();
        if (name.Length == 0) { ShowError("Proje adı boş olamaz."); return; }
        if (path.Length == 0) { ShowError("Proje klasörü boş olamaz."); return; }
        Try(() =>
        {
            _projectStore.Add(name, path);
            ProjectNameBox.Clear();
            ProjectPathBox.Clear();
            ProjectAddExpander.IsExpanded = false;
            Reload();
        });
    }

    private void ProjectEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectItemVm item }) return;
        item.EditName = item.Name;
        item.EditPath = item.Path;
        item.IsEditing = true;
    }

    private void ProjectEditBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectItemVm item }) return;
        using var dialog = new Forms.FolderBrowserDialog();
        if (Directory.Exists(item.EditPath)) dialog.SelectedPath = item.EditPath;
        if (dialog.ShowDialog(DialogOwner) == Forms.DialogResult.OK)
            item.EditPath = dialog.SelectedPath;
    }

    private void ProjectEditSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectItemVm item }) return;
        SaveProjectEdit(item);
    }

    private void SaveProjectEdit(ProjectItemVm item)
    {
        var name = item.EditName.Trim();
        var path = item.EditPath.Trim();
        if (name.Length == 0) { ShowError("Proje adı boş olamaz."); return; }
        if (path.Length == 0) { ShowError("Proje klasörü boş olamaz."); return; }
        item.IsEditing = false;
        if (name != item.Name || path != item.Path)
            Try(() => _projectStore.Update(item.Id, name, path));
        Reload();
    }

    private void ProjectEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectItemVm item })
        {
            item.IsEditing = false;
            if (_reloadPending) Reload();
        }
    }

    private void ProjectDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectItemVm item }) return;
        Try(() =>
        {
            _projectStore.Delete(item.Id);
            _undoId = item.Id;
            _undoKind = UndoKind.Project;
            UndoText.Text = "Proje silindi.";
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
            Reload();
        });
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
        PlansProjectsPanel.Visibility = Visibility.Collapsed;
        PlanDetailPanel.Visibility = Visibility.Visible;

        if (!_planTitleEditing)
            PlanDetailTitleText.Text = plan.Title;

        var allProjects = _projectStore.List();

        if (!_planProjectEditing)
        {
            var (full, _) = DescribePlanLocation(plan, allProjects);
            PlanDetailPathText.Text = full;
            PlanDetailPathText.ToolTip = full;
        }

        PlanDetailTitleText.Visibility = _planTitleEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanTitleEditButton.Visibility = _planTitleEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanTitleEditRow.Visibility = _planTitleEditing ? Visibility.Visible : Visibility.Collapsed;

        PlanDetailPathText.Visibility = _planProjectEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanPathEditButton.Visibility = _planProjectEditing ? Visibility.Collapsed : Visibility.Visible;
        PlanPathEditRow.Visibility = _planProjectEditing ? Visibility.Visible : Visibility.Collapsed;

        // Planın bağlı olduğu projeler (çoklu proje desteği): ilki ana proje.
        var planProjectIds = _planStore.GetPlanProjects(plan.Id);
        PlanDetailProjectChips.Clear();
        var planProjects = new List<(long Id, string Name)>();
        for (var i = 0; i < planProjectIds.Count; i++)
        {
            var project = allProjects.FirstOrDefault(p => p.Id == planProjectIds[i]);
            if (project is null) continue;
            PlanDetailProjectChips.Add(new PlanProjectChipVm(project.Id, project.Name, IsMain: i == 0));
            planProjects.Add((project.Id, project.Name));
        }

        PlanStepProjectOptions.Clear();
        PlanStepProjectOptions.Add(new PlanStepProjectOption(null, "Proje: —"));
        foreach (var (projId, projName) in planProjects)
            PlanStepProjectOptions.Add(new PlanStepProjectOption(projId, projName));

        var projectNameById = allProjects.ToDictionary(p => p.Id, p => p.Name);
        var assigneeOrder = PlanAssignees.Order(steps);
        PlanDetailSteps.Clear();
        foreach (var step in steps)
        {
            var name = step.ProjectId is { } pid && projectNameById.TryGetValue(pid, out var n) ? n : null;
            PlanDetailSteps.Add(new PlanStepItemVm(step, name, AssigneeBrush(assigneeOrder, step.Assignee)));
        }
        PlanCopyAssignmentsButton.Visibility = PlanAssignees.AnyAssigned(steps) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Sorumlu rozetinin rengi: sorumlunun plandaki sırasına göre kişi renklerinden biri (sorumlusuz
    /// adımlarda soluk renk).</summary>
    private Brush AssigneeBrush(IReadOnlyList<string> order, string? assignee) =>
        PlanAssignees.ColorSlot(order, assignee) is var slot and > 0
            ? (Brush)FindResource(Theme.PersonBrushKey(slot))
            : (Brush)FindResource("Muted");

    /// <summary>📋 Kime ne verildi: planın adımlarını sorumluya göre özetleyip panoya kopyalar.</summary>
    private void PlanCopyAssignments_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan) return;
        Try(() =>
        {
            var summary = PlanAssignees.BuildSummary(_planStore.GetPlan(plan.Id).Steps);
            if (summary.Length == 0) return;
            Clipboard.SetText(summary);
            // Kısa geri bildirim: düğme 2 sn "kopyalandı" yazar.
            PlanCopyAssignmentsButton.Content = "✓ Panoya kopyalandı";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); PlanCopyAssignmentsButton.Content = "📋 Kime ne verildi"; };
            timer.Start();
        });
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

    private void PlanAddExpander_Expanded(object sender, RoutedEventArgs e)
    {
        _planNewProjectIds.Clear();
        RebuildPlanNewProjectChips();
        SetPlanNewFolderOverride(null);
        PlanStepsEditorControl.Clear();
        Dispatcher.InvokeAsync(() => PlanTitleBox.Focus(), DispatcherPriority.Input);
    }

    private void PlanAddClose_Click(object sender, RoutedEventArgs e) => PlanAddExpander.IsExpanded = false;

    private void SetPlanNewFolderOverride(string? folder)
    {
        _planNewFolderOverride = folder;
        PlanNewFolderText.Text = folder ?? "";
        PlanNewFolderClearButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PlanNewFolderClear_Click(object sender, RoutedEventArgs e) => SetPlanNewFolderOverride(null);

    private void PlanNewProjectAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        ShowAddProjectMenu(anchor, _planNewProjectIds, project =>
        {
            _planNewProjectIds.Add(project.Id);
            RebuildPlanNewProjectChips();
        });
    }

    private void PlanNewProjectRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlanProjectChipVm chip }) return;
        _planNewProjectIds.Remove(chip.Id);
        RebuildPlanNewProjectChips();
    }

    private void PlanNewProjectPromote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlanProjectChipVm chip }) return;
        _planNewProjectIds.Remove(chip.Id);
        _planNewProjectIds.Insert(0, chip.Id);
        RebuildPlanNewProjectChips();
    }

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
        if (_planNewFolderOverride is { } current && Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(DialogOwner) != Forms.DialogResult.OK) return;
        SetPlanNewFolderOverride(dialog.SelectedPath);
    }

    private void PlanCreate_Click(object sender, RoutedEventArgs e)
    {
        var title = PlanTitleBox.Text.Trim();
        var stepItems = PlanStepsEditorControl.StepItems;
        if (title.Length == 0) { ShowError("Plan başlığı boş olamaz."); return; }
        if (stepItems.Count == 0) { ShowError("En az bir adım eklemelisiniz."); return; }

        var projectIds = _planNewProjectIds.Count > 0 ? (IReadOnlyList<long>)_planNewProjectIds : null;
        var folder = projectIds is null ? _planNewFolderOverride : null;

        Try(() =>
        {
            var plan = _planStore.CreatePlan(title, folder, steps: [], projectIds: projectIds);
            foreach (var item in stepItems)
                _planStore.AddStep(plan.Id, item.Text, projectId: item.ProjectId, assignee: item.Assignee);

            PlanTitleBox.Clear();
            PlanStepsEditorControl.Clear();
            _planNewProjectIds.Clear();
            SetPlanNewFolderOverride(null);
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

    /// <summary>▶ Çalıştır: bkz. <see cref="PlanRunFlow"/> (PATH kontrolü → açık onay → başlat).</summary>
    private void RunPlan(long id) =>
        PlanRunFlow.Run(this, id, _planStore, _projectStore, ShowError, onPlanMissing: Reload);

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
        _planProjectEditing = true;
        var selected = PlanProjectOptions.FirstOrDefault(o => o.Id == plan.ProjectId) ?? PlanProjectOptions.FirstOrDefault();
        PlanDetailProjectBox.ItemsSource = PlanProjectOptions;
        PlanDetailProjectBox.SelectedItem = selected;
        PlanDetailPathBox.Text = plan.ProjectId is null ? plan.ProjectPath ?? "" : "";
        PlanDetailPathText.Visibility = Visibility.Collapsed;
        PlanPathEditButton.Visibility = Visibility.Collapsed;
        PlanPathEditRow.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() => { PlanDetailPathBox.Focus(); PlanDetailPathBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void CancelPlanProjectEdit()
    {
        _planProjectEditing = false;
        PlanDetailPathText.Visibility = Visibility.Visible;
        PlanPathEditButton.Visibility = Visibility.Visible;
        PlanPathEditRow.Visibility = Visibility.Collapsed;
        if (_reloadPending) Reload();
    }

    private void PlanPathEditCancel_Click(object sender, RoutedEventArgs e) => CancelPlanProjectEdit();

    private void PlanPathEditBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        var current = PlanDetailPathBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(DialogOwner) != Forms.DialogResult.OK) return;
        PlanDetailPathBox.Text = dialog.SelectedPath;
        PlanDetailProjectBox.SelectedItem = PlanProjectOptions.FirstOrDefault(o => o.Id is null);
    }

    private void PlanPathEditSave_Click(object sender, RoutedEventArgs e) => SavePlanProjectEdit();

    /// <summary>Plan konum düzenlemesini kaydeder: ComboBox'ta bir proje seçiliyse plana bağlanır
    /// (eski tek seferlik klasör korunur ama PlanFolder.Resolve proje yolunu öncelikli kullanır); "(Proje
    /// yok)" seçiliyken klasör kutusu doluysa plan projeden koparılıp o klasöre bağlanır; ikisi de boşsa
    /// plan hiçbir klasöre bağlı kalmaz.</summary>
    private void SavePlanProjectEdit()
    {
        if (_currentPlan is not { } plan) { CancelPlanProjectEdit(); return; }
        var selectedProjectId = (PlanDetailProjectBox.SelectedItem as ProjectComboItem)?.Id;
        var folderText = PlanDetailPathBox.Text.Trim();

        _planProjectEditing = false;
        PlanDetailPathText.Visibility = Visibility.Visible;
        PlanPathEditButton.Visibility = Visibility.Visible;
        PlanPathEditRow.Visibility = Visibility.Collapsed;

        Try(() =>
        {
            if (selectedProjectId is { } projectId)
            {
                _planStore.UpdatePlan(plan.Id, projectId: projectId);
            }
            else if (folderText.Length > 0)
            {
                if (plan.ProjectId is not null) _planStore.UpdatePlan(plan.Id, clearProject: true);
                _planStore.UpdatePlan(plan.Id, projectPath: folderText);
            }
            else
            {
                _planStore.UpdatePlan(plan.Id, clearProject: true);
            }
            Reload();
        });
    }

    /// <summary>Plan detayındaki proje çipleri satırına yeni bir proje ekler (bkz. <see cref="PlanStore.SetPlanProjects"/>).
    /// Hiç proje seçilmemişken ilk eklenen ana proje olur.</summary>
    private void PlanDetailProjectAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan || sender is not FrameworkElement anchor) return;
        var current = _planStore.GetPlanProjects(plan.Id);
        ShowAddProjectMenu(anchor, current, project =>
        {
            Try(() =>
            {
                _planStore.SetPlanProjects(plan.Id, [.. current, project.Id]);
                Reload();
            });
        });
    }

    private void PlanDetailProjectRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan) return;
        if (sender is not FrameworkElement { DataContext: PlanProjectChipVm chip }) return;
        Try(() =>
        {
            var current = _planStore.GetPlanProjects(plan.Id).Where(id => id != chip.Id).ToList();
            _planStore.SetPlanProjects(plan.Id, current);
            Reload();
        });
    }

    private void PlanDetailProjectPromote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is not { } plan) return;
        if (sender is not FrameworkElement { DataContext: PlanProjectChipVm chip }) return;
        Try(() =>
        {
            var current = _planStore.GetPlanProjects(plan.Id).Where(id => id != chip.Id).ToList();
            current.Insert(0, chip.Id);
            _planStore.SetPlanProjects(plan.Id, current);
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
        item.EditProjectId = item.Step.ProjectId;
        item.EditAssignee = item.Step.Assignee ?? "";
        item.EditDue = item.Step.DueText ?? "";
        item.IsEditing = true;
    }

    private void PlanStepEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PlanStepItemVm item }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveStepEdit(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelPlanStepEdit(item);
        }
    }

    private void PlanStepEditSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanStepItemVm item }) SaveStepEdit(item);
    }

    /// <summary>Adım düzenlemesini kaydeder: metin değiştiyse <see cref="PlanStore.UpdateStep"/>, bağlı proje
    /// değiştiyse <see cref="PlanStore.SetStepProject"/>, sorumlu/son tarih değiştiyse
    /// <see cref="PlanStore.SetStepAssignee"/> çağrılır (hiçbiri değişmediyse hiçbir şey yapılmaz).</summary>
    private void SaveStepEdit(PlanStepItemVm item)
    {
        var text = item.EditText.Trim();
        var newProjectId = item.EditProjectId;
        var newAssignee = PlanAssignees.Normalize(item.EditAssignee);
        var newDue = PlanAssignees.NormalizeDue(item.EditDue);
        item.IsEditing = false;
        Try(() =>
        {
            if (text.Length > 0 && text != item.Text)
                _planStore.UpdateStep(item.Id, text);
            if (newProjectId != item.Step.ProjectId)
                _planStore.SetStepProject(item.Id, newProjectId);
            if (newAssignee != item.Step.Assignee || newDue != item.Step.DueText)
                _planStore.SetStepAssignee(item.Id, newAssignee, newDue);
            Reload();
        });
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
