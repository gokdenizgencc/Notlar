using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Notlar.App;

/// <summary>Sessiz tepsi balonu. WinForms <see cref="Forms.NotifyIcon.ShowBalloonTip(int, string, string, Forms.ToolTipIcon)"/>
/// NIIF_NOSOUND bayrağını desteklemez; bu yüzden aynı simge (NotifyIcon'un iç pencere tutamacı ve kimliği yansımayla
/// okunur) için Shell_NotifyIcon(NIM_MODIFY, NIF_INFO) doğrudan çağrılır. Balon tıklaması yine NotifyIcon'un
/// BalloonTipClicked olayına düşer. Yansıma başarısız olursa false döner; çağıran normal (sesli) balona döner.</summary>
internal static class SilentBalloon
{
    private const uint NIM_MODIFY = 0x1;
    private const uint NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;
    private const uint NIIF_ERROR = 0x3;
    private const uint NIIF_NOSOUND = 0x10;

    private static readonly FieldInfo? IdField =
        typeof(Forms.NotifyIcon).GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? WindowField =
        typeof(Forms.NotifyIcon).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? AddedField =
        typeof(Forms.NotifyIcon).GetField("_added", BindingFlags.NonPublic | BindingFlags.Instance);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    public static bool TryShow(Forms.NotifyIcon icon, string title, string text, bool isError)
    {
        try
        {
            if (IdField?.GetValue(icon) is not uint id
                || WindowField?.GetValue(icon) is not Forms.NativeWindow window
                || window.Handle == IntPtr.Zero
                || AddedField?.GetValue(icon) is not true)
                return false;

            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = window.Handle,
                uID = id,
                uFlags = NIF_INFO,
                szTip = "",
                szInfo = text,
                szInfoTitle = title,
                uTimeoutOrVersion = 5000,
                dwInfoFlags = (isError ? NIIF_ERROR : NIIF_INFO) | NIIF_NOSOUND,
            };
            return Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return false;
        }
    }
}
