using NAudio.Wave;

namespace Notlar.Kayit;

/// <summary>Bir toplantı kaydını dinlemek için oynatıcı: kayıt klasöründeki <c>ben.wav</c> ve <c>karsi.wav</c>
/// (var olanlar) birlikte, karıştırılarak çalınır. Dosyalar açık tutulur; kaydı silmeden ya da yeniden çevirmeden
/// önce <see cref="Dispose"/> edilmelidir. <see cref="Ended"/> ses thread'inden tetiklenebilir (UI'ye aktarmak
/// çağıranın işi).</summary>
public sealed class RecordingPlayer : IDisposable
{
    private readonly Mixer _mixer;
    private readonly WaveOut _output;
    private bool _disposed;

    private RecordingPlayer(Mixer mixer)
    {
        _mixer = mixer;
        _output = new WaveOut();
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(mixer);
    }

    /// <summary>Kayıt sonuna gelindi; oynatma durdu ve konum başa sarıldı.</summary>
    public event Action? Ended;

    public bool IsPlaying => !_disposed && _output.PlaybackState == PlaybackState.Playing;

    public TimeSpan Duration => _mixer.Duration;

    public TimeSpan Position
    {
        get => _mixer.Position;
        set => _mixer.Position = value;
    }

    /// <summary>Klasördeki ses dosyalarını açar. Çalınabilir dosya yoksa ya da ses çıkış aygıtı açılamazsa Türkçe
    /// mesajlı <see cref="InvalidOperationException"/> fırlatır.</summary>
    public static RecordingPlayer Open(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var readers = new List<WaveFileReader>();
        try
        {
            foreach (var name in new[] { RecordingFiles.MicrophoneFileName, RecordingFiles.LoopbackFileName })
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path)) continue;

                // Okuma paylaşımıyla açılır; yine de açıkken klasör silinemez (bkz. sınıf açıklaması).
                var reader = new WaveFileReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                var format = reader.WaveFormat;
                var playable = format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 && format.Channels == 1
                    && reader.Length > 0 && (readers.Count == 0 || format.SampleRate == readers[0].WaveFormat.SampleRate);
                if (playable) readers.Add(reader);
                else reader.Dispose();
            }
            if (readers.Count == 0)
                throw new InvalidOperationException("Dinlenecek ses dosyası bulunamadı.");

            var mixer = new Mixer(readers);
            try
            {
                return new RecordingPlayer(mixer);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("Ses çıkış aygıtı açılamadı: " + ex.Message, ex);
            }
        }
        catch
        {
            foreach (var reader in readers) reader.Dispose();
            throw;
        }
    }

    public void Play()
    {
        if (_disposed) return;
        if (_mixer.Position >= _mixer.Duration) _mixer.Position = TimeSpan.Zero;
        _output.Play();
    }

    public void Pause()
    {
        if (_disposed) return;
        if (_output.PlaybackState == PlaybackState.Playing) _output.Pause();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.PlaybackStopped -= OnPlaybackStopped;
        try
        {
            _output.Stop();
        }
        catch (Exception)
        {
            // Aygıt bu arada çıkarılmış olabilir; yine de kaynaklar bırakılır.
        }
        _output.Dispose();
        _mixer.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        _mixer.Position = TimeSpan.Zero;
        Ended?.Invoke();
    }

    /// <summary>Aynı hızdaki 16 bit mono dosyaları örnek örnek toplayan (kırparak) kaynak. Kısa dosya sessizlikle
    /// tamamlanır; süre en uzun dosyanınkidir. Okuma ses thread'inde, konum değişikliği UI thread'inde olduğundan
    /// kilitlidir.</summary>
    private sealed class Mixer : IWaveProvider, IDisposable
    {
        private readonly object _sync = new();
        private readonly List<WaveFileReader> _readers;
        private readonly long _lengthBytes;
        private byte[] _scratch = [];
        private long _positionBytes;

        public Mixer(List<WaveFileReader> readers)
        {
            _readers = readers;
            WaveFormat = new WaveFormat(readers[0].WaveFormat.SampleRate, 16, 1);
            _lengthBytes = readers.Max(r => r.Length) & ~1L;
        }

        public WaveFormat WaveFormat { get; }

        public TimeSpan Duration => ToTime(_lengthBytes);

        public TimeSpan Position
        {
            get
            {
                lock (_sync) return ToTime(_positionBytes);
            }
            set
            {
                var bytes = (long)(Math.Max(0, value.TotalSeconds) * WaveFormat.AverageBytesPerSecond) & ~1L;
                lock (_sync) _positionBytes = Math.Clamp(bytes, 0, _lengthBytes);
            }
        }

        public int Read(Span<byte> buffer)
        {
            lock (_sync)
            {
                var count = (int)Math.Min(buffer.Length & ~1, _lengthBytes - _positionBytes);
                if (count <= 0) return 0;

                var target = buffer[..count];
                target.Clear();
                if (_scratch.Length < count) _scratch = new byte[count];

                foreach (var reader in _readers)
                {
                    if (_positionBytes >= reader.Length) continue;
                    reader.Position = _positionBytes;
                    var read = 0;
                    while (read < count)
                    {
                        var n = reader.Read(_scratch, read, count - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    for (var i = 0; i + 1 < read; i += 2)
                    {
                        var sum = BitConverter.ToInt16(target[i..]) + BitConverter.ToInt16(_scratch, i);
                        BitConverter.TryWriteBytes(target[i..], (short)Math.Clamp(sum, short.MinValue, short.MaxValue));
                    }
                }

                _positionBytes += count;
                return count;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                foreach (var reader in _readers) reader.Dispose();
                _readers.Clear();
            }
        }

        private TimeSpan ToTime(long bytes) => TimeSpan.FromSeconds((double)bytes / WaveFormat.AverageBytesPerSecond);
    }
}
