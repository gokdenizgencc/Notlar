using System.Collections.Concurrent;
using Notlar.Core;

namespace Notlar.Kayit;

/// <summary>Kayıtları arka planda tek tek metne çeviren iş kuyruğu (tek işçi thread, BelowNormal öncelik, arka plan).
/// İş akışı: ses dosyalarını bul → MarkTranscribing → (gerekirse) modeli indir → ben.wav ve karsi.wav'ı (var olanları;
/// kullanıcı aralık seçtiyse yalnızca RecordingStore.GetRanges aralıklarını) çevir → TranscriptMerger.Merge →
/// SaveTranscript (durum Hazir). Hiç konuşma yoksa boş transkriptle Hazir.
/// Hata → MarkFailed(Türkçe mesaj) + <see cref="Failed"/>. <see cref="Dispose"/> ile iptal edilen iş Cevriliyor
/// durumunda kalır; bir sonraki açılışta RecordingStore.RecoverInterrupted onu yeniden Bekliyor yapar.
/// Olaylar işçi thread'inden tetiklenir (UI'ye aktarmak çağıranın işi); olay işleyicisindeki hata kuyruğu durdurmaz.</summary>
public sealed class TranscriptionQueue : IDisposable
{
    public const string PhaseDownloadingModel = "Model indiriliyor";
    public const string PhaseMicrophone = "Ben çevriliyor";
    public const string PhaseLoopback = "Karşı taraf çevriliyor";

    private readonly RecordingStore _store;
    private readonly WhisperModelManager _models;
    private readonly WhisperTranscriber _transcriber;
    private readonly Func<long, string> _folderForRecording;
    private readonly BlockingCollection<long> _queue = new();
    private readonly HashSet<long> _pending = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;
    private long _currentId;
    private bool _disposed;

    /// <param name="folderForRecording">Kayıt id'sinden ses klasörünü verir; App'te genelde
    /// <c>id =&gt; RecordingFiles.FolderFor(AppPaths.RecordingsDir, id)</c>.</param>
    public TranscriptionQueue(RecordingStore store, WhisperModelManager models, WhisperTranscriber transcriber,
        Func<long, string> folderForRecording)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _folderForRecording = folderForRecording ?? throw new ArgumentNullException(nameof(folderForRecording));
        _worker = new Thread(WorkerLoop)
        {
            Name = "Notlar.TranscriptionQueue",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    /// <summary>(id, aşama, oran 0–1). Aşama: <see cref="PhaseDownloadingModel"/>, <see cref="PhaseMicrophone"/>,
    /// <see cref="PhaseLoopback"/>.</summary>
    public event Action<long, string, double>? ProgressChanged;

    /// <summary>Transkript kaydedildi (durum Hazir).</summary>
    public event Action<long>? Completed;

    /// <summary>(id, Türkçe hata mesajı). Kayıt MarkFailed ile Hata durumuna alınmıştır (kayıt silinmediyse).</summary>
    public event Action<long, string>? Failed;

    /// <summary>Şu an işlenen kaydın id'si; boştaysa null.</summary>
    public long? CurrentId
    {
        get
        {
            var id = Interlocked.Read(ref _currentId);
            return id == 0 ? null : id;
        }
    }

    /// <summary>Kaydı kuyruğa ekler. Aynı id zaten kuyrukta bekliyor ya da işleniyorsa yok sayılır (false döner).</summary>
    public bool Enqueue(long id)
    {
        lock (_pending)
        {
            if (_disposed || !_pending.Add(id)) return false;
        }
        try
        {
            _queue.Add(id);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Dispose sırasında CompleteAdding çağrıldı.
            lock (_pending) _pending.Remove(id);
            return false;
        }
    }

    /// <summary>Kuyruğu durdurur: bekleyen işler bırakılır, süren iş iptal edilir (en çok birkaç sn beklenir).</summary>
    public void Dispose()
    {
        lock (_pending)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts.Cancel();
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _worker) _worker.Join(TimeSpan.FromSeconds(5));
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var id in _queue.GetConsumingEnumerable(_cts.Token))
            {
                Interlocked.Exchange(ref _currentId, id);
                try
                {
                    Process(id, _cts.Token);
                }
                finally
                {
                    Interlocked.Exchange(ref _currentId, 0);
                    lock (_pending) _pending.Remove(id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose.
        }
    }

    private void Process(long id, CancellationToken ct)
    {
        try
        {
            var recording = _store.Get(id);
            var folder = _folderForRecording(id);
            var micPath = Path.Combine(folder, RecordingFiles.MicrophoneFileName);
            var loopbackPath = Path.Combine(folder, RecordingFiles.LoopbackFileName);
            var hasMic = !recording.AudioDeleted && File.Exists(micPath);
            var hasLoopback = !recording.AudioDeleted && File.Exists(loopbackPath);
            if (!hasMic && !hasLoopback)
            {
                Fail(id, "Ses dosyası bulunamadı.");
                return;
            }

            var ranges = _store.GetRanges(id);
            _store.MarkTranscribing(id);

            string modelPath;
            try
            {
                if (!_models.IsModelPresent) Report(id, PhaseDownloadingModel, 0);
                modelPath = _models.EnsureModelAsync(Progress(id, PhaseDownloadingModel), ct).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Fail(id, "Model indirilemedi: " + ex.Message);
                return;
            }

            IReadOnlyList<TranscriptSegment> mine = [];
            IReadOnlyList<TranscriptSegment> theirs = [];
            if (hasMic)
            {
                Report(id, PhaseMicrophone, 0);
                mine = _transcriber.TranscribeAsync(micPath, Speaker.Ben, recording.Language, modelPath,
                    Progress(id, PhaseMicrophone), ct, ranges).GetAwaiter().GetResult();
            }
            if (hasLoopback)
            {
                Report(id, PhaseLoopback, 0);
                theirs = _transcriber.TranscribeAsync(loopbackPath, Speaker.KarsiTaraf, recording.Language, modelPath,
                    Progress(id, PhaseLoopback), ct, ranges).GetAwaiter().GetResult();
            }

            ct.ThrowIfCancellationRequested();
            _store.SaveTranscript(id, TranscriptMerger.Merge(mine, theirs));
            Raise(() => Completed?.Invoke(id));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Uygulama kapanıyor: kayıt Cevriliyor kalır, açılışta RecoverInterrupted kurtarır.
        }
        catch (RecordingNotFoundException ex)
        {
            Raise(() => Failed?.Invoke(id, ex.Message));
        }
        catch (Exception ex)
        {
            Fail(id, "Metne çevrilemedi: " + ex.Message);
        }
    }

    private void Fail(long id, string message)
    {
        try
        {
            _store.MarkFailed(id, message);
        }
        catch (Exception ex) when (ex is RecordingNotFoundException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            // Kayıt silinmiş / veritabanına yazılamıyor; olayla yine de bildir.
        }
        Raise(() => Failed?.Invoke(id, message));
    }

    private void Report(long id, string phase, double fraction) =>
        Raise(() => ProgressChanged?.Invoke(id, phase, fraction));

    /// <summary>Raporları aynı thread'de, sırayla iletir (Progress&lt;T&gt; gibi thread havuzuna atmaz).</summary>
    private IProgress<double> Progress(long id, string phase) => new SyncProgress(f => Report(id, phase, f));

    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Olay işleyicisindeki hata kuyruğu durdurmamalı; App kendi tarafında günlüğe yazar.
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
