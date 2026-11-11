using Notlar.Core;

namespace Notlar.Core.Tests;

/// <summary>
/// Üretimde App ve MCP süreçleri aynı SQLite dosyasını paylaşır, her biri kendi NoteStore'u ve
/// (yazan tarafta) NamedEventChangeSignal / ChangeListener çiftiyle. Bu testler o senaryoyu simüle eder:
/// aynı DB dosyasına iki ayrı NoteStore örneğinden eşzamanlı yazma/okuma ve gerçek named-event sinyali
/// üzerinden uçtan uca bildirim.
/// </summary>
public class IntegrationTests
{
    [Fact]
    public async Task Two_stores_writing_concurrently_see_each_others_notes()
    {
        using var f = new StoreFixture();
        var storeA = f.Store;
        var storeB = f.Reopen();

        const int perStore = 100;
        var idsA = new List<long>(perStore);
        var idsB = new List<long>(perStore);

        var ct = TestContext.Current.CancellationToken;
        var taskA = Task.Run(() =>
        {
            for (var i = 0; i < perStore; i++)
            {
                var note = storeA.Add($"A-{i}");
                idsA.Add(note.Id);
                if (i % 10 == 0)
                    storeA.SetDone(note.Id, true);
            }
        }, ct);
        var taskB = Task.Run(() =>
        {
            for (var i = 0; i < perStore; i++)
            {
                var note = storeB.Add($"B-{i}");
                idsB.Add(note.Id);
                if (i % 10 == 0)
                    storeB.SetDone(note.Id, true);
            }
        }, ct);

        await Task.WhenAll(taskA, taskB);

        var allIds = idsA.Concat(idsB).ToList();
        Assert.Equal(perStore * 2, allIds.Count);
        Assert.Equal(allIds.Count, allIds.Distinct().Count());

        Assert.Equal(perStore * 2, storeA.List(new NoteFilter()).Count);
        Assert.Equal(perStore * 2, storeB.List(new NoteFilter()).Count);
        Assert.Equal(20, storeA.List(new NoteFilter()).Count(n => n.Done));

        // Her store diğerinin yazdıklarını görebilmeli (çapraz görünürlük).
        foreach (var id in idsA)
            Assert.NotNull(storeB.Get(id));
        foreach (var id in idsB)
            Assert.NotNull(storeA.Get(id));
    }

    [Fact]
    public async Task Concurrent_reads_while_writing_do_not_throw_and_see_final_count()
    {
        using var f = new StoreFixture();
        const int n = 150;
        using var cts = new CancellationTokenSource();
        var ct = TestContext.Current.CancellationToken;
        Exception? readerException = null;

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < n; i++)
                f.Store.Add($"n-{i}");
        }, ct);

        var reader = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                    f.Store.List(new NoteFilter());
            }
            catch (Exception ex)
            {
                readerException = ex;
            }
        }, ct);

        await writer;
        cts.Cancel();
        await reader;

        Assert.Null(readerException);
        Assert.Equal(n, f.Store.List(new NoteFilter()).Count);
    }

    [Fact]
    public void Signal_round_trip_store_to_listener_end_to_end()
    {
        using var f = new StoreFixture();
        var name = @"Local\Notlar.Test." + Guid.NewGuid().ToString("N");
        using var signal = new NamedEventChangeSignal(name);
        var storeA = new NoteStore(f.DbPath, f.Time, signal);
        var storeB = f.Reopen();

        var observedCount = 0;
        Exception? observedError = null;
        using var observed = new ManualResetEventSlim();

        void OnChange()
        {
            Interlocked.Exchange(ref observedCount, storeB.List(new NoteFilter()).Count);
            if (Volatile.Read(ref observedCount) >= 1)
                observed.Set();
        }

        void OnError(Exception ex) => observedError = ex;

        using var listener = new ChangeListener(name, OnChange, OnError);

        storeA.Add("x");

        Assert.True(observed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref observedCount));
        Assert.Null(observedError);
    }

    [Fact]
    public void Listener_coalescing_still_observes_all_writes_eventually()
    {
        using var f = new StoreFixture();
        var name = @"Local\Notlar.Test." + Guid.NewGuid().ToString("N");
        using var signal = new NamedEventChangeSignal(name);
        var storeA = new NoteStore(f.DbPath, f.Time, signal);
        var storeB = f.Reopen();

        const int total = 50;
        var latestCount = 0;
        using var reachedTarget = new ManualResetEventSlim();

        void OnChange()
        {
            var count = storeB.List(new NoteFilter()).Count;
            Interlocked.Exchange(ref latestCount, count);
            if (count >= total)
                reachedTarget.Set();
        }

        using var listener = new ChangeListener(name, OnChange);

        // Hızlı ardışık yazımlar: auto-reset event ara sinyalleri birleştirebilir (coalesce),
        // ama en son uyanışta tam liste yeniden okunduğu için hiçbir yazım kaybolmamalı.
        for (var i = 0; i < total; i++)
            storeA.Add($"n-{i}");

        Assert.True(reachedTarget.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(total, Volatile.Read(ref latestCount));
    }
}
