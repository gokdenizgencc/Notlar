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

    /// <summary>Kurulu uygulamanın dizini; sonunda ayırıcı var ki "Notlar" önek karşılaştırması
    /// "NotlarXyz" gibi bir klasörle yanlışlıkla eşleşmesin.</summary>
    private static string InstalledDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Notlar") + Path.DirectorySeparatorChar;

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

    private static string? RegisteredPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        // Command değeri her zaman tek bir tırnaklı yol olarak yazılır (ek argüman yok).
        return (key?.GetValue(ValueName) as string)?.Trim('"');
    }

    /// <summary>Zaten etkinken kaydı yalnızca şu durumlarda günceller: kayıtlı hedef artık yoksa
    /// (ör. uygulama taşındı/silindi) ya da bu süreç kurulu uygulamanın kendisiyse. Böylece
    /// geliştirme/Debug çalıştırması kurulu uygulamanın otomatik başlatma kaydını ele geçirmez.</summary>
    private static bool ShouldRewrite()
    {
        var registeredPath = RegisteredPath();
        if (string.IsNullOrEmpty(registeredPath) || !File.Exists(registeredPath))
            return true;

        var processPath = Environment.ProcessPath;
        return processPath is not null && processPath.StartsWith(InstalledDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>İlk çalıştırmada açar; açıksa ve ShouldRewrite true ise kaydı bu exe'nin yoluna günceller (exe taşındıysa).</summary>
    public static void EnsureRegistration()
    {
        if (!File.Exists(MarkerPath))
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            SetEnabled(true);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
        }
        else if (IsEnabled() && ShouldRewrite())
        {
            SetEnabled(true);
        }
    }
}
