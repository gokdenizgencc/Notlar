using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notlar.App;

/// <summary>Görünüm ayarları düzenleyicisi: gün penceresi yazı boyutu ve yazar renkleri. Değerleri yalnızca
/// <see cref="ApplyTo"/> çağrıldığında ayarlara yazar; ne zaman kaydedileceğine barındıran taraf karar verir
/// (AppearanceWindow "Uygula" ile, ⚙ Ayarlar görünümü <see cref="Edited"/> üzerine anında).</summary>
public partial class AppearanceSettings : UserControl
{
    private static readonly string[] KnownAuthors = { "Ben", "Claude", "ChatGPT", "Gemini" };

    private static readonly string[] Presets =
    {
        "#D97757", "#10A37F", "#4285F4", "#8E24AA", "#E91E8C", "#546E7A", "#F4511E", "#43A047",
    };

    private static readonly Regex HexPattern = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private readonly Dictionary<string, TextBox> _hexBoxes = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    /// <summary>Kullanıcı bir değeri değiştirdiğinde (kaydırıcı, renk kutusu, hazır renk, varsayılana dön)
    /// tetiklenir; <see cref="Load"/> sırasında tetiklenmez.</summary>
    public event Action? Edited;

    public AppearanceSettings()
    {
        InitializeComponent();
        FontSizeSlider.Minimum = AppSettings.MinDayFontSize;
        FontSizeSlider.Maximum = AppSettings.MaxDayFontSize;
    }

    /// <summary>Yazı boyutu başlığı (ör. panelde "Gün penceresi yazı boyutu").</summary>
    public string FontSizeTitle
    {
        get => FontSizeCaption.Text;
        set => FontSizeCaption.Text = value;
    }

    /// <summary>Seçili yazı boyutu (tam sayıya yuvarlanmış).</summary>
    public double SelectedFontSize => Math.Round(FontSizeSlider.Value);

    /// <summary>Denetimi verilen ayarların değerleriyle doldurur (<see cref="Edited"/> tetiklenmez).</summary>
    public void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            FontSizeSlider.Value = settings.DayFontSize;
            UpdatePreview();

            _hexBoxes.Clear();
            AuthorColorsPanel.Children.Clear();
            foreach (var author in KnownAuthors)
            {
                settings.AuthorColors.TryGetValue(author, out var hex);
                AuthorColorsPanel.Children.Add(BuildAuthorRow(author, hex ?? ""));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Geçersiz renk kodu girilmiş ilk yazar; hepsi geçerliyse null. Boş kod geçerlidir (tema rengi).</summary>
    public string? FirstInvalidAuthor()
    {
        foreach (var kv in _hexBoxes)
        {
            if (!IsValidHex(kv.Value.Text.Trim()))
                return kv.Key;
        }
        return null;
    }

    /// <summary>Denetimdeki değerleri <paramref name="settings"/> üzerine yazar (kaydetmez). Geçersiz renk
    /// kodları atlanır; önce <see cref="FirstInvalidAuthor"/> ile doğrulanmalıdır.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.DayFontSize = SelectedFontSize;
        foreach (var kv in _hexBoxes)
        {
            var text = kv.Value.Text.Trim();
            if (IsValidHex(text))
                settings.AuthorColors[kv.Key] = text;
        }
    }

    /// <summary>Yazı boyutunu ve yazar renklerini varsayılana döndürür (kaydetmez).</summary>
    public void ResetToDefaults()
    {
        FontSizeSlider.Value = AppSettings.DefaultDayFontSize;
        var defaults = AppSettings.CreateDefaultAuthorColors();
        foreach (var author in KnownAuthors)
        {
            var hex = defaults.TryGetValue(author, out var v) ? v : "";
            if (_hexBoxes.TryGetValue(author, out var box))
                box.Text = hex;
        }
    }

    private static bool IsValidHex(string text) => text.Length == 0 || HexPattern.IsMatch(text);

    private UIElement BuildAuthorRow(string author, string currentHex)
    {
        // Renkler SetResourceReference ile bağlanır: panel uzun yaşar, Windows teması değişince güncellenmeli.
        var outer = new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        outer.SetResourceReference(Border.BackgroundProperty, "Surface");
        outer.SetResourceReference(Border.BorderBrushProperty, "Border");

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
            swatchRow.Children.Add(BuildSwatch(author, "", isThemeDefault: true));

        foreach (var hex in Presets)
            swatchRow.Children.Add(BuildSwatch(author, hex, isThemeDefault: false));

        stack.Children.Add(swatchRow);

        var hexBox = new TextBox
        {
            Text = currentHex,
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 3, 6, 3),
            ToolTip = "Renk kodu, ör. #D97757 (boş: tema rengi)",
        };
        hexBox.SetResourceReference(Control.BackgroundProperty, "Bg");
        hexBox.SetResourceReference(Control.ForegroundProperty, "Fg");
        hexBox.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        hexBox.SetResourceReference(Control.BorderBrushProperty, "Border");
        System.Windows.Automation.AutomationProperties.SetName(hexBox, $"{author} renk kodu");
        hexBox.TextChanged += (_, _) =>
        {
            var valid = IsValidHex(hexBox.Text.Trim());
            hexBox.SetResourceReference(Control.BorderBrushProperty, valid ? "Border" : "Danger");
            if (!_loading) Edited?.Invoke();
        };
        _hexBoxes[author] = hexBox;
        stack.Children.Add(hexBox);

        return outer;
    }

    private UIElement BuildSwatch(string author, string hex, bool isThemeDefault)
    {
        var ellipse = new Ellipse
        {
            Width = 22,
            Height = 22,
            StrokeThickness = isThemeDefault ? 1 : 0,
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            ToolTip = isThemeDefault ? "Tema rengi (varsayılan)" : hex,
        };
        if (isThemeDefault) ellipse.SetResourceReference(Shape.FillProperty, "Fg");
        else ellipse.Fill = ParseColor(hex);
        ellipse.SetResourceReference(Shape.StrokeProperty, "Border");
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
        if (!_loading && IsInitialized) Edited?.Invoke();
    }

    private void UpdatePreview()
    {
        if (FontSizeValueText is null || FontSizePreviewText is null) return;
        var size = SelectedFontSize;
        FontSizeValueText.Text = size.ToString("0");
        FontSizePreviewText.FontSize = size;
    }
}
