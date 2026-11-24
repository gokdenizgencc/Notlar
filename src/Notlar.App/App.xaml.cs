using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Notlar.Core;
using Forms = System.Windows.Forms;

namespace Notlar.App;

public partial class App : Application
{
    private const int ASFW_ANY = -1;

    // Kullanıcıya özel çekirdek nesne adları: tek implementasyon için AppPaths.BuildPerUserName kullanılır.
    private static readonly string MutexName = AppPaths.BuildPerUserName(@"Local\Notlar.App.Tek");
    private static readonly string ShowEventName = AppPaths.BuildPerUserName(@"Local\Notlar.App.Goster");
    private static readonly string CloseEventName = AppPaths.BuildPerUserName(@"Local\Notlar.App.Kapat");

    private Mutex? _mutex;
    private bool _isFirstInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private EventWaitHandle? _closeEvent;
    private RegisteredWaitHandle? _closeWait;
    private ChangeListener? _listener;
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private RecordingController? _recordings;
    private GlobalHotkeys? _hotkeys;

    // Hafta içi otomatik kayıt: 20 sn'de bir RecordingSchedule.Decide'a sorulur.
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private DateOnly? _consentHintDate;

    // Teams araması algılama: 3 sn'de bir mikrofon kullanımı yoklanır. Başlangıç ikinci yoklamada onaylanır (anlık
    // mikrofon denemeleri kayıt başlatmasın); bitiş 10 sn kesintisiz boşta kalınca (kısa kopmalar kaydı bölmesin).
    private readonly DispatcherTimer _teamsTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TeamsCallTrigger _teamsTrigger = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
    private readonly ITeamsCallProbe _teamsProbe = new TeamsCallProbe();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
            _isFirstInstance = isFirstInstance;
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _closeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CloseEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Write(ex);
            // Mutex serbest bırakılmaz: OnExit zaten _isFirstInstance ise bir kez bırakır;
            // burada da bırakılırsa çıkışta ApplicationException (mükerrer bırakma) oluşur.
            MessageBox.Show(
                "Notlar zaten farklı yetkiyle çalışıyor; bu nedenle başlatılamıyor.\n\nDiğer örneği kapatıp tekrar deneyin.",
                "Notlar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (!_isFirstInstance)
        {
            // Zaten çalışıyor: mevcut örneğe paneli açmasını söyle ve çık.
            // Windows ön plana getirme kısıtlaması nedeniyle, hedef sürece izin vermeden
            // Set() çağırmak panelin arka planda kalmasına yol açabilir.
            AllowSetForegroundWindow(ASFW_ANY);
            _showEvent.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) => { AppLog.Write(args.Exception); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write(args.ExceptionObject as Exception);
        // Tepsi/ContextMenuStrip olay işleyicilerindeki hatalar WinForms mesaj döngüsünden geçer;
        // aboneliksiz bırakılırsa WinForms çökme iletişim kutusunu tetikler.
        Forms.Application.ThreadException += (_, args) => AppLog.Write(args.Exception);

        Theme.Apply(Resources, AppSettings.Load(), force: true);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // ⚙ Ayarlar → Görünüm (tema modu, vurgu, yazı tipi, yoğunluk) kaydedilince anında uygulanır; görünümle
        // ilgisiz bir kayıtta Theme.Apply sözlüğe dokunmaz.
        AppSettings.Changed += OnSettingsChangedForTheme;

        var dataDirExisted = Directory.Exists(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.DataDir);
        TryLog(() => SecureDataDirOnce(justCreated: !dataDirExisted));

        NoteStore store;
        TaskStore taskStore;
        MemoryStore memoryStore;
        SkillRegistry skillRegistry;
        SkillTranslations skillTranslations;
        PlanStore planStore;
        ProjectStore projectStore;
        RecordingStore recordingStore;
        AppFlags appFlags;
        try
        {
            var signal = ChangeSignals.CreateOrNull(AppPaths.ChangeEventName, AppLog.Write);
            store = new NoteStore(AppPaths.DbPath, TimeProvider.System, signal);
            taskStore = new TaskStore(AppPaths.DbPath, TimeProvider.System, signal);
            memoryStore = new MemoryStore(AppPaths.DbPath, TimeProvider.System, signal);
            skillRegistry = new SkillRegistry(AppPaths.DbPath, TimeProvider.System, signal);
            skillTranslations = new SkillTranslations(AppPaths.DbPath, TimeProvider.System, signal);
            planStore = new PlanStore(AppPaths.DbPath, TimeProvider.System, signal);
            projectStore = new ProjectStore(AppPaths.DbPath, TimeProvider.System, signal);
            recordingStore = new RecordingStore(AppPaths.DbPath, TimeProvider.System, signal);
            appFlags = new AppFlags(AppPaths.DbPath, signal);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show($"Not veritabanı açılamadı.\n\n{ex.Message}\n\nYedekler: {AppPaths.BackupDir}", "Notlar",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        TryLog(AutoStart.EnsureRegistration);

        // Kayıt denetleyicisi panelden önce kurulur: panel Kayıtlar görünümünü ve sağ alttaki kayıt düğmesini ona bağlar.
        var recordings = _recordings = new RecordingController(recordingStore, planStore, Dispatcher);
        _panel = new PanelWindow(store, taskStore, memoryStore, skillRegistry, skillTranslations, planStore, projectStore,
            appFlags, recordings);
        _tray = new TrayIcon(
            onToggle: OnTrayClick,
            onOpen: _panel.ShowNearTray,
            onExport: _panel.ExportMarkdown,
            onExit: ExitApp,
            isRecording: () => recordings.IsRecording,
            recordingElapsed: () => recordings.Elapsed,
            onToggleRecording: ToggleRecording,
            onOpenRecordings: () => OpenRecordings(null));
        recordings.RecordingStateChanged += () => _tray?.SetRecording(recordings.IsRecording);
        recordings.Completed += OnRecordingCompleted;
        recordings.Failed += OnRecordingFailed;
        Notifier.Attach(_tray, OpenRecordings);
        recordings.Warning += message => Notifier.Show(NotificationKind.Error, "Notlar — kayıt", message, () => OpenRecordings(null));
        try
        {
            _listener = new ChangeListener(AppPaths.ChangeEventName, () => Dispatcher.InvokeAsync(() =>
            {
                _panel.Reload();
                NoteEditorWindow.ReloadAll();
            }), AppLog.Write);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Canlı güncelleme olmadan devam edilir; panel yine de her açılışta yeniden yükler.
            AppLog.Write(ex);
        }
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => Dispatcher.InvokeAsync(_panel.ShowNearTray), null, Timeout.Infinite, executeOnlyOnce: false);
        if (_closeEvent is not null)
        {
            _closeWait = ThreadPool.RegisterWaitForSingleObject(
                _closeEvent, (_, _) => Dispatcher.InvokeAsync(ShutdownNow), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        _scheduleTimer.Tick += (_, _) => TryLog(CheckRecordingSchedule);
        _scheduleTimer.Start();
        TryLog(CheckRecordingSchedule);

        _teamsTimer.Tick += (_, _) => TryLog(CheckTeamsCall);
        _teamsTimer.Start();

        // ⚙ Ayarlar → Genel: Alt+F4 ile çıkış, "Paneli açık başlat".
        TryLog(InitializeGeneralBehavior);

        // ⚙ Ayarlar → Kısayollar: sistem geneli kısayollar (ayar değişince kendini yeniden kaydeder).
        TryLog(() => _hotkeys = new GlobalHotkeys(Dispatcher, OnGlobalHotkey));

        // Yedekleme ve temizlik I/O ağırlıklıdır; tepsi/panel zaten kurulduktan sonra arka planda çalıştır
        // ki UI iş parçacığı başlangıçta bloklanmasın.
        Task.Run(() =>
        {
            TryLog(() => store.PurgeDeleted(TimeSpan.FromDays(7)));
            // Otomatik yedek: ⚙ Ayarlar → Veri ve yedekleme'deki sıklık/adet (varsayılan günlük, 7 yedek); saatte bir de denetlenir.
            TryLog(DataBackupService.RunAutomaticIfDue);
        });
        DataBackupService.StartScheduler();
    }

    /// <summary>Veri klasörüne (%APPDATA%\Notlar) bir kez, korumalı (yalnızca geçerli kullanıcı + SYSTEM
    /// tam yetki) bir DACL uygular. Klasör az önce oluşturulduysa ya da ayarlarda henüz "güvenli"
    /// işaretlenmemişse çalışır; başarısızlık sessizce günlüğe yazılır, uygulama engellenmez.
    /// %APPDATA%\Notlar dışına dokunmaz.</summary>
    private static void SecureDataDirOnce(bool justCreated)
    {
        var settings = AppSettings.Load();
        if (!justCreated && settings.DataDirSecured) return;

        var userSid = WindowsIdentity.GetCurrent().User;
        if (userSid is null) return;

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            userSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(AppPaths.DataDir).SetAccessControl(security);

        settings.DataDirSecured = true;
        settings.Save();
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

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // General: açık/koyu uygulama modu; Color/VisualStyle: Windows vurgu rengi. "Sistem" teması ve "Windows vurgu
        // rengi" seçiliyken bu değişiklikler canlı izlenir; diğer seçimlerde sonuç değişmediğinden Apply bir şey yapmaz.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.InvokeAsync(() => TryLog(() => Theme.Apply(Resources, AppSettings.Load())));
    }

    private void OnSettingsChangedForTheme() =>
        Dispatcher.InvokeAsync(() => TryLog(() => Theme.Apply(Resources, AppSettings.Load())));

    private void ExitApp() => RequestExit();

    /// <summary>Global kısayola basıldı (⚙ Ayarlar → Kısayollar).</summary>
    private void OnGlobalHotkey(GlobalHotkeyAction action)
    {
        switch (action)
        {
            case GlobalHotkeyAction.TogglePanel: _panel?.ToggleFromHotkey(); break;
            case GlobalHotkeyAction.QuickNote: _panel?.ShowQuickNote(); break;
            case GlobalHotkeyAction.ToggleRecording: ToggleRecording(); break;
        }
    }

    /// <summary>Tepsi menüsü: kayıt sürüyorsa durdurur, yoksa başlatır.</summary>
    private void ToggleRecording()
    {
        if (_recordings is not { } recordings) return;
        if (recordings.IsRecording)
        {
            var stoppedId = recordings.CurrentRecordingId;
            recordings.Stop();
            Notifier.Show(NotificationKind.RecordingManual, "Kayıt durdu",
                StoppedHint(), () => OpenRecordings(stoppedId));
        }
        else if (recordings.Start(owner: null))
        {
            var id = recordings.CurrentRecordingId;
            Notifier.Show(NotificationKind.RecordingManual, "Kayıt başladı",
                "Durdurmak için tepsi menüsünden ⏹ Kaydı durdur'u seçin.", () => OpenRecordings(id));
        }
    }

    /// <summary>Hafta içi otomatik kayıt (⚙ Ayarlar → Kayıt): pencere içinde kaydı başlatır, pencere bitince otomatik
    /// başlatılmış kaydı durdurur. Elle başlatılan kayda dokunmaz; kayıt günde en çok bir kez otomatik başlar.</summary>
    private void CheckRecordingSchedule()
    {
        if (_recordings is not { } recordings) return;
        var settings = AppSettings.Load();
        if (!settings.AutoRecordEnabled
            || !RecordingSchedule.TryParseTime(settings.AutoRecordStart, out var start)
            || !RecordingSchedule.TryParseTime(settings.AutoRecordEnd, out var end))
            return;

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        DateOnly? last = DateOnly.TryParseExact(settings.AutoRecordLastDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;

        switch (RecordingSchedule.Decide(now, start, end, recordings.IsRecording, recordings.IsCurrentAutomatic, last,
                    settings.AutoRecordDays))
        {
            case ScheduleAction.Start:
                if (!RecordingController.ConsentGiven)
                {
                    // Onay verilince aynı gün başlayabilsin diye "bugün denendi" işaretlenmez; balon günde bir kez.
                    if (_consentHintDate == today) return;
                    _consentHintDate = today;
                    Notifier.Show(NotificationKind.Error, "Otomatik kayıt başlatılamadı",
                        "Önce bir kez elle kayıt başlatıp katılımcı bilgilendirme uyarısını onaylayın.",
                        () => OpenRecordings(null));
                    return;
                }

                // Başarısız olsa da bugün yeniden denenmez (ör. ses aygıtı yok: 20 sn'de bir hata balonu çıkmasın).
                AppSettings.Update(s => s.AutoRecordLastDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (recordings.StartAutomatic(out var error))
                {
                    var id = recordings.CurrentRecordingId;
                    Notifier.Show(NotificationKind.RecordingScheduled, "Toplantı kaydı otomatik başladı",
                        $"{RecordingSchedule.FormatTime(end)}'da kendiliğinden duracak.", () => OpenRecordings(id));
                }
                else
                {
                    Notifier.Show(NotificationKind.Error, "Otomatik kayıt başlatılamadı", error ?? "Bilinmeyen hata.",
                        () => OpenRecordings(null));
                }
                break;

            case ScheduleAction.Stop:
                var stoppedId = recordings.CurrentRecordingId;
                recordings.Stop();
                Notifier.Show(NotificationKind.RecordingScheduled, "Toplantı kaydı durdu",
                    StoppedHint(), () => OpenRecordings(stoppedId));
                break;
        }
    }

    /// <summary>Teams araması otomatik kaydı (⚙ Ayarlar → Kayıt): Teams mikrofonu kullanmaya başlayınca kaydı
    /// başlatır, bırakınca yalnızca kendi başlattığı kaydı durdurur. Zaten bir kayıt sürüyorsa (elle ya da
    /// zamanlamayla) yenisini başlatmaz ve o kayda dokunmaz.</summary>
    private void CheckTeamsCall()
    {
        if (_recordings is not { } recordings) return;

        // Ayar kapalıyken yoklama yapılmaz; yalnızca Teams'in başlattığı kayıt sürüyorsa bitişi izlenmeye devam eder.
        if (!AppSettings.Load().TeamsAutoRecordEnabled && !recordings.IsCurrentTeams)
        {
            _teamsTrigger.Reset();
            return;
        }

        var action = _teamsTrigger.Update(DateTime.Now, _teamsProbe.IsMicrophoneInUse(), recordings.IsRecording,
            recordings.IsCurrentTeams);
        switch (action)
        {
            case ScheduleAction.Start:
                if (!RecordingController.ConsentGiven)
                {
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    if (_consentHintDate == today) return;
                    _consentHintDate = today;
                    Notifier.Show(NotificationKind.Error, "Otomatik kayıt başlatılamadı",
                        "Önce bir kez elle kayıt başlatıp katılımcı bilgilendirme uyarısını onaylayın.",
                        () => OpenRecordings(null));
                    return;
                }

                if (recordings.StartAutomatic(out var error, RecordingStartSource.Teams))
                {
                    var id = recordings.CurrentRecordingId;
                    Notifier.Show(NotificationKind.RecordingTeams, "Teams araması algılandı — kayıt başladı",
                        "Arama bitince kendiliğinden duracak.", () => OpenRecordings(id));
                }
                else
                {
                    Notifier.Show(NotificationKind.Error, "Otomatik kayıt başlatılamadı", error ?? "Bilinmeyen hata.",
                        () => OpenRecordings(null));
                }
                break;

            case ScheduleAction.Stop:
                var stoppedId = recordings.CurrentRecordingId;
                recordings.Stop();
                Notifier.Show(NotificationKind.RecordingTeams, "Teams araması bitti — kayıt durdu",
                    StoppedHint(), () => OpenRecordings(stoppedId));
                break;
        }
    }

    /// <summary>Paneli Kayıtlar bölümünde açar; <paramref name="selectId"/> verilirse o kaydı seçer.</summary>
    private void OpenRecordings(long? selectId) => _panel?.ShowRecordings(selectId);

    /// <summary>"Kayıt durdu" bildiriminin alt metni: otomatik çeviri açıksa kaydın kendiliğinden çevrileceğini söyler.</summary>
    private static string StoppedHint() => AppSettings.Load().AutoTranscribeAfterRecording
        ? "Kayıt metne çevriliyor; hazır olunca bildirilecek."
        : "Dinleyip 📝 Transkript et ile metne çevirebilirsiniz.";

    private void OnRecordingCompleted(long id)
    {
        var title = TryGetRecordingTitle(id);
        Notifier.Show(NotificationKind.TranscriptReady, "Notlar", $"Kayıt metne çevrildi: {title}", () => OpenRecordings(id));
    }

    private void OnRecordingFailed(long id, string message)
    {
        var title = TryGetRecordingTitle(id);
        Notifier.Show(NotificationKind.Error, "Kayıt metne çevrilemedi", $"{title}: {message}", () => OpenRecordings(id));
    }

    private string TryGetRecordingTitle(long id)
    {
        try
        {
            return _recordings?.Store.Get(id).Title ?? $"#{id}";
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return $"#{id}";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        AppSettings.Changed -= OnSettingsChangedForTheme;
        _scheduleTimer.Stop();
        _teamsTimer.Stop();
        _hotkeys?.Dispose();
        _showWait?.Unregister(null);
        _closeWait?.Unregister(null);
        // Kayıt sürüyorsa düzgün durdurulup bitirilir; kullanıcı "Transkript et" diyene kadar bekler.
        _recordings?.Dispose();
        _listener?.Dispose();
        _tray?.Dispose();
        if (_isFirstInstance) _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
