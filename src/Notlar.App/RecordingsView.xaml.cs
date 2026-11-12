using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Notlar.Core;
using Notlar.Kayit;

namespace Notlar.App;

/// <summary>Durum rozetinin renk türü; rozet rengi XAML'de bu değere göre tema kaynaklarından seçilir.</summary>
public enum RecordingBadgeKind { Muted, Recording, Running, Ready, Error }

/// <summary>Listede bir kayıt satırı. Çeviri ilerlemesi geldikçe durum rozeti yerinde güncellenir.</summary>
public sealed class RecordingListItem : INotifyPropertyChanged
{
    private string _title = "";
    private string _meta = "";
    private string _statusLabel = "";
    private string? _statusToolTip;
    private RecordingBadgeKind _kind;

    public RecordingListItem(long id) => Id = id;

    public long Id { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Meta { get => _meta; set => Set(ref _meta, value); }
    public string StatusLabel { get => _statusLabel; set => Set(ref _statusLabel, value); }
    public string? StatusToolTip { get => _statusToolTip; set => Set(ref _statusToolTip, value); }
    public RecordingBadgeKind Kind { get => _kind; set => Set(ref _kind, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Eylem düğmelerini tek satırda tüm genişliğe yayar: son çocuk (⋯) doğal genişliğinde kalır, diğerleri
/// mümkün olduğunca eşit genişlik alır (metni uzun olan gerekirse daha geniş olur). Yer yetmezse önce en geniş
/// düğme daralır ve metni "…" ile kısalır.</summary>
public sealed class ActionStrip : Panel
{
    private double[] _widths = [];

    /// <summary>Çocuklar arasındaki boşluk (px).</summary>
    public double Spacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren();
        if (children.Count == 0) return default;

        var natural = new double[children.Count];
        foreach (var (child, i) in children.Select((c, i) => (c, i)))
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            natural[i] = child.DesiredSize.Width;
        }

        var gaps = Spacing * (children.Count - 1);
        if (double.IsPositiveInfinity(availableSize.Width))
        {
            _widths = natural;
        }
        else
        {
            _widths = Distribute(natural, Math.Max(0, availableSize.Width - gaps));
        }

        double height = 0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(_widths[i], availableSize.Height));
            height = Math.Max(height, children[i].DesiredSize.Height);
        }
        var width = double.IsPositiveInfinity(availableSize.Width) ? _widths.Sum() + gaps : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren();
        if (children.Count != _widths.Length)
            _widths = Distribute(children.Select(c => c.DesiredSize.Width).ToArray(),
                Math.Max(0, finalSize.Width - Spacing * Math.Max(0, children.Count - 1)));

        double x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Arrange(new Rect(x, 0, _widths[i], finalSize.Height));
            x += _widths[i] + Spacing;
        }
        return finalSize;
    }

    private List<UIElement> VisibleChildren() =>
        InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private static double[] Distribute(double[] natural, double available)
    {
        var n = natural.Length;
        var widths = new double[n];
        if (n == 0) return widths;

        // Son çocuk sabit genişlikte.
        widths[n - 1] = Math.Min(natural[n - 1], available);
        var rest = available - widths[n - 1];
        var flexible = Enumerable.Range(0, n - 1).ToList();
        if (flexible.Count == 0) return widths;

        if (flexible.Sum(i => natural[i]) >= rest)
        {
            // Sığmıyor: önce en geniş(ler) daralır; kısa düğmeler doğal genişliğinde kalır (tavan genişlik).
            var shortest = flexible.OrderBy(i => natural[i]).ToList();
            var remaining = rest;
            while (shortest.Count > 0 && natural[shortest[0]] <= remaining / shortest.Count)
            {
                widths[shortest[0]] = natural[shortest[0]];
                remaining -= natural[shortest[0]];
                shortest.RemoveAt(0);
            }
            foreach (var i in shortest) widths[i] = Math.Max(0, remaining / shortest.Count);
            return widths;
        }

        // Sığıyor: eşit paya göre doldur; payından genişi kendi genişliğinde kalır, kalan yer diğerlerine eşit bölünür.
        var pending = flexible.OrderByDescending(i => natural[i]).ToList();
        while (pending.Count > 0 && natural[pending[0]] > rest / pending.Count)
        {
            widths[pending[0]] = natural[pending[0]];
            rest -= natural[pending[0]];
            pending.RemoveAt(0);
        }
        foreach (var i in pending) widths[i] = rest / pending.Count;
        return widths;
    }
}

