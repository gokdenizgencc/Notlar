using System;
using System.IO;
using Microsoft.Win32;
using Notlar.Core;

namespace Notlar.App;

/// <summary>HKCU Run anahtarı ile "Windows ile başlat".</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Notlar";

    private static string MarkerPath => Path.Combine(AppPaths.DataDir, "ilk-calistirma.txt");
    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>İlk çalıştırmada açar; açıksa kaydı bu exe'nin yoluna günceller (exe taşındıysa).</summary>
    public static void EnsureRegistration()
    {
        if (!File.Exists(MarkerPath))
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            SetEnabled(true);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
        }
        else if (IsEnabled())
        {
            SetEnabled(true);
        }
    }
}
