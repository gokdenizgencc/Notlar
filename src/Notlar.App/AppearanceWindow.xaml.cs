using System;
using System.Windows;
using System.Windows.Input;

namespace Notlar.App;

/// <summary>"⚙ Görünüm" dialogu (gün penceresinden açılır). Düzenleyici <see cref="AppearanceSettings"/> denetimidir
/// (panelin ⚙ Ayarlar görünümüyle ortak); her değişiklik anında kaydedilir ve uygulanır, bu yüzden "Uygula/Vazgeç"
/// yoktur. Kapanırken bekleyen gecikmeli kayıt yazılır.</summary>
public partial class AppearanceWindow : Window
{
    public AppearanceWindow(AppSettings settings)
    {
        InitializeComponent();
        Editor.Load(settings);
        Editor.SaveFailed += message =>
            MessageBox.Show(this, message, "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
        Closing += (_, _) => Editor.FlushPendingSave();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>Esc pencereyi kapatır (değişiklikler zaten kaydedilmiştir).</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseButton_Click(sender, e);
    }
}
