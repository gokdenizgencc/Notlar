using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Transkriptte bir konuşmacının adını değiştirme penceresi. Sesten tahmin edilen takma adın gerçek kişi
/// olmadığını söyler; kullanıcı gerçek adı yazar ya da takma ada döner. Sonuç <see cref="NewName"/>'dedir
/// (null: takma ada dön).</summary>
public partial class PersonNameWindow : Window
{
    private readonly string _current;
    private readonly bool _userNamed;

    public PersonNameWindow(int person, RecordingPerson? info)
    {
        InitializeComponent();
        _current = info?.Name ?? TranscriptFormatter.PersonLabel(person);
        _userNamed = info?.IsUserNamed == true;

        TitleText.Text = $"“{TextSafety.Flatten(_current)}” kim?";
        InfoText.Text = Describe(person, info);
        NameBox.Text = _userNamed ? _current : "";
        ResetButton.Visibility = _userNamed ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Kaydedilecek ad; null: sesten tahmin edilen takma ada dön.</summary>
    public string? NewName { get; private set; }

    private static string Describe(int person, RecordingPerson? info)
    {
        if (info is null || info.AutoName == TranscriptFormatter.PersonLabel(person))
            return "Bu konuşmacının adı bilinmiyor. Gerçek adını yazabilirsin.";

        var tone = info.MedianF0Hz is { } hz
            ? string.Create(CultureInfo.InvariantCulture, $"{SpeakerNaming.Describe(info.Gender)}, ses tonu ≈{hz:0} Hz")
            : SpeakerNaming.Describe(info.Gender);
        var auto = info.AddressName is { } addressed
            ? $"“{addressed}” adı, toplantıda bu kişiye böyle seslenildiği için konuşmadan çıkarıldı; büyük olasılıkla doğru ama kesin değil ({tone})."
            : $"“{info.AutoName}” uygulamanın sesin tonuna göre verdiği bir takma addır ({tone}); gerçek kişi değildir.";
        return info.IsUserNamed
            ? $"Bu adı sen verdin. Otomatik ad: {auto}"
            : $"{auto} Gerekirse doğru adını yazabilirsin.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string? clean;
        try
        {
            clean = SpeakerNaming.CleanUserName(NameBox.Text);
        }
        catch (ArgumentException ex)
        {
            HintText.Text = ex.Message;
            HintText.SetResourceReference(ForegroundProperty, "DangerText");
            return;
        }
        // Takma ad değiştirilmeden kaydedilirse bir şey değişmez; boş ad takma ada döner.
        if (!_userNamed && clean is null)
        {
            DialogResult = false;
            return;
        }
        NewName = clean;
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        NewName = null;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SaveButton_Click(sender, e);
            e.Handled = true;
        }
    }
}
