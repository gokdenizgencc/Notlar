using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Notlar.Core;

namespace Notlar.Mcp;

/// <summary>"Projeler": kullanıcının kayıtlı projelerinin listesi ve klasör yolları. Kullanıcı bir
/// projeden isimle bahsettiğinde (ör. plan oluştururken) önce burada ara.</summary>
[McpServerToolType]
public sealed class ProjectTools(ProjectStore store)
{
    [McpServerTool(Name = "proje_listele")]
    [Description("Kullanıcının kayıtlı projelerinin listesi: id, ad ve klasör yolu. Kullanıcı bir projeden isimle bahsettiğinde, o projeyi ve klasörünü bulmak için burayı kullan.")]
    public string ProjeListele() => Run(() =>
    {
        var projects = store.List();
        if (projects.Count == 0)
            return "Proje yok.";

        var sb = new StringBuilder();
        foreach (var p in projects)
            sb.Append($"[#{p.Id}] {TextSafety.Flatten(p.Name)} — {TextSafety.Flatten(p.Path)}\n");
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "proje_ekle")]
    [Description("Yeni bir proje kaydeder: ad ve var olan bir klasörün tam yolu.")]
    public string ProjeEkle(
        [Description("Projenin adı.")] string ad,
        [Description("Projenin bulunduğu klasörün tam yolu; var olmalı.")] string klasor) =>
        Run(() =>
        {
            var project = store.Add(ad, klasor);
            return $"Proje eklendi: #{project.Id} {project.Name} ({project.Path}).";
        });

    [McpServerTool(Name = "proje_guncelle")]
    [Description("Var olan bir projenin adını ve/veya klasör yolunu günceller.")]
    public string ProjeGuncelle(
        [Description("Projenin id'si.")] long id,
        [Description("Yeni ad (opsiyonel).")] string? ad = null,
        [Description("Yeni klasör yolu (opsiyonel); var olmalı.")] string? klasor = null) =>
        Run(() =>
        {
            var project = store.Update(id, ad, klasor);
            return $"Proje güncellendi: #{project.Id} {project.Name} ({project.Path}).";
        });

    [McpServerTool(Name = "proje_sil")]
    [Description("Bir projeyi siler (yumuşak silme). Bu projeye bağlı planlar kendi kayıtlı proje klasörünü kullanmaya devam eder.")]
    public string ProjeSil([Description("Projenin id'si.")] long id) => Run(() =>
    {
        store.Delete(id);
        return $"Proje silindi: #{id}.";
    });

    private static string Run(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is ProjectNotFoundException or ArgumentException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (SqliteException ex)
        {
            throw new McpException($"Veritabanına erişilemedi ({ex.SqliteErrorCode}). Biraz sonra tekrar dene.", ex);
        }
    }
}
