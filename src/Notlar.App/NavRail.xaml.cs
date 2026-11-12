using System;
using System.Windows;
using System.Windows.Controls;

namespace Notlar.App;

/// <summary>Panelin bölümleri (sol ikon şeridindeki sırayla; Ctrl+1…5 ilk beşine karşılık gelir).</summary>
public enum PanelSection { Notes, Memory, Plans, Recordings, Skills, Settings }

/// <summary>Panelin solundaki 46 px dikey ikon şeridi. Yalnızca gezinme isteğini bildirir; hangi bölümün
/// etkin olduğuna PanelWindow karar verir ve <see cref="SetActive"/> ile şeride yansıtır.</summary>
public partial class NavRail : UserControl
{
    public NavRail() => InitializeComponent();

    /// <summary>Bir şerit öğesine tıklandığında (etkin öğe dahil) tetiklenir.</summary>
    public event Action<PanelSection>? SectionRequested;

    /// <summary>Etkin öğeyi vurgular (arka plan + solda 3 px vurgu çizgisi); null hiçbirini vurgulamaz.</summary>
    public void SetActive(PanelSection? section)
    {
        foreach (var (button, value) in new (Button, PanelSection)[]
                 {
                     (NotesItem, PanelSection.Notes), (MemoryItem, PanelSection.Memory), (PlansItem, PanelSection.Plans),
                     (RecordingsItem, PanelSection.Recordings), (SkillsItem, PanelSection.Skills),
                     (SettingsItem, PanelSection.Settings),
                 })
        {
            button.Tag = section == value ? "on" : null;
        }
    }

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: PanelSection section })
            SectionRequested?.Invoke(section);
    }
}
