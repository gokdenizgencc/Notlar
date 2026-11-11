using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Notlar.Core;

namespace Notlar.Mcp;

[McpServerToolType]
public sealed class NoteTools(NoteStore store, TimeProvider time, McpAuthor? author = null)
{
    [McpServerTool(Name = "not_ekle")]
    [Description("Kullanıcının kişisel not uygulamasına yeni not ekler. Kullanıcı 'not al', 'not et', 'kaydet', 'aklında tut' dediğinde bunu kullan. Kayıt saati otomatik atanır, ya da tarih/saat belirtilerek geçmiş (veya istenen) bir güne not eklenebilir. Metindeki #kelime etiket olur.")]
    public string NotEkle(
        [Description("Not metni. İsteğe bağlı #etiket içerebilir.")] string metin,
        [Description("Notun tarihi. Boşsa bugün.")] string? tarih = null,
        [Description("Notun saati. Boşsa şimdiki saat.")] string? saat = null) => Run(() =>
    {
        var now = time.GetLocalNow();
        var date = ParseDate(tarih) ?? DateOnly.FromDateTime(now.DateTime);
        var timeOfDay = ParseTime(saat) ?? TimeOnly.FromDateTime(now.DateTime);
        var createdAt = new DateTimeOffset(date.Year, date.Month, date.Day,
            timeOfDay.Hour, timeOfDay.Minute, timeOfDay.Second, now.Offset);
        var note = store.AddAt(metin, createdAt, author?.Name ?? NoteAuthor.Claude);
        return $"Not eklendi: {NoteFormatter.Line(note)} — {DayGrouper.FormatDate(note.Day)}";
    });

    [McpServerTool(Name = "notlari_listele")]
    [Description("Notları gün başlıklarına göre gruplanmış listeler. Tarih verilmezse son 7 günü gösterir.")]
    public string NotlariListele(
        [Description("Başlangıç tarihi (yyyy-MM-dd). Boşsa bitişten 6 gün öncesi.")] string? baslangic = null,
        [Description("Bitiş tarihi (yyyy-MM-dd). Boşsa bugün.")] string? bitis = null,
        [Description("Sadece bu etiketi taşıyan notlar, örn. 'iş'.")] string? etiket = null,
        [Description("true ise yalnızca tamamlanmamış notlar.")] bool sadece_acik = false,
        [Description("Sadece bu yazarın notları, örn. 'Ben', 'Claude', 'ChatGPT'. Boşsa hepsi.")] string? yazar = null) => Run(() =>
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var to = ParseDate(bitis) ?? today;
        var from = ParseDate(baslangic) ?? to.AddDays(-6);
        var notes = store.List(new NoteFilter(from, to, etiket, sadece_acik, ParseAuthor(yazar)));
        if (notes.Count == 0)
            return "Bu aralıkta not yok.";

        var sb = new StringBuilder();
        foreach (var group in DayGrouper.Group(notes))
        {
            sb.Append("## ").Append(DayGrouper.Header(group.Date, today)).Append('\n');
            foreach (var note in group.Notes)
                sb.Append(NoteFormatter.Line(note)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "not_guncelle")]
    [Description("Var olan bir notun metnini değiştirir. Kayıt saati değişmez.")]
    public string NotGuncelle(
        [Description("Notun id'si (notlari_listele çıktısındaki 'id').")] long id,
        [Description("Yeni not metni.")] string metin) =>
        Run(() => $"Not güncellendi: {NoteFormatter.Line(store.Update(id, metin))}");

    [McpServerTool(Name = "not_tamamla")]
    [Description("Notu tamamlandı olarak işaretler (üstü çizilir) veya tekrar açar.")]
    public string NotTamamla(
        [Description("Notun id'si.")] long id,
        [Description("true: tamamlandı, false: tekrar aç.")] bool tamamlandi = true) => Run(() =>
    {
        var note = store.SetDone(id, tamamlandi);
        return $"{(tamamlandi ? "Not tamamlandı" : "Not tekrar açıldı")}: {NoteFormatter.Line(note)}";
    });

    [McpServerTool(Name = "not_sil")]
    [Description("Notu siler.")]
    public string NotSil([Description("Notun id'si.")] long id) => Run(() =>
    {
        store.Delete(id);
        return $"Not silindi (id: {id}).";
    });

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new ArgumentException($"Geçersiz tarih '{value}', yyyy-MM-dd bekleniyor.");
    }

    private static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        throw new ArgumentException($"Geçersiz saat '{value}', HH:mm bekleniyor.");
    }

    private static string? ParseAuthor(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [McpServerTool(Name = "notlarda_ara")]
    [Description("Notların metninde kelime arar (Türkçe büyük/küçük harf duyarsız). Kullanıcı 'şunla ilgili notlarımı bul', 'X geçen notlar' dediğinde kullan.")]
    public string NotlardaAra(
        [Description("Aranacak kelimeler; tümünü içeren notlar bulunur.")] string sorgu,
        [Description("Sadece bu yazarın notları, örn. 'Ben', 'Claude', 'ChatGPT'. Boşsa hepsi.")] string? yazar = null,
        [Description("Sonuç sayısı sınırı.")] int limit = 20) => Run(() =>
    {
        var clampedLimit = Math.Clamp(limit, 1, 100);
        var notes = store.Search(sorgu, ParseAuthor(yazar), clampedLimit);
        if (notes.Count == 0)
            return "Eşleşen not yok.";

        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var sb = new StringBuilder();
        sb.Append(notes.Count).Append(" not bulundu.\n\n");
        foreach (var group in DayGrouper.Group(notes))
        {
            sb.Append("## ").Append(DayGrouper.Header(group.Date, today)).Append('\n');
            foreach (var note in group.Notes)
                sb.Append(NoteFormatter.Line(note)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    });

    private static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is NoteNotFoundException or ArgumentException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (SqliteException ex)
        {
            throw new McpException($"Veritabanına erişilemedi ({ex.SqliteErrorCode}). Biraz sonra tekrar dene.", ex);
        }
    }
}
