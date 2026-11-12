using Notlar.Core;

namespace Notlar.Kayit;

/// <summary>Ses dosyalarının saklama süresi: eski kayıtların ses klasörü silinir, metin (transkript/özet) kalır.</summary>
public static class AudioRetention
{
    /// <summary>Sesi <paramref name="age"/>'den eski kayıtların klasörünü siler ve MarkAudioDeleted ile işaretler;
    /// sesi silinen kayıt id'lerini döndürür. Henüz çevrilmeyi bekleyen / çevrilen ya da kaydı süren kayıtlara
    /// dokunulmaz. Silinemeyen klasör (dosya kullanımda vb.) atlanır, bir sonraki temizlikte yeniden denenir.</summary>
    public static IReadOnlyList<long> Purge(RecordingStore store, string recordingsDir, TimeSpan age)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsDir);

        var purged = new List<long>();
        foreach (var recording in store.ListAudioOlderThan(age))
        {
            if (recording.Status is RecordingStatus.Kaydediliyor or RecordingStatus.Bekliyor or RecordingStatus.Cevriliyor)
                continue;
            if (!TryDeleteFolder(RecordingFiles.FolderFor(recordingsDir, recording.Id))) continue;
            store.MarkAudioDeleted(recording.Id);
            purged.Add(recording.Id);
        }
        return purged;
    }

    /// <summary>Kaydın ses klasörünü siler (kayıt silinince çağrılır). Klasör yoksa bir şey yapmaz.
    /// Silinemezse <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> fırlatır.</summary>
    public static void DeleteRecordingAudio(string recordingsDir, long id)
    {
        var folder = RecordingFiles.FolderFor(recordingsDir, id);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static bool TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
