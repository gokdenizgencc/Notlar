using NAudio.CoreAudioApi;
using NAudio.Wave;
using Notlar.Core;

namespace Notlar.Kayit;

/// <summary>⚙ Ayarlar'daki mikrofon testi: seçili mikrofonu (paylaşımlı WASAPI) dinler, diske hiçbir şey yazmaz ve her
/// veri bloğunun tepe değerini (0–1) <see cref="LevelChanged"/> ile bildirir. Olay ses thread'inden tetiklenir
/// (UI'ye aktarmak çağıranın işi). Yalnızca ayarlar görünürken açık tutulmalı.</summary>
public sealed class MicrophoneLevelMonitor : IDisposable
{
    private readonly object _sync = new();
    private IWaveIn? _capture;
    private SampleKind _kind;
    private int _channels;
    private float[] _mono = [];

    /// <summary>Tepe değeri, 0–1.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Dinleme sırasında aygıt hatası (çıkarıldı vb.); dinleme durmuştur.</summary>
    public event Action<Exception>? Error;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _capture is not null;
        }
    }

    /// <summary>Dinlemeyi başlatır (sürüyorsa önce durdurur). <paramref name="deviceId"/> null → varsayılan mikrofon.
    /// Mikrofon yoksa ya da açılamazsa Türkçe <see cref="InvalidOperationException"/>; <paramref name="fallbackNote"/>:
    /// seçili aygıt bulunamayıp varsayılana düşüldüyse açıklama.</summary>
    public void Start(string? deviceId, out string? fallbackNote)
    {
        Stop();
#pragma warning disable CS0618 // bkz. MeetingRecorder: IWaveIn uygulayan klasik sınıf bilinçli olarak kullanılıyor.
        var device = AudioDevices.Open(capture: true, deviceId, out fallbackNote)
            ?? throw new InvalidOperationException("Mikrofon bulunamadı.");
        IWaveIn capture = new WasapiCapture(device);
#pragma warning restore CS0618
        try
        {
            var format = capture.WaveFormat;
            lock (_sync)
            {
                _kind = AudioConversion.GetSampleKind(format);
                _channels = Math.Max(1, format.Channels);
                _capture = capture;
            }
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException
                                       or UnauthorizedAccessException or InvalidOperationException)
        {
            Stop();
            throw new InvalidOperationException("Mikrofon açılamadı: " + ex.Message, ex);
        }
    }

    public void Stop()
    {
        IWaveIn? capture;
        lock (_sync)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Zaten durmuş.
        }
        capture.Dispose();
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float peak;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _capture)) return;
            var span = e.BufferSpan;
            var length = Math.Min(e.BytesRecorded, span.Length);
            var frameSize = AudioConversion.BytesPerSample(_kind) * _channels;
            var frames = length / frameSize;
            if (frames == 0) return;
            if (_mono.Length < frames) _mono = new float[frames];
            AudioConversion.DownmixToMono(span[..(frames * frameSize)], _kind, _channels, _mono);
            peak = AudioPreferences.Peak(_mono.AsSpan(0, frames));
        }
        try
        {
            LevelChanged?.Invoke(peak);
        }
        catch (Exception)
        {
            // İşleyici hatası ses thread'ini düşürmemeli.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _capture)) return;
        }
        try
        {
            Error?.Invoke(e.Exception);
        }
        catch (Exception)
        {
            // Yut.
        }
    }
}
