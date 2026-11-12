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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Toplantı kayıtları penceresi (tekil; <see cref="Open"/> açıksa öne getirir). Solda kayıt listesi, sağda
/// seçili kaydın başlığı (düzenlenebilir), durumu/çeviri ilerlemesi, dil ve proje seçimi, transkripti ve özeti;
/// Claude işlemleri (✨ Özetle, 🗂 Görev planla, ▶ Planı başlat, 💬 Claude'a bağla) ve kayıt işlemleri
/// (🔁 Yeniden çevir, 📋 Kopyala, 🗑 Sil). Veri değişiklik sinyaliyle (<see cref="ReloadAll"/>) yenilenir, seçim korunur.</summary>
public partial class RecordingsWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static RecordingsWindow? _instance;

    private static readonly IReadOnlyList<LanguageOption> LanguageOptions =
    [
        new("tr", "Türkçe"),
        new("en", "İngilizce"),
        new("auto", "Otomatik"),
    ];

    private readonly RecordingController _controller;
    private readonly RecordingStore _store;
    private readonly PlanStore _planStore;
    private readonly ProjectStore _projectStore;
    private readonly ObservableCollection<RecordingListItem> _items = [];
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private long? _selectedId;
    private Recording? _current;
    private bool _hasTranscript;
    private long? _selectedProjectId;
    private bool _suppressEvents;
    private bool _closed;

    public sealed record LanguageOption(string Code, string Label);

    /// <summary>Listede bir kayıt satırı. Çeviri ilerlemesi geldikçe durum etiketi yerinde güncellenir.</summary>
    public sealed class RecordingListItem : INotifyPropertyChanged
    {
        private string _title = "";
        private string _meta = "";
        private string _statusLabel = "";
        private Brush _statusBrush = Brushes.Gray;

        public RecordingListItem(long id) => Id = id;

        public long Id { get; }
        public string Title { get => _title; set => Set(ref _title, value); }
        public string Meta { get => _meta; set => Set(ref _meta, value); }
        public string StatusLabel { get => _statusLabel; set => Set(ref _statusLabel, value); }
        public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private RecordingsWindow(RecordingController controller, PlanStore planStore, ProjectStore projectStore)
    {
        _controller = controller;
        _store = controller.Store;
        _planStore = planStore;
        _projectStore = projectStore;
        InitializeComponent();

        RecordingsList.ItemsSource = _items;
        LanguageBox.ItemsSource = LanguageOptions;

        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); MessageBar.Visibility = Visibility.Collapsed; };

        _controller.RecordingStateChanged += OnRecordingStateChanged;
        _controller.ProgressChanged += OnProgressChanged;
        _controller.Completed += OnTranscriptionFinished;
        _controller.Failed += OnTranscriptionFailed;
        _controller.JobsChanged += UpdateButtons;

        Reload();
        UpdateRecordState();
    }

    /// <summary>Tekil pencereyi açar (yoksa oluşturur) ya da öne getirir; <paramref name="selectId"/> verilirse o
    /// kaydı seçer.</summary>
    public static void Open(RecordingController controller, PlanStore planStore, ProjectStore projectStore, long? selectId = null)
    {
        if (_instance is null)
        {
            _instance = new RecordingsWindow(controller, planStore, projectStore);
            _instance.Show();
        }

        if (selectId is { } id) _instance.SelectRecording(id);

        if (_instance.WindowState == WindowState.Minimized)
            _instance.WindowState = WindowState.Normal;
        _instance.Activate();
    }

    /// <summary>Veri dışarıdan (başka süreç / MCP / bu süreçteki başka bir pencere) değiştiğinde çağrılır.</summary>
    public static void ReloadAll() => _instance?.Reload();

    private void SelectRecording(long id)
    {
        CommitTitle();
        _selectedId = id;
        Reload();
        if (RecordingsList.SelectedItem is { } item) RecordingsList.ScrollIntoView(item);
    }

    // ---------------------------------------------------------------- Yükleme

    private void Reload()
    {
        if (_closed) return;

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
            selected ??= _items.FirstOrDefault();
            RecordingsList.SelectedItem = selected;
            _selectedId = selected?.Id;
        }
        finally
        {
            _suppressEvents = false;
        }

        EmptyListText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshProjects();
        ShowDetail();
    }

    private void Fill(RecordingListItem item, Recording r)
    {
        item.Title = r.Title;
        item.Meta = $"{r.StartedAt.ToString("dd.MM.yyyy HH:mm", Inv)} · {DurationLabel(r)}";
        item.StatusLabel = StatusLabel(r);
        item.StatusBrush = StatusBrush(r.Status);
    }

    private void RefreshProjects()
    {
        IReadOnlyList<Project> projects;
        try
        {
            projects = _projectStore.List();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            projects = [];
        }

        var options = new List<ProjectComboItem> { new(null, "(proje yok)") };
        options.AddRange(projects.Select(p => new ProjectComboItem(p.Id, p.Name)));

        _suppressEvents = true;
        try
        {
            ProjectBox.ItemsSource = options;
            var selected = options.FirstOrDefault(o => o.Id == _selectedProjectId) ?? options[0];
            ProjectBox.SelectedItem = selected;
            _selectedProjectId = selected.Id;
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void ShowDetail()
    {
        if (_selectedId is not { } id)
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

        MetaText.Text = BuildMeta(r);
        UpdateStatus(r);

        _suppressEvents = true;
        try
        {
            LanguageBox.SelectedItem = LanguageOptions.FirstOrDefault(o => o.Code == r.Language) ?? LanguageOptions[0];
        }
        finally
        {
            _suppressEvents = false;
        }

        // Metin değişmediyse yeniden atanmaz (kaydırma konumu korunur).
        var transcript = TranscriptFormatter.Format(segments);
        if (TranscriptBox.Text != transcript) TranscriptBox.Text = transcript;
        TranscriptPlaceholder.Text = TranscriptPlaceholderText(r);
        TranscriptPlaceholder.Visibility = _hasTranscript ? Visibility.Collapsed : Visibility.Visible;

        var summary = r.Summary ?? "";
        if (SummaryBox.Text != summary) SummaryBox.Text = summary;
        SummaryPlaceholder.Visibility = summary.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (otherRecording)
        {
            TranscriptBox.ScrollToHome();
            SummaryBox.ScrollToHome();
        }

        UpdateButtons();
    }

    private void HideDetail()
    {
        _current = null;
        _hasTranscript = false;
        DetailPanel.Visibility = Visibility.Collapsed;
        NoSelectionText.Visibility = Visibility.Visible;
    }

    private string BuildMeta(Recording r)
    {
        var parts = new List<string>
        {
            r.StartedAt.ToString("dd.MM.yyyy HH:mm", Inv),
            r.Status == RecordingStatus.Kaydediliyor ? "kayıt sürüyor" : $"süre {DurationLabel(r)}",
        };
        if (r.PlanId is { } planId)
        {
            string planLabel;
            try
            {
                planLabel = $"Plan #{planId}: {_planStore.GetPlan(planId).Plan.Title}";
            }
            catch (PlanNotFoundException)
            {
                planLabel = $"Plan #{planId} (silinmiş)";
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
                planLabel = $"Plan #{planId}";
            }
            parts.Add(planLabel);
        }
        if (r.AudioDeleted) parts.Add("ses dosyaları silindi");
        return string.Join(" · ", parts);
    }

    private void UpdateStatus(Recording r)
    {
        StatusBadge.Background = StatusBrush(r.Status);
        StatusBadgeText.Text = StatusName(r.Status);

        var progress = r.Status == RecordingStatus.Cevriliyor || _controller.IsTranscribing(r.Id)
            ? _controller.GetProgress(r.Id)
            : null;
        if (progress is { } p)
        {
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
        if (_closed || _current is not { } r) return;
        var id = r.Id;
        var ready = r.Status == RecordingStatus.Hazir;
        var summarizing = _controller.IsSummarizing(id);
        var planning = _controller.IsPlanning(id);
        var busyAudio = r.Status is RecordingStatus.Kaydediliyor or RecordingStatus.Cevriliyor
            || _controller.CurrentRecordingId == id || _controller.IsTranscribing(id);

        SummarizeButton.Content = summarizing ? "✨ Özetleniyor…" : "✨ Özetle";
        SummarizeButton.IsEnabled = ready && _hasTranscript && !summarizing;

        PlanButton.Content = planning ? "🗂 Planlanıyor…" : "🗂 Görev planla";
        PlanButton.IsEnabled = ready && _hasTranscript && !planning;

        RunPlanButton.IsEnabled = r.PlanId is not null;
        ClaudeButton.IsEnabled = ready;

        RequeueButton.IsEnabled = (r.Status is RecordingStatus.Hata or RecordingStatus.Hazir) && !r.AudioDeleted && !busyAudio;
        RequeueButton.ToolTip = r.AudioDeleted
            ? "Ses dosyaları silinmiş; kayıt yeniden çevrilemez."
            : "Kaydı yeniden metne çevir (ör. dili değiştirdikten sonra)";

        CopyButton.IsEnabled = _hasTranscript || !string.IsNullOrWhiteSpace(r.Summary);

        DeleteButton.IsEnabled = !busyAudio && !summarizing && !planning;
        DeleteButton.ToolTip = busyAudio
            ? "Kaydedilen ya da metne çevrilen kayıt silinemez."
            : "Kaydı, transkripti ve ses dosyalarını sil";
    }

    // ---------------------------------------------------------------- Kayıt durumu

    private void OnRecordingStateChanged()
    {
        UpdateRecordState();
        if (_controller.CurrentRecordingId is { } id) _selectedId = id;
        Reload();
    }

    private void UpdateRecordState()
    {
        if (_controller.IsRecording)
        {
            RecordButton.Content = "⏹ Durdur";
            RecordButton.Background = (Brush)FindResource("Danger");
            RecordButton.Foreground = Brushes.White;
            RecordElapsedText.Visibility = Visibility.Visible;
            UpdateElapsed();
            _elapsedTimer.Start();
        }
        else
        {
            RecordButton.Content = "⏺ Kaydı başlat";
            RecordButton.ClearValue(BackgroundProperty);
            RecordButton.ClearValue(ForegroundProperty);
            RecordElapsedText.Visibility = Visibility.Collapsed;
            _elapsedTimer.Stop();
        }
    }

    private void UpdateElapsed() => RecordElapsedText.Text = "● " + FormatDuration(_controller.Elapsed);

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsRecording) _controller.Stop();
        else _controller.Start(this);
    }

    private void OnProgressChanged(long id, string phase, double fraction)
    {
        if (_items.FirstOrDefault(i => i.Id == id) is { } item)
        {
            item.StatusLabel = ProgressLabel(phase, fraction);
            item.StatusBrush = StatusBrush(RecordingStatus.Cevriliyor);
        }
        if (_current?.Id == id)
        {
            StatusBadge.Background = StatusBrush(RecordingStatus.Cevriliyor);
            StatusBadgeText.Text = StatusName(RecordingStatus.Cevriliyor);
            StatusText.Text = ProgressLabel(phase, fraction);
            TranscribeProgress.Value = Math.Clamp(fraction, 0, 1);
            TranscribeProgress.Visibility = Visibility.Visible;
        }
    }

    private void OnTranscriptionFinished(long id) => Reload();

    private void OnTranscriptionFailed(long id, string message) => Reload();

    // ---------------------------------------------------------------- Seçim, başlık, dil, proje

    private void RecordingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        CommitTitle();
        _selectedId = (RecordingsList.SelectedItem as RecordingListItem)?.Id;
        ShowDetail();
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
        if (_current is not { } r) return;
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

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _current is not { } r || LanguageBox.SelectedItem is not LanguageOption option) return;
        if (option.Code == r.Language) return;

        try
        {
            _current = _store.SetLanguage(r.Id, option.Code);
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
            settings.RecordingLanguage = option.Code;
            settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }

        MetaText.Text = BuildMeta(_current);
        if (_current.Status == RecordingStatus.Hazir && !_current.AudioDeleted)
            ShowMessage($"Dil {option.Label} olarak ayarlandı. Mevcut transkripti bu dille yeniden oluşturmak için 🔁 Yeniden çevir'e basın.");
        else
            ShowMessage($"Dil {option.Label} olarak ayarlandı; yeni kayıtlar da bu dille çevrilecek.");
    }

    private void ProjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _selectedProjectId = (ProjectBox.SelectedItem as ProjectComboItem)?.Id;
    }

    // ---------------------------------------------------------------- Claude işlemleri

    private async void SummarizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } r) return;
        if (!string.IsNullOrWhiteSpace(r.Summary) && !Confirm("Bu kaydın zaten bir özeti var. Yeni özet mevcut özetin yerine geçecek. Devam edilsin mi?"))
            return;

        var id = r.Id;
        try
        {
            await _controller.SummarizeAsync(id);
            if (!_closed)
            {
                if (_current?.Id == id) ShowMessage("Özet hazır.");
                Reload();
            }
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

    private async void PlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } r) return;
        if (r.PlanId is { } existing && !Confirm($"Bu kayıttan zaten Plan #{existing} oluşturuldu. Yeni bir plan oluşturulsun mu?"))
            return;

        var id = r.Id;
        var projectId = _selectedProjectId;
        try
        {
            var plan = await _controller.CreatePlanAsync(id, projectId);
            if (plan is null) return;
            var message = $"Plan #{plan.Id} oluşturuldu: {plan.Title}";
            if (_closed)
            {
                MessageBox.Show(message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ShowMessage(message + " (Panelde Planlar sekmesinde görebilirsiniz.)");
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

    private void RunPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.PlanId is not { } planId) return;
        PlanRunFlow.Run(this, planId, _planStore, _projectStore, message => ShowMessage(message, isError: true));
    }

    private void ClaudeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } r) return;
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

    // ---------------------------------------------------------------- Kayıt işlemleri

    private void RequeueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } r) return;
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

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } r) return;
        var sb = new StringBuilder();
        sb.Append(r.Title).Append("\r\n\r\n");
        if (_hasTranscript)
            sb.Append(TranscriptBox.Text.Replace("\n", "\r\n"));
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

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } r) return;
        if (!Confirm($"\"{r.Title}\" kaydı silinsin mi?\n\nTranskript, özet ve ses dosyaları silinir; bu işlem geri alınamaz."))
            return;

        try
        {
            _controller.Delete(r.Id);
            _selectedId = null;
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

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Pencere açıksa mesaj çubuğunda, kapandıysa (iş pencere kapandıktan sonra bittiyse) mesaj kutusuyla
    /// gösterir.</summary>
    private void ReportError(string message)
    {
        if (_closed)
            MessageBox.Show(message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            ShowMessage(message, isError: true);
    }

    private void ShowMessage(string message, bool isError = false)
    {
        if (_closed) return;
        MessageText.Text = message;
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Danger" : "Fg");
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

    private static string StatusName(RecordingStatus status) => status switch
    {
        RecordingStatus.Kaydediliyor => "Kaydediliyor",
        RecordingStatus.Bekliyor => "Bekliyor",
        RecordingStatus.Cevriliyor => "Çevriliyor",
        RecordingStatus.Hazir => "Hazır",
        RecordingStatus.Hata => "Hata",
        _ => "",
    };

    /// <summary>Liste rozeti: çevrilirken aşama + yüzde (ör. "Ben çevriliyor %45").</summary>
    private string StatusLabel(Recording r)
    {
        if ((r.Status == RecordingStatus.Cevriliyor || _controller.IsTranscribing(r.Id))
            && _controller.GetProgress(r.Id) is { } p)
            return ProgressLabel(p.Phase, p.Fraction);
        return StatusName(r.Status);
    }

    private static string ProgressLabel(string phase, double fraction) =>
        string.Create(Inv, $"{phase} %{(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)}");

    private Brush StatusBrush(RecordingStatus status) => status switch
    {
        RecordingStatus.Kaydediliyor => (Brush)FindResource("Danger"),
        RecordingStatus.Bekliyor => (Brush)FindResource("Muted"),
        RecordingStatus.Cevriliyor => (Brush)FindResource("Accent"),
        RecordingStatus.Hazir => (Brush)FindResource("Success"),
        RecordingStatus.Hata => (Brush)FindResource("Danger"),
        _ => (Brush)FindResource("Muted"),
    };

    private static string TranscriptPlaceholderText(Recording r) => r.Status switch
    {
        RecordingStatus.Kaydediliyor => "Kayıt sürüyor. Durdurduğunuzda ses bu bilgisayarda metne çevrilecek.",
        RecordingStatus.Bekliyor => "Metne çevrilmek için sırada bekliyor…",
        RecordingStatus.Cevriliyor => "Metne çevriliyor… (ilk çeviride Whisper modeli indirilir, ~550 MB)",
        RecordingStatus.Hata => "Transkript yok.",
        _ => "Kayıtta konuşma bulunamadı.",
    };

    private string DurationLabel(Recording r)
    {
        if (r.Duration is { } d) return FormatDuration(d);
        return _controller.CurrentRecordingId == r.Id ? "kaydediliyor" : "—";
    }

    /// <summary>"mm:ss"; bir saat ve üzerinde "h:mm:ss".</summary>
    private static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? string.Create(Inv, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(Inv, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc başlık düzenlemesinden vazgeçer.
        if (e.Key == Key.Escape && TitleBox.IsKeyboardFocusWithin && _current is { } r)
        {
            e.Handled = true;
            TitleBox.Text = r.Title;
            TitleBox.SelectAll();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => CommitTitle();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _elapsedTimer.Stop();
        _messageTimer.Stop();
        _controller.RecordingStateChanged -= OnRecordingStateChanged;
        _controller.ProgressChanged -= OnProgressChanged;
        _controller.Completed -= OnTranscriptionFinished;
        _controller.Failed -= OnTranscriptionFailed;
        _controller.JobsChanged -= UpdateButtons;
        if (_instance == this) _instance = null;
    }
}
