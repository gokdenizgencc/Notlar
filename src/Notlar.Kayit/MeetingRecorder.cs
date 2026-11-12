using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Notlar.Kayit;

/// <summary>Toplantı kaydı: varsayılan çıkış aygıtının sistem sesi (WASAPI loopback → <c>karsi.wav</c>) ve
/// varsayılan mikrofon (WASAPI → <c>ben.wav</c>) iki ayrı dosyaya, ikisi de sessizlik dolgulu olarak kaydedilir.
/// Mikrofon yoksa ya da açılamazsa yalnızca sistem sesi kaydedilir (<see cref="MicrophoneAvailable"/> = false);
/// sistem sesi açılamazsa yalnızca mikrofon. İkisi de açılamazsa <see cref="Start"/> Türkçe
/// <see cref="InvalidOperationException"/> fırlatır ve hiçbir dosya bırakmaz.
/// <see cref="Error"/> ses thread'lerinden tetiklenir (UI'ye aktarmak çağıranın işi).</summary>
public sealed class MeetingRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    private ChannelRecorder? _microphone;
    private ChannelRecorder? _loopback;
    private bool _disposed;

    public bool IsRecording { get; private set; }

    /// <summary>Son <see cref="Start"/>'ta mikrofon kanalı kaydediliyor mu.</summary>
    public bool MicrophoneAvailable { get; private set; }

    /// <summary>Son <see cref="Start"/>'ta sistem sesi (loopback) kanalı kaydediliyor mu.</summary>
    public bool LoopbackAvailable { get; private set; }

    /// <summary>Mikrofon açılamadıysa nedeni (yoksa null) — kullanıcıya bilgi vermek için.</summary>
    public Exception? MicrophoneError { get; private set; }

    /// <summary>Sistem sesi açılamadıysa nedeni (yoksa null).</summary>
    public Exception? LoopbackError { get; private set; }

    /// <summary>Son kaydın klasörü.</summary>
    public string? Folder { get; private set; }

    /// <summary>Kayıt süresi (durdurulunca donar, yeni kayıtta sıfırlanır).</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Kayıt sürerken bir kanalda oluşan hata (aygıt çıkarıldı, disk dolu…). Diğer kanal sürer.</summary>
    public event Action<Exception>? Error;

    public void Start(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording) throw new InvalidOperationException("Kayıt zaten sürüyor.");

            Directory.CreateDirectory(folder);
            Folder = folder;
            MicrophoneError = null;
            LoopbackError = null;

            _loopback = TryStartChannel(CreateLoopbackCapture, Path.Combine(folder, RecordingFiles.LoopbackFileName),
                out var loopbackError);
            _microphone = TryStartChannel(CreateMicrophoneCapture, Path.Combine(folder, RecordingFiles.MicrophoneFileName),
                out var microphoneError);
            LoopbackError = loopbackError;
            MicrophoneError = microphoneError;
            LoopbackAvailable = _loopback is not null;
            MicrophoneAvailable = _microphone is not null;

            if (_loopback is null && _microphone is null)
            {
                throw new InvalidOperationException(
                    "Ses kaydı başlatılamadı: ne sistem sesi ne de mikrofon kaydedilebiliyor. " +
                    $"Sistem sesi: {Describe(loopbackError)} Mikrofon: {Describe(microphoneError)}",
                    loopbackError ?? microphoneError);
            }

            _clock.Restart();
            IsRecording = true;
        }
    }

    /// <summary>Kaydı durdurur, dosyaları kapatır. Kayıt yoksa bir şey yapmaz.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!IsRecording) return;
            _clock.Stop();
            DisposeChannel(ref _loopback);
            DisposeChannel(ref _microphone);
            IsRecording = false;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync) _disposed = true;
    }

    private ChannelRecorder? TryStartChannel(Func<IWaveIn?> createCapture, string wavPath, out Exception? error)
    {
        error = null;
        IWaveIn? capture = null;
        ChannelRecorder? recorder = null;
        try
        {
            capture = createCapture();
            if (capture is null)
            {
                error = new InvalidOperationException("Varsayılan ses aygıtı bulunamadı.");
                return null;
            }
            recorder = new ChannelRecorder(capture, wavPath, fillSilence: true);
            recorder.Error += OnChannelError;
            recorder.Start();
            return recorder;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException
                                       or InvalidOperationException or NotSupportedException or ArgumentException
                                       or IOException)
        {
            error = ex;
            if (recorder is not null)
            {
                recorder.Error -= OnChannelError;
                recorder.Dispose();
            }
            else
            {
                capture?.Dispose();
            }
            TryDelete(wavPath);
            return null;
        }
    }

    // NAudio 3'te WasapiCapture/WasapiLoopbackCapture "obsolete" (yerine WasapiRecorderBuilder önerilir); ancak yeni
    // WasapiRecorder IWaveIn uygulamıyor. ChannelRecorder IWaveIn üzerinden test edilebilir kaldığı için klasik
    // sınıflar bilinçli olarak kullanılıyor.
#pragma warning disable CS0618
    private static IWaveIn? CreateLoopbackCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)) return null;
        return new WasapiLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console));
    }

    private static IWaveIn? CreateMicrophoneCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
        return new WasapiCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console));
    }
#pragma warning restore CS0618

    private void OnChannelError(Exception error) => Error?.Invoke(error);

    private void DisposeChannel(ref ChannelRecorder? channel)
    {
        if (channel is null) return;
        channel.Error -= OnChannelError;
        channel.Dispose();
        channel = null;
    }

    private static string Describe(Exception? error) =>
        error is null ? "—" : error.Message.TrimEnd('.') + ".";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Boş dosya kalırsa çeviri kuyruğu onu sessiz kanal olarak işler.
        }
    }
}
