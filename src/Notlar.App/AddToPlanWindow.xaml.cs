using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Seçili bir ya da birden çok notu bir plana adım olarak ekler. Mevcut bir plan seçilebilir
/// (adım sayısı/durumu ile listelenir, en yeni güncellenen en üstte) ya da projesiz yeni bir plan
/// oluşturulabilir (proje daha sonra Planlar sekmesinden bağlanabilir). Plan seçildikten sonra mevcut
/// adımlar salt okunur gösterilir ve eklenecek konum (en başa / en sona / belirli bir adımdan sonra)
/// seçilir; notlar seçilen konuma sırasıyla eklenir.</summary>
public partial class AddToPlanWindow : Window
{
    private readonly PlanStore _planStore;
    private readonly ProjectStore _projectStore;
    private readonly IReadOnlyList<string> _noteTexts;
    private List<Plan> _allPlans = [];
    private Plan? _selectedPlan;
    private List<PlanStep> _selectedPlanSteps = [];

    public long ResultPlanId { get; private set; }
    public string ResultPlanTitle { get; private set; } = "";
    public int ResultAddedCount { get; private set; }

    public sealed record PlanPickItem(long Id, string Title, string StatusLabel, Brush StatusBrush, string StepCountLabel);

    public sealed record StepPickItem(int Order, string Label);

    public AddToPlanWindow(PlanStore planStore, ProjectStore projectStore, IReadOnlyList<string> noteTexts)
    {
        _planStore = planStore;
        _projectStore = projectStore;
        _noteTexts = noteTexts;
        InitializeComponent();
        LoadPlans();
        Dispatcher.InvokeAsync(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    private void LoadPlans()
    {
        _allPlans = _planStore.ListPlans().ToList();
        ApplySearch();
    }

    private void ApplySearch()
    {
        var query = SearchBox.Text.Trim();
        var filtered = query.Length == 0
            ? _allPlans
            : _allPlans.Where(p => p.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var items = filtered.Select(p =>
        {
            var steps = _planStore.GetPlan(p.Id).Steps;
            return new PlanPickItem(p.Id, p.Title, PlanStatusLabel(p.Status), PlanStatusBrush(p.Status), $"{steps.Count} adım");
        }).ToList();

        PlansList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplySearch();

    private void PlanPick_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlanPickItem item }) return;
        SelectPlan(item.Id);
    }

    private void NewPlanExpander_Expanded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => NewPlanTitleBox.Focus(), DispatcherPriority.Input);

    private void NewPlanTitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CreateNewPlan();
        }
    }

    private void NewPlanCreate_Click(object sender, RoutedEventArgs e) => CreateNewPlan();

    private void CreateNewPlan()
    {
        var title = NewPlanTitleBox.Text.Trim();
        if (title.Length == 0) return;
        try
        {
            var plan = _planStore.CreatePlan(title, projectPath: null, steps: []);
            NewPlanExpander.IsExpanded = false;
            NewPlanTitleBox.Clear();
            LoadPlans();
            SelectPlan(plan.Id);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SelectPlan(long id)
    {
        var (plan, steps) = _planStore.GetPlan(id);
        _selectedPlan = plan;
        _selectedPlanSteps = steps.OrderBy(s => s.Order).ToList();

        ListPanel.Visibility = Visibility.Collapsed;
        ListScroll.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        SelectedPlanTitleText.Text = plan.Title;
        StepsList.ItemsSource = _selectedPlanSteps.Select(s => $"{s.Order}. {FlattenStepText(s.Text)}").ToList();
        NoStepsText.Visibility = _selectedPlanSteps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AfterStepBox.ItemsSource = _selectedPlanSteps
            .Select(s => new StepPickItem(s.Order, $"{s.Order}. {FlattenStepText(s.Text)}"))
            .ToList();
        AfterStepBox.SelectedIndex = _selectedPlanSteps.Count > 0 ? _selectedPlanSteps.Count - 1 : -1;
        AfterStepRadio.IsEnabled = _selectedPlanSteps.Count > 0;

        EndRadio.IsChecked = true;
        AddButton.IsEnabled = true;

        var planProjectIds = _planStore.GetPlanProjects(plan.Id);
        var liveProjects = _projectStore.List();
        var options = new List<ProjectComboItem> { new(null, "—") };
        options.AddRange(planProjectIds
            .Select(id => liveProjects.FirstOrDefault(p => p.Id == id))
            .Where(p => p is not null)
            .Select(p => new ProjectComboItem(p!.Id, p.Name)));
        StepProjectBox.ItemsSource = options;
        StepProjectBox.SelectedIndex = 0;
    }

    private static string FlattenStepText(string text) =>
        text.Length > 60 ? string.Concat(text.AsSpan(0, 57), "...") : text;

    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        _selectedPlan = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        ListScroll.Visibility = Visibility.Visible;
        AddButton.IsEnabled = false;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlan is not { } plan) return;
        try
        {
            int? basePosition = StartRadio.IsChecked == true
                ? 1
                : AfterStepRadio.IsChecked == true && AfterStepBox.SelectedItem is StepPickItem step
                    ? step.Order + 1
                    : null; // "En sona ekle" (varsayılan)

            var projectId = (StepProjectBox.SelectedItem as ProjectComboItem)?.Id;

            for (var i = 0; i < _noteTexts.Count; i++)
            {
                var position = basePosition is { } p ? p + i : (int?)null;
                _planStore.AddStep(plan.Id, _noteTexts[i], position, projectId: projectId);
            }

            ResultPlanId = plan.Id;
            ResultPlanTitle = plan.Title;
            ResultAddedCount = _noteTexts.Count;
            DialogResult = true;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or PlanNotFoundException)
        {
            MessageBox.Show(ex.Message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private static string PlanStatusLabel(PlanStatus status) => status switch
    {
        PlanStatus.Taslak => "Taslak",
        PlanStatus.Calisiyor => "Çalışıyor",
        PlanStatus.Bitti => "Bitti",
        PlanStatus.Hata => "Hata",
        _ => "",
    };

    private Brush PlanStatusBrush(PlanStatus status) => status switch
    {
        PlanStatus.Taslak => (Brush)FindResource("Muted"),
        PlanStatus.Calisiyor => (Brush)FindResource("Accent"),
        PlanStatus.Bitti => (Brush)FindResource("Success"),
        PlanStatus.Hata => (Brush)FindResource("Danger"),
        _ => (Brush)FindResource("Muted"),
    };
}
