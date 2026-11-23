# Zamanlı kayıt, elle transkript ve aralık seçimi — uygulama planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kayıt hafta içi 09:30–10:30 kendiliğinden başlar/durur; duran kayıt kullanıcı `📝 Transkript et`'e basana kadar çevrilmez; kullanıcı kaydı dinleyip aralık seçebilir; görev düğmeleri yeniden adlandırılır.

**Architecture:** Saf mantık (durum, aralık normalizasyonu/kırpma, zamanlama kararı) `Notlar.Core`'da ve testlidir. Ses (oynatıcı, aralıklı çeviri) `Notlar.Kayit`'ta. Zamanlayıcı ve arayüz `Notlar.App`'te; `RecordingController` tek giriş noktası kalır.

**Tech Stack:** .NET 10, WPF, SQLite (Microsoft.Data.Sqlite), NAudio 3.3.0, Whisper.net 1.9.1, xUnit v3.

Spec: `docs/superpowers/specs/2026-11-23-zamanli-kayit-elle-transkript-design.md`

## Global Constraints

- Arayüz metinleri Türkçe, tam diakritikli.
- Testler yalnızca `Notlar.Core` ve `Notlar.Mcp`'ye başvurur; test edilecek saf mantık Core'a konur.
- Çalışma ağacındaki ilgisiz değişikliklere (`DayWindow*`, `NoteEditorWindow*`, `PanelWindow*`, `Theme.cs`, `DayLineEditor.cs`) dokunulmaz, commit'e alınmaz.
- Commit mesajlarında Claude/AI imzası yok. Push yok.
- Test: `dotnet test tests/Notlar.Core.Tests`; derleme: `dotnet build Notlar.slnx`.

---

### Task 1: Core — `Kaydedildi` durumu, aralıklar, zamanlama kararı

**Files:**
- Modify: `src/Notlar.Core/Recording.cs`, `src/Notlar.Core/RecordingStore.cs`
- Create: `src/Notlar.Core/TimeRanges.cs`, `src/Notlar.Core/RecordingSchedule.cs`
- Test: `tests/Notlar.Core.Tests/RecordingStoreTests.cs` (güncelle), `TimeRangesTests.cs`, `RecordingScheduleTests.cs` (yeni)

**Produces:**
- `enum RecordingStatus { Kaydediliyor, Kaydedildi, Bekliyor, Cevriliyor, Hazir, Hata }`
- `record TimeRange(long StartMs, long EndMs)`
- `static IReadOnlyList<TimeRange> TimeRanges.Normalize(IEnumerable<TimeRange>)` — negatif → 0, `End <= Start` atılır, sıralı, çakışan/bitişik birleşir.
- `static IReadOnlyList<(int Start, int End)> TimeRanges.ClipRegions(IReadOnlyList<(int Start, int End)> regions, IReadOnlyList<TimeRange> ranges, int sampleRate)` — boş `ranges` → `regions` aynen; aksi hâlde kesişimler (örnek cinsinden).
- `RecordingStore.Finish` → `Kaydedildi`; `RecoverInterrupted`: `Kaydediliyor → Kaydedildi`, `Cevriliyor → Bekliyor`, dönen liste yalnızca `Bekliyor`.
- `RecordingStore.SetRanges(long id, IEnumerable<TimeRange>)`, `GetRanges(long id)`; tablo `recording_ranges`.
- `enum ScheduleAction { None, Start, Stop }`
- `static ScheduleAction RecordingSchedule.Decide(DateTime now, TimeOnly start, TimeOnly end, bool isRecording, bool currentIsAuto, DateOnly? lastAutoStart)`
- `static bool RecordingSchedule.TryParseTime(string?, out TimeOnly)` ("HH:mm" / "H:mm").

- [ ] Testleri yaz (Finish → Kaydedildi; RecoverInterrupted kuralları; Set/GetRanges; Normalize; ClipRegions; Decide: hafta sonu, pencere içi başlat, günde bir kez, pencere dışı otomatik kaydı durdur, elle kayda dokunma, `end <= start` → None).
- [ ] Çalıştır, kırmızıyı gör; uygula; yeşili gör; commit.

### Task 2: Kayit — aralıklı çeviri ve oynatıcı

**Files:**
- Modify: `src/Notlar.Kayit/WhisperTranscriber.cs`, `src/Notlar.Kayit/TranscriptionQueue.cs`, `src/Notlar.Kayit/AudioRetention.cs`
- Create: `src/Notlar.Kayit/RecordingPlayer.cs`

**Produces:**
- `WhisperTranscriber.TranscribeAsync(string wavPath, Speaker speaker, string language, string modelPath, IProgress<double>? progress = null, CancellationToken ct = default, IReadOnlyList<TimeRange>? ranges = null)` — bölgeler `TimeRanges.ClipRegions` ile kırpılır.
- `TranscriptionQueue.Process` `_store.GetRanges(id)` okur, iki kanala geçirir.
- `AudioRetention.Purge` `Kaydedildi` kayıtların sesini de 14 gün kuralıyla siler (mevcut davranış; yalnız aktif durumlar korunur).
- `sealed class RecordingPlayer : IDisposable` — `static RecordingPlayer Open(string folder)`, `Play()`, `Pause()`, `bool IsPlaying`, `TimeSpan Position {get;set;}`, `TimeSpan Duration`, `event Action? Stopped` (sona gelince; arka plan thread'inden gelebilir).

- [ ] Uygula; `dotnet build`; commit.

### Task 3: App — denetleyici, zamanlayıcı, ayarlar

**Files:**
- Modify: `src/Notlar.App/RecordingController.cs`, `src/Notlar.App/AppSettings.cs`, `src/Notlar.App/App.xaml.cs`, `src/Notlar.App/SettingsView.xaml(.cs)`, `src/Notlar.Mcp/RecordingTools.cs`

**Produces:**
- `RecordingController.Stop()` kuyruğa almaz. `bool StartAutomatic(out string? error)` (KVKK onayı yoksa `false`, mesaj kutusu göstermez). `bool IsCurrentAutomatic`. `bool ConsentGiven`.
- `AppSettings.AutoRecordEnabled = true`, `AutoRecordStart = "09:30"`, `AutoRecordEnd = "10:30"`, `AutoRecordLastDate` (string?, `yyyy-MM-dd`).
- `App`: 20 sn'lik `DispatcherTimer` → `RecordingSchedule.Decide` → başlat/durdur + balon.
- `SettingsView` Kayıt bölümü: onay kutusu + iki saat kutusu.
- `RecordingTools` durum etiketi: `Kaydedildi => "kaydedildi (transkript bekliyor)"`.

- [ ] Uygula; build; test; commit.

### Task 4: App — Kayıtlar görünümü

**Files:**
- Modify: `src/Notlar.App/RecordingsView.xaml`, `src/Notlar.App/RecordingsView.xaml.cs`

- Oynatıcı satırı (▶/⏸, Slider, süre, aralık şeridi, `[ Başlangıç`, `Bitiş ]`, aralık çipleri).
- `TranscribeButton` (`Kaydedildi`'de birincil; etiket aralık sayısına göre).
- Yeniden adlandırma: `🗂 Görev çıkart`, `🗂 Görevler çıkarılıyor…`, `▶ Görevleri başlat`, `🗂 Görevleri yeniden çıkart`.
- `Kaydedildi` durum/rozet/yer tutucu metinleri.
- Oynatıcı; kayıt değişince, görünüm gizlenince, silmeden ve çeviriden önce kapatılır; uygulama kayıt yaparken devre dışı.

- [ ] Uygula; build; uygulamayı çalıştırıp elle doğrula; commit.
