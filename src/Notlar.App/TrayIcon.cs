using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using Forms = System.Windows.Forms;

namespace Notlar.App;

public sealed class TrayIcon : IDisposable
{
    private const string NormalTooltip = "Notlar";
    private const string RecordingTooltip = "Notlar — kaydediliyor";

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _normalIcon;
    private readonly Icon _recordingIcon;
    private bool _syncingAutoStart;
    private Action? _balloonClick;

    /// <param name="isRecording">Kayıt sürüyor mu (menü açılırken öğe metni için).</param>
    /// <param name="recordingElapsed">Süren kaydın süresi.</param>
    /// <param name="onToggleRecording">"🎙 Kaydı başlat" / "⏹ Kaydı durdur".</param>
    /// <param name="onOpenRecordings">"Kayıtlar…" — Kayıtlar penceresini açar.</param>
    public TrayIcon(Action onToggle, Action onOpen, Action onExport, Action onExit,
        Func<bool> isRecording, Func<TimeSpan> recordingElapsed, Action onToggleRecording, Action onOpenRecordings)
    {
        _normalIcon = CreateIcon(recording: false);
        _recordingIcon = CreateIcon(recording: true);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Aç", null, (_, _) => onOpen());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var recordItem = new Forms.ToolStripMenuItem("🎙 Kaydı başlat", null, (_, _) => onToggleRecording());
        menu.Items.Add(recordItem);
        menu.Items.Add("Kayıtlar…", null, (_, _) => onOpenRecordings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Markdown olarak dışa aktar", null, (_, _) => onExport());
        var autoStart = new Forms.ToolStripMenuItem("Windows ile başlat") { CheckOnClick = true, Checked = AutoStart.IsEnabled() };
        autoStart.CheckedChanged += (_, _) =>
        {
            if (_syncingAutoStart) return;
            try
            {
                AutoStart.SetEnabled(autoStart.Checked);
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
                SyncAutoStartCheck(autoStart);
            }
        };
        menu.Items.Add(autoStart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => onExit());
        // Menü her açıldığında kutucuğu gerçek kayıt defteri durumuyla senkronize et
        // (ör. kayıt elle silinmiş/eklenmiş olabilir); SetEnabled tetiklenmesin.
        // Kayıt öğesinin metni de o anki duruma (ve süreye) göre güncellenir.
        menu.Opening += (_, _) =>
        {
            SyncAutoStartCheck(autoStart);
            recordItem.Text = isRecording()
                ? $"⏹ Kaydı durdur ({FormatElapsed(recordingElapsed())})"
                : "🎙 Kaydı başlat";
        };

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _normalIcon,
            Text = NormalTooltip,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) onToggle();
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var click = _balloonClick;
            _balloonClick = null;
            click?.Invoke();
        };
    }

    /// <summary>Kayıt sürerken kırmızı noktalı ikon ve "Notlar — kaydediliyor" ipucu gösterir.</summary>
    public void SetRecording(bool recording)
    {
        _notifyIcon.Icon = recording ? _recordingIcon : _normalIcon;
        _notifyIcon.Text = recording ? RecordingTooltip : NormalTooltip;
    }

    /// <summary>Tepsi balonu (bildirim) gösterir; <paramref name="onClick"/> balona tıklanınca çağrılır.</summary>
    public void ShowBalloon(string title, string text, Action? onClick, bool isError = false)
    {
        _balloonClick = onClick;
        _notifyIcon.ShowBalloonTip(5000, Truncate(title, 60), Truncate(string.IsNullOrWhiteSpace(text) ? " " : text, 250),
            isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    /// <summary>Windows balon başlığı/metni uzunluk sınırlıdır (63 / 255 karakter).</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");

    /// <summary>Kutucuğu kayıt defteri durumuyla günceller; CheckedChanged'in yeniden tetiklenmesini (SetEnabled çağrısını) engeller.</summary>
    private void SyncAutoStartCheck(Forms.ToolStripMenuItem autoStart)
    {
        _syncingAutoStart = true;
        try
        {
            autoStart.Checked = AutoStart.IsEnabled();
        }
        finally
        {
            _syncingAutoStart = false;
        }
    }

    /// <summary>"mm:ss"; bir saat ve üzerinde "h:mm:ss".</summary>
    private static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");

    /// <summary>Gömülü .ico yerine çalışma anında çizilen basit ikon (mavi daire, beyaz "N"); kayıt sürerken sağ
    /// altta beyaz çerçeveli kırmızı nokta eklenir.</summary>
    private static Icon CreateIcon(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var background = new SolidBrush(Color.FromArgb(0x0F, 0x6C, 0xBD));
            g.FillEllipse(background, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("N", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
            if (recording)
            {
                using var dot = new SolidBrush(Color.FromArgb(0xE8, 0x11, 0x23));
                g.FillEllipse(Brushes.White, 16, 16, 16, 16);
                g.FillEllipse(dot, 18, 18, 12, 12);
            }
        }
        // İki ikon bir kez oluşturulur ve uygulama ömrü boyunca yaşar; HICON sızıntısı önemsiz.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
