using System.Globalization;
using System.Text.RegularExpressions;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Notlar.Kayit;

/// <summary>Whisper.net yerel kütüphane (runtime) seçimi ve GPU durumu; süreç geneli.
/// <para>Seçim sırası <c>Vulkan → Cpu</c>'dur (<see cref="RuntimeOptions.RuntimeLibraryOrder"/>; Whisper.net'in varsayılanı
/// Cuda, Cuda12, Vulkan, CoreML, OpenVino, Cpu, CpuNoAvx). Vulkan kütüphanesi yüklenemezse (ör. sürücüde vulkan-1.dll
/// yok) Whisper.net kendiliğinden Cpu kütüphanesine geçer. Kütüphane süreç başına bir kez yüklenir; sonradan
/// değiştirilemez, bu yüzden GPU'dan CPU'ya düşüş aynı (Vulkan) kütüphanede <c>UseGpu=false</c> ile yapılır — Vulkan
/// paketindeki CPU arka ucu Cpu paketininkiyle aynı hızdadır.</para>
/// <para>GPU olarak yalnızca ayrık (UMA olmayan) Vulkan aygıtı kullanılır: tümleşik GPU (ör. Intel UHD 630) bu modelde
/// CPU'dan bile yavaştır (28 sn'lik pencere ~30 sn). Çift GPU'da ayrık olanın dizini ggml_vulkan'ın aygıt listesinden
/// bulunur.</para>
/// <para>Çökme koruması: GPU açıkken her çeviri süresince model klasöründe <c>whisper-gpu.calisiyor</c> işaret dosyası
/// durur. Süreç GPU işi sırasında ölürse (sürücü çökmesi gibi yönetilen kodla yakalanamayan hatalar) bir sonraki
/// açılışta işaret bulunur: o oturum yalnızca Cpu kütüphanesiyle çalışır ve <c>whisper-gpu.cokme</c> sayacı artar;
/// sayaç 2'ye ulaşırsa GPU kalıcı kapanır (dosya silinince yeniden denenir). Başarılı bir GPU çevirisi sayacı sıfırlar.
/// <c>NOTLAR_WHISPER_CPU=1</c> ortam değişkeni GPU'yu her zaman kapatır.</para></summary>
public static partial class WhisperRuntime
{
    internal const string RunningMarkerFileName = "whisper-gpu.calisiyor";
    internal const string CrashCountFileName = "whisper-gpu.cokme";
    internal const int CrashLimit = 2;

    private static readonly object Gate = new();
    private static readonly List<(int Index, string Name, bool Discrete)> Devices = [];
    private static IDisposable? _logger;
    private static bool _configured;
    private static string? _gpuOffReason;
    private static string? _lastGpuBackend;
    private static bool _gpuInitFailed;

    /// <summary>Teşhis iletileri (hangi kütüphane yüklendi, hangi GPU seçildi, CPU'ya neden düşüldü…). Çeviri
    /// thread'inden tetiklenir; işleyicideki hata yutulur.</summary>
    public static event Action<string>? Diagnostic;

    /// <summary>Yüklenen yerel kütüphane: "Vulkan", "Cpu" …; henüz hiçbir model yüklenmediyse null.</summary>
    public static string? LoadedLibrary => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>Son çevirinin çalıştığı arka uç, ör. "Vulkan: NVIDIA GeForce GTX 1660 Ti" ya da "CPU"; henüz çeviri
    /// yapılmadıysa null.</summary>
    public static string? LastBackend { get; private set; }

    /// <summary>GPU bu süreçte kapalıysa nedeni (çökme koruması, ortam değişkeni, GPU hatası, ayrık GPU yok); açıksa null.</summary>
    public static string? GpuDisabledReason
    {
        get
        {
            lock (Gate) return _gpuOffReason;
        }
    }

