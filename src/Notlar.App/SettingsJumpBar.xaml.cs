using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notlar.App;

/// <summary>⚙ Ayarlar sayfasının en üstündeki bölüm atlama çubuğu. Çipler sayfadaki bölüm başlıklarını
/// (SectionTitle stilli TextBlock'lar; bölüm denetimlerinin içindekiler dahil) metinlerine göre bulur ve tıklanınca
/// başlığı kaydırma alanının en üstüne getirir. Başlığı sayfada olmayan bölümün çipi gizlenir.</summary>
public partial class SettingsJumpBar : UserControl
{
    private static readonly string[] Sections =
        ["Görünüm", "Genel", "Kısayollar", "Bildirimler", "Kayıt", "Gizlilik", "Veri", "Hakkında"];

    private readonly Dictionary<string, Button> _chips = new(StringComparer.Ordinal);

    public SettingsJumpBar()
    {
        InitializeComponent();
        foreach (var section in Sections)
        {
            var chip = new Button { Content = section, Tag = section, ToolTip = $"{section} bölümüne git" };
            chip.SetResourceReference(StyleProperty, "JumpChip");
            chip.Click += Chip_Click;
            _chips[section] = chip;
            ChipsPanel.Children.Add(chip);
        }
        // Bölüm denetimleri (diğer UserControl'ler) yüklenip içerikleri kurulduktan sonra çipler eşitlenir.
        Loaded += (_, _) => Dispatcher.InvokeAsync(RefreshChips, System.Windows.Threading.DispatcherPriority.Loaded);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RefreshChips();
        };
    }

    private void RefreshChips()
    {
        foreach (var (section, chip) in _chips)
            chip.Visibility = FindSectionTitle(section) is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;
        if (FindSectionTitle(section) is not { } title) return;

        try
        {
            if (FindAncestor<ScrollViewer>(title) is { } scroller && title.IsVisible)
            {
                var y = title.TranslatePoint(new Point(0, 0), scroller).Y;
                scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + y - 8));
            }
            else
            {
                title.BringIntoView();
            }
        }
        catch (InvalidOperationException)
        {
            // Ortak görsel ata yoksa (ağaç henüz kurulmamış) en azından görünür yap.
            title.BringIntoView();
        }
    }

    /// <summary>Bu çubuğun bulunduğu sayfada (üst panelin mantıksal alt ağacında) <paramref name="section"/> adlı bölüm
    /// başlığını arar: önce tam eşleşen, yoksa "Veri ve yedek" gibi bu adla başlayan başlık.</summary>
    private TextBlock? FindSectionTitle(string section)
    {
        if (Parent is not DependencyObject root) return null;
        var sectionStyle = TryFindResource("SectionTitle") as Style;

        TextBlock? prefixMatch = null;
        foreach (var block in EnumerateTextBlocks(root))
        {
            if (!IsSectionTitle(block, sectionStyle)) continue;
            var text = block.Text?.Trim() ?? "";
            if (string.Equals(text, section, StringComparison.CurrentCultureIgnoreCase)) return block;
            if (prefixMatch is null && text.StartsWith(section + " ", StringComparison.CurrentCultureIgnoreCase))
                prefixMatch = block;
        }
        return prefixMatch;
    }

    /// <summary>SectionTitle stilini (ya da ondan türeyen bir stili) kullanan TextBlock.</summary>
    private static bool IsSectionTitle(TextBlock block, Style? sectionStyle)
    {
        if (sectionStyle is null) return false;
        for (var style = block.Style; style is not null; style = style.BasedOn)
        {
            if (ReferenceEquals(style, sectionStyle)) return true;
        }
        return false;
    }

    /// <summary>Mantıksal ağaçta belge sırasıyla TextBlock'ları dolaşır (gizli/şablonu kurulmamış bölümler dahil).</summary>
    private IEnumerable<TextBlock> EnumerateTextBlocks(DependencyObject node)
    {
        if (ReferenceEquals(node, this)) yield break;
        if (node is TextBlock block) yield return block;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is not DependencyObject d) continue;
            foreach (var found in EnumerateTextBlocks(d))
                yield return found;
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match) return match;
        }
        return null;
    }
}
