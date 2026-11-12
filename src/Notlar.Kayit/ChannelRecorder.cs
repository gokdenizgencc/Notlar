using System.Diagnostics;
using NAudio.Wave;

namespace Notlar.Kayit;

/// <summary>Tek bir ses kaynağını (mikrofon ya da sistem sesi) 16 kHz mono 16 bit PCM WAV dosyasına kaydeder.
/// Yakalanan veri (IEEE float / PCM16 / PCM24 / PCM32, n kanal, WaveFormatExtensible dahil) kanalların ortalamasıyla
/// mono'ya indirilir, <see cref="NAudio.Dsp.WdlResampler"/> ile 16 kHz'e çevrilir. Dosya 5 sn'de bir diske boşaltılır;
/// süreç çökse bile WAV başlığı büyük ölçüde doğru kalır.
/// <para><c>fillSilence</c>: WASAPI loopback, sistemde ses çalmazken hiç veri vermez. Bu durumda kaydın zaman ekseni
/// kaymasın diye, saat (Stopwatch) ile yazılan örnek sayısı arasındaki fark 100 ms'yi aşınca araya sıfır (sessizlik)
/// eklenir. İki kanal ayrı dosyalara yazıldığı için bu, birleştirmede zaman hizasını korur.</para>
/// <para>Sahiplik: verilen <see cref="IWaveIn"/> bu sınıfa geçer ve <see cref="Dispose"/> ile kapatılır.
/// <see cref="Error"/> olayı yakalama/zamanlayıcı thread'inden tetiklenir (UI'ye aktarmak çağıranın işi).</para></summary>
public sealed class ChannelRecorder : IDisposable
{
    public const int SampleRate = 16000;

    private const int SilenceGapSamples = SampleRate / 10;                       // 100 ms
    private static readonly TimeSpan DataPauseThreshold = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SilenceTimerInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly IWaveIn _capture;
    private readonly bool _fillSilence;
    private readonly SampleKind _kind;
    private readonly int _channels;
    private readonly int _frameSize;
    private readonly MonoResampler _resampler;
    private readonly WaveFileWriter _writer;
    private readonly Stopwatch _clock = new();
    private readonly object _sync = new();

    private Timer? _silenceTimer;
    private float[] _mono = [];
    private float[] _resampled = [];
    private byte[] _pcm = [];
    private long _written;
    private TimeSpan _lastData;
    private TimeSpan _lastFlush;
    private bool _started;
    private bool _closed;
    private bool _disposed;

