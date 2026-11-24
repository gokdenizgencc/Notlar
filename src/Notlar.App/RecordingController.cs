using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Notlar.Core;
using Notlar.Kayit;

namespace Notlar.App;

/// <summary>Süren kaydı kim başlattı? Otomatik tetikler yalnızca kendi başlattıkları kaydı durdurur.</summary>
public enum RecordingStartSource
{
    /// <summary>Kullanıcı (panel / tepsi).</summary>
    Manual,

    /// <summary>Hafta içi zamanlama (RecordingSchedule).</summary>
    Schedule,

    /// <summary>Teams araması algılandı (TeamsCallTrigger).</summary>
    Teams,
}

/// <summary>Toplantı kaydının uygulama tarafı: ses kaydedici (<see cref="MeetingRecorder"/>), çeviri kuyruğu
/// (<see cref="TranscriptionQueue"/>) ve <see cref="RecordingStore"/> burada bir araya gelir. Kayıt/kuyruk olayları
/// arka plan thread'lerinden gelir; bu sınıf hepsini UI thread'ine aktarır, dolayısıyla dışarıya açılan tüm olaylar
/// UI thread'inde tetiklenir. Claude ile özetleme / plan çıkarma işleri de burada yürütülür; böylece Kayıtlar
/// penceresi kapansa bile iş tamamlanıp kaydedilir.</summary>
public sealed class RecordingController : IDisposable
{
    /// <summary>Ses dosyalarının saklanma süresi (metin kalır).</summary>
    private static readonly TimeSpan AudioRetentionAge = TimeSpan.FromDays(14);

    private const string ClaudeNotFoundMessage =
        "'claude' komutu PATH üzerinde bulunamadı. Claude Code'un kurulu ve PATH'e eklenmiş olduğundan emin olun.";

    private readonly RecordingStore _store;
    private readonly PlanStore _planStore;
    private readonly Dispatcher _dispatcher;
    private readonly MeetingRecorder _recorder = new();
    private readonly TranscriptionQueue _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<long, (string Phase, double Fraction)> _progress = [];
    private readonly HashSet<long> _summarizing = [];
    private readonly HashSet<long> _planning = [];
    private long? _currentId;
    private RecordingStartSource _currentSource;
    private bool _disposed;

    // İlerleme olayları işçi thread'inde çok sık gelebilir (ör. model indirirken her okunan blokta); yalnızca
    // aşama ya da yüzde değiştiğinde UI'ye aktarılır. Kuyrukta tek işçi olduğundan bu alanlar tek thread'den yazılır.
    private long _lastProgressId;
    private string? _lastProgressPhase;
    private int _lastProgressPercent = -1;

