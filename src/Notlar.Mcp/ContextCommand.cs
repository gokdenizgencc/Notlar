using System.Text;
using Notlar.Core;

namespace Notlar.Mcp;

/// <summary>`NotlarMcp.exe --baglam`: Claude Code oturum başlangıç hook'u için, MCP sunucusunu başlatmadan
/// stdout'a kompakt bir Türkçe bağlam özeti (hafıza + son iki günün notları) yazar ve çıkar. Salt okunur
/// kullanım olduğu için değişiklik sinyali kullanmaz. Hook akışını asla kesmemesi için her hata yutulur.</summary>
public static class ContextCommand
{
    /// <summary>args içinde --baglam varsa bağlamı yazıp true döner (çağıran süreç 0 ile çıkmalı).</summary>
    public static bool TryRun(string[] args)
    {
        if (!args.Contains("--baglam"))
            return false;

        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var time = TimeProvider.System;
            var memory = new MemoryStore(AppPaths.DbPath, time, NullChangeSignal.Instance);
            var notes = new NoteStore(AppPaths.DbPath, time, NullChangeSignal.Instance);
            var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
            Console.WriteLine(SessionContext.Build(memory, notes, today));
        }
        catch (Exception ex)
        {
            Console.WriteLine(FormatError(ex));
        }

        return true;
    }

    /// <summary>Hata satırı: yalnızca genel bir Türkçe mesaj ve istisna TÜRÜ. İstisna mesajı (dosya yolu veya
    /// veritabanından gelen, Claude'un bağlamına sızabilecek metin içerebilir) bilerek yazılmaz.</summary>
    public static string FormatError(Exception ex) => $"Notlar bağlamı yüklenemedi ({ex.GetType().Name}).";
}
