using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Notlar.Core;

namespace Notlar.Mcp;

[McpServerToolType]
public sealed class TaskTools(TaskStore store)
{
    [McpServerTool(Name = "gorev_ekle")]
    [Description("Kullanıcının gizli görev listesine yeni bir görev başlığı ekler. Kullanıcı 'görev ekle', 'task ekle', 'yapılacaklara ekle' dediğinde kullan.")]
    public string GorevEkle(
        [Description("Görev başlığı.")] string baslik,
        [Description("Görevi ekleyen asistan, örn. 'Claude' veya 'ChatGPT'.")] string yazar = "Claude") => Run(() =>
    {
        var task = store.Add(baslik, yazar);
        return $"Görev eklendi: {Line(task)}";
    });

    [McpServerTool(Name = "gorevleri_listele")]
    [Description("Görev listesini gösterir; açık görevler önce, tamamlananlar sonra listelenir.")]
    public string GorevleriListele(
        [Description("true ise yalnızca tamamlanmamış görevler.")] bool sadece_acik = false) => Run(() =>
    {
        var tasks = store.List(sadece_acik);
        if (tasks.Count == 0)
            return "Görev yok.";

        var sb = new StringBuilder();
        foreach (var task in tasks)
            sb.Append(Line(task)).Append('\n');
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "gorev_tamamla")]
    [Description("Görevi tamamlandı olarak işaretler veya tekrar açar.")]
    public string GorevTamamla(
        [Description("Görevin id'si.")] long id,
        [Description("true: tamamlandı, false: tekrar aç.")] bool tamamlandi = true) => Run(() =>
    {
        var task = store.SetDone(id, tamamlandi);
        return $"{(tamamlandi ? "Görev tamamlandı" : "Görev tekrar açıldı")}: {Line(task)}";
    });

    [McpServerTool(Name = "gorev_guncelle")]
    [Description("Var olan bir görevin başlığını değiştirir.")]
    public string GorevGuncelle(
        [Description("Görevin id'si.")] long id,
        [Description("Yeni görev başlığı.")] string baslik) =>
        Run(() => $"Görev güncellendi: {Line(store.Rename(id, baslik))}");

    [McpServerTool(Name = "gorev_sil")]
    [Description("Görevi siler.")]
    public string GorevSil([Description("Görevin id'si.")] long id) => Run(() =>
    {
        store.Delete(id);
        return $"Görev silindi (id: {id}).";
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
