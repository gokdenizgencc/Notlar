using System;
using System.IO;
using Notlar.Core;

namespace Notlar.App;

public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Log yazılamıyorsa yapacak bir şey yok; uygulama çökmesin.
        }
    }
}
