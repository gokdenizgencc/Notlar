using System;
using System.Windows;
using System.Windows.Input;

namespace Notlar.App;

/// <summary>"⚙ Görünüm" dialogu: yazı boyutu ve yazar renkleri ayarlarını düzenler. Düzenleyici
/// <see cref="AppearanceSettings"/> denetimidir (panelin ⚙ Ayarlar görünümüyle ortak); burada değişiklikler
/// yalnızca "Uygula" ile kaydedilir.</summary>
public partial class AppearanceWindow : Window
{
    private readonly AppSettings _settings;

    public AppearanceWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Editor.Load(settings);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => Editor.ResetToDefaults();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Esc, Vazgeç ile aynı şekilde kaydetmeden kapatır.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CancelButton_Click(sender, e);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.FirstInvalidAuthor() is { } invalid)
        {
            MessageBox.Show(this, $"{invalid} için geçersiz renk kodu. Örnek: #D97757", "Notlar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Editor.ApplyTo(_settings);

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
