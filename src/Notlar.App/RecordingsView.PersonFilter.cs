using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Shapes;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Transkript sekmesinin üstündeki kişi çipleri: kayıttaki her kişi kendi renginde, adı (takma adsa "~" ve
/// italik) ve konuşma satırı sayısıyla. Çipe tıklamak o kişiyi filtreye ekler/çıkarır (birden çok kişi seçilebilir),
/// "Tümü" filtreyi kaldırır; sağ tık → "Adını değiştir…". Filtre kayıt değişince ve yeniden çeviride sıfırlanır,
/// kişi yeniden adlandırılınca korunur. Süzme/sayım mantığı: <see cref="TranscriptPersonFilter"/>.</summary>
public partial class RecordingsView
{
    /// <summary>Dar panelde daraltılmadan gösterilen en fazla kişi çipi (≈ iki satır); büyük modda daha fazlası.</summary>
    private const int NarrowMaxChips = 6;
    private const int WideMaxChips = 12;

    /// <summary>Seçili kaydın tüm segmentleri (filtre uygulanmamış).</summary>
    private IReadOnlyList<TranscriptSegment> _segments = [];

    /// <summary>Seçili kayıttaki kişiler (satır sayılarıyla).</summary>
    private IReadOnlyList<TranscriptPersonCount> _persons = [];

    /// <summary>Filtredeki kişiler; boşsa filtre kapalı (tüm satırlar).</summary>
    private IReadOnlyList<int> _personFilter = [];

    /// <summary>Çok kişili kayıtta "+N" ile tüm çipler açıldı mı?</summary>
    private bool _chipsExpanded;

    private bool PersonFilterActive => TranscriptPersonFilter.IsActive(_personFilter);

    /// <summary>Detay yüklenirken: segmentleri ve kişileri alır; başka kayda geçildiyse filtre sıfırlanır, aynı
    /// kayıtta (ör. kişi yeniden adlandırıldı) korunur ve artık olmayan kişiler filtreden düşer.</summary>
    private void SetTranscriptSegments(IReadOnlyList<TranscriptSegment> segments, bool otherRecording)
    {
        if (otherRecording) ResetPersonFilter();
        _segments = segments;
        _persons = TranscriptPersonFilter.Count(segments);
        _personFilter = TranscriptPersonFilter.Prune(_personFilter, _persons);
    }

    private void ResetPersonFilter()
    {
        _personFilter = [];
        _chipsExpanded = false;
    }

    /// <summary>Transkripti (filtreye göre) ve kişi çiplerini çizer. Metin değişmediyse belge yeniden oluşturulmaz
    /// (kaydırma konumu ve seçim korunur).</summary>
    private void RenderTranscript(bool force)
    {
        var visible = TranscriptPersonFilter.Apply(_segments, _personFilter);
        var transcript = TranscriptFormatter.Format(visible);
        if (force || transcript != _transcriptText)
        {
            _transcriptText = transcript;
            TranscriptBox.Document = BuildTranscriptDocument(visible);
        }
        BuildPersonChips();
    }

    private void SetPersonFilter(IReadOnlyList<int> filter)
    {
        _personFilter = filter;
        RenderTranscript(force: false);
        TranscriptBox.ScrollToHome();
        UpdatePersonFilterBar();
    }

    /// <summary>Çip satırı yalnızca Transkript sekmesinde ve kişi bilgisi olan transkriptte görünür.</summary>
    private void UpdatePersonFilterBar()
    {
        var show = _persons.Count > 0 && TranscriptBox.Visibility == Visibility.Visible;
        PersonFilterBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var info = TranscriptPersonFilter.Describe(_persons, _personFilter);
        PersonFilterText.Text = info ?? "";
        PersonFilterText.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildPersonChips()
    {
        PersonChips.Children.Clear();
        if (_persons.Count == 0)
        {
            UpdatePersonFilterBar();
            return;
        }

        var all = Chip(null, "Tümü", _segments.Count, italic: false, selected: !PersonFilterActive,
            "Filtreyi kaldır: tüm satırları göster");
        all.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (PersonFilterActive) SetPersonFilter([]);
        };
        PersonChips.Children.Add(all);

        var max = _wide ? WideMaxChips : NarrowMaxChips;
        var (visible, hidden) = TranscriptPersonFilter.Chips(_persons, _personFilter, max, _chipsExpanded);
        foreach (var p in visible)
        {
            var person = p.Person;
            var chip = Chip(person, p.Label, p.Lines, italic: p.IsGuess, selected: _personFilter.Contains(person),
                ChipToolTip(p));
            if (PersonFilterActive && !_personFilter.Contains(person)) chip.Opacity = 0.7;
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SetPersonFilter(TranscriptPersonFilter.Toggle(_personFilter, person));
            };
            var rename = new MenuItem { Header = MenuHeader($"“{p.Name}” adını değiştir…") };
            rename.Click += (_, _) => EditPersonName(person);
            chip.ContextMenu = new ContextMenu { Items = { rename } };
            PersonChips.Children.Add(chip);
        }

        // Çok kişili kayıt: "+N kişi" tüm çipleri açar, "Daralt" geri kapatır.
        if (hidden > 0 || (_chipsExpanded && _persons.Count > max + 1))
        {
            var more = Chip(null, hidden > 0 ? string.Create(CultureInfo.InvariantCulture, $"+{hidden} kişi") : "Daralt",
                null, italic: false, selected: false,
                hidden > 0 ? "Diğer kişileri de göster" : "Yalnızca en çok konuşan kişileri göster");
            more.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _chipsExpanded = hidden > 0;
                BuildPersonChips();
            };
            PersonChips.Children.Add(more);
        }

        UpdatePersonFilterBar();
    }

    private static string ChipToolTip(TranscriptPersonCount p)
    {
        var source = p.Source switch
        {
            PersonNameSource.Kullanici => "Bu adı sen verdin.",
            PersonNameSource.Hitap => "Ad, toplantıda bu kişiye bu adla seslenildiği için konuşmadan çıkarıldı (büyük olasılıkla doğru).",
            _ when p.Numbered => "Bu konuşmacının adı bilinmiyor.",
            _ => "~ Takma ad: sesin tonuna göre verildi, gerçek kişi değil.",
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{p.Label}: {p.Lines} satır. {source}\nTıkla: yalnızca bu kişinin satırları (birden çok kişi seçilebilir). Sağ tık: adını değiştir.");
    }

    /// <summary>Yuvarlak köşeli çip: kişiyse renkli nokta + ad + satır sayısı. Seçiliyse kişinin (Tümü'de Accent)
    /// rengiyle çerçeveli ve dolgulu.</summary>
    private static Border Chip(int? person, string text, int? count, bool italic, bool selected, string toolTip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (person is { } p)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, Theme.PersonBrushKey(p));
            row.Children.Add(dot);
        }

        var name = new TextBlock
        {
            Text = text,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (person is { } colored) name.SetResourceReference(TextBlock.ForegroundProperty, Theme.PersonBrushKey(colored));
        else name.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Fg" : "Muted");
        row.Children.Add(name);

        if (count is { } n)
        {
            var countText = new TextBlock
            {
                Text = n.ToString(CultureInfo.InvariantCulture),
                FontSize = 10.5,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            row.Children.Add(countText);
        }

        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            Padding = new Thickness(selected ? 7.5 : 8, selected ? 1.5 : 2, selected ? 7.5 : 8, selected ? 1.5 : 2),
            Margin = new Thickness(0, 0, 5, 4),
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Child = row,
        };
        TextElement.SetFontSize(chip, 11.5);
        chip.SetResourceReference(Border.BorderBrushProperty,
            !selected ? "Border" : person is { } c ? Theme.PersonBrushKey(c) : "Accent");
        chip.SetResourceReference(Border.BackgroundProperty, selected ? "TagBg" : "Surface");
        return chip;
    }
}
