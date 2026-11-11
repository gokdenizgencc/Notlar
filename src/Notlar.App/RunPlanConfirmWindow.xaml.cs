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
    }

    public RunPlanConfirmWindow(string planTitle, string folderLabel, IReadOnlyList<PlanStep> steps)
    {
        InitializeComponent();
        TitleText.Text = planTitle;
        QuestionText.Text = $"Claude Code bu klasörde açılıp planı uygulamaya başlayacak: {folderLabel}. Devam edilsin mi?";
        StepsList.ItemsSource = steps
            .OrderBy(s => s.Order)
            .Select((s, i) => new StepRow
            {
                Number = $"{i + 1}.",
                Text = s.Text,
                AuthorLabel = s.Author == NoteAuthor.Ben ? "" : $"(ekleyen: {s.Author})",
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
