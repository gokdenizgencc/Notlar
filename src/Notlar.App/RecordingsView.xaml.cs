using System.Windows.Controls;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Panel içindeki Kayıtlar görünümü (SÖZLEŞME — imzalar sabit; gövdeler doldurulacak).</summary>
public partial class RecordingsView : UserControl
{
    public RecordingsView() => InitializeComponent();

    /// <summary>Bir kez, panel kurulurken çağrılır.</summary>
    public void Initialize(RecordingController controller, PlanStore planStore, ProjectStore projectStore) { }

    /// <summary>Veri dışarıdan değiştiğinde (değişiklik sinyali) çağrılır; seçim korunur.</summary>
    public void Reload() { }

    /// <summary>İlgili kaydı seçer ve detayını gösterir (dar modda detay görünümüne geçer).</summary>
    public void Select(long id) { }

    /// <summary>true: büyük mod (liste solda, detay sağda); false: dar panel (liste → detay, ‹ geri).</summary>
    public bool Wide { get; set; }
}
