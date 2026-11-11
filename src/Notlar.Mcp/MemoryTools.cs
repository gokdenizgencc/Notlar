using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Notlar.Core;

namespace Notlar.Mcp;

/// <summary>Kullanıcının Claude ile paylaştığı, kalıcı ve kullanıcıya görünür hafıza: Claude burada kişiler,
/// projeler, tercihler ve devam eden işler gibi kısa kartlar tutar. Her oturum başında bu kartlar
/// (--baglam ile) Claude'un bağlamına otomatik olarak yüklenir; kullanıcı bunları Notlar uygulamasında
/// görebilir ve düzenleyebilir.</summary>
[McpServerToolType]
public sealed class MemoryTools(MemoryStore store)
{
    [McpServerTool(Name = "hafiza_kaydet")]
    [Description("""
        Kullanıcı ile paylaşılan, kalıcı Claude hafızasına yeni bir kart ekler. Burayı kullanıcı hakkında
        kalıcı/önemli bilgiler için kullan: kişiler, projeler, tercihler, devam eden işler. Her kart kısa
        olmalı (başlık + 1-2 cümlelik içerik). Önce hafiza_ara ile aynı konu var mı kontrol et; varsa
        yeni kart eklemek yerine hafiza_guncelle kullan. Önerilen kategoriler: kişiler, projeler,
        tercihler, devam-eden, genel.
        """)]
    public string HafizaKaydet(
        [Description("Kısa başlık, örn. 'Ahmet Bey' veya 'Notlar projesi'.")] string baslik,
        [Description("Kısa içerik (1-2 cümle).")] string icerik,
        [Description("Kategori, örn. kişiler, projeler, tercihler, devam-eden, genel. Boşsa 'genel'.")] string? kategori = null) =>
        Run(() => $"Hafızaya kaydedildi: {Line(store.Add(baslik, icerik, kategori))}");

    [McpServerTool(Name = "hafiza_ara")]
    [Description("Hafıza kartları arasında başlık, içerik ve kategoride geçen tüm terimleri arar. Yeni bir kart eklemeden önce tekrar oluşturmamak için burayı kullan.")]
    public string HafizaAra([Description("Arama metni (boşlukla ayrılmış terimler, hepsi eşleşmeli).")] string sorgu) => Run(() =>
    {
        var results = store.Search(sorgu);
        return Format(results, "Eşleşen hafıza kaydı yok.");
    });

    [McpServerTool(Name = "hafiza_listele")]
    [Description("Hafıza kartlarını listeler; verilirse yalnızca bir kategoriyi gösterir.")]
    public string HafizaListele(
        [Description("Kategoriye göre filtrelemek için, örn. kişiler. Boşsa tümü.")] string? kategori = null) => Run(() =>
    {
        var results = store.List(kategori);
        return Format(results, "Hafıza kaydı yok.");
    });

    [McpServerTool(Name = "hafiza_guncelle")]
    [Description("Var olan bir hafıza kartını günceller (belirtilmeyen alanlar değişmez). Aynı konuda yeni kart eklemek yerine bunu kullan.")]
    public string HafizaGuncelle(
        [Description("Hafıza kaydının id'si.")] long id,
        [Description("Yeni başlık (opsiyonel).")] string? baslik = null,
        [Description("Yeni içerik (opsiyonel).")] string? icerik = null,
        [Description("Yeni kategori (opsiyonel).")] string? kategori = null) =>
        Run(() => $"Hafıza güncellendi: {Line(store.Update(id, baslik, icerik, kategori))}");

    [McpServerTool(Name = "hafiza_sil")]
    [Description("Bir hafıza kartını siler.")]
    public string HafizaSil([Description("Hafıza kaydının id'si.")] long id) => Run(() =>
    {
        store.Delete(id);
        return $"Hafıza kaydı silindi (id: {id}).";
    });

    private static string Format(IReadOnlyList<MemoryItem> items, string emptyMessage)
    {
        if (items.Count == 0)
            return emptyMessage;

        var sb = new StringBuilder();
        foreach (var item in items)
            sb.Append(Line(item)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string Line(MemoryItem item) =>
        $"[#{item.Id} · {item.Category}] {item.Title}: {item.Content.ReplaceLineEndings(" ")}";

    private static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is MemoryNotFoundException or ArgumentException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (SqliteException ex)
        {
            throw new McpException($"Veritabanına erişilemedi ({ex.SqliteErrorCode}). Biraz sonra tekrar dene.", ex);
        }
    }
}
