# Notlar — Tasarım Dokümanı

Tarih: 2026-11-11

## Amaç

Windows'ta sürekli arka planda çalışan, az kaynak tüketen kişisel not uygulaması.
Kullanıcı Claude Code'a "not al: ..." dediğinde Claude notu MCP üzerinden uygulamaya ekler.
Kullanıcı tray panelinden de not ekleyip düzenleyebilir.

## Kapsam

Dahil:
- Tray ikonu + açılır panel (WPF)
- Tarih başlıkları altında gruplanmış notlar, her notta otomatik kayıt saati
- İsteğe bağlı `#etiket` (metinden otomatik çıkarılır), etikete göre filtre
- Tamamlandı kutucuğu (üstü çizili gösterim)
- Panelden ekle / düzenle / sil (5 sn geri al)
- Claude için MCP sunucusu (yerel, stdio)
- Markdown dışa aktarma, Windows ile başlat

Hariç (YAGNI): hatırlatma/bildirim, bulut senkronu, telefon erişimi, ses, arama kutusu.

## Teknoloji

- .NET 10 (LTS) — kurulacak (`winget install Microsoft.DotNet.SDK.10`)
- WPF + `System.Windows.Forms.NotifyIcon` (`UseWindowsForms=true`, ek paket yok)
- `Microsoft.Data.Sqlite`
- `ModelContextProtocol` (resmi C# MCP SDK) + `Microsoft.Extensions.Hosting`
- xUnit (testler)

## Mimari

```
Claude Code ──stdio──► NotlarMcp.exe ─┐
                                      ├─► Notlar.Core.dll ─► %APPDATA%\Notlar\notlar.db
Tray ikonu ──tıkla──► NotlarApp.exe ──┘
```

Solution: `C:\Users\kullanici\source\repos\Notlar\Notlar.sln`

| Proje | Görev | Bağımlılık |
|---|---|---|
| `Notlar.Core` | Model, SQLite deposu, etiket ayrıştırma, gün gruplama, Markdown dışa aktarma, değişiklik sinyali | Microsoft.Data.Sqlite |
| `Notlar.Mcp` | MCP sunucusu, araçlar Core'u çağırır | Core, ModelContextProtocol |
| `Notlar.App` | WPF tray paneli | Core |
| `Notlar.Core.Tests` | Core + MCP araç testleri | Core, Mcp, xUnit |

### Veri modeli

Tablo `notes`:

| Kolon | Tip | Not |
|---|---|---|
| `id` | INTEGER PK AUTOINCREMENT | |
| `text` | TEXT NOT NULL | Ham metin, `#etiket`ler dahil |
| `created_at` | TEXT NOT NULL | Yerel saat, ISO 8601 + offset. Ekleme anında atanır, sonradan değişmez |
| `updated_at` | TEXT NULL | Son düzenleme |
| `done` | INTEGER NOT NULL DEFAULT 0 | 0/1 |
| `deleted_at` | TEXT NULL | Soft delete (geri al için). Listelerde hariç |

- Etiketler ayrı tabloda tutulmaz; metinden regex ile çıkarılır: `#([\p{L}\p{N}_-]+)`, küçük harfe çevrilir (tr-TR kültürü ile).
- Gösterimde `#etiket` metinden çıkarılır, rozet olarak gösterilir. Düzenlemede ham metin gösterilir.
- Kayıt "günü" = `created_at`'in yerel tarihi.
- Bağlantı ayarları: `journal_mode=WAL`, `busy_timeout=5000`.
- Uygulama açılışında: 7 günden eski soft-delete kayıtlar kalıcı silinir; günlük yedek `%APPDATA%\Notlar\yedek\notlar-YYYY-MM-DD.db` (son 7 tutulur).

### Değişiklik bildirimi

- `Notlar.Core` her yazma işleminden sonra named `EventWaitHandle` (`Local\Notlar.Degisti`) sinyaller.
- `NotlarApp` arka plan thread'inde bu event'i bekler, sinyal gelince listeyi yeniden yükler (UI thread'e dispatch).
- Panel her açılışta ayrıca veriyi yeniden yükler (yedek güvence). Polling yok.

## MCP sunucusu (`NotlarMcp.exe`)

Kayıt: `claude mcp add notlar --scope user -- "%LOCALAPPDATA%\Programs\Notlar\NotlarMcp.exe"`

Araçlar:

| Araç | Parametreler | Dönüş |
|---|---|---|
| `not_ekle` | `metin` (zorunlu) | Eklenen not (id, saat, etiketler). Saat her zaman şimdiki an |
| `notlari_listele` | `baslangic?`, `bitis?` (yyyy-MM-dd), `etiket?`, `sadece_acik?` (bool) | Gün başlıklarına göre gruplanmış metin. Varsayılan: son 7 gün |
| `not_guncelle` | `id`, `metin` | Güncel not |
| `not_tamamla` | `id`, `tamamlandi` (varsayılan true) | Güncel not |
| `not_sil` | `id` | Onay mesajı (soft delete) |

- Araç açıklaması Claude'a: "Kullanıcı 'not al', 'not et', 'kaydet' dediğinde `not_ekle` kullan."
- Ek olarak kullanıcının `~/.claude/CLAUDE.md` dosyasına aynı yönerge bir satır olarak eklenir.
- stdout yalnızca MCP protokolüne ayrılır; log stderr'e yazılır.

## Tray paneli (`NotlarApp.exe`)

- Tek örnek: named `Mutex`. İkinci başlatma mevcut paneli açar ve çıkar.
- Sol tık: panel sağ altta, görev çubuğunun üstünde açılır (~360×520). Odak kaybında gizlenir.
- Sağ tık menüsü: Aç · Markdown olarak dışa aktar · Windows ile başlat (✓) · Çıkış.
- Windows ile başlat: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Notlar`. İlk çalıştırmada açık.
- Tema: `HKCU\...\Themes\Personalize\AppsUseLightTheme` okunur, açık/koyu.

Panel içeriği:
- Üstte yazı kutusu, Enter → `not_ekle` ile aynı Core çağrısı. Boş metin yok sayılır.
- Etiket filtre çipleri (Tümü + mevcut etiketler). Etiket yoksa satır gizli.
- Liste: günler yeniden eskiye, gün içinde saat sırası eskiden yeniye.
- Gün başlığı: "BUGÜN · 11 Kasım 2026, Çarşamba", "DÜN · ...", diğerleri "10 Kasım 2026, Salı" (tr-TR).
- Satır: kutucuk · `HH:mm` · metin · etiket rozetleri. Tamamlanan: üstü çizili, soluk.
- Çift tık: yerinde düzenleme, Enter kaydet, Esc iptal. Boşaltıp Enter → değişiklik iptal.
- Silme: üzerine gelince 🗑 veya seçiliyken Delete tuşu → soft delete + 5 sn "Geri al" çubuğu.

Markdown dışa aktarma: `Belgeler\Notlar-YYYY-MM-DD.md`, sonra Explorer'da seçili açılır.

```markdown
## 11 Kasım 2026, Çarşamba
- [ ] 09:12 Sunumu bitir #iş
- [x] 14:32 Faturayı öde #ev
```

## Hata durumları

| Durum | Davranış |
|---|---|
| DB kilitli | `busy_timeout` 5 sn bekler; yine başarısızsa panelde kısa hata çubuğu, MCP'de `isError` ile açıklayıcı mesaj |
| Bilinmeyen `id` (MCP) | `isError`: "Not bulunamadı: {id}" |
| Boş metin | Panel yok sayar; MCP `isError` döner |
| DB dosyası yok | Otomatik oluşturulur (şema `CREATE TABLE IF NOT EXISTS`) |
| DB bozuk | Panel hata gösterir, yedek klasörünün yolunu bildirir; otomatik geri yükleme yok |
| Beklenmeyen hata (App) | `%APPDATA%\Notlar\log.txt`'ye yazılır, uygulama çökmez |

## Kurulum

- `dotnet publish` → framework-dependent: `%LOCALAPPDATA%\Programs\Notlar\app\` ve `...\mcp\` (ayrı klasörler, bağımlılık çakışması yok).
- `kur.ps1`: çalışan süreçleri kapatır, publish eder, MCP'yi `--scope user` kaydeder, `~/.claude/CLAUDE.md`'ye yönerge satırı ekler (yoksa), uygulamayı başlatır.
- Run anahtarını uygulama yazar: ilk açılışta açar, sonraki açılışlarda açıksa yolu günceller.

## Test

- `Notlar.Core.Tests` (geçici DB dosyasıyla):
  - Ekle/listele/güncelle/tamamla/sil/geri al
  - Etiket ayrıştırma (Türkçe karakter: `#işler`, `#İstanbul` → `istanbul`)
  - Gün gruplama ve sıralama, gece yarısı sınırı
  - Markdown dışa aktarma çıktısı
  - Değişiklik sinyalinin yazma sonrası tetiklenmesi
  - MCP araç metodları (doğrudan çağrı, hata durumları)
- Manuel kontrol listesi (panel): açılma konumu, odak kaybında gizlenme, tema, düzenleme, geri al, Claude'dan eklenen notun panelde anında görünmesi, yeniden başlatmada otomatik açılma, boştaki bellek (< 80 MB hedef).
