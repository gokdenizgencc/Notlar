using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notlar.App;

/// <summary>"⚙ Görünüm" dialogu: yazı boyutu ve yazar renkleri ayarlarını düzenler.</summary>
public partial class AppearanceWindow : Window
{
    private static readonly string[] KnownAuthors = { "Ben", "Claude", "ChatGPT", "Gemini" };

    private static readonly string[] Presets =
    {
        "#D97757", "#10A37F", "#4285F4", "#8E24AA", "#E91E8C", "#546E7A", "#F4511E", "#43A047",
    };

    private static readonly Regex HexPattern = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly Dictionary<string, TextBox> _hexBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _workingColors;

    public AppearanceWindow(AppSettings settings)
    {
        _settings = settings;
        _workingColors = new Dictionary<string, string>(settings.AuthorColors, StringComparer.OrdinalIgnoreCase);
        InitializeComponent();

        FontSizeSlider.Value = settings.DayFontSize;
        UpdatePreview();

        foreach (var author in KnownAuthors)
            AuthorColorsPanel.Children.Add(BuildAuthorRow(author));
    }

    private UIElement BuildAuthorRow(string author)
    {
        _workingColors.TryGetValue(author, out var currentHex);
        currentHex ??= "";

        var outer = new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };

        var stack = new StackPanel();
        outer.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = author,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var swatchRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };

        if (author.Equals("Ben", StringComparison.OrdinalIgnoreCase))
        {
            swatchRow.Children.Add(BuildSwatch(author, "", isThemeDefault: true));
        }

        foreach (var hex in Presets)
            swatchRow.Children.Add(BuildSwatch(author, hex, isThemeDefault: false));

        stack.Children.Add(swatchRow);

        var hexBox = new TextBox
        {
            Text = currentHex,
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)FindResource("Bg"),
            Foreground = (Brush)FindResource("Fg"),
            BorderBrush = (Brush)FindResource("Border"),
            Padding = new Thickness(6, 3, 6, 3),
        };
        hexBox.TextChanged += (_, _) =>
        {
            var text = hexBox.Text.Trim();
            if (text.Length == 0 || HexPattern.IsMatch(text))
            {
                hexBox.BorderBrush = (Brush)FindResource("Border");
                _workingColors[author] = text;
            }
            else
            {
                hexBox.BorderBrush = (Brush)FindResource("Danger");
            }
        };
        _hexBoxes[author] = hexBox;
        stack.Children.Add(hexBox);

        return outer;
    }

    private UIElement BuildSwatch(string author, string hex, bool isThemeDefault)
    {
        var fill = isThemeDefault ? (Brush)FindResource("Fg") : ParseColor(hex);

        var ellipse = new Ellipse
        {
            Width = 22,
            Height = 22,
            Fill = fill,
            Stroke = (Brush)FindResource("Border"),
            StrokeThickness = isThemeDefault ? 1 : 0,
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            ToolTip = isThemeDefault ? "Tema rengi (varsayılan)" : hex,
        };
        ellipse.MouseLeftButtonDown += (_, _) =>
        {
            if (_hexBoxes.TryGetValue(author, out var box))
                box.Text = hex;
        };
        return ellipse;
    }

    private static Brush ParseColor(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (FontSizeValueText is null || FontSizePreviewText is null) return;
        var size = Math.Round(FontSizeSlider.Value);
        FontSizeValueText.Text = size.ToString("0");
        FontSizePreviewText.FontSize = size;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        FontSizeSlider.Value = AppSettings.DefaultDayFontSize;
        var defaults = AppSettings.CreateDefaultAuthorColors();
        foreach (var author in KnownAuthors)
        {
            var hex = defaults.TryGetValue(author, out var v) ? v : "";
            if (_hexBoxes.TryGetValue(author, out var box))
                box.Text = hex;
            _workingColors[author] = hex;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var kv in _hexBoxes)
        {
            var text = kv.Value.Text.Trim();
            if (text.Length > 0 && !HexPattern.IsMatch(text))
            {
                MessageBox.Show(this, $"{kv.Key} için geçersiz renk kodu. Örnek: #D97757", "Notlar",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _settings.DayFontSize = Math.Round(FontSizeSlider.Value);
        foreach (var kv in _hexBoxes)
            _settings.AuthorColors[kv.Key] = kv.Value.Text.Trim();

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(this, $"Ayarlar kaydedilemedi: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