    /// <summary>İlk modeli yüklemeden önce bir kez çağrılır: günlük dinleyicisini bağlar, çökme işaretlerini okur ve
    /// kütüphane sırasını belirler. <paramref name="stateDir"/>: işaret dosyalarının klasörü (model klasörü).</summary>
    internal static void EnsureConfigured(string stateDir)
    {
        lock (Gate)
        {
            if (_configured) return;
            _configured = true;
            _logger = LogProvider.AddLogger(OnNativeLog);

            if (Environment.GetEnvironmentVariable("NOTLAR_WHISPER_CPU") == "1")
                _gpuOffReason = "NOTLAR_WHISPER_CPU=1";
            else
                _gpuOffReason = CheckCrashMarkers(stateDir);

            // RuntimeOptions yalnızca kütüphane yüklenmeden önce etkilidir; daha önce (başka kod) yüklendiyse dokunma.
            if (RuntimeOptions.LoadedLibrary is null)
            {
                RuntimeOptions.RuntimeLibraryOrder = _gpuOffReason is null
                    ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
                    : [RuntimeLibrary.Cpu];
            }
        }
        Raise(_gpuOffReason is null
            ? "Whisper kütüphane sırası: Vulkan → Cpu."
            : "Whisper GPU kapalı (" + _gpuOffReason + "); yalnızca CPU kullanılacak.");
    }

    /// <summary>Bu çeviride GPU denenmeli mi? (Kapalı değilse ve ayrık GPU yok diye bilinmiyorsa.)</summary>
    internal static bool ShouldTryGpu
    {
        get
        {
            lock (Gate) return _gpuOffReason is null;
        }
    }

    /// <summary>Kullanılacak GPU dizini: aygıt listesi biliniyorsa ilk ayrık aygıt, bilinmiyorsa 0. Liste var ama ayrık
    /// aygıt yoksa null (GPU kullanılmamalı).</summary>
    internal static int? PreferredGpuDevice
    {
        get
        {
            lock (Gate)
            {
                if (Devices.Count == 0) return 0;
                foreach (var d in Devices)
                    if (d.Discrete) return d.Index;
                return null;
            }
        }
    }

    /// <summary>GPU'yu bu sürecin geri kalanı için kapatır (GPU hatasından sonra).</summary>
    internal static void DisableGpu(string reason)
    {
        lock (Gate) _gpuOffReason ??= reason;
        Raise("Whisper GPU kapatıldı: " + reason);
    }

    /// <summary>Bir model yükleme/işlemci kurulumu öncesi: son GPU arka uç kaydını sıfırlar.</summary>
    internal static void BeginAttempt()
    {
        lock (Gate)
        {
            _lastGpuBackend = null;
            _gpuInitFailed = false;
        }
    }

    /// <summary>Son model yüklemesinde ağırlıklar GPU belleğine mi kondu (whisper "VulkanN total size" yazdı mı)?</summary>
    internal static bool AttemptUsedGpu
    {
        get
        {
            lock (Gate) return _lastGpuBackend is not null && !_gpuInitFailed;
        }
    }

    internal static void RecordBackend(bool gpu, int device)
    {
        string backend;
        lock (Gate)
        {
            string? name = null;
            foreach (var d in Devices)
                if (d.Index == device) name = d.Name;
            backend = gpu ? $"{LoadedLibrary}: {name ?? _lastGpuBackend}" : "CPU";
        }
        if (backend == LastBackend) return;
        LastBackend = backend;
        Raise($"Whisper arka ucu: {backend} (kütüphane {LoadedLibrary ?? "?"}).");
    }

