# Zamanlı kayıt, elle transkript ve aralık seçimi — tasarım

Tarih: 2026-11-23

## Amaç

Toplantı kaydı akışı şu hâle gelir:

1. Hafta içi 09:30'da kayıt kendiliğinden başlar, 10:30'da kendiliğinden durur.
2. Duran kayıt çeviri kuyruğuna **gitmez**; `Kaydedildi` durumunda kullanıcıyı bekler.
3. Kullanıcı kaydı dinler, isterse bir ya da birden çok zaman aralığı işaretler.
4. `📝 Transkript et` ile yalnızca işaretli aralıklar (aralık yoksa tüm kayıt) metne çevrilir.
5. `🗂 Görev çıkart` transkriptten plan çıkarır (mevcut `🗂 Plan`).
6. `▶ Görevleri başlat` onay penceresinden sonra terminalde Claude Code'u açar (mevcut `▶ Planı başlat`).

## Kapsam dışı

- Uygulama kapalıyken kayıt (Windows görev zamanlayıcısı vb.) yok; zamanlayıcı yalnızca Notlar açıkken çalışır.
- Terminal PowerShell olarak kalır (`PlanLauncher`), cmd'ye çevrilmez.
- Onay penceresi (`RunPlanConfirmWindow`) korunur.
- Dalga biçimi (waveform) çizimi yok; düz zaman çubuğu.

## 1. Yeni durum: `Kaydedildi`

`RecordingStatus` enum'una `Kaydedildi` eklenir (durum veritabanında metin olarak saklandığından şema değişmez).

- `RecordingStore.Finish(id)`: `ended_at = şimdi`, durum **`Kaydedildi`** (eskiden `Bekliyor`).
- `RecordingStore.Requeue(id)`: değişmez (durum `Bekliyor`); `Kaydedildi`, `Hazir` ve `Hata` durumlarından çağrılır.
- `RecordingStore.RecoverInterrupted()`:
  - `Kaydediliyor` → `Kaydedildi` (`ended_at` boşsa şimdi). **Kuyruğa alınmaz.**
  - `Cevriliyor` → `Bekliyor`.
  - Dönen liste: yalnızca `Bekliyor` kayıtlar (kullanıcı çeviriyi başlatmış, yarıda kalmış).
- `RecordingController.Stop()`: `_store.Finish` çağırır, **`_queue.Enqueue` çağırmaz**.
- Arayüz: rozet "Kaydedildi" (Muted), durum metni "Kayıt hazır; dinleyip 📝 Transkript et'e basın.",
  transkript yer tutucusu aynı yönlendirmeyi yapar.