/// <summary>Paneldeki 🎙 Kayıtlar görünümü. Liste: üstte geniş kayıt düğmesi, kayıt satırları (başlık, zaman · süre,
/// durum rozeti). Dar panelde (<see cref="Wide"/> = false) satıra tıklayınca detay açılır, "‹ Kayıtlar" ile listeye
/// dönülür; büyük modda liste solda (≈210 px), detay sağdadır. Detay: yerinde düzenlenen başlık, zaman · süre · dil,
/// durum/hata, 3 ana eylem (✨ Özetle, 🗂 Plan ya da ▶ Planı başlat, 💬 Claude) ve ⋯ menüsü (🗂 Yeniden planla,
/// 🔁 Yeniden çevir, 📋 Kopyala, 🌐 Dil, 📁 Proje, 🗑 Sil); Transkript / Özet sekmeleri. Claude işleri
/// <see cref="RecordingController"/>'da yürür; görünüm gizlense de tamamlanıp kaydedilir. Veri değişiklik
/// sinyaliyle (<see cref="Reload"/>) yenilenir; seçim ve dar moddaki detay ekranı korunur.</summary>
public partial class RecordingsView : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double WideListWidth = 210;
    private const long HourMs = 3_600_000;

    private static readonly IReadOnlyList<(string Code, string Label)> LanguageOptions =
    [
        ("tr", "Türkçe"),
        ("en", "İngilizce"),
        ("auto", "Otomatik"),
    ];

    private RecordingController? _controller;
    private RecordingStore? _store;
    private PlanStore? _planStore;
    private ProjectStore? _projectStore;
    private readonly ObservableCollection<RecordingListItem> _items = [];
    private readonly RecordingListItem _detailStatus = new(0);
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private long? _selectedId;
    private Recording? _current;
    private bool _hasTranscript;
    private string _transcriptText = "";
    private bool _planMissing;
    private long? _selectedProjectId;
    private string? _selectedProjectName;
    private bool _suppressEvents;
    private bool _attached;
    private bool _wide;
    private bool _showDetail;

    static RecordingsView()
    {
        // Panel penceresi Esc'i (paneli gizlemek için) PreviewKeyDown'da yakalar; başlık düzenlenirken Esc
        // düzenlemeden vazgeçmeli. Sınıf işleyicileri örnek işleyicilerinden önce çalıştığından burada yakalanır.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(OnAnyWindowPreviewKeyDown));
    }

    public RecordingsView()
    {
        InitializeComponent();
        RecordingsList.ItemsSource = _items;
        DetailBadge.DataContext = _detailStatus;
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); MessageBar.Visibility = Visibility.Collapsed; };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CommitTitle();
        };
        ApplyLayout();
    }

    /// <summary>Bir kez, panel kurulurken çağrılır.</summary>
    public void Initialize(RecordingController controller, PlanStore planStore, ProjectStore projectStore)
    {
        if (_controller is not null) return;
        _controller = controller;
        _store = controller.Store;
        _planStore = planStore;
        _projectStore = projectStore;

        if (IsLoaded) Attach();
        Reload();
        UpdateRecordState();
    }

    /// <summary>Veri dışarıdan değiştiğinde (değişiklik sinyali) çağrılır; seçim ve dar moddaki detay ekranı korunur.</summary>
    public void Reload()
    {
        if (_store is null) return;

        IReadOnlyList<Recording> list;
        try
        {
            list = _store.List(200);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
            return;
        }

        _suppressEvents = true;
        try
        {
            var sameShape = list.Count == _items.Count && list.Select(r => r.Id).SequenceEqual(_items.Select(i => i.Id));
            if (!sameShape)
            {
                _items.Clear();
                foreach (var r in list)
                    _items.Add(new RecordingListItem(r.Id));
            }
            for (var i = 0; i < list.Count; i++)
                Fill(_items[i], list[i]);

            var selected = _selectedId is { } id ? _items.FirstOrDefault(i => i.Id == id) : null;
            // Büyük modda detay alanı boş kalmasın; dar modda kullanıcı seçmeden detay açılmaz.
            if (selected is null && _wide) selected = _items.FirstOrDefault();
            RecordingsList.SelectedItem = selected;
            _selectedId = selected?.Id;
            if (selected is null) _showDetail = false;
        }
        finally
        {
            _suppressEvents = false;
        }

        EmptyListText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshProjects();
        ShowDetail();
        ApplyLayout();
    }

    /// <summary>İlgili kaydı seçer ve detayını gösterir (dar modda detay görünümüne geçer).</summary>
    public void Select(long id)
    {
        CommitTitle();
        _selectedId = id;
        _showDetail = true;
        Reload();
        if (RecordingsList.SelectedItem is { } item) RecordingsList.ScrollIntoView(item);
    }

    /// <summary>true: büyük mod (liste solda, detay sağda); false: dar panel (liste → detay, ‹ geri).</summary>
    public bool Wide
    {
        get => _wide;
        set
        {
            if (_wide == value) return;
            _wide = value;
            // Dar moda dönünce, kullanıcı büyük modda bir kayda tıkladıysa onun detayı açık kalır; yalnızca otomatik
            // seçilmiş kayıt için liste gösterilir.
            if (_store is null) ApplyLayout();
            else Reload();
        }
    }

    // ---------------------------------------------------------------- Yaşam döngüsü

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        Attach();
        Reload();
        UpdateRecordState();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        CommitTitle();
        Detach();
    }

    private void Attach()
    {
        if (_attached || _controller is null) return;
        _attached = true;
        _controller.RecordingStateChanged += OnRecordingStateChanged;
        _controller.ProgressChanged += OnProgressChanged;
        _controller.Completed += OnTranscriptionFinished;
        _controller.Failed += OnTranscriptionFailed;
        _controller.JobsChanged += UpdateButtons;
    }

    private void Detach()
    {
        if (!_attached || _controller is null) return;
        _attached = false;
        _elapsedTimer.Stop();
        _controller.RecordingStateChanged -= OnRecordingStateChanged;
        _controller.ProgressChanged -= OnProgressChanged;
        _controller.Completed -= OnTranscriptionFinished;
        _controller.Failed -= OnTranscriptionFailed;
        _controller.JobsChanged -= UpdateButtons;
    }

    // ---------------------------------------------------------------- Düzen

    /// <summary>Büyük modda liste + detay yan yana; dar modda ya liste ya detay.</summary>
    private void ApplyLayout()
    {
        if (_wide)
        {
            ListColumn.Width = new GridLength(WideListWidth);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            ListPane.Visibility = Visibility.Visible;
            DetailHost.Visibility = Visibility.Visible;
            DetailHost.BorderThickness = new Thickness(1, 0, 0, 0);
            BackBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            var detail = _showDetail && _current is not null;
            ListColumn.Width = detail ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = detail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ListPane.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
            DetailHost.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
            DetailHost.BorderThickness = new Thickness(0);
            BackBar.Visibility = Visibility.Visible;
        }
    }

    private void OpenDetail()
    {
        if (_current is null) return;
        _showDetail = true;
        ApplyLayout();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        CommitTitle();
        _showDetail = false;
        ApplyLayout();
        if (RecordingsList.SelectedItem is { } item) RecordingsList.ScrollIntoView(item);
    }

    // ---------------------------------------------------------------- Yükleme

    private void Fill(RecordingListItem item, Recording r)
    {
        item.Title = r.Title;
        item.Meta = $"{TimeLabel(r.StartedAt)} · {DurationLabel(r)}";
        SetListStatus(item, r);
    }

    private void RefreshProjects()
    {
        if (_selectedProjectId is not { } projectId || _projectStore is null) return;
        try
        {
            var project = _projectStore.List().FirstOrDefault(p => p.Id == projectId);
            _selectedProjectId = project?.Id;
            _selectedProjectName = project?.Name;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }

    private void ShowDetail()
    {
        if (_store is null || _selectedId is not { } id)
        {
            HideDetail();
            return;
        }

        Recording r;
        IReadOnlyList<TranscriptSegment> segments;
        try
        {
            r = _store.Get(id);
            segments = _store.GetSegments(id);
        }
        catch (RecordingNotFoundException)
        {
            HideDetail();
            return;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
            return;
        }

        var otherRecording = _current?.Id != r.Id;
        _current = r;
        _hasTranscript = segments.Count > 0;

        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        // Kullanıcı başlığı düzenliyorsa yazdığı metin ezilmez.
        if (otherRecording || !TitleBox.IsKeyboardFocusWithin)
            TitleBox.Text = r.Title;

        UpdateMeta(r);
        UpdateStatus(r);

        // Metin değişmediyse yeniden oluşturulmaz (kaydırma konumu ve seçim korunur).
        var transcript = TranscriptFormatter.Format(segments);
        if (otherRecording || transcript != _transcriptText)
        {
            _transcriptText = transcript;
            TranscriptBox.Document = BuildTranscriptDocument(segments);
        }
        TranscriptPlaceholder.Text = TranscriptPlaceholderText(r);

        var summary = r.Summary ?? "";
        if (SummaryBox.Text != summary) SummaryBox.Text = summary;

        if (otherRecording)
        {
            TranscriptBox.ScrollToHome();
            SummaryBox.ScrollToHome();
        }

        UpdateTabs();
        UpdateButtons();
    }

    private void HideDetail()
    {
        _current = null;
        _hasTranscript = false;
        _transcriptText = "";
        TranscriptBox.Document = new FlowDocument();
        DetailPanel.Visibility = Visibility.Collapsed;
        NoSelectionText.Visibility = Visibility.Visible;
    }

    /// <summary>Transkript: satır başına bir segment; zaman damgası soluk, "Ben" ve "Karşı taraf" ayrı renkte.</summary>
    private static FlowDocument BuildTranscriptDocument(IReadOnlyList<TranscriptSegment> segments)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        if (segments.Count == 0) return doc;

        var withHours = segments.Max(s => Math.Max(s.StartMs, s.EndMs)) >= HourMs;
        foreach (var s in segments)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };

            var stamp = new Run($"[{Stamp(s.StartMs, withHours)}] ");
            stamp.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            paragraph.Inlines.Add(stamp);

            var speaker = new Run(TranscriptFormatter.Label(s.Speaker) + ":") { FontWeight = FontWeights.SemiBold };
            speaker.SetResourceReference(TextElement.ForegroundProperty, s.Speaker == Speaker.Ben ? "SpeakerBen" : "SpeakerKarsi");
            paragraph.Inlines.Add(speaker);

            paragraph.Inlines.Add(new Run(" " + TextSafety.Flatten(s.Text)));
            doc.Blocks.Add(paragraph);
        }
        return doc;
    }

    private void UpdateMeta(Recording r)
    {
        MetaText.Text = string.Join(" · ",
            TimeLabel(r.StartedAt),
            r.Status == RecordingStatus.Kaydediliyor ? "kayıt sürüyor" : DurationLabel(r),
            LanguageLabel(r.Language));

        var extra = new List<string>();
        _planMissing = false;
        if (r.PlanId is { } planId)
        {
            try
            {
                extra.Add($"Plan #{planId}: {_planStore!.GetPlan(planId).Plan.Title}");
            }
            catch (PlanNotFoundException)
            {
                _planMissing = true;
                extra.Add($"Plan #{planId} (silinmiş)");
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
                extra.Add($"Plan #{planId}");
            }
        }
        if (_selectedProjectName is { } projectName) extra.Add("Proje: " + projectName);
        if (r.AudioDeleted) extra.Add("ses dosyaları silindi");

        MetaExtraText.Text = string.Join(" · ", extra);
        MetaExtraText.Visibility = extra.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus(Recording r)
    {
        _detailStatus.Kind = KindOf(r.Status);
        _detailStatus.StatusLabel = StatusName(r.Status);

        var progress = IsInProgress(r) ? _controller!.GetProgress(r.Id) : null;
        if (progress is { } p)
        {
            _detailStatus.Kind = RecordingBadgeKind.Running;
            _detailStatus.StatusLabel = StatusName(RecordingStatus.Cevriliyor);
            StatusText.Text = ProgressLabel(p.Phase, p.Fraction);
            TranscribeProgress.Value = Math.Clamp(p.Fraction, 0, 1);
            TranscribeProgress.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Text = r.Status switch
            {
                RecordingStatus.Kaydediliyor => "Kayıt sürüyor; durdurulunca metne çevrilecek.",
                RecordingStatus.Bekliyor => "Metne çevrilmek için sırada bekliyor.",
                RecordingStatus.Cevriliyor => "Metne çevriliyor…",
                RecordingStatus.Hazir => _hasTranscript ? "Metne çevrildi." : "Metne çevrildi; kayıtta konuşma bulunamadı.",
                RecordingStatus.Hata => "Metne çevrilemedi.",
                _ => "",
            };
            TranscribeProgress.Visibility = Visibility.Collapsed;
        }

        var showError = r.Status == RecordingStatus.Hata && !string.IsNullOrWhiteSpace(r.Error);
        ErrorText.Text = showError ? r.Error : "";
        ErrorText.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateButtons()
    {
        if (_controller is null || _current is not { } r) return;
        var id = r.Id;
        var ready = r.Status == RecordingStatus.Hazir;
        var summarizing = _controller.IsSummarizing(id);
        var planning = _controller.IsPlanning(id);
        var hasPlan = r.PlanId is not null && !_planMissing;

        SummarizeButton.Content = summarizing ? "✨ Özetleniyor…" : "✨ Özetle";
        SummarizeButton.IsEnabled = ready && _hasTranscript && !summarizing;
        SummarizeButton.ToolTip = summarizing ? "Claude özet çıkarıyor…"
            : ready && !_hasTranscript ? "Transkript boş; özetlenecek bir şey yok."
            : "Transkripti Claude'a özetlet";

        if (planning)
        {
            PlanButton.Content = "🗂 Planlanıyor…";
            PlanButton.IsEnabled = false;
            PlanButton.ToolTip = "Claude transkriptten görev planı çıkarıyor…";
        }
        else if (hasPlan)
        {
            PlanButton.Content = "▶ Planı başlat";
            PlanButton.IsEnabled = true;
            PlanButton.ToolTip = $"Bu kayıttan oluşturulan Plan #{r.PlanId}'i Claude Code ile çalıştır (yeni plan için ⋯ → Yeniden planla)";
        }
        else
        {
            PlanButton.Content = "🗂 Plan";
            PlanButton.IsEnabled = ready && _hasTranscript;
            PlanButton.ToolTip = ready && !_hasTranscript
                ? "Transkript boş; görev planı çıkarılamaz."
                : "Transkriptten Claude'a görev planı çıkart" + ProjectHint();
        }

        ClaudeButton.IsEnabled = ready;
        ClaudeButton.ToolTip = "Bu kayıt hakkında konuşmak için Claude Code oturumu aç" +
            (_selectedProjectName is { } name ? $" (klasör: {name} projesi)" : " (klasör: kullanıcı klasörü)");
    }

    private string ProjectHint() => _selectedProjectName is { } name ? $" ({name} projesine bağlanır)" : "";

    /// <summary>Transkript / Özet sekmesinin içeriğini ve yer tutucularını gösterir.</summary>
    private void UpdateTabs()
    {
        var showSummary = SummaryTab.IsChecked == true;
        var hasSummary = !string.IsNullOrEmpty(SummaryBox.Text);

        TranscriptBox.Visibility = !showSummary ? Visibility.Visible : Visibility.Collapsed;
        TranscriptPlaceholder.Visibility = !showSummary && !_hasTranscript ? Visibility.Visible : Visibility.Collapsed;
        SummaryBox.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
        SummaryPlaceholder.Visibility = showSummary && !hasSummary ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || SummaryBox is null) return;
        UpdateTabs();
    }

    // ---------------------------------------------------------------- Kayıt durumu

    private void OnRecordingStateChanged()
    {
        UpdateRecordState();
        if (_controller?.CurrentRecordingId is { } id) _selectedId = id;
        Reload();
    }

    private void UpdateRecordState()
    {
        if (_controller is null) return;
        if (_controller.IsRecording)
        {
            RecordButton.SetResourceReference(BackgroundProperty, "Surface");
            RecordButton.SetResourceReference(ForegroundProperty, "Record");
            MiniRecordButton.SetResourceReference(BackgroundProperty, "Surface");
            MiniRecordButton.SetResourceReference(ForegroundProperty, "Record");
            RecordButton.ToolTip = "Kaydı durdur; kayıt bu bilgisayarda metne çevrilecek.";
            MiniRecordButton.ToolTip = RecordButton.ToolTip;
            UpdateElapsed();
            if (_attached) _elapsedTimer.Start();
        }
        else
        {
            RecordButton.Content = "⏺ Kaydı başlat";
            MiniRecordButton.Content = "⏺ Kayıt";
            RecordButton.ClearValue(BackgroundProperty);
            RecordButton.ClearValue(ForegroundProperty);
            MiniRecordButton.ClearValue(BackgroundProperty);
            MiniRecordButton.ClearValue(ForegroundProperty);
            RecordButton.ToolTip = "Mikrofon (Ben) ve bilgisayardan gelen ses (Karşı taraf) kaydedilir; metne çeviri bu bilgisayarda yapılır.";
            MiniRecordButton.ToolTip = "Yeni kayıt başlat";
            _elapsedTimer.Stop();
        }
    }

    private void UpdateElapsed()
    {
        if (_controller is not { IsRecording: true }) return;
        var elapsed = FormatDuration(_controller.Elapsed);
        RecordButton.Content = $"⏹ Durdur · {elapsed}  ●";
        MiniRecordButton.Content = $"⏹ {elapsed}";
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        if (_controller.IsRecording) _controller.Stop();
        else _controller.Start(OwnerWindow);
    }

    private void OnProgressChanged(long id, string phase, double fraction)
    {
        if (_items.FirstOrDefault(i => i.Id == id) is { } item)
        {
            item.Kind = RecordingBadgeKind.Running;
            item.StatusLabel = BadgeProgressLabel(phase, fraction);
            item.StatusToolTip = ProgressLabel(phase, fraction);
        }
        if (_current?.Id == id)
        {
            _detailStatus.Kind = RecordingBadgeKind.Running;
            _detailStatus.StatusLabel = StatusName(RecordingStatus.Cevriliyor);
            StatusText.Text = ProgressLabel(phase, fraction);
            TranscribeProgress.Value = Math.Clamp(fraction, 0, 1);
            TranscribeProgress.Visibility = Visibility.Visible;
        }
    }

    private void OnTranscriptionFinished(long id) => Reload();

    private void OnTranscriptionFailed(long id, string message) => Reload();

    // ---------------------------------------------------------------- Seçim, başlık

    private void RecordingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        CommitTitle();
        _selectedId = (RecordingsList.SelectedItem as RecordingListItem)?.Id;
        if (_selectedId is null) _showDetail = false;
        ShowDetail();
        ApplyLayout();
    }

    /// <summary>Dar modda satıra tıklamak detayı açar (seçili satıra yeniden tıklamak da).</summary>
    private void RecordingItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: RecordingListItem item }) return;
        if (!ReferenceEquals(RecordingsList.SelectedItem, item)) RecordingsList.SelectedItem = item;
        if (_current?.Id == item.Id) OpenDetail();
    }

    private void RecordingsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RecordingsList.SelectedItem is not RecordingListItem item || _current?.Id != item.Id) return;
        e.Handled = true;
        OpenDetail();
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTitle();
        }
    }

    private void TitleBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTitle();

    /// <summary>Başlık kutusundaki metin değiştiyse kaydı yeniden adlandırır; boşsa eski başlığa döner.</summary>
    private void CommitTitle()
    {
        if (_store is null || _current is not { } r) return;
        var text = TitleBox.Text.Trim();
        if (text == r.Title) return;
        if (text.Length == 0)
        {
            TitleBox.Text = r.Title;
            return;
        }

        try
        {
            _current = _store.Rename(r.Id, text);
            TitleBox.Text = _current.Title;
            if (_items.FirstOrDefault(i => i.Id == r.Id) is { } item) item.Title = _current.Title;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            TitleBox.Text = r.Title;
            ShowMessage(Describe(ex), isError: true);
        }
    }

    /// <summary>Esc: başlık düzenlemesinden vazgeçer.</summary>
    private void CancelTitleEdit()
    {
        if (_current is not { } r) return;
        TitleBox.Text = r.Title;
        TitleBox.SelectAll();
    }

    private static void OnAnyWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || Keyboard.FocusedElement is not TextBox box) return;
        DependencyObject? node = box;
        while (node is not null and not RecordingsView)
            node = VisualTreeHelper.GetParent(node);
        if (node is RecordingsView view && ReferenceEquals(view.TitleBox, box))
        {
            e.Handled = true;
            view.CancelTitleEdit();
        }
    }

    // ---------------------------------------------------------------- Claude işlemleri

    private async void SummarizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null || _current is not { } r) return;
        if (!string.IsNullOrWhiteSpace(r.Summary) && !Confirm("Bu kaydın zaten bir özeti var. Yeni özet mevcut özetin yerine geçecek. Devam edilsin mi?"))
            return;

        var id = r.Id;
        try
        {
            await _controller.SummarizeAsync(id);
            if (_current?.Id == id)
            {
                ShowMessage("Özet hazır.");
                SummaryTab.IsChecked = true;
            }
            Reload();
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ReportError("Özet oluşturulamadı: " + Describe(ex));
        }
    }

    private void PlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } r) return;
        if (r.PlanId is { } planId && !_planMissing) RunPlan(planId);
        else CreatePlan();
    }

    /// <summary>🗂 Görev planla (ya da ⋯ → Yeniden planla): transkriptten Claude'a görev planı çıkartır.</summary>
    private async void CreatePlan()
    {
        if (_controller is null || _current is not { } r) return;
        if (r.PlanId is { } existing && !_planMissing
            && !Confirm($"Bu kayıttan zaten Plan #{existing} oluşturuldu. Yeni bir plan oluşturulsun mu?"))
            return;

        var id = r.Id;
        var projectId = _selectedProjectId;
        try
        {
            var plan = await _controller.CreatePlanAsync(id, projectId);
            if (plan is null) return;
            var message = $"Plan #{plan.Id} oluşturuldu: {plan.Title}";
            if (!IsVisible)
                MessageBox.Show(message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                ShowMessage(message + " (📋 Planlar sekmesinde görebilirsiniz.)");
            Reload();
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ReportError("Görev planı oluşturulamadı: " + Describe(ex));
        }
    }

    private void RunPlan(long planId)
    {
        if (_planStore is null || _projectStore is null || Window.GetWindow(this) is not { } owner) return;
        PlanRunFlow.Run(owner, planId, _planStore, _projectStore, message => ShowMessage(message, isError: true));
    }

    private void ClaudeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_projectStore is null || _current is not { } r) return;
        try
        {
            string? folder = null;
            if (_selectedProjectId is { } projectId)
                folder = _projectStore.Get(projectId).Path;
            ClaudeSessionLauncher.Launch(r.Id, folder);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
        }
    }

    // ---------------------------------------------------------------- ⋯ menüsü

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null || _current is not { } r) return;
        var id = r.Id;
        var ready = r.Status == RecordingStatus.Hazir;
        var summarizing = _controller.IsSummarizing(id);
        var planning = _controller.IsPlanning(id);
        var busyAudio = r.Status is RecordingStatus.Kaydediliyor or RecordingStatus.Cevriliyor
            || _controller.CurrentRecordingId == id || _controller.IsTranscribing(id);

        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };

        if (r.PlanId is not null && !_planMissing)
        {
            var replan = MenuEntry(planning ? "🗂 Planlanıyor…" : "🗂 Yeniden planla", ready && _hasTranscript && !planning,
                ready && !_hasTranscript ? "Transkript boş; görev planı çıkarılamaz."
                    : "Transkriptten Claude'a yeni bir görev planı çıkart" + ProjectHint(),
                CreatePlan);
            menu.Items.Add(replan);
        }

        menu.Items.Add(MenuEntry("🔁 Yeniden çevir",
            (r.Status is RecordingStatus.Hata or RecordingStatus.Hazir) && !r.AudioDeleted && !busyAudio,
            r.AudioDeleted ? "Ses dosyaları silinmiş; kayıt yeniden çevrilemez." : "Kaydı yeniden metne çevir (ör. dili değiştirdikten sonra)",
            Requeue));

        menu.Items.Add(MenuEntry("📋 Kopyala", _hasTranscript || !string.IsNullOrWhiteSpace(r.Summary),
            "Transkripti ve özeti panoya kopyala", Copy));

        menu.Items.Add(new Separator());

        var currentLanguage = LanguageOptions.FirstOrDefault(o => o.Code == r.Language);
        var language = new MenuItem
        {
            Header = MenuHeader("🌐 Dil: " + (currentLanguage.Label ?? r.Language)),
            ToolTip = "Metne çeviri dili; yeni kayıtlar için de varsayılan olur",
        };
        foreach (var option in LanguageOptions)
        {
            var item = new MenuItem { Header = MenuHeader(option.Label), IsChecked = option.Code == r.Language };
            var code = option.Code;
            var label = option.Label;
            item.Click += (_, _) => SetLanguage(code, label);
            language.Items.Add(item);
        }
        menu.Items.Add(language);

        var project = new MenuItem
        {
            Header = MenuHeader(_selectedProjectName is { } projectName ? "📁 Proje: " + projectName : "📁 Proje"),
            ToolTip = "Görev planı bu projeye bağlanır; Claude oturumu bu projenin klasöründe açılır",
        };
        var none = new MenuItem { Header = MenuHeader("(proje yok)"), IsChecked = _selectedProjectId is null };
        none.Click += (_, _) => SetProject(null, null);
        project.Items.Add(none);
        try
        {
            foreach (var p in _projectStore!.List())
            {
                var item = new MenuItem { Header = MenuHeader(p.Name), IsChecked = p.Id == _selectedProjectId };
                var projectId = p.Id;
                var name = p.Name;
                item.Click += (_, _) => SetProject(projectId, name);
                project.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        menu.Items.Add(project);

        menu.Items.Add(new Separator());

        var delete = MenuEntry("🗑 Sil", !busyAudio && !summarizing && !planning,
            busyAudio ? "Kaydedilen ya da metne çevrilen kayıt silinemez."
                : summarizing || planning ? "Claude işi sürerken kayıt silinemez."
                : "Kaydı, transkripti ve ses dosyalarını sil",
            Delete);
        // ContextMenu sistem görünümünde (her temada açık zemin) açılır; açık zeminde okunur kırmızı "Danger"dır.
        delete.SetResourceReference(ForegroundProperty, "Danger");
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string header, bool enabled, string toolTip, Action onClick)
    {
        var item = new MenuItem { Header = MenuHeader(header), IsEnabled = enabled, ToolTip = toolTip };
        ToolTipService.SetShowOnDisabled(item, true);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>MenuItem başlığında '_' erişim tuşu sayılır; proje adlarındaki alt çizgi korunur.</summary>
    private static string MenuHeader(string text) => text.Replace("_", "__");

    private void SetLanguage(string code, string label)
    {
        if (_store is null || _current is not { } r || code == r.Language) return;

        try
        {
            _current = _store.SetLanguage(r.Id, code);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
            return;
        }

        // Yeni kayıtlar için de varsayılan dil olur.
        try
        {
            var settings = AppSettings.Load();
            settings.RecordingLanguage = code;
            settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }

        UpdateMeta(_current);
        if (_current.Status == RecordingStatus.Hazir && !_current.AudioDeleted)
            ShowMessage($"Dil {label} olarak ayarlandı. Mevcut transkripti bu dille yeniden oluşturmak için ⋯ → 🔁 Yeniden çevir'i seçin.");
        else
            ShowMessage($"Dil {label} olarak ayarlandı; yeni kayıtlar da bu dille çevrilecek.");
    }

    private void SetProject(long? projectId, string? name)
    {
        _selectedProjectId = projectId;
        _selectedProjectName = name;
        if (_current is { } r) UpdateMeta(r);
        UpdateButtons();
    }

    // ---------------------------------------------------------------- Kayıt işlemleri

    private void Requeue()
    {
        if (_controller is null || _current is not { } r) return;
        var question = r.Status == RecordingStatus.Hazir
            ? "Kayıt yeniden metne çevrilecek; mevcut transkript yenisiyle değiştirilecek (özet korunur). Devam edilsin mi?"
            : "Kayıt yeniden metne çevrilmek üzere sıraya alınsın mı?";
        if (!Confirm(question)) return;

        try
        {
            _controller.Requeue(r.Id);
            ShowMessage("Kayıt metne çevrilmek üzere sıraya alındı.");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
        }
        Reload();
    }

    private void Copy()
    {
        if (_current is not { } r) return;
        var sb = new StringBuilder();
        sb.Append(r.Title).Append("\r\n\r\n");
        if (_hasTranscript)
            sb.Append(_transcriptText.Replace("\n", "\r\n"));
        if (!string.IsNullOrWhiteSpace(r.Summary))
        {
            if (_hasTranscript) sb.Append("\r\n\r\n");
            sb.Append("Özet\r\n\r\n").Append(r.Summary.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            ShowMessage("Panoya kopyalandı.");
        }
        catch (ExternalException ex)
        {
            AppLog.Write(ex);
            ShowMessage("Pano şu an kullanılamıyor; lütfen tekrar deneyin.", isError: true);
        }
    }

    private void Delete()
    {
        if (_controller is null || _current is not { } r) return;
        if (!Confirm($"\"{r.Title}\" kaydı silinsin mi?\n\nTranskript, özet ve ses dosyaları silinir; bu işlem geri alınamaz."))
            return;

        try
        {
            _controller.Delete(r.Id);
            _selectedId = null;
            _showDetail = false;
            ShowMessage("Kayıt silindi.");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
        }
        Reload();
    }

    // ---------------------------------------------------------------- Yardımcılar

    /// <summary>Görünümün penceresi (görünürse); mesaj kutularının sahibi.</summary>
    private Window? OwnerWindow => Window.GetWindow(this) is { IsVisible: true } window ? window : null;

    private bool Confirm(string question)
    {
        var result = OwnerWindow is { } owner
            ? MessageBox.Show(owner, question, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(question, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>Görünüm görünürse mesaj çubuğunda, değilse (panel gizli / başka sekme açık; iş bu arada bittiyse)
    /// mesaj kutusuyla gösterir.</summary>
    private void ReportError(string message)
    {
        if (!IsVisible)
            MessageBox.Show(message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            ShowMessage(message, isError: true);
    }

    private void ShowMessage(string message, bool isError = false)
    {
        MessageText.Text = message;
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "DangerText" : "Fg");
        MessageBar.Visibility = Visibility.Visible;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void MessageClose_Click(object sender, RoutedEventArgs e)
    {
        _messageTimer.Stop();
        MessageBar.Visibility = Visibility.Collapsed;
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException or InvalidOperationException or FormatException or ArgumentException
            or RecordingNotFoundException or PlanNotFoundException or ProjectNotFoundException => ex.Message,
        SqliteException => $"Veritabanı hatası. Yedekler: {AppPaths.BackupDir}",
        _ => $"Bir hata oluştu, ayrıntı: {AppPaths.LogPath}",
    };

    private bool IsInProgress(Recording r) =>
        _controller is not null && (r.Status == RecordingStatus.Cevriliyor || _controller.IsTranscribing(r.Id));

    private static string StatusName(RecordingStatus status) => status switch
    {
        RecordingStatus.Kaydediliyor => "Kaydediliyor",
        RecordingStatus.Bekliyor => "Bekliyor",
        RecordingStatus.Cevriliyor => "Çevriliyor",
        RecordingStatus.Hazir => "Hazır",
        RecordingStatus.Hata => "Hata",
        _ => "",
    };

    private static RecordingBadgeKind KindOf(RecordingStatus status) => status switch
    {
        RecordingStatus.Kaydediliyor => RecordingBadgeKind.Recording,
        RecordingStatus.Cevriliyor => RecordingBadgeKind.Running,
        RecordingStatus.Hazir => RecordingBadgeKind.Ready,
        RecordingStatus.Hata => RecordingBadgeKind.Error,
        _ => RecordingBadgeKind.Muted,
    };

    /// <summary>Liste rozeti: çevrilirken "Çevriliyor %n" (tam aşama ToolTip'te); hazırsa özet varlığı "özet ✓".</summary>
    private void SetListStatus(RecordingListItem item, Recording r)
    {
        if (IsInProgress(r) && _controller!.GetProgress(r.Id) is { } p)
        {
            item.Kind = RecordingBadgeKind.Running;
            item.StatusLabel = BadgeProgressLabel(p.Phase, p.Fraction);
            item.StatusToolTip = ProgressLabel(p.Phase, p.Fraction);
            return;
        }

        item.Kind = KindOf(r.Status);
        item.StatusLabel = r.Status == RecordingStatus.Hazir && !string.IsNullOrWhiteSpace(r.Summary)
            ? "Hazır · özet ✓"
            : StatusName(r.Status);
        item.StatusToolTip = r.Status == RecordingStatus.Hata && !string.IsNullOrWhiteSpace(r.Error) ? r.Error : null;
    }

    /// <summary>Tam aşama etiketi (ör. "Ben çevriliyor %45").</summary>
    private static string ProgressLabel(string phase, double fraction) =>
        string.Create(Inv, $"{phase} %{Percent(fraction)}");

    /// <summary>Dar rozet etiketi: "Çevriliyor %45" ya da model indirilirken "İndiriliyor %12".</summary>
    private static string BadgeProgressLabel(string phase, double fraction) =>
        string.Create(Inv, $"{(phase == TranscriptionQueue.PhaseDownloadingModel ? "İndiriliyor" : "Çevriliyor")} %{Percent(fraction)}");

    private static int Percent(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);

    private static string TranscriptPlaceholderText(Recording r) => r.Status switch
    {
        RecordingStatus.Kaydediliyor => "Kayıt sürüyor. Durdurduğunuzda ses bu bilgisayarda metne çevrilecek.",
        RecordingStatus.Bekliyor => "Metne çevrilmek için sırada bekliyor…",
        RecordingStatus.Cevriliyor => "Metne çevriliyor… (ilk çeviride Whisper modeli indirilir, ~550 MB)",
        RecordingStatus.Hata => "Transkript yok.",
        _ => "Kayıtta konuşma bulunamadı.",
    };

    private static string LanguageLabel(string code) =>
        LanguageOptions.FirstOrDefault(o => o.Code == code).Label ?? code;

    /// <summary>"Bugün 14:05", "Dün 11:30" ya da "12.11.2026 14:05".</summary>
    private static string TimeLabel(DateTimeOffset startedAt)
    {
        var local = startedAt.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return "Bugün " + local.ToString("HH:mm", Inv);
        if (local.Date == today.AddDays(-1)) return "Dün " + local.ToString("HH:mm", Inv);
        return local.ToString("dd.MM.yyyy HH:mm", Inv);
    }

    private string DurationLabel(Recording r)
    {
        if (r.Duration is { } d) return FormatDuration(d);
        return _controller?.CurrentRecordingId == r.Id ? "kaydediliyor" : "—";
    }

    /// <summary>"mm:ss"; bir saat ve üzerinde "h:mm:ss".</summary>
    private static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(Inv, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
    }

    /// <summary>Transkript zaman damgası (TranscriptFormatter ile aynı biçim).</summary>
    private static string Stamp(long ms, bool withHours)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return withHours
            ? string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(Inv, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
    }
}
