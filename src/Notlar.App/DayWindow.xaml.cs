using System;
using System.Collections.Generic;
using System.Globalization;
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
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly NoteStore _store;
    private AppSettings _settings;
    private double _fontSize;

    public DateOnly Day { get; }

    public DayWindow(NoteStore store, DateOnly day)
    {
        _store = store;
        Day = day;
        _settings = AppSettings.Load();
        _fontSize = _settings.DayFontSize;
        InitializeComponent();
        Title = "Notlar — " + DayGrouper.FormatDate(day);
        Loaded += (_, _) => Reload();
        AppSettings.Changed += OnSettingsChanged;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        AppSettings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _settings = AppSettings.Load();
            _fontSize = _settings.DayFontSize;
            Body.FontSize = _fontSize;
            Reload();
        });
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

            BuildHeader(dayNotes);

            EmptyText.Visibility = dayNotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            Body.FontSize = _fontSize;

            NotesList.Children.Clear();
            var ordered = dayNotes.ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                NotesList.Children.Add(BuildRow(ordered[i], isFirst: i == 0));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(this, $"İşlem başarısız: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BuildHeader(IReadOnlyList<Note> dayNotes)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        HeaderText.Text = Day.ToString("d MMMM yyyy", Tr);

        var weekday = Day.ToString("dddd", Tr);
        string? relative = Day == today ? "Bugün" : Day == today.AddDays(-1) ? "Dün" : Day == today.AddDays(1) ? "Yarın" : null;
        SubHeaderText.Text = relative is null ? weekday : $"{weekday} · {relative}";

        SummaryPillsPanel.Children.Clear();

        var totalText = dayNotes.Count == 1 ? "1 not" : $"{dayNotes.Count} not";
        SummaryPillsPanel.Children.Add(BuildPill(totalText, (Brush)FindResource("Fg"), (Brush)FindResource("Bg")));

        var doneCount = dayNotes.Count(n => n.Done);
        if (doneCount > 0)
            SummaryPillsPanel.Children.Add(BuildPill($"{doneCount} tamamlandı", (Brush)FindResource("Muted"), (Brush)FindResource("Bg")));

        var byAuthor = dayNotes
            .GroupBy(n => n.Author.DisplayName(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count());
        foreach (var group in byAuthor)
        {
            var authorName = group.Key;
            var color = AuthorColors.For(authorName, _settings) ?? (Brush)FindResource("Fg");
            var bg = Tint(color);
            SummaryPillsPanel.Children.Add(BuildPill($"{group.Count()} {authorName}", color, bg));
        }
    }

    private static Brush Tint(Brush brush)
    {
        if (brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            var tinted = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B));
            tinted.Freeze();
            return tinted;
        }
        return brush;
    }

    private UIElement BuildPill(string text, Brush foreground, Brush background)
    {
        var border = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0),
        };
        border.Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = foreground,
        };
        return border;
    }

    private UIElement BuildRow(Note note, bool isFirst)
    {
        var container = new Border
        {
            Padding = new Thickness(0, 14, 0, 14),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = isFirst ? new Thickness(0) : new Thickness(0, 1, 0, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var checkBox = new System.Windows.Controls.CheckBox
        {
            IsChecked = note.Done,
            Margin = new Thickness(0, 4, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        checkBox.Click += (_, _) => ToggleDone(note);
        Grid.SetColumn(checkBox, 0);
        grid.Children.Add(checkBox);

        var authorName = note.Author.DisplayName();
        var authorColor = AuthorColors.For(authorName, _settings) ?? (Brush)FindResource("Fg");

        var timePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        timePanel.Children.Add(new TextBlock
        {
            Text = note.CreatedAt.ToString("HH:mm"),
            Foreground = (Brush)FindResource("Muted"),
        });
        var authorBadge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Tint(authorColor),
        };
        authorBadge.Child = new TextBlock
        {
            Text = authorName,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = authorColor,
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
            Foreground = authorColor,
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
            var tagsPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
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
        container.Child = grid;
        return container;
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

    private void AppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new AppearanceWindow(_settings) { Owner = this };
        var result = window.ShowDialog();
        if (result == true)
        {
            _settings = AppSettings.Load();
            _fontSize = _settings.DayFontSize;
            Reload();
        }
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
                SetFontSize(AppSettings.DefaultDayFontSize);
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
        var clamped = Math.Clamp(size, AppSettings.MinDayFontSize, AppSettings.MaxDayFontSize);
        if (Math.Abs(clamped - _fontSize) < 0.01) return;

        _fontSize = clamped;
        Body.FontSize = _fontSize;

        try
        {
            _settings.DayFontSize = _fontSize;
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }
}
