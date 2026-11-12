using System.Security.Principal;

namespace Notlar.Core;

public static class AppPaths
{
    /// <summary>Kullanıcı SID'i alınamazsa kullanılan (paylaşılan) olay adı.</summary>
    public const string ChangeEventNameFallback = @"Local\Notlar.Degisti";

    /// <summary>Geçerli kullanıcının SID'i (ör. S-1-5-21-…); alınamazsa null. (Statik başlatma sırası
    /// nedeniyle ChangeEventName'den önce tanımlı olmalı.)</summary>
    public static string? CurrentUserSid { get; } = TryGetSid();

    /// <summary>Süreçler arası "veri değişti" olayının kullanıcıya özel adı:
    /// <c>Local\Notlar.Degisti.&lt;SID&gt;</c>. Başka bir kullanıcının aynı adı önceden ele geçirmesini
    /// (squatting) önler. SID alınamazsa <see cref="ChangeEventNameFallback"/>.</summary>
    public static string ChangeEventName { get; } = BuildPerUserName(ChangeEventNameFallback);

    /// <summary>Verilen çekirdek nesnesi adının sonuna geçerli kullanıcının SID'ini ekler ("&lt;ad&gt;.&lt;SID&gt;");
    /// SID alınamazsa adı olduğu gibi döndürür. App, mutex/göster olayı adları için de kullanabilir.</summary>
    public static string BuildPerUserName(string baseName)
    {
        var sid = CurrentUserSid;
        return sid is null ? baseName : $"{baseName}.{sid}";
    }

    private static string? TryGetSid()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notlar");

    public static string DbPath => Path.Combine(DataDir, "notlar.db");
    public static string BackupDir => Path.Combine(DataDir, "yedek");
    public static string LogPath => Path.Combine(DataDir, "log.txt");
    public static string RecordingsDir => Path.Combine(DataDir, "kayitlar");
    public static string ModelsDir => Path.Combine(DataDir, "modeller");
}
