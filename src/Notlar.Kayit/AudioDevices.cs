using NAudio.CoreAudioApi;
using Notlar.Core;

namespace Notlar.Kayit;

/// <summary>Bir ses aygıtı: WASAPI kimliği ve Windows'taki görünen adı.</summary>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Etkin ses aygıtlarını listeler ve kaydedilmiş seçimi (aygıt kimliği) bir <see cref="MMDevice"/>'a çevirir.
/// Seçili aygıt artık yoksa varsayılan aygıta düşülür (bkz. <see cref="AudioPreferences.ResolveDevice"/>).</summary>
public static class AudioDevices
{
    /// <summary>Etkin mikrofonlar.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Microphones() => List(DataFlow.Capture);

    /// <summary>Etkin çıkış (hoparlör/kulaklık) aygıtları — sistem sesi bunlardan birinin loopback'iyle kaydedilir.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Outputs() => List(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                string name;
                try
                {
                    name = device.FriendlyName;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    name = device.ID;
                }
                result.Add(new AudioDeviceInfo(device.ID, name));
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>Seçili aygıtı açar; seçim yoksa ya da aygıt bulunamazsa varsayılan aygıt (o da yoksa null).
    /// <paramref name="fallbackNote"/>: seçili aygıt bulunamayıp varsayılana düşüldüyse günlüğe yazılacak açıklama.</summary>
    internal static MMDevice? Open(bool capture, string? preferredId, out string? fallbackNote)
    {
        fallbackNote = null;
        var flow = capture ? DataFlow.Capture : DataFlow.Render;
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var ids = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) ids.Add(device.ID);
            }
            var resolved = AudioPreferences.ResolveDevice(preferredId, ids, out var missing);
            if (resolved is not null)
            {
                try
                {
                    return enumerator.GetDevice(resolved);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    missing = true;
                    fallbackNote = $"{Kind(capture)} aygıtı açılamadı ({ex.Message}); varsayılan aygıt kullanılıyor.";
                }
            }
            if (missing)
                fallbackNote ??= $"Seçili {Kind(capture).ToLowerInvariant()} aygıtı bulunamadı (çıkarılmış ya da devre dışı); varsayılan aygıt kullanılıyor.";
        }

        return enumerator.HasDefaultAudioEndpoint(flow, Role.Console)
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console)
            : null;
    }

    private static string Kind(bool capture) => capture ? "Mikrofon" : "Çıkış";
}
