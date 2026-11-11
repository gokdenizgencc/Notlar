using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Notlar.Core;

namespace Notlar.Mcp;

[McpServerToolType]
public sealed class TaskTools(TaskStore store, McpAuthor? author = null)
{
    [McpServerTool(Name = "kisisel_ekle")]
    [Description("Kullanıcının gizli, ÖZEL kişisel listesine yeni bir kayıt ekler (kişisel plan, iş, özel yapılacak). Notlar uygulamasının gizli bölümünde gösterilir. Yalnızca kullanıcı açıkça kişisel/özel bir şeyden bahsettiğinde kullan ('kişisel listeme ekle', 'kişisel planlarım', 'özel işlerim'). İş notlarını buraya ASLA koyma.")]
    public string KisiselEkle(
        [Description("Kişisel kaydın başlığı.")] string baslik,
        [Description("Kaydı ekleyen asistan, örn. 'Claude' veya 'ChatGPT'. Boşsa bu MCP sunucusunun varsayılan yazarı kullanılır.")] string? yazar = null) => Run(() =>
    {
        var effectiveAuthor = string.IsNullOrWhiteSpace(yazar) ? (author?.Name ?? NoteAuthor.Claude) : yazar.Trim();
        var task = store.Add(baslik, effectiveAuthor);
        return $"Kişisel kayıt eklendi: {Line(task)}";
    });

    [McpServerTool(Name = "kisisel_listele")]
    [Description("Kullanıcının gizli kişisel listesini gösterir; açık kayıtlar önce, tamamlananlar sonra listelenir. Bu veriler özeldir; kullanıcı açıkça istemeden okuma.")]
    public string KisiselListele(
        [Description("true ise yalnızca tamamlanmamış kayıtlar.")] bool sadece_acik = false) => Run(() =>
    {
        var tasks = store.List(sadece_acik);
        if (tasks.Count == 0)
            return "Kişisel kayıt yok.";

        var sb = new StringBuilder();
        foreach (var task in tasks)
            sb.Append(Line(task)).Append('\n');
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "kisisel_tamamla")]
    [Description("Kişisel kaydı tamamlandı olarak işaretler veya tekrar açar.")]
    public string KisiselTamamla(
        [Description("Kaydın id'si.")] long id,
        [Description("true: tamamlandı, false: tekrar aç.")] bool tamamlandi = true) => Run(() =>
    {
        var task = store.SetDone(id, tamamlandi);
        return $"{(tamamlandi ? "Kişisel kayıt tamamlandı" : "Kişisel kayıt tekrar açıldı")}: {Line(task)}";
    });

    [McpServerTool(Name = "kisisel_guncelle")]
    [Description("Var olan bir kişisel kaydın başlığını değiştirir.")]
    public string KisiselGuncelle(
        [Description("Kaydın id'si.")] long id,
        [Description("Yeni başlık.")] string baslik) =>
        Run(() => $"Kişisel kayıt güncellendi: {Line(store.Rename(id, baslik))}");

    [McpServerTool(Name = "kisisel_sil")]
    [Description("Kişisel kaydı siler.")]
    public string KisiselSil([Description("Kaydın id'si.")] long id) => Run(() =>
    {
        store.Delete(id);
        return $"Kişisel kayıt silindi (id: {id}).";
    });

    private static string Line(TaskItem task) =>
        $"{(task.Done ? "[x]" : "[ ]")} {task.Title.ReplaceLineEndings(" ")}  (id: {task.Id}, yazar: {task.Author})";

    private static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is TaskNotFoundException or ArgumentException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (SqliteException ex)
        {
            throw new McpException($"Veritabanına erişilemedi ({ex.SqliteErrorCode}). Biraz sonra tekrar dene.", ex);
        }
    }
}
