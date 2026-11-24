namespace Notlar.Core;

/// <summary>Kayıt seslerinin saklama süresi seçenekleri (⚙ Ayarlar → Veri ve yedekleme). Süre gün olarak saklanır;
/// <see cref="Never"/> (0) ses dosyalarının hiç silinmeyeceği anlamına gelir.</summary>
public static class AudioRetentionPolicy
{
    public const int DefaultDays = 14;
    public const int Never = 0;

    /// <summary>Arayüzde sunulan seçenekler (gün; 0 = hiç silme).</summary>
    public static IReadOnlyList<int> Options { get; } = [7, 14, 30, 90, Never];

    /// <summary>Bilinmeyen değeri varsayılana çeker.</summary>
    public static int Normalize(int days) => Options.Contains(days) ? days : DefaultDays;

    /// <summary>Temizlikte kullanılacak yaş; "hiç silme" ise null (temizlik yapılmaz).</summary>
    public static TimeSpan? ToAge(int days)
    {
        var normalized = Normalize(days);
        return normalized == Never ? null : TimeSpan.FromDays(normalized);
    }

    public static string Describe(int days)
    {
        var normalized = Normalize(days);
        return normalized == Never ? "Hiç silme" : $"{normalized} gün";
    }
}
