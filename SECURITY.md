# Güvenlik

## Açık bildirme

Bir güvenlik açığı bulduysan lütfen **herkese açık issue açma**. GitHub'ın
"Security → Report a vulnerability" (özel güvenlik bildirimi) özelliğini kullan. Kişisel bir proje olduğu için yanıt
süresi garanti edilmez, ama bildirimler ciddiye alınır.

Bildirimde şunlar işe yarar: etkilenen dosya/özellik, yeniden üretme adımları, olası etki.

## Tehdit modeli (kısaca)

Notlar tek kullanıcılı, yerel bir masaüstü uygulamasıdır; ağdan dinleyen bir servisi yoktur.

- **Güven sınırı:** geçerli Windows kullanıcısı. Aynı kullanıcı olarak çalışan kötü amaçlı yazılım kapsam dışıdır.
- **MCP sunucusu** yalnızca stdio üzerinden, onu başlatan istemciyle konuşur.
- **Veri** `%APPDATA%\Notlar` altında durur; kurulum ve veri klasörü ACL'leri geçerli kullanıcıyla sınırlandırılır.
- **Süreçler arası sinyaller** (event/mutex) kullanıcı SID'i ile adlandırılır.
- **Dış süreçler** (`claude`, terminal) kabuk yorumlaması olmadan, argüman listesiyle başlatılır; kullanıcı/MCP kaynaklı
  metin komut satırına ham olarak girmez.
- **Prompt injection:** not, hafıza, özet ve transkript içeriği modele açıkça "veri" olarak çerçevelenir; MCP çıktılarında
  satır sonu, bidi ve görünmez karakterler temizlenir. Bu bir azaltmadır, garanti değildir.
- **Model indirmeleri** HTTPS üzerinden yapılır ve koda sabitlenmiş SHA-256 özetiyle doğrulanır.
- **Planlar:** kullanıcının yazmadığı adımlar işaretlenir; çalışan/yarıda kalmış plana MCP üzerinden adım eklenemez; plan
  oturumu izin atlatma bayrağı olmadan açılır (Claude Code'un kendi izin istemleri devrededir).

## Bilinen sınırlar

- "Kişisel" bölüm şifrelenmez; yalnızca arayüzde gizlenir ve MCP erişimi varsayılan olarak kapalıdır. Veritabanı dosyasını
  okuyabilen her süreç içeriğe ulaşır.
- Plan onayı adım bazında kalıcı olarak kaydedilmez: taslak bir plana, ▶ onayından önce yapay zekâ adım ekleyebilir. Bu
  adımlar onay penceresinde "(ekleyen: …)" etiketiyle görünür.
- NuGet kilit dosyası (`packages.lock.json`) yoktur; `kur.ps1` derleme anındaki paket sürümlerini kullanır.
- Markdown dışa aktarma not metnini kaçışsız yazar; dışa aktarılan dosyayı HTML işleyen bir görüntüleyicide açarken
  güvenmediğin içeriğe dikkat et.

## Kapsam dışı

- Fiziksel erişimi ya da aynı kullanıcı hesabını ele geçirmiş saldırgan.
- Üçüncü taraf bağımlılıklardaki açıklar (ilgili projeye bildirilmeli; sürüm yükseltme PR'ları memnuniyetle karşılanır).
