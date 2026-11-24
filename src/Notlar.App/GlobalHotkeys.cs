using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Bir global kısayolun kayıt durumu (⚙ Ayarlar → Kısayollar satırında gösterilir).</summary>
public sealed record HotkeyStatus(bool Registered, string? Error);

/// <summary>Sistem geneli kısayolları (RegisterHotKey) ayarlara göre kaydeder. Görünmez, yalnızca ileti alan bir
/// pencereye (HWND_MESSAGE) bağlıdır; <see cref="AppSettings.Changed"/> olduğunda kombinasyonlar değiştiyse
/// uygulamayı yeniden başlatmadan yeniden kaydeder. Kayıt başarısızlığı (başka uygulama kullanıyor) uygulamayı
/// düşürmez, <see cref="StatusOf"/> ile satırda gösterilir.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const int IdBase = 0x4E00; // Uygulamaya özel; 0x0000–0xBFFF aralığında olmalı.

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Uygulamanın tek örneği (App kurar). Ayarlar arayüzü durum okumak ve kaydedici kutu odaktayken
    /// kısayolları askıya almak için kullanır; null ise (ör. tasarımcı) arayüz durum göstermez.</summary>
    public static GlobalHotkeys? Instance { get; private set; }

    private readonly Dispatcher _dispatcher;
    private readonly Action<GlobalHotkeyAction> _onPressed;
    private readonly HwndSource _window;
    private readonly Dictionary<GlobalHotkeyAction, HotkeyCombo> _registered = new();
    private readonly Dictionary<GlobalHotkeyAction, HotkeyStatus> _status = new();
    private string? _appliedKey;
    private int _suspendCount;
    private bool _disposed;

    /// <summary>Kayıt durumları değişti (arayüz satırlarını tazelesin).</summary>
    public event Action? StatusChanged;

    public GlobalHotkeys(Dispatcher dispatcher, Action<GlobalHotkeyAction> onPressed)
    {
        _dispatcher = dispatcher;
        _onPressed = onPressed;
        _window = new HwndSource(new HwndSourceParameters("Notlar.GlobalHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: görünmez, yalnızca ileti alan pencere.
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
        AppSettings.Changed += OnSettingsChanged;
        Instance = this;
        Apply(AppSettings.Load());
    }

    public HotkeyStatus? StatusOf(GlobalHotkeyAction action) => _status.GetValueOrDefault(action);

    /// <summary>Kaydedici kutu odaktayken çağrılır: kayıtlı kısayollar bırakılır ki basılan tuşlar (mevcut kısayol
    /// dahil) kutuya ulaşsın. Her <see cref="Suspend"/> için bir <see cref="Resume"/> gerekir.</summary>
    public void Suspend()
    {
        if (_suspendCount++ > 0) return;
        UnregisterAll();
    }

    public void Resume()
    {
        if (_suspendCount == 0 || --_suspendCount > 0) return;
        _appliedKey = null;
        Apply(AppSettings.Load());
    }

    private void OnSettingsChanged() => _dispatcher.InvokeAsync(() =>
    {
        if (_disposed || _suspendCount > 0) return;
        try
        {
            Apply(AppSettings.Load());
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    });

    /// <summary>Ayarlardaki açık kısayolları kaydeder. Ayarlar başka bir alan için kaydedildiyse (panel konumu vb.)
    /// ve kısayollar değişmediyse dokunmaz.</summary>
    private void Apply(AppSettings settings)
    {
        if (_disposed || _suspendCount > 0) return;

        var key = string.Join("|", Array.ConvertAll(Enum.GetValues<GlobalHotkeyAction>(),
            a => $"{settings.GetHotkeyEnabled(a)}:{settings.GetHotkey(a)}"));
        if (key == _appliedKey) return;
        _appliedKey = key;

        UnregisterAll();
        _status.Clear();

        var seen = new HashSet<HotkeyCombo>();
        foreach (var action in Enum.GetValues<GlobalHotkeyAction>())
        {
            if (!settings.GetHotkeyEnabled(action)) continue;
            if (!HotkeyCombo.TryParse(settings.GetHotkey(action), out var combo))
            {
                _status[action] = new HotkeyStatus(false, "Geçersiz kısayol.");
                continue;
            }
            if (!seen.Add(combo))
            {
                _status[action] = new HotkeyStatus(false, "Bu kısayol başka bir eyleme atanmış.");
                continue;
            }

            if (RegisterHotKey(_window.Handle, IdBase + (int)action, (int)combo.Modifiers | MOD_NOREPEAT, combo.VirtualKey))
            {
                _registered[action] = combo;
                _status[action] = new HotkeyStatus(true, null);
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                var message = error == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? $"{combo} başka bir uygulama (ya da Windows) tarafından kullanılıyor; başka bir kısayol seçin."
                    : $"{combo} kaydedilemedi: {new Win32Exception(error).Message}";
                _status[action] = new HotkeyStatus(false, message);
                AppLog.Info($"RegisterHotKey({action}, {combo}) başarısız: {error}");
            }
        }

        StatusChanged?.Invoke();
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered.Keys)
            UnregisterHotKey(_window.Handle, IdBase + (int)action);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        var id = wParam.ToInt32() - IdBase;
        if (!Enum.IsDefined(typeof(GlobalHotkeyAction), id)) return IntPtr.Zero;

        handled = true;
        var action = (GlobalHotkeyAction)id;
        try
        {
            _onPressed(action);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppSettings.Changed -= OnSettingsChanged;
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (Instance == this) Instance = null;
    }
}
