using System.Windows.Controls;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Panel içindeki ⚙ Ayarlar görünümü (SÖZLEŞME — imzalar sabit; gövdeler doldurulacak).</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Bir kez, panel kurulurken çağrılır.</summary>
    public void Initialize(AppSettings settings, AppFlags flags) { }

    /// <summary>Görünüm her açıldığında / veri değiştiğinde güncel değerleri yükler.</summary>
    public void Reload() { }
}
