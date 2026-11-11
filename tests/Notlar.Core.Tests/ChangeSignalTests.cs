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

        Assert.True(received.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
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

    [Fact]
    public void Listener_survives_onChange_exception_and_reports_it_to_onError()
    {
        var name = @"Local\Notlar.Test." + Guid.NewGuid().ToString("N");
        using var received = new ManualResetEventSlim();
        using var errorReceived = new ManualResetEventSlim();
        Exception? observedError = null;
        var callCount = 0;

        void OnChange()
        {
            if (Interlocked.Increment(ref callCount) == 1)
                throw new InvalidOperationException("boom");
            received.Set();
        }

        void OnError(Exception ex)
        {
            observedError = ex;
            errorReceived.Set();
        }

        using var listener = new ChangeListener(name, OnChange, OnError);
        using var signal = new NamedEventChangeSignal(name);

        signal.Notify();
        Assert.True(errorReceived.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        signal.Notify();
        Assert.True(received.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(observedError);
        Assert.Equal("boom", observedError!.Message);
    }

    [Fact]
    public void CreateOrNull_returns_working_signal_for_fresh_name()
    {
        var name = @"Local\Notlar.Test." + Guid.NewGuid().ToString("N");
        using var received = new ManualResetEventSlim();
        using var listener = new ChangeListener(name, received.Set);

        var signal = ChangeSignals.CreateOrNull(name);
        try
        {
            Assert.IsType<NamedEventChangeSignal>(signal);
            signal.Notify();
            Assert.True(received.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            (signal as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(WaitHandleCannotBeOpenedException))]
    public void CreateOrNull_returns_NullChangeSignal_and_reports_onError_when_creation_throws(Type exceptionType)
    {
        Exception? observed = null;
        IChangeSignal Throwing(string n) => throw (Exception)Activator.CreateInstance(exceptionType)!;

        var signal = ChangeSignals.CreateOrNull("whatever", Throwing, ex => observed = ex);

        Assert.Same(NullChangeSignal.Instance, signal);
        Assert.IsType(exceptionType, observed);
    }
}
