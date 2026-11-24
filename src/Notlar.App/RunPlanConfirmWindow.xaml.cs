using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Notlar.Core;

namespace Notlar.App;

/// <summary>▶ Çalıştır onayı: planın tüm adımlarını numaralı, kaydırılabilir bir liste olarak gösterir
/// (MessageBox uzun planlar için yetersiz kalıyordu). "Ben" dışında biri tarafından eklenen adımlar
/// "(ekleyen: &lt;yazar&gt;)" etiketiyle vurgulanır.</summary>
public partial class RunPlanConfirmWindow : Window
{
    public bool Confirmed { get; private set; }

    public sealed class StepRow
    {
        public required string Number { get; init; }
        public required string Text { get; init; }
        public required string AuthorLabel { get; init; }
        public Visibility AuthorLabelVisibility => AuthorLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public required string ProjectLabel { get; init; }
        public Visibility ProjectLabelVisibility => ProjectLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary><paramref name="extraFolders"/>: planın bağlı olduğu ana projeden başka projelerin klasörleri
    /// (bkz. PlanFolder.ResolveAll); Claude Code'a --add-dir ile erişim verilecektir.
    /// <paramref name="projectNamesById"/>: adımların pill'lerinde proje adını göstermek için.</summary>
    public RunPlanConfirmWindow(
        string planTitle, string folderLabel, IReadOnlyList<string> extraFolders,
        IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<long, string> projectNamesById)
    {
        InitializeComponent();
        TitleText.Text = TextSafety.Flatten(planTitle);
        QuestionText.Text = $"Claude Code bu klasörde açılıp planı uygulamaya başlayacak: {folderLabel}. Devam edilsin mi?";

        if (extraFolders.Count > 0)
        {
            ExtraFoldersPanel.Visibility = Visibility.Visible;
            ExtraFoldersList.ItemsSource = extraFolders;
        }

        StepsList.ItemsSource = steps
            .OrderBy(s => s.Order)
            .Select((s, i) => new StepRow
            {
                Number = $"{i + 1}.",
                // Görünmez/bidi karakterler atılır: pencerede görünen metin Claude'un okuyacağıyla aynı olsun.
                Text = TextSafety.Flatten(s.Text),
                AuthorLabel = s.Author == NoteAuthor.Ben ? "" : $"(ekleyen: {TextSafety.Flatten(s.Author)})",
                ProjectLabel = s.ProjectId is { } pid && projectNamesById.TryGetValue(pid, out var name) ? $"[{name}]" : "",
            })
            .ToList();
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
