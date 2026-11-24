using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Uygulama kaynak sözlüğüne renkleri, yazı tipini (<c>AppFont</c>) ve yoğunluk boşluklarını yazar. Pencereler
/// bunlara DynamicResource ile bağlıdır; <see cref="Apply"/> her çağrıldığında anında güncellenirler.</summary>
public static class Theme
{
    public const string DefaultFontFamily = "Segoe UI";

    /// <summary>Görünüm → Yazı tipi seçenekleri: (görünen ad, denenecek aile adları). Yalnızca makinede yüklü olanlar
    /// gösterilir (<see cref="AvailableFonts"/>).</summary>
    private static readonly (string Label, string[] Families)[] FontCandidates =
    [
        ("Segoe UI", ["Segoe UI"]),
        ("Segoe UI Variable", ["Segoe UI Variable Text", "Segoe UI Variable"]),
        ("Calibri", ["Calibri"]),
        ("Arial", ["Arial"]),
        ("Verdana", ["Verdana"]),
        ("Bahnschrift", ["Bahnschrift"]),
        ("Georgia", ["Georgia"]),
        ("Cascadia Code", ["Cascadia Code"]),
        ("Cascadia Mono", ["Cascadia Mono"]),
        ("Consolas", ["Consolas"]),
    ];

    private static IReadOnlyList<(string Label, string Family)>? _availableFonts;
    private static string? _lastAppliedKey;

    /// <summary>Windows uygulamaları açık temada mı? (Kişiselleştirme → Renkler → Uygulama modu.)</summary>
    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return true;
        }
    }

    /// <summary>Windows vurgu rengi (DWM AccentColor, ABGR); okunamazsa null.</summary>
    public static Rgb? TryGetWindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return new Rgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
            if (key?.GetValue("ColorizationColor") is int argb)
                return new Rgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
        return null;
    }

    /// <summary>Seçilebilir ve makinede yüklü yazı tipleri (görünen ad, aile adı). İlk çağrıda hesaplanır.</summary>
    public static IReadOnlyList<(string Label, string Family)> AvailableFonts()
    {
        if (_availableFonts is not null) return _availableFonts;

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                installed.Add(family.Source);
                foreach (var name in family.FamilyNames.Values)
                    installed.Add(name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }

        var list = new List<(string, string)>();
        foreach (var (label, families) in FontCandidates)
        {
            var found = families.FirstOrDefault(installed.Contains);
            if (found is not null) list.Add((label, found));
            else if (label == DefaultFontFamily) list.Add((label, DefaultFontFamily));
        }
        return _availableFonts = list;
    }

    /// <summary>Ayardaki aile adını uygulanacak FontFamily'ye çevirir; boş ya da yüklü değilse varsayılan.</summary>
    public static string ResolveFontFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return DefaultFontFamily;
        return AvailableFonts().Any(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase))
            ? family
            : DefaultFontFamily;
    }

    /// <summary>Temayı uygulama kaynaklarına yazar. Sonuç bir öncekiyle aynıysa (ör. ayarların görünümle ilgisiz bir
    /// alanı kaydedildiyse) sözlüğe dokunmaz; <paramref name="force"/> bunu atlar.</summary>
    public static void Apply(ResourceDictionary resources, AppSettings settings, bool force = false)
    {
        var light = AppearanceOptions.ResolveLight(settings.ThemeMode, IsSystemLight());
        var windowsAccent = settings.AccentColor == AppearanceOptions.AccentWindows ? TryGetWindowsAccent() : null;
        var palette = AppearanceOptions.ResolvePalette(settings.AccentColor, light, windowsAccent);
        var font = ResolveFontFamily(settings.UiFontFamily);
        var compact = settings.Density == AppearanceOptions.DensityCompact;

        var key = $"{light}|{palette}|{font}|{compact}";
        if (!force && key == _lastAppliedKey) return;
        _lastAppliedKey = key;

        Set(resources, "Bg", AccentPalette.Bg(light).ToHex());
        Set(resources, "Surface", AccentPalette.Surface(light).ToHex());
        Set(resources, "Fg", AccentPalette.Fg(light).ToHex());
        Set(resources, "Muted", light ? "#6B6B6B" : "#9E9E9E");
        // Vurguya bağlı renkler (seçilen renkten türetilir; metin kontrastı ≥4,5:1, bkz. AccentPalette).
        Set(resources, "Accent", palette.Accent.ToHex());
        Set(resources, "OnAccent", palette.OnAccent.ToHex());
        Set(resources, "TagBg", palette.TagBg.ToHex());
        Set(resources, "NavActiveBg", palette.NavActiveBg.ToHex());
        Set(resources, "Border", light ? "#E0E0E0" : "#3A3A3A");
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
        // Hata METNİ rengi: "Danger" koyu temada arka plan (beyaz yazılı şerit/düğme) için koyu kırmızıdır ve
        // koyu zeminde yazı olarak okunmaz (~2,2:1). Yazı/ikon için bu kullanılır (Bg ve Surface üzerinde ≥4,5:1).
        Set(resources, "DangerText", light ? "#C42B1C" : "#FF8A80");
        // Transkriptte "Kişi N:" etiketlerinin renkleri (Bg ve Surface üzerinde ≥4,5:1).
        for (var i = 0; i < PersonLight.Length; i++)
            Set(resources, "Person" + (i + 1), light ? PersonLight[i] : PersonDark[i]);

        // Yazı tipi: pencereler FontFamily="{DynamicResource AppFont}" ile bağlıdır. Eksik glifler için Segoe UI yedektir.
        var fontFamily = font == DefaultFontFamily ? new FontFamily(font) : new FontFamily($"{font}, {DefaultFontFamily}");
        resources["AppFont"] = fontFamily;

        // Yoğunluk: liste satırlarının iç boşlukları (Rahat = eski değerler).
        resources["RowPadding"] = compact ? new Thickness(8, 2, 8, 2) : new Thickness(8, 5, 8, 5);
        resources["RowPaddingWide"] = compact ? new Thickness(8, 3, 8, 3) : new Thickness(8, 6, 8, 6);
        resources["DayRowPadding"] = compact ? new Thickness(8, 4, 8, 4) : new Thickness(8, 8, 8, 8);
        resources["ListItemPadding"] = compact ? new Thickness(12, 4, 12, 4) : new Thickness(12, 8, 12, 8);
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
