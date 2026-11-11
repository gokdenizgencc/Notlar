namespace Notlar.Core;

public interface IChangeSignal
{
    void Notify();
}

public sealed class NullChangeSignal : IChangeSignal
{
    public static readonly NullChangeSignal Instance = new();
    public void Notify() { }
}

/// <summary>Süreçler arası "veri değişti" sinyali (named auto-reset event).</summary>
public sealed class NamedEventChangeSignal(string name) : IChangeSignal, IDisposable
{
    private readonly EventWaitHandle _handle = new(false, EventResetMode.AutoReset, name);

    public void Notify() => _handle.Set();
    public void Dispose() => _handle.Dispose();
}

/// <summary>Sinyali arka plan thread'inde bekler; her sinyalde onChange çağrılır (UI thread'e dispatch çağıranın işi).
/// onChange içinde oluşan hatalar işlemi sonlandırmaz; onError'a iletilir (verilmişse) ve dinleme sürer.</summary>
public sealed class ChangeListener : IDisposable
{
    private readonly EventWaitHandle _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public ChangeListener(string name, Action onChange, Action<Exception>? onError = null)
    {
        _handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _thread = new Thread(() => Loop(onChange, onError)) { IsBackground = true, Name = "Notlar.ChangeListener" };
        _thread.Start();
    }

    private void Loop(Action onChange, Action<Exception>? onError)
    {
        WaitHandle[] handles = [_handle, _cts.Token.WaitHandle];
        while (WaitHandle.WaitAny(handles) == 0)
        {
            try
            {
                onChange();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(1));
        _handle.Dispose();
        _cts.Dispose();
    }
}
