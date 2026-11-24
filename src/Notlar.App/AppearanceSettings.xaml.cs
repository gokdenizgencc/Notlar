using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Görünüm ayarları düzenleyicisi: tema modu, vurgu rengi, yazı tipi, yazı boyutları, yoğunluk ve yazar
/// renkleri. Her değişiklik kendisi tarafından anında kaydedilir (<see cref="AppSettings.Update"/>; kaydırıcılar ve
/// renk kodları yazarken diske boğulmasın diye kısa bir gecikmeyle). Kayıt <see cref="AppSettings.Changed"/>'i
/// tetikler; App temayı yeniden uygular, açık pencereler ve önizleme kartı DynamicResource ile güncellenir.</summary>
public partial class AppearanceSettings : UserControl
{
    private static readonly string[] KnownAuthors = { "Ben", "Claude", "ChatGPT", "Gemini" };

    private static readonly string[] AuthorPresets =
    {
        "#D97757", "#10A37F", "#4285F4", "#8E24AA", "#E91E8C", "#546E7A", "#F4511E", "#43A047",
    };

    /// <summary>"Varsayılan" kutucuğunun gösterdiği renk (açık temadaki uygulama mavisi).</summary>
    private const string DefaultSwatchHex = "#0F6CBD";

    private static readonly Regex HexPattern = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private readonly Dictionary<string, TextBox> _hexBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RadioButton> _accentButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Renk seçimi radyo düğmeleri iki ayrı panelde durduğundan grup adıyla bağlanır; aynı denetim iki
    /// pencerede açıksa birbirini etkilemesin diye ad örneğe özeldir.</summary>
    private readonly string _accentGroup = "Accent_" + Guid.NewGuid().ToString("N");

    private bool _loading;
    private string _themeMode = AppearanceOptions.ThemeSystem;
    private string _accent = AppearanceOptions.AccentDefault;
    private string _fontFamily = "";
    private string _density = AppearanceOptions.DensityComfortable;

    /// <summary>Kaydetme başarısız olduğunda (mesaj). Barındıran taraf kullanıcıya gösterir.</summary>
    public event Action<string>? SaveFailed;

    public AppearanceSettings()
    {
        InitializeComponent();
        _loading = true;
        FontSizeSlider.Minimum = AppSettings.MinDayFontSize;
        FontSizeSlider.Maximum = AppSettings.MaxDayFontSize;
        FontSizeSlider.Value = AppSettings.DefaultDayFontSize;
        EditorFontSlider.Minimum = AppSettings.MinEditorFontSize;
        EditorFontSlider.Maximum = AppSettings.MaxEditorFontSize;
        EditorFontSlider.Value = AppSettings.DefaultEditorFontSize;
        _loading = false;

        BuildAccentChoices();
        BuildFontChoices();

        _saveTimer.Tick += (_, _) => SaveNow();
        // Pencere kapanırken / görünüm değişirken bekleyen kayıt kaybolmasın.
        Unloaded += (_, _) => FlushPendingSave();
    }

    /// <summary>Denetimi verilen ayarların değerleriyle doldurur (kaydetmez).</summary>
    public void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            _themeMode = settings.ThemeMode;
            _accent = settings.AccentColor;
            _fontFamily = settings.UiFontFamily;
            _density = settings.Density;

            ThemeSystem.IsChecked = _themeMode == AppearanceOptions.ThemeSystem;
            ThemeLight.IsChecked = _themeMode == AppearanceOptions.ThemeLight;
            ThemeDark.IsChecked = _themeMode == AppearanceOptions.ThemeDark;

            DensityComfortable.IsChecked = _density == AppearanceOptions.DensityComfortable;
            DensityCompact.IsChecked = _density == AppearanceOptions.DensityCompact;

            SyncAccent();
            SyncFont();

            FontSizeSlider.Value = settings.DayFontSize;
            EditorFontSlider.Value = settings.EditorFontSize;
            UpdateSizeTexts();

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

    /// <summary>Kullanıcı bir renk kodu yazıyor mu? (Bu sırada dışarıdan yeniden yükleme satırları silmesin.)</summary>
    public bool IsEditingText => Keyboard.FocusedElement is TextBox focused && IsAncestorOf(focused);

    /// <summary>Gecikmeli bekleyen bir kayıt varsa hemen yazar.</summary>
    public void FlushPendingSave()
    {
        if (_saveTimer.IsEnabled) SaveNow();
    }

