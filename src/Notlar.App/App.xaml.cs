using System;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Notlar.Core;

namespace Notlar.App;

public partial class App : Application
{
    private const string MutexName = @"Local\Notlar.App.Tek";
    private const string ShowEventName = @"Local\Notlar.App.Goster";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private ChangeListener? _listener;
    private TrayIcon? _tray;
    private PanelWindow? _panel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirstInstance)
        {
            // Zaten çalışıyor: mevcut örneğe paneli açmasını söyle ve çık.
            _showEvent.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) => { AppLog.Write(args.Exception); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write(args.ExceptionObject as Exception);

        Theme.Apply(Resources);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        NoteStore store;
        try
        {
            store = new NoteStore(AppPaths.DbPath, TimeProvider.System, new NamedEventChangeSignal(AppPaths.ChangeEventName));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show($"Not veritabanı açılamadı.\n\n{ex.Message}\n\nYedekler: {AppPaths.BackupDir}", "Notlar",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        try
        {
            store.PurgeDeleted(TimeSpan.FromDays(7));
            store.Backup(AppPaths.BackupDir, keep: 7);
            AutoStart.EnsureRegistration();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }

        _panel = new PanelWindow(store);
        _tray = new TrayIcon(
            onToggle: _panel.Toggle,
            onOpen: _panel.ShowNearTray,
            onExport: _panel.ExportMarkdown,
            onExit: ExitApp);
        _listener = new ChangeListener(AppPaths.ChangeEventName, () => Dispatcher.InvokeAsync(_panel.Reload), AppLog.Write);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => Dispatcher.InvokeAsync(_panel.ShowNearTray), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.InvokeAsync(() => Theme.Apply(Resources));
    }

    private void ExitApp()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _showWait?.Unregister(null);
        _listener?.Dispose();
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }
}
