using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Günün notlarını büyük, yeniden boyutlandırılabilir bir pencerede gösterir (yalnızca görüntüleme + tamamlandı işareti).</summary>
public partial class DayWindow : Window
{
    private const double MinFontSize = 10;
    private const double MaxFontSize = 40;
    private const double DefaultFontSize = 16;

    private readonly NoteStore _store;
    private double _fontSize = DefaultFontSize;

    public DateOnly Day { get; }

    public DayWindow(NoteStore store, DateOnly day)
    {
        _store = store;
        Day = day;
        InitializeComponent();
        Title = "Notlar — " + DayGrouper.FormatDate(day);
        Reload();
    }

    /// <summary>Store'dan güncel notları okuyup görünümü tazeler. Pencere kapalıysa/yüklenmemişse güvenli şekilde hiçbir şey yapmaz.</summary>
    public void Reload()
    {
        if (!IsLoaded) return;

        try
        {
            var notes = _store.List(new NoteFilter(From: Day, To: Day));
            var groups = DayGrouper.Group(notes);
            var dayNotes = groups.FirstOrDefault(g => g.Date == Day)?.Notes ?? Array.Empty<Note>();

            var today = DateOnly.FromDateTime(DateTime.Now);
            HeaderText.Text = DayGrouper.Header(Day, today);
            CountText.Text = dayNotes.Count == 1 ? "1 not" : $"{dayNotes.Count} not";

            EmptyText.Visibility = dayNotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            NotesList.Children.Clear();
            foreach (var note in dayNotes)
                NotesList.Children.Add(BuildRow(note));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(this, $"İşlem başarısız: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UIElement BuildRow(Note note)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var checkBox = new System.Windows.Controls.CheckBox
        {
            IsChecked = note.Done,
            Margin = new Thickness(0, 4, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        checkBox.Click += (_, _) => ToggleDone(note);
        Grid.SetColumn(checkBox, 0);
        grid.Children.Add(checkBox);

        var timePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        timePanel.Children.Add(new TextBlock
        {
            Text = note.CreatedAt.ToString("HH:mm"),
            Foreground = (Brush)FindResource("Muted"),
        });
        var isClaude = note.Author == NoteAuthor.Claude;
        var authorBadge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = isClaude ? (Brush)FindResource("TagBg") : Brushes.Transparent,
        };
        authorBadge.Child = new TextBlock
        {
            Text = note.Author.DisplayName(),
            FontSize = 10,
            Foreground = isClaude ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"),
        };
        timePanel.Children.Add(authorBadge);
        Grid.SetColumn(timePanel, 1);
        grid.Children.Add(timePanel);

        var body = new StackPanel();
        Grid.SetColumn(body, 2);

        var textBox = new TextBox
        {
            Text = note.DisplayText,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("Fg"),
            TextWrapping = TextWrapping.Wrap,
            IsReadOnlyCaretVisible = true,
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        if (note.Done)
        {
            textBox.TextDecorations = TextDecorations.Strikethrough;
            textBox.Opacity = 0.5;
        }
        body.Children.Add(textBox);

        if (note.Tags.Count > 0)
        {
            var tagsPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var tag in note.Tags)
            {
                var border = new Border
                {
                    Background = (Brush)FindResource("TagBg"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 0, 6, 0),
                    Margin = new Thickness(0, 0, 4, 0),
                };
                border.Child = new TextBlock
                {
                    Text = tag,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Accent"),
                };
                tagsPanel.Children.Add(border);
            }
            body.Children.Add(tagsPanel);
        }

        grid.Children.Add(body);
        return grid;
    }

    private void ToggleDone(Note note)
    {
        try
        {
            _store.SetDone(note.Id, !note.Done);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(this, $"İşlem başarısız: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Reload();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key is Key.OemPlus or Key.Add)
            {
                SetFontSize(_fontSize + 1);
                e.Handled = true;
            }
            else if (e.Key is Key.OemMinus or Key.Subtract)
            {
                SetFontSize(_fontSize - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                SetFontSize(DefaultFontSize);
                e.Handled = true;
            }
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        SetFontSize(_fontSize + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    private void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
        Body.FontSize = _fontSize;
    }
}