    /// <summary>GPU'lu çeviri başlarken işaret dosyasını yazar.</summary>
    internal static void MarkGpuRunStarted(string stateDir) =>
        TryIo(() =>
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, RunningMarkerFileName),
                DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        });

    /// <summary>GPU'lu çeviri bittiğinde (başarılı ya da yönetilen bir hatayla): işareti siler; başarılı GPU
    /// çevirisinde çökme sayacını sıfırlar.</summary>
    internal static void MarkGpuRunFinished(string stateDir, bool gpuSucceeded) =>
        TryIo(() =>
        {
            File.Delete(Path.Combine(stateDir, RunningMarkerFileName));
            if (gpuSucceeded) File.Delete(Path.Combine(stateDir, CrashCountFileName));
        });

    /// <summary>Önceki sürecin GPU işi sırasında ölüp ölmediğine bakar; GPU kapatılmalıysa nedeni döndürür.</summary>
    internal static string? CheckCrashMarkers(string stateDir)
    {
        var marker = Path.Combine(stateDir, RunningMarkerFileName);
        var counter = Path.Combine(stateDir, CrashCountFileName);
        var crashes = 0;
        TryIo(() =>
        {
            if (File.Exists(counter)) _ = int.TryParse(File.ReadAllText(counter).Trim(), out crashes);
        });
        var interrupted = false;
        TryIo(() => interrupted = File.Exists(marker));
        if (interrupted)
        {
            crashes++;
            var count = crashes;
            TryIo(() =>
            {
                File.WriteAllText(counter, count.ToString(CultureInfo.InvariantCulture));
                File.Delete(marker);
            });
            return count >= CrashLimit
                ? $"GPU çevirisi {count} kez yarıda kaldı; GPU kalıcı kapatıldı ({CrashCountFileName} silinince yeniden denenir)"
                : "önceki GPU çevirisi yarıda kaldı; bu oturum CPU ile çalışıyor";
        }
        return crashes >= CrashLimit
            ? $"GPU çevirisi {crashes} kez yarıda kaldı; GPU kalıcı kapatıldı ({CrashCountFileName} silinince yeniden denenir)"
            : null;
    }

    private static void OnNativeLog(WhisperLogLevel level, string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = VulkanDeviceRegex().Match(line);
            if (m.Success)
            {
                var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var name = m.Groups[2].Value.Trim();
                var discrete = m.Groups[3].Value == "0";
                lock (Gate)
                {
                    Devices.RemoveAll(d => d.Index == index);
                    Devices.Add((index, name, discrete));
                    Devices.Sort((a, b) => a.Index.CompareTo(b.Index));
                }
                Raise($"Vulkan aygıtı {index}: {name} ({(discrete ? "ayrık" : "tümleşik")}).");
                continue;
            }
            var b = ModelBufferRegex().Match(line);
            if (b.Success)
            {
                var buffer = b.Groups[1].Value;
                lock (Gate) _lastGpuBackend = buffer.Equals("CPU", StringComparison.OrdinalIgnoreCase) ? null : buffer;
                continue;
            }
            if (level == WhisperLogLevel.Error)
            {
                if (line.Contains("whisper_backend_init_gpu", StringComparison.Ordinal)
                    || line.Contains("vulkan", StringComparison.OrdinalIgnoreCase))
                    lock (Gate) _gpuInitFailed = true;
                Raise("whisper: " + line);
            }
        }
    }

    private static void TryIo(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // İşaret dosyaları en iyi çaba; yazılamaması çeviriyi durdurmamalı.
        }
    }

    internal static void Raise(string message)
    {
        try
        {
            Diagnostic?.Invoke(message);
        }
        catch (Exception)
        {
            // İşleyici hatası çeviriyi durdurmamalı.
        }
    }

    // ggml_vulkan: 0 = NVIDIA GeForce GTX 1660 Ti (NVIDIA) | uma: 0 | fp16: 1 | ...
    [GeneratedRegex(@"ggml_vulkan:\s*(\d+)\s*=\s*(.+?)\s*(?:\([^()]*\))?\s*\|\s*uma:\s*(\d)")]
    private static partial Regex VulkanDeviceRegex();

    // whisper_model_load:      Vulkan0 total size =   573.40 MB   (CPU'da: "CPU total size")
    [GeneratedRegex(@"whisper_model_load:\s*(\S+)\s+total size")]
    private static partial Regex ModelBufferRegex();
}
