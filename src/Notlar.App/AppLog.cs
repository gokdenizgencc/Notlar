using System;
using System.IO;
using Notlar.Core;

namespace Notlar.App;

public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxLogBytes = 1024 * 1024;

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                try
                {
                    RotateIfTooLarge();
                }
                catch
                {
                    // Döndürme başarısız olsa bile (ör. dosya başka bir süreçte açık) günlük eklemesi devam etsin.
                }
                File.AppendAllText(AppPaths.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Log yazılamıyorsa yapacak bir şey yok; uygulama çökmesin.
        }
    }

    // Log dosyası 1 MB'ı aşarsa log.old.txt'ye taşınır (üzerine yazılır) ve sıfırdan başlanır.
    private static void RotateIfTooLarge()
    {
        var info = new FileInfo(AppPaths.LogPath);
        if (info.Exists && info.Length > MaxLogBytes)
        {
            var oldPath = Path.Combine(AppPaths.DataDir, "log.old.txt");
            File.Move(AppPaths.LogPath, oldPath, overwrite: true);
        }
    }
}
