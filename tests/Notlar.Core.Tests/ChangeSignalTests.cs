using Notlar.Core;

namespace Notlar.Core.Tests;

public class ChangeSignalTests
{
    [Fact]
    public void Listener_is_called_when_signal_notifies()
    {
        var name = @"Local\Notlar.Test." + Guid.NewGuid().ToString("N");
        using var received = new ManualResetEventSlim();
        using var listener = new ChangeListener(name, received.Set);
        using var signal = new NamedEventChangeSignal(name);

        signal.Notify();

        Assert.True(received.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Listener_dispose_stops_thread_quickly()
    {
        var name = @"Local\Notlar.Test." + Guid.NewGuid().ToString("N");
        var listener = new ChangeListener(name, () => { });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        listener.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
    }
}
