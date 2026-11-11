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
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
