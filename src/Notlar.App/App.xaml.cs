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

    // Hafta içi otomatik kayıt: 20 sn'de bir RecordingSchedule.Decide'a sorulur.
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private DateOnly? _consentHintDate;

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

        Theme.Apply(Resources);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

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
            onToggle: _panel.Toggle,
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
        recordings.Warning += message => _tray?.ShowBalloon("Notlar — kayıt", message, () => OpenRecordings(null), isError: true);
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
                _closeEvent, (_, _) => Dispatcher.InvokeAsync(Shutdown), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        _scheduleTimer.Tick += (_, _) => TryLog(CheckRecordingSchedule);
        _scheduleTimer.Start();
        TryLog(CheckRecordingSchedule);

        // Yedekleme ve temizlik I/O ağırlıklıdır; tepsi/panel zaten kurulduktan sonra arka planda çalıştır
        // ki UI iş parçacığı başlangıçta bloklanmasın.
        Task.Run(() =>
        {
            TryLog(() => store.PurgeDeleted(TimeSpan.FromDays(7)));
            TryLog(() => store.Backup(AppPaths.BackupDir, keep: 7));
        });
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
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.InvokeAsync(() => Theme.Apply(Resources));
    }

    private void ExitApp() => Shutdown();

    /// <summary>Tepsi menüsü: kayıt sürüyorsa durdurur, yoksa başlatır.</summary>
    private void ToggleRecording()
    {
        if (_recordings is not { } recordings) return;
        if (recordings.IsRecording) recordings.Stop();
        else recordings.Start(owner: null);
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

        switch (RecordingSchedule.Decide(now, start, end, recordings.IsRecording, recordings.IsCurrentAutomatic, last))
        {
            case ScheduleAction.Start:
                if (!RecordingController.ConsentGiven)
                {
                    // Onay verilince aynı gün başlayabilsin diye "bugün denendi" işaretlenmez; balon günde bir kez.
                    if (_consentHintDate == today) return;
                    _consentHintDate = today;
                    _tray?.ShowBalloon("Otomatik kayıt başlatılamadı",
                        "Önce bir kez elle kayıt başlatıp katılımcı bilgilendirme uyarısını onaylayın.",
                        () => OpenRecordings(null), isError: true);
                    return;
                }

                // Başarısız olsa da bugün yeniden denenmez (ör. ses aygıtı yok: 20 sn'de bir hata balonu çıkmasın).
                AppSettings.Update(s => s.AutoRecordLastDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (recordings.StartAutomatic(out var error))
                {
                    var id = recordings.CurrentRecordingId;
                    _tray?.ShowBalloon("Toplantı kaydı otomatik başladı",
                        $"{RecordingSchedule.FormatTime(end)}'da kendiliğinden duracak.", () => OpenRecordings(id));
                }
                else
                {
                    _tray?.ShowBalloon("Otomatik kayıt başlatılamadı", error ?? "Bilinmeyen hata.",
                        () => OpenRecordings(null), isError: true);
                }
                break;

            case ScheduleAction.Stop:
                var stoppedId = recordings.CurrentRecordingId;
                recordings.Stop();
                _tray?.ShowBalloon("Toplantı kaydı durdu", "Dinleyip 📝 Transkript et ile metne çevirebilirsiniz.",
                    () => OpenRecordings(stoppedId));
                break;
        }
    }

    /// <summary>Paneli Kayıtlar bölümünde açar; <paramref name="selectId"/> verilirse o kaydı seçer.</summary>
    private void OpenRecordings(long? selectId) => _panel?.ShowRecordings(selectId);

    private void OnRecordingCompleted(long id)
    {
        var title = TryGetRecordingTitle(id);
        _tray?.ShowBalloon("Notlar", $"Kayıt metne çevrildi: {title}", () => OpenRecordings(id));
    }

    private void OnRecordingFailed(long id, string message)
    {
        var title = TryGetRecordingTitle(id);
        _tray?.ShowBalloon("Kayıt metne çevrilemedi", $"{title}: {message}", () => OpenRecordings(id), isError: true);
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
        _scheduleTimer.Stop();
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
