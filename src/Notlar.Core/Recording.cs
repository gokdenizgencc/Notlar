namespace Notlar.Core;

/// <summary>Kaydediliyor → Kaydedildi (kullanıcı "Transkript et" diyene kadar bekler) → Bekliyor (çeviri kuyruğunda)
/// → Cevriliyor → Hazir | Hata.</summary>
public enum RecordingStatus { Kaydediliyor, Kaydedildi, Bekliyor, Cevriliyor, Hazir, Hata }

/// <summary>Transkript satırının ses kanalı: mikrofon "Ben", sistem sesi (loopback) "Karşı taraf". Kimin konuştuğunu
/// bu değil, <see cref="TranscriptSegment.Person"/> söyler (bir kanalda birden çok kişi konuşabilir).</summary>
public enum Speaker { Ben, KarsiTaraf }

/// <summary>Bir toplantı kaydı. <paramref name="Language"/>: 'tr' | 'en' | 'auto'.</summary>
public sealed record Recording(
    long Id, string Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    RecordingStatus Status, string Language, string? Summary, long? PlanId,
    string? Error, bool AudioDeleted)
{
    public TimeSpan? Duration => EndedAt - StartedAt;
}

/// <summary>Transkriptin bir satırı (konuşma). <paramref name="Person"/>: sesine göre bulunan kişi numarası
/// (1'den başlar, ilk konuşma sırasına göre). Kişi ayrımı yapılmadıysa/yapılamadıysa null (etiketsiz satır).
/// <paramref name="PersonName"/>: kişinin görünen adı (bkz. <see cref="RecordingPerson"/>); yalnızca okurken
/// <see cref="RecordingStore.GetSegments"/> doldurur, yoksa (eski kayıtlar) "Kişi N" gösterilir.
/// <paramref name="PersonNameSource"/>: adın kaynağı (kullanıcının verdiği gerçek ad, hitaptan çıkan ad ya da
/// sesten tahmin edilmiş takma ad).</summary>
public sealed record TranscriptSegment(long StartMs, long EndMs, Speaker Speaker, string Text, int? Person = null,
    string? PersonName = null, PersonNameSource PersonNameSource = PersonNameSource.Tahmin);