    /// <summary>WAV dosyası hemen oluşturulur (varsa üzerine yazılır). Yakalama biçimi desteklenmiyorsa
    /// <see cref="NotSupportedException"/>.</summary>
    public ChannelRecorder(IWaveIn capture, string wavPath, bool fillSilence)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);

        var format = capture.WaveFormat;
        _kind = AudioConversion.GetSampleKind(format);
        _channels = Math.Max(1, format.Channels);
        _frameSize = AudioConversion.BytesPerSample(_kind) * _channels;
        _resampler = new MonoResampler(format.SampleRate, SampleRate);
        _capture = capture;
        _fillSilence = fillSilence;
        WavPath = wavPath;
        SourceFormat = format;

        _writer = new WaveFileWriter(wavPath, new WaveFormat(SampleRate, 16, 1));
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public string WavPath { get; }

    /// <summary>Aygıtın verdiği özgün biçim (tanılama için).</summary>
    public WaveFormat SourceFormat { get; }

    /// <summary>Kayıt başladığından beri geçen süre (saat).</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Dosyaya yazılmış ses süresi.</summary>
    public TimeSpan RecordedDuration
    {
        get
        {
            lock (_sync) return TimeSpan.FromSeconds((double)_written / SampleRate);
        }
    }

    /// <summary>Yakalama sırasında oluşan hata (aygıt çıkarıldı, disk dolu…). Olay geldikten sonra dosya kapatılmıştır.</summary>
    public event Action<Exception>? Error;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Kayıt zaten başlatıldı.");
            _started = true;
            _clock.Start();
        }

        try
        {
            _capture.StartRecording();
        }
        catch
        {
            lock (_sync)
            {
                _clock.Reset();
                CloseWriter();
            }
            throw;
        }

        if (_fillSilence)
            _silenceTimer = new Timer(_ => OnSilenceTick(), null, SilenceTimerInterval, SilenceTimerInterval);
    }

    /// <summary>Kaydı durdurur ve WAV dosyasını kapatır (başlık güncellenir). Sonradan gelen veri yok sayılır.
    /// Birden çok kez çağrılabilir.</summary>
    public void Stop()
    {
        _silenceTimer?.Dispose();
        _silenceTimer = null;

        bool wasRunning;
        lock (_sync) wasRunning = _started && !_closed;
        if (wasRunning)
        {
            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Aygıt zaten durmuş olabilir; dosyayı yine de düzgün kapatırız.
            }
        }

        Exception? error = null;
        lock (_sync)
        {
            if (_closed) return;
            try
            {
                if (_fillSilence && _started) FillGapUpTo(_clock.Elapsed, incoming: 0, threshold: SilenceGapSamples);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
            }
            _clock.Stop();
            CloseWriter();
        }
        if (error is not null) Error?.Invoke(error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        lock (_sync) _disposed = true;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var span = e.BufferSpan;
        var length = Math.Min(e.BytesRecorded, span.Length);
        if (length <= 0) return;
        var data = span[..length];

        Exception? error = null;
        lock (_sync)
        {
            if (_closed) return;
            try
            {
                var frames = data.Length / _frameSize;
                if (frames == 0) return;
                EnsureCapacity(ref _mono, frames);
                AudioConversion.DownmixToMono(data, _kind, _channels, _mono);

                EnsureCapacity(ref _resampled, _resampler.MaxOutputLength(frames));
                var produced = _resampler.Process(_mono.AsSpan(0, frames), _resampled);

                var now = _clock.Elapsed;
                // Yalnızca veri akışında gerçek bir duraklama olduysa (loopback sustu) boşluğu doldur; sürekli akışta
                // olağan gecikme yüzünden yanlışlıkla sessizlik eklenmesin.
                if (_fillSilence && now - _lastData > DataPauseThreshold)
                    FillGapUpTo(now, produced, SilenceGapSamples);
                _lastData = now;

                WriteSamples(_resampled.AsSpan(0, produced));
                MaybeFlush(now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = ex;
                CloseWriter();
            }
        }
        if (error is not null) Fail(error);
    }

    private void OnSilenceTick()
    {
        Exception? error = null;
        lock (_sync)
        {
            if (_closed || !_started) return;
            try
            {
                var now = _clock.Elapsed;
                if (now - _lastData > DataPauseThreshold)
                    FillGapUpTo(now, incoming: 0, threshold: SilenceGapSamples);
                MaybeFlush(now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
                CloseWriter();
            }
        }
        if (error is not null) Fail(error);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        lock (_sync)
        {
            if (_closed) return;
            CloseWriter();
        }
        Error?.Invoke(e.Exception);
    }

    private void Fail(Exception error)
    {
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        try
        {
            _capture.StopRecording();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Zaten durmuş.
        }
        Error?.Invoke(error);
    }

    /// <summary>Saate göre olması gereken örnek sayısı ile (yazılan + gelmekte olan) arasındaki fark eşiği aşıyorsa
    /// aradaki farkı sıfırla doldurur. Kilit altında çağrılır.</summary>
    private void FillGapUpTo(TimeSpan now, int incoming, int threshold)
    {
        var expected = (long)(now.TotalSeconds * SampleRate);
        var gap = expected - (_written + incoming);
        if (gap > threshold) WriteSilence(gap);
    }

    private void WriteSilence(long samples)
    {
        EnsureCapacity(ref _pcm, (int)Math.Min(samples, SampleRate) * 2);
        Array.Clear(_pcm);
        while (samples > 0)
        {
            var chunk = (int)Math.Min(samples, _pcm.Length / 2);
            _writer.Write(_pcm, 0, chunk * 2);
            _written += chunk;
            samples -= chunk;
        }
    }

    private void WriteSamples(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        EnsureCapacity(ref _pcm, samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var s = AudioConversion.ToPcm16(samples[i]);
            _pcm[2 * i] = (byte)s;
            _pcm[2 * i + 1] = (byte)(s >> 8);
        }
        _writer.Write(_pcm, 0, samples.Length * 2);
        _written += samples.Length;
    }

    private void MaybeFlush(TimeSpan now)
    {
        if (now - _lastFlush < FlushInterval) return;
        _lastFlush = now;
        _writer.Flush();
    }

    private void CloseWriter()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Başlık güncellenemedi; dosya yine de okunabilir (son Flush'a kadar).
        }
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new T[Math.Max(length, buffer.Length * 2)];
    }
}
