using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Transkriptteki kişi etiketinin (Run.Tag) işareti: etiketin hangi kişiye ait olduğunu ve adın kaynağını taşır.</summary>
public sealed record PersonLabelTag(int Person, PersonNameSource Source, bool Numbered);

/// <summary>Transkriptteki kişi adlarının değiştirilmesi: ad etiketine tıklamak ya da sağ tıklayıp "Adını değiştir…"
/// demek, o kişiye gerçek adını yazma penceresini açar; ad kaydedilince o kayıttaki tüm satırları güncellenir.</summary>
public partial class RecordingsView
{
    /// <summary>Kişi etiketi: kişiye özel renkte, kalın. Adın kaynağı görünür: sesten tahmin edilen takma ad italik
    /// ve başında "~" (gerçek kişi değil); hitaptan çıkan ve kullanıcının verdiği adlar düz.</summary>
    private static Run PersonLabelRun(TranscriptSegment segment, int person)
    {
        var name = TranscriptFormatter.DisplayName(segment)!;
        var numbered = name == TranscriptFormatter.PersonLabel(person);
        var guessed = segment.PersonNameSource == PersonNameSource.Tahmin && !numbered;
        var label = new Run((guessed ? "~" : "") + name + ": ")
        {
            FontWeight = FontWeights.SemiBold,
            FontStyle = guessed ? FontStyles.Italic : FontStyles.Normal,
            Tag = new PersonLabelTag(person, segment.PersonNameSource, numbered),
        };
        label.SetResourceReference(TextElement.ForegroundProperty, Theme.PersonBrushKey(person));
        return label;
    }

    /// <summary>Etiketin üzerine gelince gösterilen açıklama.</summary>
    private static string LabelToolTip(PersonLabelTag tag) => tag.Source switch
    {
        PersonNameSource.Kullanici => "Bu adı sen verdin. Değiştirmek için tıkla.",
        PersonNameSource.Hitap => "Toplantıda bu kişiye bu adla seslenildiği için konuşmadan çıkarıldı (büyük olasılıkla doğru). Düzeltmek için tıkla.",
        _ when tag.Numbered => "Bu konuşmacının adı bilinmiyor. Ad vermek için tıkla.",
        _ => "~ Takma ad: sesin tonuna göre verildi, gerçek kişi değil. Gerçek adı yazmak için tıkla.",
    };

    /// <summary>Fare noktasının altındaki kişi etiketi; etiket değilse null.</summary>
    private PersonLabelTag? LabelTagAt(Point point)
    {
        var position = TranscriptBox.GetPositionFromPoint(point, snapToText: false);
        return position?.Parent is Run { Tag: PersonLabelTag tag } ? tag : null;
    }

    private int? PersonLabelAt(Point point) => LabelTagAt(point)?.Person;

    private void TranscriptBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var tag = e.LeftButton == MouseButtonState.Released ? LabelTagAt(e.GetPosition(TranscriptBox)) : null;
        TranscriptBox.Cursor = tag is not null ? Cursors.Hand : null;
        var tip = tag is null ? null : LabelToolTip(tag);
        if (!Equals(TranscriptBox.ToolTip, tip)) TranscriptBox.ToolTip = tip;
    }

    private void TranscriptBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Sürükleyip metin seçen kullanıcıyı engelleme: yalnızca seçimsiz tek tıklama.
        if (!TranscriptBox.Selection.IsEmpty) return;
        if (PersonLabelAt(e.GetPosition(TranscriptBox)) is not { } person) return;
        e.Handled = true;
        Dispatcher.BeginInvoke(() => EditPersonName(person));
    }

    private void TranscriptBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (PersonLabelAt(Mouse.GetPosition(TranscriptBox)) is not { } person) return;
        e.Handled = true;

        var name = CurrentPersonName(person);
        var item = new MenuItem { Header = $"“{name}” adını değiştir…" };
        item.Click += (_, _) => EditPersonName(person);
        var menu = new ContextMenu { PlacementTarget = TranscriptBox, Placement = PlacementMode.MousePoint };
        menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private string CurrentPersonName(int person)
    {
        try
        {
            if (_store is not null && _current is { } r
                && _store.GetPersons(r.Id).FirstOrDefault(p => p.Person == person) is { } info)
                return info.Name;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        return TranscriptFormatter.PersonLabel(person);
    }

    /// <summary>Kişinin adını sorar ve kaydeder (boş ad / "Tahmine dön": sesten tahmin edilen takma ada döner).</summary>
    private void EditPersonName(int person)
    {
        if (_store is null || _current is not { } r) return;

        RecordingPerson? info;
        try
        {
            info = _store.GetPersons(r.Id).FirstOrDefault(p => p.Person == person);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
            return;
        }

        var dialog = new PersonNameWindow(person, info) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _store.RenamePerson(r.Id, person, dialog.NewName);
        }
        catch (ArgumentException ex)
        {
            ShowMessage(ex.Message, isError: true);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            ShowMessage(Describe(ex), isError: true);
            return;
        }
        ShowDetail();
    }
}
