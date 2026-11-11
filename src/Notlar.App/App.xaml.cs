using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Notlar.Core;
using Forms = System.Windows.Forms;

namespace Notlar.App;

public partial class App : Application
{
    private const string MutexName = @"Local\Notlar.App.Tek";
    private const string ShowEventName = @"Local\Notlar.App.Goster";
    private const int ASFW_ANY = -1;

    private Mutex? _mutex;
    private bool _isFirstInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private ChangeListener? _listener;
    private TrayIcon? _tray;
    private PanelWindow? _panel;

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

        NoteStore store;
        TaskStore taskStore;
        MemoryStore memoryStore;
        SkillRegistry skillRegistry;
        try
        {
            var signal = ChangeSignals.CreateOrNull(AppPaths.ChangeEventName, AppLog.Write);
            store = new NoteStore(AppPaths.DbPath, TimeProvider.System, signal);
            taskStore = new TaskStore(AppPaths.DbPath, TimeProvider.System, signal);
            memoryStore = new MemoryStore(AppPaths.DbPath, TimeProvider.System, signal);
            skillRegistry = new SkillRegistry(AppPaths.DbPath, TimeProvider.System, signal);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show($"Not veritabanı açılamadı.\n\n{ex.Message}\n\nYedekler: {AppPaths.BackupDir}", "Notlar",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        TryLog(() => store.PurgeDeleted(TimeSpan.FromDays(7)));
        TryLog(() => store.Backup(AppPaths.BackupDir, keep: 7));
        TryLog(AutoStart.EnsureRegistration);

        _panel = new PanelWindow(store, taskStore, memoryStore, skillRegistry);
        _tray = new TrayIcon(
            onToggle: _panel.Toggle,
            onOpen: _panel.ShowNearTray,
            onExport: _panel.ExportMarkdown,
            onExit: ExitApp);
        try
        {
            _listener = new ChangeListener(AppPaths.ChangeEventName, () => Dispatcher.InvokeAsync(_panel.Reload), AppLog.Write);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Canlı güncelleme olmadan devam edilir; panel yine de her açılışta yeniden yükler.
            AppLog.Write(ex);
        }
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => Dispatcher.InvokeAsync(_panel.ShowNearTray), null, Timeout.Infinite, executeOnlyOnce: false);
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

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _showWait?.Unregister(null);
        _listener?.Dispose();
        _tray?.Dispose();
        if (_isFirstInstance) _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