    public RecordingController(RecordingStore store, PlanStore planStore, Dispatcher dispatcher)
    {
        _store = store;
        _planStore = planStore;
        _dispatcher = dispatcher;
        _queue = new TranscriptionQueue(store, new WhisperModelManager(AppPaths.ModelsDir), new WhisperTranscriber(),
            id => RecordingFiles.FolderFor(AppPaths.RecordingsDir, id));
        _queue.ProgressChanged += OnQueueProgress;
        _queue.Completed += id => Post(() => OnQueueCompleted(id));
        _queue.Failed += (id, message) => Post(() => OnQueueFailed(id, message));
        _recorder.Error += ex => Post(() => OnRecorderError(ex));
        WhisperRuntime.Diagnostic += AppLog.Info;

        // Önceki oturumda yarım kalan çeviriler (uygulama kapandı, çöktü…) yeniden kuyruğa alınır. Yarım kalan kayıt
        // Kaydedildi durumuna geçer ve kullanıcının "Transkript et" demesini bekler.
        try
        {
            foreach (var id in _store.RecoverInterrupted())
                _queue.Enqueue(id);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }

        // Eski ses dosyalarının temizliği disk I/O'dur; UI thread'ini bekletmesin.
        Task.Run(() =>
        {
            try
            {
                AudioRetention.Purge(_store, AppPaths.RecordingsDir, AudioRetentionAge);
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        });
    }

    public RecordingStore Store => _store;

    public bool IsRecording => _recorder.IsRecording;

    /// <summary>Şu an kaydedilen kaydın id'si; kayıt yoksa null.</summary>
    public long? CurrentRecordingId => _currentId;

    public TimeSpan Elapsed => _recorder.Elapsed;

    /// <summary>Süren kayıt zamanlayıcı tarafından (otomatik) başlatıldı mı?</summary>
    public bool IsCurrentAutomatic => _currentId is not null && _currentSource == RecordingStartSource.Schedule;

    /// <summary>Süren kayıt Teams araması algılanınca mı başlatıldı?</summary>
    public bool IsCurrentTeams => _currentId is not null && _currentSource == RecordingStartSource.Teams;

    /// <summary>Tek seferlik KVKK uyarısı onaylanmış mı? Onaysız otomatik kayıt başlatılmaz.</summary>
    public static bool ConsentGiven => AppSettings.Load().RecordingConsentShown;

    /// <summary>Kayıt başladı / durdu.</summary>
    public event Action? RecordingStateChanged;

    /// <summary>(id, aşama, oran 0–1) — çeviri ilerlemesi.</summary>
    public event Action<long, string, double>? ProgressChanged;

    /// <summary>Kayıt metne çevrildi.</summary>
    public event Action<long>? Completed;

    /// <summary>(id, Türkçe hata mesajı) — çeviri başarısız oldu (silinmiş kayıtlar için tetiklenmez).</summary>
    public event Action<long, string>? Failed;

    /// <summary>Kayıt sürerken bir ses kanalında oluşan hata (Türkçe mesaj); kayıt diğer kanalla sürer.</summary>
    public event Action<string>? Warning;

    /// <summary>Bir Claude işi (özetleme / plan çıkarma) başladı ya da bitti.</summary>
    public event Action? JobsChanged;

    /// <summary>Kaydın son bilinen çeviri ilerlemesi; çevrilmiyorsa null.</summary>
    public (string Phase, double Fraction)? GetProgress(long id) =>
        _progress.TryGetValue(id, out var p) ? p : null;

    /// <summary>Kayıt şu an kuyruk tarafından işleniyor mu?</summary>
    public bool IsTranscribing(long id) => _queue.CurrentId == id;

    public bool IsSummarizing(long id) => _summarizing.Contains(id);

    public bool IsPlanning(long id) => _planning.Contains(id);

    /// <summary>Yeni bir kayıt başlatır. İlk seferde tek seferlik KVKK uyarısı gösterilir; kullanıcı vazgeçerse ya da
    /// ses aygıtları açılamazsa false döner (hata kullanıcıya gösterilir).</summary>
    public bool Start(Window? owner)
    {
        if (_disposed || _recorder.IsRecording) return false;
        if (!EnsureConsent(owner)) return false;
        return StartCore(owner, RecordingStartSource.Manual, out _);
    }

    /// <summary>Zamanlayıcının ya da Teams tetiğinin başlattığı kayıt: hiçbir mesaj kutusu göstermez. KVKK uyarısı
    /// henüz onaylanmadıysa ya da ses aygıtları açılamazsa false döner (<paramref name="error"/>: Türkçe neden).
    /// Kanallardan yalnızca biri açılabildiyse kayıt başlar ve <see cref="Warning"/> ile bildirilir.</summary>
    public bool StartAutomatic(out string? error, RecordingStartSource source = RecordingStartSource.Schedule)
    {
        error = null;
        if (_disposed || _recorder.IsRecording) return false;
        if (!ConsentGiven)
        {
            error = "Kayıt uyarısı henüz onaylanmadı.";
            return false;
        }
        return StartCore(owner: null, source, out error);
    }

    private bool StartCore(Window? owner, RecordingStartSource source, out string? error)
    {
        error = null;
        var automatic = source != RecordingStartSource.Manual;

        string language;
        try
        {
            language = RecordingStore.ValidateLanguage(AppSettings.Load().RecordingLanguage);
        }
        catch (ArgumentException)
        {
            language = "tr";
        }

        Recording recording;
        try
        {
            recording = _store.Start(language);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            error = $"Kayıt başlatılamadı. {ex.Message}";
            if (!automatic) ShowMessage(owner, $"Kayıt başlatılamadı.\n\n{ex.Message}", MessageBoxImage.Error);
            return false;
        }

        var folder = RecordingFiles.FolderFor(AppPaths.RecordingsDir, recording.Id);
        try
        {
            _recorder.Start(folder);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            TryLog(() => _store.MarkFailed(recording.Id, "Kayıt başlatılamadı: " + ex.Message));
            TryLog(() => AudioRetention.DeleteRecordingAudio(AppPaths.RecordingsDir, recording.Id));
            error = ex is InvalidOperationException ? ex.Message : $"Ses kaydı başlatılamadı. {ex.Message}";
            if (!automatic)
            {
                ShowMessage(owner, ex is InvalidOperationException
                    ? ex.Message
                    : $"Ses kaydı başlatılamadı.\n\n{ex.Message}", MessageBoxImage.Error);
            }
            return false;
        }

        _currentId = recording.Id;
        _currentSource = source;
        RecordingStateChanged?.Invoke();

        // Kanallardan biri açılamadıysa kullanıcı bir kez bilgilendirilir; kayıt diğer kanalla sürer.
        string? notice = null;
        if (!_recorder.MicrophoneAvailable)
        {
            notice = "Mikrofon açılamadı; yalnızca karşı tarafın sesi (bilgisayardan gelen ses) kaydediliyor." +
                Detail(_recorder.MicrophoneError);
        }
        else if (!_recorder.LoopbackAvailable)
        {
            notice = "Bilgisayardan gelen ses kaydedilemiyor; yalnızca mikrofon (sizin sesiniz) kaydediliyor." +
                Detail(_recorder.LoopbackError);
        }
        if (notice is not null)
        {
            if (automatic) Warning?.Invoke(notice);
            else ShowMessage(owner, notice, MessageBoxImage.Information);
        }
        return true;
    }

    /// <summary>Kaydı durdurur ve bitirir (durum Kaydedildi). Kayıt kendiliğinden metne çevrilmez; kullanıcı
    /// dinleyip (isterse aralık seçip) "Transkript et" deyince <see cref="Requeue"/> ile kuyruğa alınır.</summary>
    public void Stop()
    {
        if (!_recorder.IsRecording && _currentId is null) return;
        var id = _currentId;
        _currentId = null;
        _currentSource = RecordingStartSource.Manual;
        TryLog(_recorder.Stop);

        if (id is { } recordingId)
        {
            try
            {
                _store.Finish(recordingId);
            }
            catch (RecordingNotFoundException ex)
            {
                AppLog.Write(ex);
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
                ShowMessage(null, $"Kayıt kaydedilemedi.\n\n{ex.Message}", MessageBoxImage.Error);
            }
        }
        RecordingStateChanged?.Invoke();
    }

    /// <summary>Kaydı metne çevrilmek üzere kuyruğa alır (durum Bekliyor): "Transkript et" ve "Yeniden çevir".
    /// Kullanıcı aralık seçtiyse (RecordingStore.GetRanges) yalnızca o aralıklar çevrilir.</summary>
    public void Requeue(long id)
    {
        _store.Requeue(id);
        _queue.Enqueue(id);
    }

    /// <summary>Kaydı siler (soft delete) ve ses klasörünü kaldırır. Klasör silinemezse (dosya kullanımda vb.)
    /// bir sonraki ses temizliğinde yeniden denenir.</summary>
    public void Delete(long id)
    {
        if (_currentId == id || IsTranscribing(id))
            throw new InvalidOperationException("Kaydedilen ya da metne çevrilen bir kayıt silinemez.");

        _store.Delete(id);
        _progress.Remove(id);
        try
        {
            AudioRetention.DeleteRecordingAudio(AppPaths.RecordingsDir, id);
            _store.MarkAudioDeleted(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(ex);
        }
    }

    /// <summary>✨ Özetle: transkripti `claude -p` ile özetletir ve özeti kayda yazar. Aynı kayıt için zaten bir
    /// özetleme sürüyorsa hiçbir şey yapmaz. Hata durumunda Türkçe mesajlı istisna fırlatır (veri değişmez);
    /// uygulama kapanırken <see cref="OperationCanceledException"/>.</summary>
    public async Task SummarizeAsync(long id)
    {
        if (_disposed || !_summarizing.Add(id)) return;
        JobsChanged?.Invoke();
        try
        {
            var claude = FindClaude();
            var input = ClaudeHeadless.BuildSummaryInput(_store.Get(id), _store.GetSegments(id));
            var output = await ClaudeHeadless.RunAsync(ClaudeHeadless.BuildInvocation(claude, input),
                ClaudeHeadless.DefaultTimeout, _cts.Token);
            _store.SetSummary(id, output);
        }
        finally
        {
            _summarizing.Remove(id);
            if (!_disposed) JobsChanged?.Invoke();
        }
    }

    /// <summary>🗂 Görev planla: transkriptten Claude'a görev planı çıkartır, planı oluşturur (yazar "Claude",
    /// <paramref name="projectId"/> verilmişse o projeye bağlı) ve kayda bağlar. Aynı kayıt için zaten sürüyorsa
    /// null döner. Hata durumunda Türkçe mesajlı istisna fırlatır.</summary>
    public async Task<Plan?> CreatePlanAsync(long id, long? projectId)
    {
        if (_disposed || !_planning.Add(id)) return null;
        JobsChanged?.Invoke();
        try
        {
            var claude = FindClaude();
            var recording = _store.Get(id);
            var input = ClaudeHeadless.BuildPlanInput(recording, _store.GetSegments(id));
            var output = await ClaudeHeadless.RunAsync(ClaudeHeadless.BuildInvocation(claude, input),
                ClaudeHeadless.DefaultTimeout, _cts.Token);

            // Claude çalışırken başlık değişmiş olabilir; yedek başlık için güncel hâli kullanılır.
            var (title, steps) = ClaudeHeadless.ParsePlan(output, _store.Get(id).Title);
            var plan = _planStore.CreatePlan(title, projectPath: null, steps, author: "Claude",
                projectIds: projectId is { } p ? [p] : null);
            _store.SetPlan(id, plan.Id);
            return plan;
        }
        finally
        {
            _planning.Remove(id);
            if (!_disposed) JobsChanged?.Invoke();
        }
    }

    /// <summary>Uygulama kapanırken çağrılır: kayıt sürüyorsa düzgün durdurulup bitirilir (durum Kaydedildi;
    /// kullanıcıyı bekler), çeviri kuyruğu ve Claude işleri durdurulur.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_currentId is { } id)
        {
            _currentId = null;
            TryLog(_recorder.Stop);
            TryLog(() => _store.Finish(id));
        }
        TryLog(_recorder.Dispose);
        TryLog(_cts.Cancel);
        TryLog(_queue.Dispose);
    }

    private bool EnsureConsent(Window? owner)
    {
        var settings = AppSettings.Load();
        if (settings.RecordingConsentShown) return true;

        const string text = "Toplantıyı kaydetmeden önce katılımcıları bilgilendir.\n\n" +
            "Kayıt, mikrofonunuzu ve bilgisayardan gelen sesi (karşı tarafı) kaydeder; ses yalnızca bu bilgisayarda " +
            "metne çevrilir ve ses dosyaları 14 gün sonra otomatik silinir.";
        var result = owner is not null
            ? MessageBox.Show(owner, text, "Notlar — Toplantı kaydı", MessageBoxButton.OKCancel, MessageBoxImage.Information)
            : MessageBox.Show(text, "Notlar — Toplantı kaydı", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (result != MessageBoxResult.OK) return false;

        try
        {
            settings.RecordingConsentShown = true;
            settings.Save();
        }
        catch (Exception ex)
        {
            // Kaydedilemese de kayıt başlar; uyarı bir sonraki sefer yeniden gösterilir.
            AppLog.Write(ex);
        }
        return true;
    }

    private void OnQueueProgress(long id, string phase, double fraction)
    {
        var percent = (int)Math.Clamp(Math.Round(fraction * 100), 0, 100);
        if (id == _lastProgressId && phase == _lastProgressPhase && percent == _lastProgressPercent) return;
        _lastProgressId = id;
        _lastProgressPhase = phase;
        _lastProgressPercent = percent;

        Post(() =>
        {
            _progress[id] = (phase, fraction);
            ProgressChanged?.Invoke(id, phase, fraction);
        });
    }

    private void OnQueueCompleted(long id)
    {
        _progress.Remove(id);
        Completed?.Invoke(id);
    }

    private void OnQueueFailed(long id, string message)
    {
        _progress.Remove(id);
        // Kuyruğu bekleyen bir kayıt silindiyse "bulunamadı" hatası gelir; kullanıcıya gösterilmez.
        try
        {
            _store.Get(id);
        }
        catch (RecordingNotFoundException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        Failed?.Invoke(id, message);
    }

    private void OnRecorderError(Exception ex)
    {
        AppLog.Write(ex);
        Warning?.Invoke("Kayıt sırasında bir ses kanalında hata oluştu: " + ex.Message);
    }

    /// <summary>Arka plan thread'inden gelen işi UI thread'ine aktarır; uygulama kapanıyorsa bırakır.</summary>
    private void Post(Action action)
    {
        if (_disposed) return;
        _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        });
    }

    private static string FindClaude() =>
        PlanLauncher.FindClaude() ?? throw new InvalidOperationException(ClaudeNotFoundMessage);

    private static string Detail(Exception? ex) => ex is null ? "" : $"\n\nAyrıntı: {ex.Message}";

    private static void ShowMessage(Window? owner, string text, MessageBoxImage image)
    {
        if (owner is { IsVisible: true })
            MessageBox.Show(owner, text, "Notlar", MessageBoxButton.OK, image);
        else
            MessageBox.Show(text, "Notlar", MessageBoxButton.OK, image);
    }

    private static void TryLog(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }
}