- MCP (`RecordingTools`) ve diğer `RecordingStatus` kullanan yerler yeni değeri tanır (switch'ler güncellenir).

## 2. Aralıklar

Yeni tablo (RecordingStore kurucusunda `CREATE TABLE IF NOT EXISTS`):

```sql
CREATE TABLE IF NOT EXISTS recording_ranges (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    recording_id INTEGER NOT NULL,
    start_ms     INTEGER NOT NULL,
    end_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recrange ON recording_ranges(recording_id, start_ms);
```

Core:

- `public sealed record TimeRange(long StartMs, long EndMs);`
- `TimeRanges.Normalize(IEnumerable<TimeRange>)` (saf, test edilir): negatifleri 0'a kırpar, `End <= Start` olanları
  atar, sıralar, çakışan/bitişik aralıkları birleştirir.
- `RecordingStore.SetRanges(id, ranges)`: normalize edip tek işlemde değiştirir; `GetRanges(id)`: sıralı liste.
  Boş liste = "tüm kayıt".

Çeviri:

- `WhisperTranscriber.TranscribeAsync(..., IReadOnlyList<TimeRange>? ranges = null, ...)`: `SpeechSegmenter.Detect`
  sonrası konuşma bölgeleri aralıklarla kesiştirilir (`ClipRegions`, saf, test edilir); null/boş ise kırpma yok.
  Segment zamanları kaydın başına göre kalır (mevcut ofset mantığı aynen çalışır).
- `TranscriptionQueue.Process`: `_store.GetRanges(id)` okuyup iki kanala da geçirir.
- `🔁 Yeniden çevir` aynı kayıtlı aralıkları kullanır.

## 3. Oynatıcı

`Notlar.Kayit.RecordingPlayer` (NAudio, `IDisposable`):

- `Open(folder)`: var olan `ben.wav` / `karsi.wav` dosyalarını `MixingSampleProvider` ile karıştırıp `WaveOutEvent`'e
  verir. Dosya yoksa `InvalidOperationException` (Türkçe mesaj).
- `Play()`, `Pause()`, `IsPlaying`, `Position` (get/set, `TimeSpan`), `Duration`, `PlaybackStopped` olayı.
- Sona gelince durur ve başa sarar.

Arayüz (`RecordingsView` detayında, eylem düğmelerinin üstünde; ses dosyası durduğu ve kayıt
`Kaydedildi`/`Hazir`/`Hata` olduğu sürece görünür):

- `▶/⏸` düğmesi, konum çubuğu (`Slider`), `mm:ss / mm:ss` etiketi.
- Çubuğun üstünde seçili aralıkları gösteren ince renkli şerit (Canvas üzerinde Accent dikdörtgenler).
- `[ Başlangıç` düğmesi o anki konumu bekleyen başlangıç olarak işaretler; `Bitiş ]` bekleyen başlangıçtan o anki
  konuma aralık ekler (bitiş ≤ başlangıç ise hata mesajı). Aralıklar anında `SetRanges` ile kaydedilir.
- Aralık listesi: `02:10 – 05:40 ✕` çipleri; çipe tıklamak o aralığın başına sarar, `✕` siler.
- Uygulama kayıt yaparken (`_controller.IsRecording`) oynatma devre dışıdır (çalan ses loopback kanalına karışır).
- Başka kayıt seçilince / görünüm gizlenince oynatıcı durur ve kapatılır (dosya kilidi kalmasın; silme çalışsın).
- `RecordingController.Delete` öncesi görünüm oynatıcıyı kapatır.

## 4. Eylem düğmeleri

- Yeni `TranscribeButton` (`📝 Transkript et`), `Kaydedildi` durumunda birincil (Accent) düğme:
  - Aralık yok: "📝 Tümünü transkript et"; var: "📝 Seçilenleri transkript et (n aralık)".
  - Tıklayınca `_controller.Requeue(id)` (durum `Bekliyor` → kuyruk).
  - `Hazir`/`Hata` durumunda gizlenir; yeniden çeviri `⋯ → 🔁 Yeniden çevir`'de kalır (aynı aralıklar).
- `🗂 Plan` → `🗂 Görev çıkart`; `🗂 Planlanıyor…` → `🗂 Görevler çıkarılıyor…`;
  `▶ Planı başlat` → `▶ Görevleri başlat`; `🗂 Yeniden planla` → `🗂 Görevleri yeniden çıkart`. Davranış aynı.
- `Kaydedildi` durumunda Özetle / Görev çıkart / Claude düğmeleri devre dışı (transkript yok).

## 5. Zamanlayıcı

Core — `RecordingSchedule` (saf, test edilir):

```csharp
public enum ScheduleAction { None, Start, Stop }

public static ScheduleAction Decide(DateTime now, TimeOnly start, TimeOnly end,
    bool isRecording, bool currentIsAuto, DateOnly? lastAutoStart);
```

- Hafta sonu → `None` (ama otomatik kayıt sürüyorsa ve pencere dışındaysa `Stop`).
- Pencere içinde (`start ≤ saat < end`), kayıt yok, `lastAutoStart != bugün` → `Start`.
- Pencere dışında ve süren kayıt otomatik başlatılmış → `Stop`.
- Elle başlatılan kayda dokunulmaz; kullanıcı otomatik kaydı erken durdurursa o gün yeniden başlamaz.
- `end <= start` ise zamanlama geçersiz sayılır → `None`.

App:

- `AppSettings`: `AutoRecordEnabled` (varsayılan `true`), `AutoRecordStart` ("09:30"), `AutoRecordEnd` ("10:30"),
  `AutoRecordLastDate` (yyyy-MM-dd; günde bir kez kuralı yeniden başlatmada da geçerli olsun diye saklanır).
- `RecordingController`: `StartAutomatic()` (KVKK onayı yoksa başlamaz, `false` döner; mesaj kutusu göstermez),
  `IsCurrentAutomatic`.
- `App.xaml.cs`: 20 sn aralıklı `DispatcherTimer` → `RecordingSchedule.Decide` → başlat/durdur + tepsi balonu
  ("Toplantı kaydı otomatik başladı", "Toplantı kaydı durdu; dinleyip transkript edebilirsiniz" → tıklayınca
  Kayıtlar açılır). KVKK onayı yoksa günde bir kez "Otomatik kayıt için önce bir kez elle kayıt başlatıp uyarıyı
  onaylayın" balonu.
- `SettingsView` Kayıt bölümü: "Hafta içi otomatik kayıt" onay kutusu + başlangıç/bitiş saat kutuları (HH:mm,
  geçersiz girişte eski değere döner).

## Hata durumları

- Otomatik başlatmada ses aygıtı açılamazsa: kayıt `Hata` olarak işaretlenir (mevcut yol), hata balonu gösterilir,
  o gün yeniden denenmez.
- Oynatıcı açılamazsa mesaj çubuğunda Türkçe hata; transkript akışı etkilenmez.
- Ses dosyaları silinmişse (14 gün) oynatıcı ve `📝 Transkript et` görünmez.

## Testler (Notlar.Core.Tests)

- `RecordingStore`: `Finish` → `Kaydedildi`; `RecoverInterrupted` yeni kuralları; `SetRanges`/`GetRanges`.
- `TimeRanges.Normalize`: sıralama, birleştirme, geçersizleri atma.
- `RecordingSchedule.Decide`: hafta sonu, pencere içi/dışı, günde bir kez, elle kayda dokunmama, geçersiz saatler.
- `ClipRegions`: kesişim, aralık yokken değişmeme. (Kayit projesinde test projesi yoksa saf mantık Core'a konur.)
- Mevcut testler yeni `Finish` davranışına göre güncellenir.
