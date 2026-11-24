using System;
using System.Diagnostics;
using Microsoft.Win32;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Teams'in mikrofonu kullanıp kullanmadığını Windows'un mikrofon kullanım kaydından okur
/// (Ayarlar → Gizlilik → Mikrofon'daki "şu anda kullanılıyor" bilgisinin kaynağı). Uygulamanın anahtarında
/// <c>LastUsedTimeStop == 0</c> ise mikrofon o an kullanımdadır. Yalnızca Teams anahtarlarına bakılır
/// (<see cref="TeamsCallTrigger.IsTeamsKey"/>); Notlar'ın kendi kaydı ya da başka uygulamalar sayılmaz.</summary>
public sealed class TeamsCallProbe : ITeamsCallProbe
{
    private const string MicrophoneKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string NonPackaged = "NonPackaged";

    public bool IsMicrophoneInUse()
    {
        using var root = Registry.CurrentUser.OpenSubKey(MicrophoneKey);
        if (root is null) return false;
        if (!AnyTeamsKeyInUse(root, nonPackaged: false))
        {
            using var nonPackaged = root.OpenSubKey(NonPackaged);
            if (nonPackaged is null || !AnyTeamsKeyInUse(nonPackaged, nonPackaged: true)) return false;
        }

        // Teams çökerse ya da bilgisayar aniden kapanırsa "kullanımda" işareti kayıt defterinde kalabilir;
        // Teams çalışmıyorsa arama da yoktur.
        return IsTeamsRunning();
    }

    private static bool AnyTeamsKeyInUse(RegistryKey parent, bool nonPackaged)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (!TeamsCallTrigger.IsTeamsKey(name, nonPackaged)) continue;
            using var key = parent.OpenSubKey(name);
            if (key?.GetValue("LastUsedTimeStart") is long start && start != 0
                && key.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                return true;
        }
        return false;
    }

    private static bool IsTeamsRunning() => AnyProcess("ms-teams") || AnyProcess("Teams");

    private static bool AnyProcess(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
}
