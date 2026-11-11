using System;
using System.Windows.Media;

namespace Notlar.App;

/// <summary>Yazar adına göre (büyük/küçük harfe duyarsız) görüntüleme rengi çözümler.</summary>
public static class AuthorColors
{
    // Bilinmeyen (gelecekteki) yazarlar için sabit, hoş görünen bir renk paleti; isim hash'ine göre seçilir.
    private static readonly string[] FallbackPalette =
    {
        "#E91E8C", "#8E24AA", "#3949AB", "#00897B",
        "#43A047", "#F4511E", "#6D4C41", "#546E7A",
        "#C0CA33", "#039BE5",
    };

    /// <summary>
    /// Yazar için kullanılacak fırçayı döndürür. Ayarlarda tanımlı bir renk varsa onu kullanır;
    /// "Ben" için boş renk tanımlıysa null döner (çağıran tema Fg rengini kullanmalı).
    /// Bilinmeyen yazarlar için isimden türetilen kararlı bir fallback renk üretir.
    /// </summary>
    public static Brush? For(string authorName, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(authorName))
            return null;

        if (s.AuthorColors is not null && s.AuthorColors.TryGetValue(authorName, out var hex))
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;

            return TryParse(hex) ?? FallbackFor(authorName);
        }

        return FallbackFor(authorName);
    }

    private static Brush FallbackFor(string authorName)
    {
        var hash = 0;
        unchecked
        {
            foreach (var c in authorName.ToLowerInvariant())
                hash = hash * 31 + c;
        }
        var index = Math.Abs(hash) % FallbackPalette.Length;
        return TryParse(FallbackPalette[index]) ?? Brushes.Gray;
    }

    private static Brush? TryParse(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }
}
