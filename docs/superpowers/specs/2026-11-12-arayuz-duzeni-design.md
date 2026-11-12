# Arayüz Düzeni (Panel Yeniden Tasarımı) — Tasarım Dokümanı

Tarih: 2026-11-12

## Sorun

- 5 sekme düğmesi 360 px panelde 2 satıra taşıyor; sekmenin adı ayrıca başlık olarak tekrar ediliyor.
- Notlar sekmesinde içerikten önce 3 satır filtre (yazar, etiket, "Başka gün") var.
- Yetenekler sekmesinde 2 satır filtre/sıralama var.
- Kayıtlar ayrı, sistem başlık çubuklu, farklı görünümlü bir pencere; 7 düğme kaydırılarak sarıyor.
- Üst çubuktaki ikonlar (⛶ ⠿ 📌 ⤢ —) anlamı belirsiz; ayarlar dağınık (tray menüsü, Görünüm penceresi, panel içi seçenekler).

## Hedef düzen (kullanıcı onaylı: "A · Sol ikon şeridi")

**İskelet**
- Solda 46 px dikey ikon şeridi: 📝 Notlar · 🧠 Hafıza · 📋 Planlar · 🎙 Kayıtlar · 🧩 Yetenekler; en altta ⚙ Ayarlar.
  Etkin sekme: hafif vurgulu arka plan + solda 3 px vurgu çizgisi. Her ikonda ToolTip (sekme adı). Klavye: Ctrl+1…5.
- Kişisel (gizli) bölüm bugünkü erişim yoluyla korunur (davranış değişmez).
- Sekme başlığı satırı ve 2 satırlık sekme düğmeleri kalkar.
- Üst çubuk: sol tarafta sürükleme tutamacı + etkin sekmenin adı; sağda 📌 sabitle, ⤢ büyük mod, — gizle (hepsinde ToolTip).
- Sağ altta sabit yuvarlak kayıt düğmesi (🔴/⏺; kayıtta ⏹ + süre). Kayıtlar sekmesindeyken listenin üstündeki büyük düğme yeterli olduğundan gizlenir.

**Notlar**: not giriş kutusu; altında tek satır: yazar çipleri (Tümü/Ben/Claude…) + `▾ #etiket` açılır menüsü + 📅 (Başka gün) ikonu. Gün listesi aynı.

**Hafıza**: arama + "+ Yeni kart" aynı satırda (kart ekleme alanı açılır/kapanır, bugünkü gibi).

**Planlar**: Planlar/Projeler alt sekmesi korunur; görünüm iskelete uyar.

**Yetenekler**: arama; altında tek satır: Kurulu/Keşfet segment + sıralama açılır menüsü + ↻ Yenile; "+ Elle ekle" arama satırına ikon olarak.

**Kayıtlar (panel içinde; RecordingsWindow kaldırılır)**
- Liste görünümü: üstte geniş kayıt düğmesi (⏺ Kaydı başlat / ⏹ Durdur · mm:ss); kayıt satırları: başlık, zaman · süre, durum rozeti (Çevriliyor %n, Hazır, Hata, özet ✓).
- Dar panel: satıra tıklayınca detay; "‹ Kayıtlar" ile geri. Büyük mod: liste solda (≈210 px), detay sağda.
- Detay: başlık (yerinde düzenlenebilir), zaman · süre · dil satırı, durum/hata satırı; 3 ana eylem: **✨ Özetle** (birincil), **🗂 Plan** (plan varsa **▶ Planı başlat** olur; plan oluşturma ⋯ menüsünde "Yeniden planla"), **💬 Claude**; **⋯** menüsü: 🔁 Yeniden çevir, 📋 Kopyala, 🌐 Dil, 📁 Proje, 🗑 Sil.
- Transkript / Özet iki sekme; konuşmacılar renkli ("Ben" ve "Karşı taraf" farklı tonda).
- Tray balonu tıklanınca paneli açar, Kayıtlar sekmesinde ilgili kaydı seçer.
- Tüm mevcut akış ve korumalar (onaylar, iş sürerken pasif düğmeler, pencere kapansa da süren headless işler) korunur.

**⚙ Ayarlar (panel içinde bir görünüm)**: Görünüm (yazı boyutu, yazar renkleri — mevcut AppearanceWindow içeriği ya da ona bağlantı), Windows ile başlat, yeni kayıtların dili, kişisel bölümün Claude'a açıklığı ve panelde mevcut olan diğer ayarlar. Tray menüsündeki seçenekler aynen kalır.

## Uygulama kuralları

- PanelWindow.xaml.cs 2600+ satır: yeni görünümler mümkün olduğunca ayrı UserControl'lere (ör. `RecordingsView`, `SettingsView`, `NavRail`) çıkarılır; PanelWindow yalnızca iskelet + gezinme koordinasyonu yapar. İş mantığı değişmez.
- Tema: yalnızca mevcut DynamicResource anahtarları; açık/koyu temada okunabilir. Kod içinde atanan renkler tema kaynaklarına taşınır.
- Kompakt panel 360×520 ve büyük mod korunur; tüm sekmeler kompakt boyutta yatay kaydırmasız sığar.

## Doğrulama

- `dotnet build` uyarısız, testler yeşil.
- Her sekmenin (kompakt + büyük mod, Kayıtlar liste + detay, Ayarlar) UI Automation ile ekran görüntüsü alınıp gözden geçirilir; kullanıcıya gösterilir.