    /// <summary>Denetimdeki değerleri <paramref name="settings"/> üzerine yazar (kaydetmez). Geçersiz renk kodları
    /// atlanır (kutunun kenarı kırmızı kalır).</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.ThemeMode = _themeMode;
        settings.AccentColor = _accent;
        settings.UiFontFamily = _fontFamily;
        settings.Density = _density;
        settings.DayFontSize = Math.Round(FontSizeSlider.Value);
        settings.EditorFontSize = Math.Round(EditorFontSlider.Value);
        foreach (var kv in _hexBoxes)
        {
            var text = kv.Value.Text.Trim();
            if (IsValidHex(text))
                settings.AuthorColors[kv.Key] = text;
        }
    }

    // ── Kaydetme ─────────────────────────────────────────────────────────────

    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        if (_loading) return;
        try
        {
            AppSettings.Update(ApplyTo);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            SaveFailed?.Invoke($"Görünüm ayarı kaydedilemedi: {ex.Message}");
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Yeni bir örnek tüm görünüm alanlarının varsayılanlarını taşır (tema: sistem, vurgu: Notlar mavisi, Segoe UI,
        // rahat, yazı boyutları, yazar renkleri). Görünümle ilgisiz ayarlara dokunulmaz (ApplyTo yalnız bunları yazar).
        Load(new AppSettings());
        SaveNow();
    }

    // ── Tema / yoğunluk ──────────────────────────────────────────────────────

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode }) return;
        _themeMode = mode;
        SaveNow();
    }

    private void Density_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string density }) return;
        _density = density;
        SaveNow();
    }

    // ── Vurgu rengi ──────────────────────────────────────────────────────────

    private void BuildAccentChoices()
    {
        var windows = Theme.TryGetWindowsAccent();
        var dot = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        if (windows is { } w) dot.Fill = Freeze(new SolidColorBrush(ToColor(w)));
        else dot.SetResourceReference(Shape.FillProperty, "Muted");
        dot.SetResourceReference(Shape.StrokeProperty, "Border");

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(dot);
        content.Children.Add(new TextBlock { Text = "Windows vurgu rengi", VerticalAlignment = VerticalAlignment.Center });

        var windowsButton = new RadioButton
        {
            Content = content,
            GroupName = _accentGroup,
            Tag = AppearanceOptions.AccentWindows,
            IsEnabled = windows is not null,
            ToolTip = windows is null
                ? "Windows vurgu rengi okunamadı."
                : "Windows'un Kişiselleştirme → Renkler ayarındaki vurgu rengi; değişince Notlar da güncellenir.",
        };
        windowsButton.SetResourceReference(StyleProperty, "ChoiceChip");
        windowsButton.Checked += Accent_Checked;
        System.Windows.Automation.AutomationProperties.SetName(windowsButton, "Windows vurgu rengi");
        _accentButtons[AppearanceOptions.AccentWindows] = windowsButton;
        WindowsAccentRow.Children.Add(windowsButton);

        SwatchPanel.Children.Add(BuildSwatch(AppearanceOptions.AccentDefault, "Varsayılan (Notlar mavisi)", DefaultSwatchHex));
        foreach (var (name, hex) in AppearanceOptions.Presets)
            SwatchPanel.Children.Add(BuildSwatch(hex, name, hex));
    }

    private RadioButton BuildSwatch(string value, string name, string displayHex)
    {
        var color = Rgb.Parse(displayHex);
        var check = ColorMath.ContrastRatio(Rgb.White, color) >= ColorMath.ContrastRatio(Rgb.Black, color)
            ? Brushes.White
            : Brushes.Black;
        var button = new RadioButton
        {
            GroupName = _accentGroup,
            Tag = value,
            Background = Freeze(new SolidColorBrush(ToColor(color))),
            Foreground = check,
            ToolTip = name,
        };
        button.SetResourceReference(StyleProperty, "AccentSwatch");
        System.Windows.Automation.AutomationProperties.SetName(button, $"Vurgu rengi: {name}");
        button.Checked += Accent_Checked;
        _accentButtons[value] = button;
        return button;
    }

    private void Accent_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string value }) return;
        UpdateAccentHint(value);
        if (_loading) return;
        _accent = value;
        SaveNow();
    }

    private void SyncAccent()
    {
        if (!_accentButtons.TryGetValue(_accent, out var selected))
        {
            // Listede olmayan bir #RRGGBB (elle düzenlenmiş dosya): hiçbir kutucuk seçili görünmez, değer korunur.
            foreach (var b in _accentButtons.Values) b.IsChecked = false;
            UpdateAccentHint(_accent);
            return;
        }
        selected.IsChecked = true;
        UpdateAccentHint(_accent);
    }

    private void UpdateAccentHint(string value)
    {
        string name;
        if (value == AppearanceOptions.AccentDefault) name = "Varsayılan (Notlar mavisi)";
        else if (value == AppearanceOptions.AccentWindows) name = "Windows vurgu rengi";
        else
        {
            name = value;
            foreach (var (presetName, hex) in AppearanceOptions.Presets)
            {
                if (string.Equals(hex, value, StringComparison.OrdinalIgnoreCase)) name = presetName;
            }
        }
        AccentHint.Text = $"Seçili: {name}. Yazılar okunur kalsın diye renk koyu temada açılır, açık temada koyulaşır.";
    }

    // ── Yazı tipi ────────────────────────────────────────────────────────────

    private void BuildFontChoices()
    {
        foreach (var (label, family) in Theme.AvailableFonts())
        {
            var isDefault = family == Theme.DefaultFontFamily;
            FontCombo.Items.Add(new ComboBoxItem
            {
                Content = isDefault ? $"{label} (varsayılan)" : label,
                Tag = isDefault ? "" : family,
                FontFamily = new FontFamily(family),
            });
        }
    }

    /// <summary>Açılır listedeki öğenin Tag'i: varsayılan (ya da yüklü olmayan) yazı tipi için "".</summary>
    private static string FontKey(string family)
    {
        var resolved = Theme.ResolveFontFamily(family);
        return resolved == Theme.DefaultFontFamily ? "" : resolved;
    }

    private void SyncFont()
    {
        var key = FontKey(_fontFamily);
        foreach (var item in FontCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } cbi && string.Equals(tag, key, StringComparison.OrdinalIgnoreCase))
            {
                FontCombo.SelectedItem = cbi;
                return;
            }
        }
        FontCombo.SelectedIndex = FontCombo.Items.Count > 0 ? 0 : -1;
    }

    private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontCombo.SelectedItem is not ComboBoxItem { Tag: string family }) return;
        if (string.Equals(family, FontKey(_fontFamily), StringComparison.OrdinalIgnoreCase)) return;
        _fontFamily = family;
        SaveNow();
    }

    // ── Yazı boyutları ───────────────────────────────────────────────────────

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSizeTexts();
        if (IsInitialized) ScheduleSave();
    }

    private void EditorFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSizeTexts();
        if (IsInitialized) ScheduleSave();
    }

    private void UpdateSizeTexts()
    {
        if (FontSizeValueText is null || EditorFontValueText is null || PreviewNoteText is null) return;
        var day = Math.Round(FontSizeSlider.Value);
        FontSizeValueText.Text = day.ToString("0");
        PreviewNoteText.FontSize = day;
        EditorFontValueText.Text = Math.Round(EditorFontSlider.Value).ToString("0");
    }

    // ── Yazar renkleri ───────────────────────────────────────────────────────

    private static bool IsValidHex(string text) => text.Length == 0 || HexPattern.IsMatch(text);

    private UIElement BuildAuthorRow(string author, string currentHex)
    {
        // Renkler SetResourceReference ile bağlanır: panel uzun yaşar, tema değişince güncellenmeli.
        var outer = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
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
            swatchRow.Children.Add(BuildAuthorSwatch(author, "", isThemeDefault: true));

        foreach (var hex in AuthorPresets)
            swatchRow.Children.Add(BuildAuthorSwatch(author, hex, isThemeDefault: false));

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
            ScheduleSave();
        };
        _hexBoxes[author] = hexBox;
        stack.Children.Add(hexBox);

        return outer;
    }

    private UIElement BuildAuthorSwatch(string author, string hex, bool isThemeDefault)
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
        else ellipse.Fill = Freeze(new SolidColorBrush(ToColor(Rgb.Parse(hex))));
        ellipse.SetResourceReference(Shape.StrokeProperty, "Border");
        ellipse.MouseLeftButtonDown += (_, _) =>
        {
            if (_hexBoxes.TryGetValue(author, out var box))
                box.Text = hex;
        };
        return ellipse;
    }

    // ── Yardımcılar ──────────────────────────────────────────────────────────

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
