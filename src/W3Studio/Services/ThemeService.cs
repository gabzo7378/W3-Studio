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

    public static bool IsDarkTheme => CurrentTheme is AppTheme.Dark or AppTheme.DarkGreen or AppTheme.DarkBlue;

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

        var p = GetThemePalette(theme);

        // 1. Pinceles principales de la interfaz
        SetBrush(res, "Win10WindowBg", p.WinBg);
        SetBrush(res, "Win10PanelBg", p.PanelBg);
        SetBrush(res, "Win10SidebarGray", p.SidebarBg);
        SetBrush(res, "Win10Border", p.Border);
        SetBrush(res, "Win10DarkBorder", p.DarkBorder);
        SetBrush(res, "Win10Text", p.Text);
        SetBrush(res, "Win10Accent", p.Accent);

        // 2. Pinceles de menús, controles, inputs y estados (Hover / Selected / AccentText)
        SetBrush(res, "ThemeMenuBg", p.MenuBg);
        SetBrush(res, "ThemeMenuPopupBg", p.MenuPopupBg);
        SetBrush(res, "ThemeToolbarBg", p.PanelBg);
        SetBrush(res, "ThemeTextMuted", p.TextMuted);
        SetBrush(res, "ThemeAccentText", p.AccentText);
        SetBrush(res, "ThemeButtonBg", p.BtnBg);
        SetBrush(res, "ThemeInputBg", p.InputBg);
        SetBrush(res, "ThemeInputText", p.InputText);
        SetBrush(res, "ThemeHoverBg", p.HoverBg);
        SetBrush(res, "ThemeSelectedBg", p.SelectedBg);
        SetBrush(res, "ThemeSelectedText", p.SelectedText);
        SetBrush(res, "ThemeAlternatingRowBg", p.AltRowBg);

        // 3. Pinceles semánticos de botones de acción (Éxito / Compilar / Peligro)
        SetBrush(res, "ThemeSuccessBg", p.SuccessBg);
        SetBrush(res, "ThemeSuccessBorder", p.SuccessBorder);
        SetBrush(res, "ThemeSuccessText", p.SuccessText);

        SetBrush(res, "ThemeWarningBg", p.WarningBg);
        SetBrush(res, "ThemeWarningBorder", p.WarningBorder);
        SetBrush(res, "ThemeWarningText", p.WarningText);

        SetBrush(res, "ThemeDangerBg", p.DangerBg);
        SetBrush(res, "ThemeDangerBorder", p.DangerBorder);
        SetBrush(res, "ThemeDangerText", p.DangerText);

        // 4. Sincronizar SystemColors de WPF para popups nativos, selección inactiva y controles estándar
        SetSystemBrush(res, SystemColors.WindowBrushKey, p.InputBg);
        SetSystemBrush(res, SystemColors.WindowTextBrushKey, p.Text);
        SetSystemBrush(res, SystemColors.ControlBrushKey, p.PanelBg);
        SetSystemBrush(res, SystemColors.ControlTextBrushKey, p.Text);
        SetSystemBrush(res, SystemColors.MenuBrushKey, p.MenuPopupBg);
        SetSystemBrush(res, SystemColors.MenuBarBrushKey, p.MenuBg);
        SetSystemBrush(res, SystemColors.MenuTextBrushKey, p.Text);
        SetSystemBrush(res, SystemColors.HighlightBrushKey, p.SelectedBg);
        SetSystemBrush(res, SystemColors.HighlightTextBrushKey, p.SelectedText);
        SetSystemBrush(res, SystemColors.InactiveSelectionHighlightBrushKey, p.HoverBg);
        SetSystemBrush(res, SystemColors.InactiveSelectionHighlightTextBrushKey, p.Text);
        SetSystemBrush(res, SystemColors.GrayTextBrushKey, p.TextMuted);
        SetSystemBrush(res, SystemColors.InfoBrushKey, p.MenuPopupBg);
        SetSystemBrush(res, SystemColors.InfoTextBrushKey, p.Text);

        if (settings != null && saveSettings)
        {
            settings.AppThemeName = theme.ToString();
            settings.Save();
        }

        ThemeChanged?.Invoke(theme);
    }

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    private static void SetSystemBrush(ResourceDictionary res, ResourceKey key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    private record ThemePalette(
        Color WinBg,
        Color PanelBg,
        Color SidebarBg,
        Color MenuBg,
        Color MenuPopupBg,
        Color Border,
        Color DarkBorder,
        Color Text,
        Color TextMuted,
        Color Accent,
        Color AccentText,
        Color BtnBg,
        Color InputBg,
        Color InputText,
        Color HoverBg,
        Color SelectedBg,
        Color SelectedText,
        Color AltRowBg,
        Color SuccessBg,
        Color SuccessBorder,
        Color SuccessText,
        Color WarningBg,
        Color WarningBorder,
        Color WarningText,
        Color DangerBg,
        Color DangerBorder,
        Color DangerText
    );

    private static ThemePalette GetThemePalette(AppTheme theme) => theme switch
    {
        // 1. Modo Oscuro (Dark / Midnight — Alto contraste estilo VS Code Dark Modern)
        AppTheme.Dark => new(
            WinBg: Color.FromRgb(24, 24, 27),          // #18181B
            PanelBg: Color.FromRgb(32, 32, 36),        // #202024
            SidebarBg: Color.FromRgb(28, 28, 32),      // #1C1C20
            MenuBg: Color.FromRgb(28, 28, 32),         // #1C1C20
            MenuPopupBg: Color.FromRgb(39, 39, 44),    // #27272C
            Border: Color.FromRgb(68, 68, 76),         // #44444C
            DarkBorder: Color.FromRgb(92, 92, 104),    // #5C5C68
            Text: Color.FromRgb(244, 244, 246),        // #F4F4F6
            TextMuted: Color.FromRgb(176, 178, 188),   // #B0B2BC (legible sobre #18181B y #202024)
            Accent: Color.FromRgb(14, 132, 228),       // #0E84E4
            AccentText: Color.FromRgb(96, 178, 255),   // #60B2FF (azul claro brillante para títulos y modificados)
            BtnBg: Color.FromRgb(48, 48, 54),          // #303036
            InputBg: Color.FromRgb(20, 20, 23),        // #141417
            InputText: Color.FromRgb(248, 248, 250),   // #F8F8FA
            HoverBg: Color.FromRgb(58, 62, 72),        // #3A3E48
            SelectedBg: Color.FromRgb(14, 112, 192),   // #0E70C0
            SelectedText: Color.FromRgb(255, 255, 255),// #FFFFFF
            AltRowBg: Color.FromRgb(28, 28, 33),       // #1C1C21
            SuccessBg: Color.FromRgb(22, 68, 38),      // #164426
            SuccessBorder: Color.FromRgb(56, 168, 88), // #38A858
            SuccessText: Color.FromRgb(210, 250, 222), // #D2FADE
            WarningBg: Color.FromRgb(74, 56, 18),      // #4A3812
            WarningBorder: Color.FromRgb(212, 160, 42),// #D4A02A
            WarningText: Color.FromRgb(255, 238, 186), // #FFEEBA
            DangerBg: Color.FromRgb(78, 26, 30),       // #4E1A1E
            DangerBorder: Color.FromRgb(208, 68, 76),  // #D0444C
            DangerText: Color.FromRgb(255, 214, 218)   // #FFD6DA
        ),

        // 2. Modo Blanco (Clean Light — Alto contraste moderno)
        AppTheme.Light => new(
            WinBg: Color.FromRgb(250, 251, 252),       // #FAFBFC
            PanelBg: Color.FromRgb(241, 243, 246),     // #F1F3F6
            SidebarBg: Color.FromRgb(229, 233, 238),   // #E5E9EE
            MenuBg: Color.FromRgb(248, 249, 251),      // #F8F9FB
            MenuPopupBg: Color.FromRgb(255, 255, 255), // #FFFFFF
            Border: Color.FromRgb(194, 201, 210),      // #C2C9D2
            DarkBorder: Color.FromRgb(148, 158, 170),  // #949EAA
            Text: Color.FromRgb(22, 26, 32),           // #161A20
            TextMuted: Color.FromRgb(84, 94, 108),     // #545E6C
            Accent: Color.FromRgb(13, 110, 253),       // #0D6EFD
            AccentText: Color.FromRgb(10, 88, 202),    // #0A58CA
            BtnBg: Color.FromRgb(233, 236, 241),       // #E9ECF1
            InputBg: Color.FromRgb(255, 255, 255),     // #FFFFFF
            InputText: Color.FromRgb(22, 26, 32),      // #161A20
            HoverBg: Color.FromRgb(218, 230, 248),     // #DAE6F8
            SelectedBg: Color.FromRgb(13, 110, 253),   // #0D6EFD
            SelectedText: Color.FromRgb(255, 255, 255),// #FFFFFF
            AltRowBg: Color.FromRgb(245, 247, 250),    // #F5F7FA
            SuccessBg: Color.FromRgb(221, 244, 225),   // #DDF4E1
            SuccessBorder: Color.FromRgb(42, 148, 66), // #2A9442
            SuccessText: Color.FromRgb(16, 68, 28),    // #10441C
            WarningBg: Color.FromRgb(255, 246, 214),   // #FFF6D6
            WarningBorder: Color.FromRgb(198, 148, 28),// #C6941C
            WarningText: Color.FromRgb(64, 44, 6),     // #402C06
            DangerBg: Color.FromRgb(252, 226, 228),    // #FCE2E4
            DangerBorder: Color.FromRgb(196, 62, 68),  // #C43E44
            DangerText: Color.FromRgb(92, 18, 22)     // #5C1216
        ),

        // 3. Modo Rosado (Sakura / Soft Rose — Contraste cálido y nítido)
        AppTheme.Pink => new(
            WinBg: Color.FromRgb(255, 244, 247),       // #FFF4F7
            PanelBg: Color.FromRgb(252, 230, 237),     // #FCE6ED
            SidebarBg: Color.FromRgb(242, 204, 218),   // #F2CCDA
            MenuBg: Color.FromRgb(254, 236, 242),      // #FEECF2
            MenuPopupBg: Color.FromRgb(255, 248, 250), // #FFF8FA
            Border: Color.FromRgb(214, 152, 174),      // #D698AE
            DarkBorder: Color.FromRgb(180, 112, 138),  // #B4708A
            Text: Color.FromRgb(48, 18, 30),           // #30121E
            TextMuted: Color.FromRgb(112, 54, 76),     // #70364C
            Accent: Color.FromRgb(208, 56, 104),       // #D03868
            AccentText: Color.FromRgb(168, 24, 74),    // #A8184A
            BtnBg: Color.FromRgb(248, 214, 226),       // #F8D6E2
            InputBg: Color.FromRgb(255, 255, 255),     // #FFFFFF
            InputText: Color.FromRgb(48, 18, 30),      // #30121E
            HoverBg: Color.FromRgb(244, 192, 210),     // #F4C0D2
            SelectedBg: Color.FromRgb(198, 48, 96),    // #C63060
            SelectedText: Color.FromRgb(255, 255, 255),// #FFFFFF
            AltRowBg: Color.FromRgb(254, 238, 243),    // #FEEEE3
            SuccessBg: Color.FromRgb(221, 244, 225),   // #DDF4E1
            SuccessBorder: Color.FromRgb(42, 148, 66), // #2A9442
            SuccessText: Color.FromRgb(16, 68, 28),    // #10441C
            WarningBg: Color.FromRgb(255, 244, 210),   // #FFF4D2
            WarningBorder: Color.FromRgb(198, 148, 28),// #C6941C
            WarningText: Color.FromRgb(64, 44, 6),     // #402C06
            DangerBg: Color.FromRgb(252, 218, 224),    // #FCDAE0
            DangerBorder: Color.FromRgb(196, 52, 72),  // #C43448
            DangerText: Color.FromRgb(92, 14, 26)     // #5C0E1A
        ),

        // 4. Modo Verde Oscuro (Night Elf / Emerald Forest — Alto contraste sobre esmeralda oscuro)
        AppTheme.DarkGreen => new(
            WinBg: Color.FromRgb(14, 25, 19),          // #0E1913
            PanelBg: Color.FromRgb(20, 36, 27),        // #14241B
            SidebarBg: Color.FromRgb(17, 31, 23),      // #111F17
            MenuBg: Color.FromRgb(18, 33, 25),         // #122119
            MenuPopupBg: Color.FromRgb(24, 44, 33),    // #182C21
            Border: Color.FromRgb(46, 86, 63),         // #2E563F
            DarkBorder: Color.FromRgb(68, 122, 90),    // #447A5A
            Text: Color.FromRgb(234, 250, 240),        // #EAFAF0 (blanco esmeralda muy nítido)
            TextMuted: Color.FromRgb(162, 206, 178),   // #A2CEB2 (verde claro legible)
            Accent: Color.FromRgb(46, 168, 82),        // #2EA852
            AccentText: Color.FromRgb(104, 232, 142),  // #68E88E (verde brillante para títulos y filas modificadas)
            BtnBg: Color.FromRgb(30, 54, 40),          // #1E3628
            InputBg: Color.FromRgb(11, 20, 15),        // #0B140F
            InputText: Color.FromRgb(238, 252, 243),   // #EEFCF3
            HoverBg: Color.FromRgb(38, 70, 52),        // #264634
            SelectedBg: Color.FromRgb(36, 136, 66),    // #248842
            SelectedText: Color.FromRgb(255, 255, 255),// #FFFFFF
            AltRowBg: Color.FromRgb(16, 30, 22),       // #101E16
            SuccessBg: Color.FromRgb(26, 76, 44),      // #1A4C2C
            SuccessBorder: Color.FromRgb(66, 188, 102),// #42BC66
            SuccessText: Color.FromRgb(220, 255, 230), // #DCFFE6
            WarningBg: Color.FromRgb(72, 58, 18),      // #483A12
            WarningBorder: Color.FromRgb(218, 168, 44),// #DAA82C
            WarningText: Color.FromRgb(255, 240, 190), // #FFF0BE
            DangerBg: Color.FromRgb(78, 26, 30),       // #4E1A1E
            DangerBorder: Color.FromRgb(210, 72, 78),  // #D2484E
            DangerText: Color.FromRgb(255, 216, 220)   // #FFD8DC
        ),

        // 5. Modo Azul Oscuro (The Frozen Throne / Icecrown — Alto contraste glaciar)
        AppTheme.DarkBlue => new(
            WinBg: Color.FromRgb(13, 21, 34),          // #0D1522
            PanelBg: Color.FromRgb(19, 31, 50),        // #131F32
            SidebarBg: Color.FromRgb(16, 26, 42),      // #101A2A
            MenuBg: Color.FromRgb(16, 27, 44),         // #101B2C
            MenuPopupBg: Color.FromRgb(23, 38, 62),    // #17263E
            Border: Color.FromRgb(45, 74, 116),        // #2D4A74
            DarkBorder: Color.FromRgb(68, 108, 164),   // #446CA4
            Text: Color.FromRgb(236, 244, 255),        // #ECF4FF (blanco hielo de alto contraste)
            TextMuted: Color.FromRgb(164, 190, 224),   // #A4BEE0 (azul hielo suave muy legible)
            Accent: Color.FromRgb(56, 139, 253),       // #388BFD
            AccentText: Color.FromRgb(118, 190, 255),  // #76BEFF (celeste brillante para títulos y filas modificadas)
            BtnBg: Color.FromRgb(28, 46, 74),          // #1C2E4A
            InputBg: Color.FromRgb(10, 16, 27),        // #0A101B
            InputText: Color.FromRgb(240, 247, 255),   // #F0F7FF
            HoverBg: Color.FromRgb(36, 60, 96),        // #243C60
            SelectedBg: Color.FromRgb(37, 108, 212),   // #256CD4
            SelectedText: Color.FromRgb(255, 255, 255),// #FFFFFF
            AltRowBg: Color.FromRgb(15, 25, 41),       // #0F1929
            SuccessBg: Color.FromRgb(20, 68, 46),      // #14442E
            SuccessBorder: Color.FromRgb(52, 176, 104),// #34B068
            SuccessText: Color.FromRgb(214, 252, 228), // #D6FCE4
            WarningBg: Color.FromRgb(74, 56, 18),      // #4A3812
            WarningBorder: Color.FromRgb(218, 168, 44),// #DAA82C
            WarningText: Color.FromRgb(255, 240, 190), // #FFF0BE
            DangerBg: Color.FromRgb(80, 26, 34),       // #501A22
            DangerBorder: Color.FromRgb(214, 72, 82),  // #D64852
            DangerText: Color.FromRgb(255, 216, 220)   // #FFD8DC
        ),

        // 6. Modo Sistema / Clásico World Editor (Predeterminado)
        _ => new(
            WinBg: Color.FromRgb(240, 240, 240),       // #F0F0F0
            PanelBg: Color.FromRgb(230, 230, 230),     // #E6E6E6
            SidebarBg: Color.FromRgb(128, 128, 128),   // #808080
            MenuBg: Color.FromRgb(245, 245, 245),      // #F5F5F5
            MenuPopupBg: Color.FromRgb(250, 250, 250), // #FAFAFA
            Border: Color.FromRgb(160, 160, 160),      // #A0A0A0
            DarkBorder: Color.FromRgb(105, 105, 105),  // #696969
            Text: Color.FromRgb(17, 17, 17),           // #111111
            TextMuted: Color.FromRgb(80, 80, 80),      // #505050
            Accent: Color.FromRgb(0, 120, 215),        // #0078D7
            AccentText: Color.FromRgb(0, 84, 153),     // #005499
            BtnBg: Color.FromRgb(225, 225, 225),       // #E1E1E1
            InputBg: Color.FromRgb(255, 255, 255),     // #FFFFFF
            InputText: Color.FromRgb(17, 17, 17),      // #111111
            HoverBg: Color.FromRgb(212, 228, 247),     // #D4E4F7
            SelectedBg: Color.FromRgb(0, 120, 215),    // #0078D7
            SelectedText: Color.FromRgb(255, 255, 255),// #FFFFFF
            AltRowBg: Color.FromRgb(245, 245, 245),    // #F5F5F5
            SuccessBg: Color.FromRgb(223, 240, 216),   // #DFF0D8
            SuccessBorder: Color.FromRgb(58, 142, 66), // #3A8E42
            SuccessText: Color.FromRgb(17, 46, 20),    // #112E14
            WarningBg: Color.FromRgb(255, 248, 220),   // #FFF8DC
            WarningBorder: Color.FromRgb(200, 157, 40),// #C89D28
            WarningText: Color.FromRgb(34, 24, 4),     // #221804
            DangerBg: Color.FromRgb(249, 226, 226),    // #F9E2E2
            DangerBorder: Color.FromRgb(196, 68, 68),  // #C44444
            DangerText: Color.FromRgb(74, 14, 14)     // #4A0E0E
        )
    };
}
