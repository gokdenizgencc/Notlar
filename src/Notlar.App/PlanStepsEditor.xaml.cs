using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Notlar.App;

/// <summary>
/// Plan adımlarını kutu kutu (her adım kendi giriş kutusunda) düzenlemeyi
/// sağlayan yeniden kullanılabilir kontrol.
/// </summary>
public partial class PlanStepsEditor : UserControl
{
    private sealed class StepRow
    {
        public required Border Root;
        public required TextBlock Number;
        public required TextBox Box;
        public required TextBlock Placeholder;
        public required Button Up;
        public required Button Down;
        public required Button Remove;
    }

    private const string PlaceholderText = "Adım yaz…";

    private readonly List<StepRow> _rows = new();
    private bool _suppressEvents;

    public event EventHandler? StepsChanged;

    public PlanStepsEditor()
    {
        InitializeComponent();
        AddRow(string.Empty, focus: false);
        RefreshNumbers();
    }

    /// <summary>Boş olmayan, kırpılmış adım metinleri, sıralı.</summary>
    public IReadOnlyList<string> Steps
    {
        get
        {
            return _rows
                .Select(r => r.Box.Text.Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }
    }

    public void SetSteps(IEnumerable<string> steps)
    {
        _suppressEvents = true;
        try
        {
            StepsPanel.Children.Clear();
            _rows.Clear();

            var list = steps?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            if (list.Count == 0)
            {
                AddRowInternal(string.Empty);
            }
            else
            {
                foreach (var s in list)
                {
                    AddRowInternal(s);
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        RefreshNumbers();
        RaiseStepsChanged();
    }

    public void Clear()
    {
        SetSteps(Array.Empty<string>());
    }

    private StepRow AddRow(string text, bool focus)
    {
        var row = AddRowInternal(text);
        RefreshNumbers();
        if (focus)
        {
            row.Box.Focus();
            row.Box.CaretIndex = row.Box.Text.Length;
        }
        RaiseStepsChanged();
        return row;
    }

    private StepRow AddRowInternal(string text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(0, 6, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 18,
        };
        Grid.SetColumn(number, 0);

        var box = new TextBox
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 28,
            Padding = new Thickness(4, 4, 4, 4),
            Margin = new Thickness(0, 2, 6, 2),
            Background = (Brush)FindResource("Surface"),
            Foreground = (Brush)FindResource("Fg"),
            CaretBrush = (Brush)FindResource("Fg"),
            BorderBrush = (Brush)FindResource("Border"),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += Box_TextChanged;
        box.PreviewKeyDown += Box_PreviewKeyDown;
        DataObject.AddPastingHandler(box, Box_Pasting);
        Grid.SetColumn(box, 1);

        var placeholder = new TextBlock
        {
            Text = PlaceholderText,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(8, 6, 6, 6),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(placeholder, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var up = new Button { Content = "↑", ToolTip = "Yukarı taşı", Style = (Style)FindResource("PlanFlatButton") };
        var down = new Button { Content = "↓", ToolTip = "Aşağı taşı", Style = (Style)FindResource("PlanFlatButton") };
        var remove = new Button { Content = "✕", ToolTip = "Adımı sil", Style = (Style)FindResource("PlanFlatButton") };
        up.Click += (_, _) => MoveRow(RowOf(up), -1);
        down.Click += (_, _) => MoveRow(RowOf(down), 1);
        remove.Click += (_, _) => RemoveRow(RowOf(remove));
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        buttons.Children.Add(remove);
        Grid.SetColumn(buttons, 2);

        grid.Children.Add(number);
        grid.Children.Add(box);
        grid.Children.Add(placeholder);
        grid.Children.Add(buttons);

        var root = new Border { Child = grid, Padding = new Thickness(0, 2, 0, 2) };

        var row = new StepRow { Root = root, Number = number, Box = box, Placeholder = placeholder, Up = up, Down = down, Remove = remove };
        _rows.Add(row);
        StepsPanel.Children.Add(root);
        return row;
    }

    private StepRow RowOf(Button button) => _rows.First(r => r.Up == button || r.Down == button || r.Remove == button);

    private int IndexOf(StepRow row) => _rows.IndexOf(row);

    private void MoveRow(StepRow row, int delta)
    {
        var index = IndexOf(row);
        var target = index + delta;
        if (target < 0 || target >= _rows.Count)
        {
            return;
        }

        _rows.RemoveAt(index);
        _rows.Insert(target, row);
        StepsPanel.Children.RemoveAt(index);
        StepsPanel.Children.Insert(target, row.Root);

        RefreshNumbers();
        row.Box.Focus();
        row.Box.CaretIndex = row.Box.Text.Length;
        RaiseStepsChanged();
    }

    private void RemoveRow(StepRow row)
    {
        var index = IndexOf(row);
        if (_rows.Count == 1)
        {
            // Son kalan kutuyu silmek yerine temizle.
            row.Box.Clear();
            row.Box.Focus();
            return;
        }

        _rows.RemoveAt(index);
        StepsPanel.Children.RemoveAt(index);

        RefreshNumbers();
        var focusIndex = Math.Min(index, _rows.Count - 1);
        var focusRow = _rows[focusIndex];
        focusRow.Box.Focus();
        focusRow.Box.CaretIndex = focusRow.Box.Text.Length;
        RaiseStepsChanged();
    }

    private void RefreshNumbers()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Number.Text = $"{i + 1}.";
            _rows[i].Up.IsEnabled = i > 0;
            _rows[i].Down.IsEnabled = i < _rows.Count - 1;
        }
    }

    private void AddStepButton_Click(object sender, RoutedEventArgs e)
    {
        AddRow(string.Empty, focus: true);
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        var row = _rows.FirstOrDefault(r => r.Box == box);
        if (row != null)
        {
            row.Placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_suppressEvents)
        {
            RaiseStepsChanged();
        }
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var row = _rows.First(r => r.Box == box);

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl && e.Key == Key.Up)
        {
            e.Handled = true;
            MoveRow(row, -1);
            return;
        }

        if (ctrl && e.Key == Key.Down)
        {
            e.Handled = true;
            MoveRow(row, 1);
            return;
        }

        if (e.Key == Key.Enter && !shift)
        {
            e.Handled = true;
            var index = IndexOf(row);
            var newRow = InsertRowAfter(index, string.Empty);
            newRow.Box.Focus();
            RaiseStepsChanged();
            return;
        }

        if (e.Key == Key.Back && box.CaretIndex == 0 && box.SelectionLength == 0 && box.Text.Length == 0 && _rows.Count > 1)
        {
            e.Handled = true;
            RemoveRow(row);
            return;
        }
    }

    private StepRow InsertRowAfter(int index, string text)
    {
        var row = AddRowInternal(text);
        var currentIndex = _rows.Count - 1;
        var target = index + 1;
        if (target != currentIndex)
        {
            _rows.RemoveAt(currentIndex);
            _rows.Insert(target, row);
            StepsPanel.Children.RemoveAt(currentIndex);
            StepsPanel.Children.Insert(target, row.Root);
        }

        RefreshNumbers();
        return row;
    }

    private void Box_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) && !e.DataObject.GetDataPresent(DataFormats.Text))
        {
            return;
        }

        var text = (string?)e.DataObject.GetData(DataFormats.UnicodeText) ?? (string?)e.DataObject.GetData(DataFormats.Text);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length <= 1)
        {
            return;
        }

        e.CancelCommand();

        var box = (TextBox)sender;
        var row = _rows.First(r => r.Box == box);
        var index = IndexOf(row);

        var caret = box.CaretIndex;
        var before = box.Text.Substring(0, caret);
        var after = box.Text.Substring(caret + box.SelectionLength);

        // İlk satır mevcut kutuya, kalanlar yeni kutulara dağılır.
        box.Text = before + lines[0];
        box.CaretIndex = box.Text.Length;

        var insertAt = index + 1;
        for (var i = 1; i < lines.Length; i++)
        {
            var lineText = i == lines.Length - 1 ? lines[i] + after : lines[i];
            var newRow = AddRowInternal(lineText);
            var currentIndex = _rows.Count - 1;
            _rows.RemoveAt(currentIndex);
            _rows.Insert(insertAt, newRow);
            StepsPanel.Children.RemoveAt(currentIndex);
            StepsPanel.Children.Insert(insertAt, newRow.Root);
            insertAt++;
        }

        RefreshNumbers();
        RaiseStepsChanged();
    }

    private void RaiseStepsChanged()
    {
        StepsChanged?.Invoke(this, EventArgs.Empty);
    }
}
