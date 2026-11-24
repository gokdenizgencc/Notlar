using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Kayıt → Ses ve metne çevirme: ses aygıtları, işlemci, kişi ayırma, kayıt sonrası otomatik
/// çeviri ve otomatik kaydın günleri. Varsayılanlar bu ayarlar eklenmeden önceki davranışın aynısıdır (Windows'un
/// varsayılan aygıtları, otomatik işlemci seçimi, kişi ayırma açık, elle "📝 Transkript et", Pazartesi–Cuma).</summary>
public sealed partial class AppSettings
{
    /// <summary>Kaydedilecek mikrofonun WASAPI aygıt kimliği; null/boş → Windows'un varsayılan mikrofonu.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Sistem sesinin (karşı taraf) loopback ile kaydedileceği çıkış aygıtının kimliği; null/boş → varsayılan.</summary>
    public string? LoopbackDeviceId { get; set; }

    /// <summary>Metne çevirme işlemcisi: "auto" | "gpu" | "cpu" (bkz. <see cref="AudioPreferences"/>).</summary>
    public string TranscriptionHardware { get; set; } = AudioPreferences.ProcessorAuto;

    /// <summary>Transkript satırları sesine göre kişilere (Kişi 1/2) ayrılsın mı?</summary>
    public bool SpeakerLabelingEnabled { get; set; } = true;

    /// <summary>Kayıt durunca kendiliğinden metne çevrilsin mi? Kapalıyken kullanıcı Kayıtlar'da "📝 Transkript et"e basar.</summary>
    public bool AutoTranscribeAfterRecording { get; set; }

    /// <summary>Otomatik kaydın günleri (<see cref="DayOfWeek"/> sayıları, 0 = Pazar). Eski ayar dosyalarında yok →
    /// Pazartesi–Cuma.</summary>
    public List<DayOfWeek>? AutoRecordDays { get; set; } = [.. RecordingSchedule.DefaultDays];

    [JsonIgnore]
    public TranscriptionProcessor Processor => AudioPreferences.ParseProcessor(TranscriptionHardware);

    partial void NormalizeAudio()
    {
        if (string.IsNullOrWhiteSpace(MicrophoneDeviceId)) MicrophoneDeviceId = null;
        if (string.IsNullOrWhiteSpace(LoopbackDeviceId)) LoopbackDeviceId = null;
        TranscriptionHardware = AudioPreferences.FormatProcessor(Processor);
        AutoRecordDays = RecordingSchedule.NormalizeDays(AutoRecordDays);
    }
}
