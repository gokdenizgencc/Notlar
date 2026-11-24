using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Kayıt detayındaki "Görevler" sekmesi: kayda bağlı planın adımları sorumluya (kime verildi) göre gruplu
/// gösterilir; her sorumlu plandaki sırasına göre ayrı renktedir. Adımdaki "👤 ad ▾" rozetine tıklayınca sorumlu
/// değiştirilir (konuşmacılardan / plandaki adlardan seçilir ya da elle yazılır). "📋 Kime ne verildi" özeti
/// ("Ahmet: x, y / Ayşe: z") panoya kopyalar.</summary>
public partial class RecordingsView
{
    /// <summary>Son çizilen görev listesinin imzası; değişmediyse liste yeniden kurulmaz (kaydırma korunur).</summary>
    private string _tasksSignature = "";
    private IReadOnlyList<PlanStep> _taskSteps = [];

    /// <summary>Kayda bağlı planın adımlarını okuyup Görevler sekmesini kurar. Bir sorumlu satır içinde
    /// düzenleniyorsa (kutu odaktaysa) yazılan metin ezilmesin diye yeniden kurulmaz.</summary>
    private void RenderTasks(Recording r)
    {
        IReadOnlyList<PlanStep> steps = [];
        var planMissing = false;
        if (r.PlanId is { } planId && _planStore is not null)
        {
            try
            {
                steps = _planStore.GetPlan(planId).Steps;
            }
            catch (PlanNotFoundException)
            {
                planMissing = true;
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        }

        TasksPlaceholder.Text = planMissing
            ? "Bu kayda bağlı görev planı silinmiş. 🗂 Görev çıkart ile görevleri yeniden çıkartabilirsiniz."
            : "Henüz görev yok. 🗂 Görev çıkart ile Claude'a toplantıdaki görevleri ve kime verildiklerini çıkartabilirsiniz.";

        var signature = $"{r.Id}|{r.PlanId}|" + string.Join("\n",
            steps.Select(s => $"{s.Id}\t{s.Order}\t{s.Status}\t{s.Text}\t{s.Assignee}\t{s.DueText}"));
        if (signature == _tasksSignature || TasksList.IsKeyboardFocusWithin) return;
        _tasksSignature = signature;
        _taskSteps = steps;

        TasksList.Children.Clear();
        if (steps.Count == 0) return;

        var order = PlanAssignees.Order(steps);
        var showHeaders = PlanAssignees.AnyAssigned(steps);
        foreach (var group in PlanAssignees.Group(steps))
        {
            if (showHeaders) TasksList.Children.Add(TaskGroupHeader(group, order));
            foreach (var step in group.Steps)
                TasksList.Children.Add(TaskRow(step, order));
        }
    }

    /// <summary>Görevler sekmesinin görünürlüğü (bkz. <see cref="UpdateTabs"/>).</summary>
    private void UpdateTasksTab(bool show)
    {
        var hasTasks = _taskSteps.Count > 0;
        TasksPane.Visibility = show && hasTasks ? Visibility.Visible : Visibility.Collapsed;
        TasksPlaceholder.Visibility = show && !hasTasks ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Grup başlığı: sorumlunun adı kendi renginde, yanında görev sayısı.</summary>
    private static FrameworkElement TaskGroupHeader(AssigneeGroup<PlanStep> group, IReadOnlyList<string> order)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
        var name = new TextBlock
        {
            Text = group.Assignee is { } a ? "👤 " + a : PlanAssignees.UnassignedLabel,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
        };
        SetAssigneeColor(name, TextBlock.ForegroundProperty, order, group.Assignee);
        var count = new TextBlock
        {
            Text = $" · {group.Steps.Count} görev",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(name);
        panel.Children.Add(count);
        return panel;
    }

    /// <summary>Bir görev satırı: durum ikonu, iş metni; altında tıklanabilir sorumlu rozeti ve (varsa) son tarih.</summary>
    private FrameworkElement TaskRow(PlanStep step, IReadOnlyList<string> order)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = StepStatusIcon(step.Status),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(icon, 0);

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        var text = new TextBlock { Text = step.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        body.Children.Add(text);

        var extras = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        extras.Children.Add(AssigneeChip(step, order));
        if (step.DueText is { } due)
        {
            var dueText = new TextBlock
            {
                Text = "⏰ " + due,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Toplantıda söylenen son tarih",
            };
            dueText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            extras.Children.Add(dueText);
        }
        body.Children.Add(extras);

        grid.Children.Add(icon);
        grid.Children.Add(body);
        return grid;
    }

    /// <summary>"👤 Ahmet ▾" rozeti (sorumlu yoksa soluk "👤 sorumlu ata ▾"); tıklayınca sorumlu menüsü açılır.</summary>
    private FrameworkElement AssigneeChip(PlanStep step, IReadOnlyList<string> order)
    {
        var label = new TextBlock
        {
            Text = (step.Assignee is { } a ? "👤 " + a : "👤 sorumlu ata") + " ▾",
            FontSize = 11,
            FontWeight = step.Assignee is null ? FontWeights.Normal : FontWeights.SemiBold,
        };
        var chip = new Border
        {
            Child = label,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Sorumluyu değiştir",
        };
        SetAssigneeColor(label, TextBlock.ForegroundProperty, order, step.Assignee);
        SetAssigneeColor(chip, Border.BorderBrushProperty, order, step.Assignee);
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowAssigneeMenu(step, chip);
        };
        return chip;
    }

    /// <summary>Sorumlunun rengi: plandaki sırasına göre kişi renklerinden biri; sorumlusuzsa soluk.</summary>
    private static void SetAssigneeColor(FrameworkElement element, DependencyProperty property, IReadOnlyList<string> order, string? assignee)
    {
        var slot = PlanAssignees.ColorSlot(order, assignee);
        element.SetResourceReference(property, slot > 0 ? Theme.PersonBrushKey(slot) : "Muted");
    }

    /// <summary>Sorumlu menüsü: plandaki sorumlular, "Ben" ve kaydın konuşmacıları arasından seçim; "Başka ad
    /// yaz…" (satır içinde) ve "Sorumluyu kaldır".</summary>
    private void ShowAssigneeMenu(PlanStep step, FrameworkElement anchor)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(PlanAssignees.NameComparer);
        void Add(string? name)
        {
            if (PlanAssignees.Normalize(name) is { } clean && seen.Add(clean)) candidates.Add(clean);
        }

        foreach (var name in PlanAssignees.Order(_taskSteps)) Add(name);
        Add(NoteAuthor.Ben);
        try
        {
            if (_store is not null && _current is { } r)
            {
                foreach (var person in _store.GetPersons(r.Id)) Add(person.Name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var name in candidates)
        {
            var item = new MenuItem
            {
                Header = MenuHeader(name == NoteAuthor.Ben ? "Ben (kendim)" : name),
                IsChecked = PlanAssignees.NameComparer.Equals(name, step.Assignee ?? ""),
            };
            var picked = name;
            item.Click += (_, _) => SetStepAssignee(step, picked);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = MenuHeader("✏️ Başka ad yaz…") };
        custom.Click += (_, _) => StartInlineAssigneeEdit(step, anchor);
        menu.Items.Add(custom);
        var clear = new MenuItem { Header = MenuHeader("✕ Sorumluyu kaldır"), IsEnabled = step.Assignee is not null };
        clear.Click += (_, _) => SetStepAssignee(step, null);
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    /// <summary>Rozetin yerine bir metin kutusu koyar: Enter ya da başka yere tıklamak kaydeder (boş = sorumluyu
    /// kaldırır).</summary>
    private void StartInlineAssigneeEdit(PlanStep step, FrameworkElement anchor)
    {
        if (anchor.Parent is not Panel panel) return;
        var index = panel.Children.IndexOf(anchor);
        if (index < 0) return;

        var box = new TextBox
        {
            Text = step.Assignee ?? "",
            FontSize = 11,
            MinWidth = 110,
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Sorumlu (kendiniz için \"Ben\"; boş = sorumluyu kaldır). Enter ile kaydedilir.",
        };
        box.SetResourceReference(BackgroundProperty, "Surface");
        box.SetResourceReference(ForegroundProperty, "Fg");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        box.SetResourceReference(BorderBrushProperty, "Accent");

        var done = false;
        void Commit()
        {
            if (done) return;
            done = true;
            SetStepAssignee(step, box.Text);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };
        box.LostKeyboardFocus += (_, _) => Commit();
        // Esc: kaydetmeden vazgeç, rozeti geri koy. Pencere düzeyindeki Esc (paneli gizleme) bundan önce çalıştığı
        // için iptal, OnAnyWindowPreviewKeyDown'da Tag'deki bu eylemle yapılır.
        box.Tag = new Action(() =>
        {
            if (done) return;
            done = true;
            var at = panel.Children.IndexOf(box);
            if (at < 0) return;
            panel.Children.RemoveAt(at);
            panel.Children.Insert(at, anchor);
        });

        panel.Children.RemoveAt(index);
        panel.Children.Insert(index, box);
        box.Focus();
        box.SelectAll();
    }

    /// <summary>Adımın sorumlusunu kaydeder (son tarih korunur) ve Görevler sekmesini yeniler.</summary>
    private void SetStepAssignee(PlanStep step, string? assignee)
    {
        if (_planStore is null) return;
        try
        {
            if (PlanAssignees.Normalize(assignee) != step.Assignee)
                _planStore.SetStepAssignee(step.Id, assignee, step.DueText);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
        }

        // Odak kutudan çıkmadan (Enter) yeniden kurulabilsin diye imza sıfırlanır ve odak listeden alınır.
        _tasksSignature = "";
        if (TasksList.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        if (_current is { } r)
        {
            RenderTasks(r);
            UpdateTabs();
        }
    }

    /// <summary>📋 Kime ne verildi: görevleri sorumluya göre özetleyip panoya kopyalar.</summary>
    private void CopyAssignmentsButton_Click(object sender, RoutedEventArgs e)
    {
        var summary = PlanAssignees.BuildSummary(_taskSteps);
        if (summary.Length == 0) return;
        try
        {
            Clipboard.SetText(summary);
            ShowMessage("Kime ne verildi panoya kopyalandı: " + summary);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
        }
    }

    private static string StepStatusIcon(StepStatus status) => status switch
    {
        StepStatus.Yapiliyor => "🔄",
        StepStatus.Bitti => "✅",
        StepStatus.Hata => "❌",
        StepStatus.Atlandi => "⏭",
        _ => "⏳",
    };
}
