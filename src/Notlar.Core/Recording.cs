namespace Notlar.Core;

public enum RecordingStatus { Kaydediliyor, Bekliyor, Cevriliyor, Hazir, Hata }

/// <summary>Transkript satırının konuşmacısı: mikrofon kanalı "Ben", sistem sesi (loopback) kanalı "Karşı taraf".</summary>
public enum Speaker { Ben, KarsiTaraf }

/// <summary>Bir toplantı kaydı. <paramref name="Language"/>: 'tr' | 'en' | 'auto'.</summary>
public sealed record Recording(
    long Id, string Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    RecordingStatus Status, string Language, string? Summary, long? PlanId,
    string? Error, bool AudioDeleted)
{
    public TimeSpan? Duration => EndedAt - StartedAt;
}

public sealed record TranscriptSegment(long StartMs, long EndMs, Speaker Speaker, string Text);
