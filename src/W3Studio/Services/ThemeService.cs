using System;
using System.Windows;
using System.Windows.Media;
using W3Studio.Models;

namespace W3Studio.Services;

public enum AppTheme
{
    System,
    Dark,
    Light,
    Pink,
    DarkGreen,
    DarkBlue
}

public static class ThemeService
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    public static event Action<AppTheme>? ThemeChanged;

    public static void Initialize(WorkspaceSettings settings)
    {
        var theme = ParseTheme(settings.AppThemeName);
        ApplyTheme(theme, settings, saveSettings: false);
    }

    public static AppTheme ParseTheme(string? name) => name?.ToLowerInvariant() switch
    {
        "dark" => AppTheme.Dark,
        "light" => AppTheme.Light,
        "pink" or "sakura" or "rosado" => AppTheme.Pink,
        "darkgreen" or "green" or "verde" => AppTheme.DarkGreen,
        "darkblue" or "blue" or "azul" => AppTheme.DarkBlue,
        _ => AppTheme.System
    };

    public static void ApplyTheme(AppTheme theme, WorkspaceSettings? settings = null, bool saveSettings = true)
    {
        CurrentTheme = theme;
        var res = Application.Current?.Resources;
        if (res == null) return;

        var (winBg, panelBg, sidebarBg, menuBg, border, darkBorder, text, textMuted, accent, btnBg, inputBg, inputText) = GetThemeColors(theme);

        SetBrush(res, "Win10WindowBg", winBg);
        SetBrush(res, "Win10PanelBg", panelBg);
        SetBrush(res, "Win10SidebarGray", sidebarBg);
        SetBrush(res, "Win10Border", border);
        SetBrush(res, "Win10DarkBorder", darkBorder);
        SetBrush(res, "Win10Text", text);
        SetBrush(res, "Win10Accent", accent);

        SetBrush(res, "ThemeMenuBg", menuBg);
        SetBrush(res, "ThemeToolbarBg", panelBg);
        SetBrush(res, "ThemeTextMuted", textMuted);
        SetBrush(res, "ThemeButtonBg", btnBg);
        SetBrush(res, "ThemeInputBg", inputBg);
        SetBrush(res, "ThemeInputText", inputText);

        if (settings != null && saveSettings)
        {
            settings.AppThemeName = theme.ToString();
            settings.Save();
        }

        ThemeChanged?.Invoke(theme);
    }

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        if (res.Contains(key))
            res[key] = new SolidColorBrush(color);
        else
            res.Add(key, new SolidColorBrush(color));
    }

    private static (
        Color winBg,
        Color panelBg,
        Color sidebarBg,
        Color menuBg,
        Color border,
        Color darkBorder,
        Color text,
        Color textMuted,
        Color accent,
        Color btnBg,
        Color inputBg,
        Color inputText
    ) GetThemeColors(AppTheme theme) => theme switch
    {
        // 1. Modo Oscuro (Dark / Midnight)
        AppTheme.Dark => (
            winBg: Color.FromRgb(30, 30, 30),        // #1E1E1E
            panelBg: Color.FromRgb(37, 37, 38),      // #252526
            sidebarBg: Color.FromRgb(45, 45, 48),    // #2D2D30
            menuBg: Color.FromRgb(37, 37, 38),       // #252526
            border: Color.FromRgb(63, 63, 70),       // #3F3F46
            darkBorder: Color.FromRgb(82, 82, 91),   // #52525B
            text: Color.FromRgb(230, 230, 230),      // #E6E6E6
            textMuted: Color.FromRgb(161, 161, 170), // #A1A1AA
            accent: Color.FromRgb(0, 122, 204),      // #007ACC
            btnBg: Color.FromRgb(51, 51, 55),        // #333337
            inputBg: Color.FromRgb(45, 45, 48),      // #2D2D30
            inputText: Color.FromRgb(245, 245, 245)
        ),

        // 2. Modo Blanco (Clean Light)
        AppTheme.Light => (
            winBg: Color.FromRgb(255, 255, 255),     // #FFFFFF
            panelBg: Color.FromRgb(248, 249, 250),   // #F8F9FA
            sidebarBg: Color.FromRgb(233, 236, 239), // #E9ECEF
            menuBg: Color.FromRgb(255, 255, 255),    // #FFFFFF
            border: Color.FromRgb(206, 212, 218),    // #CED4DA
            darkBorder: Color.FromRgb(173, 181, 189),
            text: Color.FromRgb(33, 37, 41),         // #212529
            textMuted: Color.FromRgb(108, 117, 125),
            accent: Color.FromRgb(13, 110, 253),     // #0D6EFD
            btnBg: Color.FromRgb(241, 243, 245),
            inputBg: Color.FromRgb(255, 255, 255),
            inputText: Color.FromRgb(33, 37, 41)
        ),

        // 3. Modo Rosado (Sakura / Soft Rose)
        AppTheme.Pink => (
            winBg: Color.FromRgb(255, 240, 245),     // #FFF0F5
            panelBg: Color.FromRgb(255, 228, 225),   // #FFE4E1
            sidebarBg: Color.FromRgb(232, 162, 184), // #E8A2B8
            menuBg: Color.FromRgb(255, 245, 248),    // #FFF5F8
            border: Color.FromRgb(216, 142, 165),    // #D88EA5
            darkBorder: Color.FromRgb(186, 110, 134),
            text: Color.FromRgb(58, 30, 40),         // #3A1E28
            textMuted: Color.FromRgb(117, 72, 87),
            accent: Color.FromRgb(212, 63, 108),     // #D43F6C
            btnBg: Color.FromRgb(255, 214, 227),
            inputBg: Color.FromRgb(255, 255, 255),
            inputText: Color.FromRgb(58, 30, 40)
        ),

        // 4. Modo Verde Oscuro (Fel Orc / Dark Emerald Forest)
        AppTheme.DarkGreen => (
            winBg: Color.FromRgb(12, 24, 17),        // #0C1811
            panelBg: Color.FromRgb(18, 36, 26),      // #12241A
            sidebarBg: Color.FromRgb(27, 51, 37),    // #1B3325
            menuBg: Color.FromRgb(18, 36, 26),       // #12241A
            border: Color.FromRgb(40, 78, 56),       // #284E38
            darkBorder: Color.FromRgb(61, 117, 85),
            text: Color.FromRgb(214, 245, 227),      // #D6F5E3
            textMuted: Color.FromRgb(142, 189, 160),
            accent: Color.FromRgb(46, 160, 67),      // #2EA043
            btnBg: Color.FromRgb(30, 61, 43),
            inputBg: Color.FromRgb(20, 40, 29),
            inputText: Color.FromRgb(214, 245, 227)
        ),

        // 5. Modo Azul Oscuro (The Frozen Throne / Dark Abyss)
        AppTheme.DarkBlue => (
            winBg: Color.FromRgb(10, 17, 28),        // #0A111C
            panelBg: Color.FromRgb(16, 29, 48),      // #101D30
            sidebarBg: Color.FromRgb(22, 40, 66),    // #162842
            menuBg: Color.FromRgb(16, 29, 48),       // #101D30
            border: Color.FromRgb(35, 63, 102),      // #233F66
            darkBorder: Color.FromRgb(56, 101, 161),
            text: Color.FromRgb(217, 232, 249),      // #D9E8F9
            textMuted: Color.FromRgb(139, 168, 204),
            accent: Color.FromRgb(59, 130, 246),     // #3B82F6
            btnBg: Color.FromRgb(28, 50, 84),
            inputBg: Color.FromRgb(19, 34, 56),
            inputText: Color.FromRgb(217, 232, 249)
        ),

        // 6. Modo Sistema / Clásico World Editor (Predeterminado)
        _ => (
            winBg: Color.FromRgb(240, 240, 240),     // #F0F0F0
            panelBg: Color.FromRgb(230, 230, 230),   // #E6E6E6
            sidebarBg: Color.FromRgb(128, 128, 128), // #808080
            menuBg: Color.FromRgb(245, 245, 245),    // #F5F5F5
            border: Color.FromRgb(160, 160, 160),    // #A0A0A0
            darkBorder: Color.FromRgb(105, 105, 105),// #696969
            text: Color.FromRgb(17, 17, 17),         // #111111
            textMuted: Color.FromRgb(85, 85, 85),
            accent: Color.FromRgb(0, 120, 215),      // #0078D7
            btnBg: Color.FromRgb(225, 225, 225),
            inputBg: Color.FromRgb(255, 255, 255),
            inputText: Color.FromRgb(17, 17, 17)
        )
    };
}

