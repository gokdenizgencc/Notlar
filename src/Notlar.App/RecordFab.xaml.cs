using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Notlar.App;

/// <summary>Panelin sağ altındaki sabit kayıt düğmesi. Durumu <see cref="RecordingController"/>'dan okur, süreyi
/// görünürken saniyede bir günceller; tıklama yalnızca <see cref="ToggleRequested"/> ile bildirilir (başlat/durdur
/// kararını PanelWindow verir).</summary>
public partial class RecordFab : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private RecordingController? _controller;

    public RecordFab()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateState();
        IsVisibleChanged += (_, _) => UpdateState();
    }

    /// <summary>Düğmeye tıklandığında tetiklenir.</summary>
    public event Action? ToggleRequested;

    /// <summary>Bir kez, panel kurulurken çağrılır.</summary>
    public void Attach(RecordingController controller)
    {
        _controller = controller;
        controller.RecordingStateChanged += () => Dispatcher.InvokeAsync(UpdateState);
        UpdateState();
    }

    private void UpdateState()
    {
        var recording = _controller?.IsRecording == true;
        IconText.Text = recording ? "⏹" : "⏺";
        ElapsedText.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording && _controller is { } controller) ElapsedText.Text = FormatElapsed(controller.Elapsed);
        FabButton.ToolTip = recording ? "Kaydı durdur" : "Toplantı kaydını başlat";

        // Süre yalnızca kayıt sürerken ve düğme görünürken (panel açık, Kayıtlar sekmesi dışında) güncellenir.
        if (recording && IsVisible) _timer.Start();
        else _timer.Stop();
    }

    private void FabButton_Click(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke();

    /// <summary>"mm:ss"; bir saat ve üzerinde "h:mm:ss".</summary>
    private static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
}
