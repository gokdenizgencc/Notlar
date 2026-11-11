using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Forms = System.Windows.Forms;

namespace Notlar.App;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _syncingAutoStart;

    public TrayIcon(Action onToggle, Action onOpen, Action onExport, Action onExit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Aç", null, (_, _) => onOpen());
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
        menu.Opening += (_, _) => SyncAutoStartCheck(autoStart);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Notlar",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) onToggle();
        };
    }

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

    /// <summary>Gömülü .ico yerine çalışma anında çizilen basit ikon (mavi daire, beyaz "N").</summary>
    private static Icon CreateIcon()
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
        }
        // Tek ikon, uygulama ömrü boyunca yaşar; HICON sızıntısı önemsiz.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
