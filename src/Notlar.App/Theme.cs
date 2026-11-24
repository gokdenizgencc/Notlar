using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Notlar.App;

public static class Theme
{
    public static bool IsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    public static void Apply(ResourceDictionary resources)
    {
        var light = IsLight();
        Set(resources, "Bg", light ? "#FFFFFF" : "#202020");
        Set(resources, "Surface", light ? "#F3F3F3" : "#2D2D2D");
        Set(resources, "Fg", light ? "#1A1A1A" : "#F0F0F0");
        Set(resources, "Muted", light ? "#6B6B6B" : "#9E9E9E");
        Set(resources, "Accent", light ? "#0F6CBD" : "#4CC2FF");
        Set(resources, "OnAccent", light ? "#FFFFFF" : "#002033");
        Set(resources, "Border", light ? "#E0E0E0" : "#3A3A3A");
        Set(resources, "TagBg", light ? "#E6F0FA" : "#1F3A52");
        Set(resources, "Danger", light ? "#C42B1C" : "#9E2A2A");
        Set(resources, "Success", light ? "#107C10" : "#5FB55A");
        Set(resources, "Warning", light ? "#9D5D00" : "#C9972C");
        // Panel düzeni: kayıt düğmesi, ikon şeridi, transkript konuşmacıları.
        Set(resources, "Record", light ? "#C42B1C" : "#D9363E");
        Set(resources, "OnRecord", "#FFFFFF");
        Set(resources, "RailBg", light ? "#EBEBEB" : "#181818");
        // Satır editörü (Notepad++ tarzı): koyu metin alanı, daha koyu numara şeridi, sert imleç satırı bandı.
        Set(resources, "EditorBg", light ? "#FFFFFF" : "#2B2B2B");
        Set(resources, "EditorGutter", light ? "#F0F0F0" : "#161616");
        Set(resources, "EditorLine", light ? "#E4E4E4" : "#111111");
        Set(resources, "NavActiveBg", light ? "#DCE9F7" : "#2B3A45");
        // Hata METNİ rengi: "Danger" koyu temada arka plan (beyaz yazılı şerit/düğme) için koyu kırmızıdır ve
        // koyu zeminde yazı olarak okunmaz (~2,2:1). Yazı/ikon için bu kullanılır (Bg ve Surface üzerinde ≥4,5:1).
        Set(resources, "DangerText", light ? "#C42B1C" : "#FF8A80");
        // Transkriptte "Kişi N:" etiketlerinin renkleri (Bg ve Surface üzerinde ≥4,5:1).
        for (var i = 0; i < PersonLight.Length; i++)
            Set(resources, "Person" + (i + 1), light ? PersonLight[i] : PersonDark[i]);
    }

    private static readonly string[] PersonLight =
        { "#0F6CBD", "#C2410C", "#107C10", "#8E24AA", "#00796B", "#C2185B", "#8D5A00", "#3949AB" };

    private static readonly string[] PersonDark =
        { "#4CC2FF", "#FF9E64", "#6CCB5F", "#D7A6F5", "#4DD0C1", "#FF8FB8", "#E3C04A", "#9FA8FF" };

    /// <summary>Kişi numarasının (1'den başlar) etiket rengi için kaynak anahtarı; renkler bitince baştan döner.</summary>
    public static string PersonBrushKey(int person) =>
        "Person" + ((System.Math.Max(1, person) - 1) % PersonLight.Length + 1);

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
