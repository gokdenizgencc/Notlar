# Notlar

Windows sistem tepsisinde (tray) çalışan, **tamamen yerel** kişisel not uygulaması. Claude Code'a (ya da MCP destekleyen
başka bir istemciye) "not al: …" dediğinde not, yerel bir MCP sunucusu üzerinden uygulamaya düşer. Toplantıları
kaydedip yerel Whisper ile metne çevirir, Claude ile özetleyip görev planına dönüştürür.

> Bulut yok, hesap yok, telemetri yok. Tüm veri `%APPDATA%\Notlar` altında, kendi makinende durur.

<!-- Ekran görüntüleri: docs/img/ altına eklenecek -->
<!-- ![Panel](docs/img/panel.png) -->

## Özellikler

- **Notlar** — tarih başlıkları altında gruplanır, otomatik saat, `#etiket`, tamamlandı kutusu, arama, Notepad tarzı
  gün editörü, Markdown dışa aktarma.
- **MCP sunucusu** — `not_ekle`, `notlari_listele`, `notlarda_ara` vb. araçlarla Claude notlarını okur/yazar. Her notta
  kimin yazdığı (Ben / Claude / başka istemci) görünür.
- **Hafıza** — Claude'un oturumlar arası hatırlaması gereken bilgiler (`hafiza_*`); oturum başında bağlam olarak verilir.
- **Planlar ve projeler** — adım adım planlar, adım başına proje klasörü, plandan Claude Code oturumu başlatma.
- **Kişisel bölüm** — okuma kilidiyle korunan gizli görev listesi (`kisisel_*`).
- **Yetenekler** — kurulu Claude Code skill/plugin kataloğu, Türkçe açıklamalar, kullanım sayacı.
- **Toplantı kaydı** — mikrofon + sistem sesi (WASAPI loopback) ayrı kanallarda kaydedilir; yerel Whisper
  (`large-v3-turbo`, isteğe bağlı Vulkan GPU) ile metne çevrilir; konuşmacı etiketli transkript, özet, görev planı.
  Hafta içi zamanlı / Teams araması algılandığında otomatik kayıt, elle aralık seçip çevirme, kayıt oynatıcı.

## Mimari

```
Claude Code ──stdio──► NotlarMcp.exe ─┐
                                      ├─► Notlar.Core ─► %APPDATA%\Notlar\notlar.db (SQLite)
Tray ikonu ──────────► NotlarApp.exe ─┘
                            └─► Notlar.Kayit (NAudio + Whisper.net + sherpa-onnx)
```

| Proje | Görev |
|---|---|
| `src/Notlar.Core` | Model, SQLite depoları, transkript mantığı, Claude başlatıcıları |
| `src/Notlar.Mcp` | stdio MCP sunucusu (`NotlarMcp.exe`) |
| `src/Notlar.Kayit` | Ses kaydı, Whisper çevirisi, konuşmacı ayrımı |
| `src/Notlar.App` | WPF tray paneli (`NotlarApp.exe`) |
| `tests/Notlar.Core.Tests` | xUnit testleri |

## Gereksinimler

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`)
- [Claude Code](https://claude.com/claude-code) CLI (`claude` PATH'te olmalı) — MCP kaydı ve özet/plan düğmeleri için

## Kurulum

Yönetici **olmayan** bir PowerShell'de:

```powershell
git clone <repo-url>
cd Notlar
.\kur.ps1
```

`kur.ps1` şunları yapar; çalıştırmadan önce okumanı öneririm:

1. Uygulamayı derleyip `%LOCALAPPDATA%\Programs\Notlar` altına kurar, klasör izinlerini yalnızca geçerli kullanıcıya açar.
2. `claude mcp add notlar --scope user` ile MCP sunucusunu Claude Code'a kaydeder.
3. `~/.claude/CLAUDE.md` dosyasına "not al denince `not_ekle` kullan" satırını ekler (yoksa).
4. Uygulamayı başlatır.

Başka bir istemciyi ayrı yazar adıyla bağlamak için:

```powershell
claude mcp add notlar-baska --scope user -- "$env:LOCALAPPDATA\Programs\Notlar\mcp\NotlarMcp.exe" --yazar BaskaIstemci
```

Oturum başında not/hafıza bağlamını vermek için `NotlarMcp.exe --baglam` komutunu bir SessionStart hook'una bağlayabilirsin.

## Geliştirme

```powershell
dotnet build
dotnet test
```

## Veri, gizlilik ve güvenlik

- Veritabanı, yedekler, log, ses kayıtları ve modeller `%APPDATA%\Notlar` altındadır. Depoya hiçbir kullanıcı verisi girmez.
- Ağ erişimi yalnızca model indirmek içindir: Whisper modeli (Hugging Face) ve konuşmacı modeli (GitHub, sherpa-onnx
  sürümleri). İlk kayıtta bir kez, birkaç yüz MB indirilir.
- Özet / görev planı düğmeleri transkripti yerel `claude` CLI'ına verir; o noktadan sonrası Claude Code'un kendi
  veri politikasına tabidir.
- Ses dosyaları 14 gün sonra otomatik silinir; metin kalır.
- İndirilen modeller koda sabitlenmiş SHA-256 özetiyle doğrulanır; uyuşmayan dosya kullanılmaz.
- "Kişisel" bölüm bir **gizlilik perdesidir, şifreleme değildir**: arayüzde gizlenir ve MCP üzerinden okunması/değiştirilmesi
  varsayılan olarak kapalıdır, ama kayıtlar `notlar.db` içinde düz metindir. Dosya sistemine erişimi olan her süreç
  (shell erişimli bir yapay zekâ ajanı dahil) veritabanını doğrudan okuyabilir. Gerçek sırları buraya yazma.
- Planlar: yapay zekânın eklediği adımlar "(ekleyen: …)" diye işaretlenir ve çalıştırmadan önce onay penceresinde
  gösterilir; çalışan ya da yarıda kalmış plana MCP üzerinden adım eklenemez. Yine de ▶ ile başlatmadan önce adımları oku.
- **Toplantı kaydı yasal sorumluluk doğurur.** Kayıt almadan önce katılımcıları bilgilendirmek ve bulunduğun yerin
  mevzuatına (KVKK, GDPR vb.) uymak senin sorumluluğundadır. Uygulama ilk kayıtta bunu bir kez hatırlatır.
- Not ve transkript içeriği Claude'a **veri** olarak verilir, talimat olarak değil; yine de güvenmediğin metinleri
  (ör. başkasından gelen içerik) notlara eklerken prompt injection riskini aklında tut.

Güvenlik açığı bildirimi için: [SECURITY.md](SECURITY.md).

## Lisans

[MIT](LICENSE). Üçüncü taraf bileşenler kendi lisanslarına tabidir: NAudio, Whisper.net / whisper.cpp, sherpa-onnx,
Microsoft.Data.Sqlite, ModelContextProtocol C# SDK.
