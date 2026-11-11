using Microsoft.Data.Sqlite;
using Notlar.Core;

namespace Notlar.Core.Tests;

public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.CreateCustomTimeZone("TR+3", TimeSpan.FromHours(3), "TR+3", "TR+3");

    private DateTimeOffset _utcNow = start.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override TimeZoneInfo LocalTimeZone => Tz;

    public void Set(DateTimeOffset local) => _utcNow = local.ToUniversalTime();
    public void Advance(TimeSpan by) => _utcNow += by;
}

public sealed class CountingSignal : IChangeSignal
{
    private int _count;
    public int Count => _count;
    public void Notify() => Interlocked.Increment(ref _count);
}

public sealed class StoreFixture : IDisposable
{
    public static readonly TimeSpan Tr = TimeSpan.FromHours(3);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "notlar-test-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "notlar.db");
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 14, 32, 0, Tr));
    public CountingSignal Signal { get; } = new();
    public NoteStore Store { get; }

    public StoreFixture() => Store = new NoteStore(DbPath, Time, Signal);

    public NoteStore Reopen() => new(DbPath, Time, Signal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
