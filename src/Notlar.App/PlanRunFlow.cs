using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Notlar.Core;

namespace Notlar.App;

/// <summary>▶ Planı çalıştır akışı (paneldeki Planlar sekmesi ve Kayıtlar penceresi ortak kullanır): önce 'claude'
/// komutunun PATH'te olup olmadığı kontrol edilir, planın klasörleri çözülür (PlanFolder.ResolveAll), kullanıcıdan
/// <see cref="RunPlanConfirmWindow"/> ile açık onay alınır; onaysız hiçbir şey başlatılmaz.</summary>
public static class PlanRunFlow
{
    private const string ClaudeNotFoundMessage =
        "'claude' komutu PATH üzerinde bulunamadı. Claude Code'un kurulu ve PATH'e eklenmiş olduğundan emin olun.";

    /// <summary><paramref name="showError"/>: kullanıcıya gösterilecek Türkçe hata mesajı.
    /// <paramref name="onPlanMissing"/>: plan bu arada silinmişse çağrılır (ör. listeyi yenilemek için); verilmezse
    /// "plan bulunamadı" mesajı <paramref name="showError"/> ile gösterilir.</summary>
    public static void Run(Window owner, long planId, PlanStore planStore, ProjectStore projectStore,
        Action<string> showError, Action? onPlanMissing = null)
    {
        if (!PlanLauncher.IsClaudeOnPath())
        {
            showError(ClaudeNotFoundMessage);
            return;
        }

        Plan plan;
        IReadOnlyList<PlanStep> steps;
        try
        {
            (plan, steps) = planStore.GetPlan(planId);
        }
        catch (PlanNotFoundException ex)
        {
            if (onPlanMissing is not null) onPlanMissing();
            else showError(ex.Message);
            return;
        }

        try
        {
            var (mainFolder, extraFolders) = PlanFolder.ResolveAll(plan, projectStore, planStore);
            var folderLabel = mainFolder ?? "Kullanıcı klasörü (proje bağlı değil)";
            var projectNameById = projectStore.List().ToDictionary(p => p.Id, p => p.Name);
            var confirmWindow = new RunPlanConfirmWindow(plan.Title, folderLabel, extraFolders, steps, projectNameById) { Owner = owner };
            confirmWindow.ShowDialog();
            if (!confirmWindow.Confirmed) return;

            PlanLauncher.Launch(plan, mainFolder, extraFolders);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            showError(ex switch
            {
                ArgumentException or PlanNotFoundException => ex.Message,
                Microsoft.Data.Sqlite.SqliteException => $"Veritabanı hatası. Yedekler: {AppPaths.BackupDir}",
                _ => $"Bir hata oluştu, ayrıntı: {AppPaths.LogPath}",
            });
        }
    }
}
