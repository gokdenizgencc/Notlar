namespace Notlar.Core;

public static class AppPaths
{
    public const string ChangeEventName = @"Local\Notlar.Degisti";

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notlar");

    public static string DbPath => Path.Combine(DataDir, "notlar.db");
    public static string BackupDir => Path.Combine(DataDir, "yedek");
    public static string LogPath => Path.Combine(DataDir, "log.txt");
}
