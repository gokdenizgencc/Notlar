using System.Globalization;

namespace Notlar.Kayit;

/// <summary>Kayıt klasörü düzeni: <c>&lt;kayitlar&gt;\&lt;id&gt;\ben.wav</c> (mikrofon) ve <c>karsi.wav</c> (sistem sesi).</summary>
public static class RecordingFiles
{
    /// <summary>Mikrofon kanalı ("Ben").</summary>
    public const string MicrophoneFileName = "ben.wav";

    /// <summary>Sistem sesi / loopback kanalı ("Karşı taraf").</summary>
    public const string LoopbackFileName = "karsi.wav";

    /// <summary>Kaydın ses klasörü. Yol yalnızca sayısal id'den üretilir (kullanıcı verisi içermez).</summary>
    public static string FolderFor(string recordingsDir, long id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsDir);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return Path.Combine(recordingsDir, id.ToString(CultureInfo.InvariantCulture));
    }
}
