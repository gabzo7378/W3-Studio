using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using War3Net.Build.Common;
using War3Net.Build.Info;
using W3Studio.Models;

namespace W3Studio.Services;

public enum MediaFitMode
{
    FillCrop,     // Rellenar sin deformar (recorte centrado inteligente)
    FitLetterbox, // Ajustar completo con bandas oscuras
    Stretch       // Estirar a todo el lienzo
}

public enum LoadingScreenQualityMode
{
    HighQuad1024x768, // 4 Texturas BLP (TL 512x512, TR 512x512, BL 512x256, BR 512x256) + LoadingScreen.mdx
    Standard512x512,  // 1 Textura FullScreen.blp (512x512) + LoadingScreen.mdx
    Compact256x256,   // 1 Textura FullScreen.blp (256x256) + LoadingScreen.mdx
    UncompressedTga   // 1 Textura FullScreen.tga (512x512 24-bit sin pérdida) + LoadingScreen.mdx
}

public enum MapPreviewQualityMode
{
    Standard128x128Tga, // 128x128 TGA 24-bit (Estándar oficial Battle.net / LAN)
    High256x256Tga,     // 256x256 TGA 24-bit (Alta nitidez)
    Compact64x64Tga     // 64x64 TGA 24-bit (Ultra ligero)
}

public enum MapPreviewFrameStyle
{
    None,
    ClassicGoldBevel,
    DarkSteelBevel,
    SoftVignette
}

public sealed class LoadingScreenPreset
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int CampaignScreenIndex { get; init; } = -1;
    public string MdxPath { get; init; } = string.Empty;
    public string TopLeftBlp { get; init; } = string.Empty;
    public string TopRightBlp { get; init; } = string.Empty;
    public string BotLeftBlp { get; init; } = string.Empty;
    public string BotRightBlp { get; init; } = string.Empty;
}

public sealed class MediaComposeOptions
{
    public MediaFitMode FitMode { get; set; } = MediaFitMode.FillCrop;
    public int ZoomPercent { get; set; } = 100;
    public int Brightness { get; set; } = 0;
    public int Contrast { get; set; } = 0;
    public bool BottomVignette { get; set; } = true;
    public MapPreviewFrameStyle FrameStyle { get; set; } = MapPreviewFrameStyle.None;
}

public static class MapMediaService
{
    // Plantilla MDX verificada (FormatVersion 800) para 1 sola textura (FullScreen.blp)
    private const string SingleMdxBase64 =
        "TURMWFZFUlMEAAAAIAMAAE1PREx0AQAATG9hZGluZ1NjcmVlbgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJv//z4AAAAAAAAA" +
        "AAAAAADNzEw/8pgZPwAAAACWAAAAU0VRU4QAAABCaXJ0aAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAUNAACwaAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAABNVExTMAAAADAAAAAAAAAAIAAAAExBWVMBAAAAHAAAAAAAAAABAAAAAAAAAP////8AAAAAAACAP1RFWFMMAQAA" +
        "AAAAAEZ1bGxTY3JlZW4uYmxwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEdFT1MgAQAAIAEAAFZSVFgEAAAAAAAAAPKYGT8AAAAA" +
        "AAAAAAAAAAAAAAAAzcxMP/KYGT8AAAAAzcxMPwAAAAAAAAAATlJNUwQAAAAAAAAA1///MwAAgD8AAAAA1///MwAAgD8AAAAA1///" +
        "MwAAgD8AAAAA1///MwAAgD9QVFlQAQAAAAQAAABQQ05UAQAAAAYAAABQVlRYBgAAAAAAAQACAAMAAgABAEdORFgEAAAAAAAAAE1U" +
        "R0MBAAAAAQAAAE1BVFMBAAAAAAAAAAAAAAAAAAAAAAAAAJv//z4AAAAAAAAAAAAAAADNzEw/8pgZPwAAAAAAAAAAVVZBUwEAAABV" +
        "VkJTBAAAAAAAAAAAAAAAAAAAAAAAgD8AAIA/AAAAAAAAgD8AAIA/Qk9ORWgAAABgAAAATDIAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wABAAD/////////" +
        "/1BJVlQMAAAAzcxMPs3MzD0AAAAA";

    // Plantilla MDX verificada (FormatVersion 800) para 4 cuadrantes HQ 1024x768 (LoadingScreenBL/BR/TR/TL.blp)
    private const string QuadMdxBase64 =
        "TURMWFZFUlMEAAAAIAMAAE1PREx0AQAATG9hZGluZ1NjcmVlbgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJv//z4AAAAAAAAA" +
        "AAAAAADNzEw/8pgZPwAAAACWAAAAU0VRU4QAAABCaXJ0aAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAUNAACwaAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAABNVExTwAAAADAAAAAAAAAAIAAAAExBWVMBAAAAHAAAAAAAAAABAAAAAAAAAP////8AAAAAAACAPzAAAAAAAAAA" +
        "IAAAAExBWVMBAAAAHAAAAAAAAAABAAAAAQAAAP////8AAAAAAACAPzAAAAAAAAAAIAAAAExBWVMBAAAAHAAAAAAAAAABAAAAAgAA" +
        "AP////8AAAAAAACAPzAAAAAAAAAAIAAAAExBWVMBAAAAHAAAAAAAAAABAAAAAwAAAP////8AAAAAAACAP1RFWFMwBAAAAAAAAExv" +
        "YWRpbmdTY3JlZW5CTC5ibHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABMb2FkaW5nU2NyZWVuQlIuYmxwAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAATG9hZGluZ1NjcmVlblRSLmJscAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAExvYWRpbmdTY3JlZW5UTC5ibHAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "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";

    public static List<LoadingScreenPreset> GetOfficialLoadingScreenPresets() =>
    [
        new LoadingScreenPreset
        {
            Id = "Generic",
            DisplayName = "Genérica Clásica (Mapa del Mundo)",
            Category = "Estándar / Melee",
            CampaignScreenIndex = 45,
            MdxPath = @"UI\Glues\Loading\Load-Generic\Load-Generic.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Load-Generic\Generic-Loading-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Load-Generic\Generic-Loading-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Load-Generic\Generic-Loading-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Load-Generic\Generic-Loading-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "MultiHuman",
            DisplayName = "Multijugador — Alianza Humana",
            Category = "Estándar / Melee",
            CampaignScreenIndex = -1,
            MdxPath = @"UI\Glues\Loading\Multiplayer\Load-Multiplayer-Human.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Human-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Human-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "MultiOrc",
            DisplayName = "Multijugador — Horda Orca",
            Category = "Estándar / Melee",
            CampaignScreenIndex = -1,
            MdxPath = @"UI\Glues\Loading\Multiplayer\Load-Multiplayer-Orc.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Orc-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Orc-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "MultiUndead",
            DisplayName = "Multijugador — Plaga No-Muerta",
            Category = "Estándar / Melee",
            CampaignScreenIndex = -1,
            MdxPath = @"UI\Glues\Loading\Multiplayer\Load-Multiplayer-Undead.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Undead-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Undead-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "MultiNightElf",
            DisplayName = "Multijugador — Elfos Nocturnos",
            Category = "Estándar / Melee",
            CampaignScreenIndex = -1,
            MdxPath = @"UI\Glues\Loading\Multiplayer\Load-Multiplayer-NightElf.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-NightElf-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-NightElf-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "MultiRandom",
            DisplayName = "Multijugador — Por Defecto / Raza Aleatoria",
            Category = "Estándar / Melee",
            CampaignScreenIndex = -1,
            MdxPath = @"UI\Glues\Loading\Multiplayer\Load-Multiplayer-Random.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Random-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Multiplayer\Loading-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Multiplayer\Loading-Random-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Tutorial",
            DisplayName = "Campaña — Prólogo / Tutorial (Éxodo de la Horda)",
            Category = "Campañas RoC",
            CampaignScreenIndex = 0,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\TutorialBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Tutorial-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Tutorial-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Tutorial-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Tutorial-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Lordaeron",
            DisplayName = "Campaña — Lordaeron (Reign of Chaos)",
            Category = "Campañas RoC",
            CampaignScreenIndex = 2,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\LordaeronBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Lordaeron-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Lordaeron-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Lordaeron-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Lordaeron-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Dalaran",
            DisplayName = "Campaña — Dalaran (No-Muertos RoC)",
            Category = "Campañas RoC",
            CampaignScreenIndex = 4,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\DalaranBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Dalaran-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Dalaran-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Dalaran-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Dalaran-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Quelthalas",
            DisplayName = "Campaña — Quel'Thalas / Lunargenta",
            Category = "Campañas RoC",
            CampaignScreenIndex = 17,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\QuelthalasBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Quelthalas-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Quelthalas-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Quelthalas-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Quelthalas-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Northrend",
            DisplayName = "Campaña — Rasganorte / Northrend (Human 07)",
            Category = "Campañas RoC",
            CampaignScreenIndex = 11,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\NorthrendBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Northrend-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Northrend-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Northrend-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Northrend-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Barrens",
            DisplayName = "Campaña — Los Baldíos / Kalimdor (Orcos RoC)",
            Category = "Campañas RoC",
            CampaignScreenIndex = 24,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\BarrensBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Barrens-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Barrens-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Barrens-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Barrens-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Ashenvale",
            DisplayName = "Campaña — Vallefresno / Ashenvale (Elfos RoC)",
            Category = "Campañas RoC",
            CampaignScreenIndex = 34,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\AshenvaleBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Ashenvale-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Ashenvale-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Ashenvale-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\Ashenvale-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "AshenvaleExp",
            DisplayName = "Expansión — Vallefresno / Islas Quebradas (Centinelas TFT)",
            Category = "Campañas TFT",
            CampaignScreenIndex = 46,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\AshenvaleExpansionBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\AshenvaleExpansion-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\AshenvaleExpansion-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\AshenvaleExpansion-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\AshenvaleExpansion-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "DrownedRuins",
            DisplayName = "Expansión — Ruinas Hundidas / Tumba de Sargeras (TFT)",
            Category = "Campañas TFT",
            CampaignScreenIndex = 47,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\DrownedRuinsExpansionBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\DrownedRuinsExpansion-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\DrownedRuinsExpansion-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\DrownedRuinsExpansion-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\DrownedRuinsExpansion-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "LordaeronExp",
            DisplayName = "Expansión — Tierras de la Peste / Lordaeron (TFT)",
            Category = "Campañas TFT",
            CampaignScreenIndex = 52,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\LordaeronExpansionBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\LordaeronExpansion-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\LordaeronExpansion-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\LordaeronExpansion-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\LordaeronExpansion-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "OutlandExp",
            DisplayName = "Expansión — Terrallende / Outland (Elfos de Sangre TFT)",
            Category = "Campañas TFT",
            CampaignScreenIndex = 61,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\OutlandExpansionBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\OutlandExpansion-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\OutlandExpansion-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\OutlandExpansion-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\OutlandExpansion-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "IcecrownExp",
            DisplayName = "Expansión — Corona de Hielo / Icecrown (Trono Helado TFT)",
            Category = "Campañas TFT",
            CampaignScreenIndex = 72,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\IcecrownExpansionBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\IcecrownExpansion-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\IcecrownExpansion-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\IcecrownExpansion-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\IcecrownExpansion-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "BarrensExp",
            DisplayName = "Expansión — Durotar / Fundación de Orgrimmar (Rexxar TFT)",
            Category = "Campañas TFT",
            CampaignScreenIndex = 82,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Campaigns\BarrensExpansionBackground.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\BarrensExpansion-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\BarrensExpansion-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\BarrensExpansion-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Campaigns\BarrensExpansion-BotRight.blp"
        },
        new LoadingScreenPreset
        {
            Id = "Credits",
            DisplayName = "Especial — Pantalla de Créditos",
            Category = "Especial",
            CampaignScreenIndex = 44,
            MdxPath = @"UI\Glues\Loading\Backgrounds\Load-Credits\Load-Credits.mdx",
            TopLeftBlp = @"UI\Glues\Loading\Backgrounds\Load-Credits\Credits-Loading-TopLeft.blp",
            TopRightBlp = @"UI\Glues\Loading\Backgrounds\Load-Credits\Credits-Loading-TopRight.blp",
            BotLeftBlp = @"UI\Glues\Loading\Backgrounds\Load-Credits\Credits-Loading-BotLeft.blp",
            BotRightBlp = @"UI\Glues\Loading\Backgrounds\Load-Credits\Credits-Loading-BotRight.blp"
        }
    ];

    /// <summary>
    /// Une los 4 cuadrantes .blp de una pantalla de carga de Warcraft III (o del mapa actual) en un buffer BGRA 1024x768.
    /// </summary>
    public static byte[]? StitchQuadLoadingScreen(
        BlpIconService blpService,
        MapProject? project,
        string topLeftPath,
        string topRightPath,
        string botLeftPath,
        string botRightPath,
        out int outWidth,
        out int outHeight)
    {
        outWidth = 1024;
        outHeight = 768;

        bool hasTL = blpService.TryGetTexturePixels(topLeftPath, project, out var tlPx, out int tlW, out int tlH);
        bool hasTR = blpService.TryGetTexturePixels(topRightPath, project, out var trPx, out int trW, out int trH);
        bool hasBL = blpService.TryGetTexturePixels(botLeftPath, project, out var blPx, out int blW, out int blH);
        bool hasBR = blpService.TryGetTexturePixels(botRightPath, project, out var brPx, out int brW, out int brH);

        if (!hasTL && !hasTR && !hasBL && !hasBR)
            return null;

        var canvas = new byte[outWidth * outHeight * 4];
        for (int i = 3; i < canvas.Length; i += 4)
            canvas[i] = 255;

        if (hasTL) BlitScaledRegion(tlPx, tlW, tlH, canvas, outWidth, outHeight, 0, 0, 512, 512);
        if (hasTR) BlitScaledRegion(trPx, trW, trH, canvas, outWidth, outHeight, 512, 0, 512, 512);
        if (hasBL) BlitScaledRegion(blPx, blW, blH, canvas, outWidth, outHeight, 0, 512, 512, 256);
        if (hasBR) BlitScaledRegion(brPx, brW, brH, canvas, outWidth, outHeight, 512, 512, 512, 256);

        return canvas;
    }

    private static void BlitScaledRegion(
        byte[] src, int srcW, int srcH,
        byte[] dst, int dstTotalW, int dstTotalH,
        int dstX0, int dstY0, int dstRegionW, int dstRegionH)
    {
        for (int y = 0; y < dstRegionH; y++)
        {
            int dy = dstY0 + y;
            if (dy < 0 || dy >= dstTotalH) continue;
            float sy = ((y + 0.5f) / dstRegionH) * srcH - 0.5f;

            for (int x = 0; x < dstRegionW; x++)
            {
                int dx = dstX0 + x;
                if (dx < 0 || dx >= dstTotalW) continue;
                float sx = ((x + 0.5f) / dstRegionW) * srcW - 0.5f;

                SampleBilinear(src, srcW, srcH, sx, sy, out byte b, out byte g, out byte r, out byte a);
                int di = (dy * dstTotalW + dx) * 4;
                dst[di] = b;
                dst[di + 1] = g;
                dst[di + 2] = r;
                dst[di + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Compone una imagen a la resolución destino aplicando el modo de encuadre (Fill/Fit/Stretch), zoom, brillo, contraste y marco/viñeta.
    /// </summary>
    public static byte[] ComposeImage(
        byte[] srcBgra,
        int srcW,
        int srcH,
        int dstW,
        int dstH,
        MediaComposeOptions opts)
    {
        var dst = new byte[dstW * dstH * 4];
        float zoom = Math.Clamp(opts.ZoomPercent / 100f, 0.5f, 2.5f);
        float contrastFactor = MathF.Pow((100f + Math.Clamp(opts.Contrast, -80, 80)) / 100f, 2f);
        float brightOffset = opts.Brightness * 2.0f;

        float srcAspect = (float)srcW / Math.Max(1, srcH);
        float dstAspect = (float)dstW / Math.Max(1, dstH);

        float scaleX = 1f;
        float scaleY = 1f;

        if (opts.FitMode == MediaFitMode.FillCrop)
        {
            if (srcAspect > dstAspect)
                scaleX = dstAspect / srcAspect;
            else
                scaleY = srcAspect / dstAspect;
        }
        else if (opts.FitMode == MediaFitMode.FitLetterbox)
        {
            if (srcAspect > dstAspect)
                scaleY = srcAspect / dstAspect;
            else
                scaleX = dstAspect / srcAspect;
        }

        scaleX /= zoom;
        scaleY /= zoom;

        for (int y = 0; y < dstH; y++)
        {
            float ny = (y + 0.5f) / dstH - 0.5f;
            float v = 0.5f + ny * scaleY;

            for (int x = 0; x < dstW; x++)
            {
                float nx = (x + 0.5f) / dstW - 0.5f;
                float u = 0.5f + nx * scaleX;

                int di = (y * dstW + x) * 4;
                if (u < 0f || u > 1f || v < 0f || v > 1f)
                {
                    dst[di] = 12;
                    dst[di + 1] = 12;
                    dst[di + 2] = 14;
                    dst[di + 3] = 255;
                    continue;
                }

                float sx = u * srcW - 0.5f;
                float sy = v * srcH - 0.5f;
                SampleBilinear(srcBgra, srcW, srcH, sx, sy, out byte sb, out byte sg, out byte sr, out _);

                float r = ((sr - 128f) * contrastFactor + 128f) + brightOffset;
                float g = ((sg - 128f) * contrastFactor + 128f) + brightOffset;
                float b = ((sb - 128f) * contrastFactor + 128f) + brightOffset;

                if (opts.BottomVignette && y > dstH * 0.58f)
                {
                    float t = (y - dstH * 0.58f) / (dstH * 0.42f);
                    float shade = 1f - (t * t * 0.58f);
                    r *= shade;
                    g *= shade;
                    b *= shade;
                }

                dst[di] = (byte)Math.Clamp(b, 0f, 255f);
                dst[di + 1] = (byte)Math.Clamp(g, 0f, 255f);
                dst[di + 2] = (byte)Math.Clamp(r, 0f, 255f);
                dst[di + 3] = 255;
            }
        }

        if (opts.FrameStyle != MapPreviewFrameStyle.None)
        {
            ApplyPreviewFrame(dst, dstW, dstH, opts.FrameStyle);
        }

        return dst;
    }

    private static void ApplyPreviewFrame(byte[] bgra, int w, int h, MapPreviewFrameStyle style)
    {
        int border = Math.Max(3, Math.Min(w, h) / 28);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int edge = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));
                int i = (y * w + x) * 4;

                if (style == MapPreviewFrameStyle.SoftVignette)
                {
                    int vig = Math.Max(6, Math.Min(w, h) / 10);
                    if (edge < vig)
                    {
                        float f = MathF.Pow((float)edge / vig, 1.2f);
                        bgra[i] = (byte)(bgra[i] * f);
                        bgra[i + 1] = (byte)(bgra[i + 1] * f);
                        bgra[i + 2] = (byte)(bgra[i + 2] * f);
                    }
                    continue;
                }

                if (edge > border) continue;

                if (edge == 0)
                {
                    bgra[i] = 12; bgra[i + 1] = 14; bgra[i + 2] = 18; bgra[i + 3] = 255;
                    continue;
                }
                if (edge == border)
                {
                    bgra[i] = (byte)(bgra[i] * 0.35f);
                    bgra[i + 1] = (byte)(bgra[i + 1] * 0.35f);
                    bgra[i + 2] = (byte)(bgra[i + 2] * 0.35f);
                    continue;
                }

                bool lit = (y <= border && x < w - y) || (x <= border && y < h - x);
                if (style == MapPreviewFrameStyle.ClassicGoldBevel)
                {
                    bgra[i] = lit ? (byte)72 : (byte)24;
                    bgra[i + 1] = lit ? (byte)184 : (byte)86;
                    bgra[i + 2] = lit ? (byte)236 : (byte)124;
                    bgra[i + 3] = 255;
                }
                else if (style == MapPreviewFrameStyle.DarkSteelBevel)
                {
                    byte v = lit ? (byte)168 : (byte)64;
                    bgra[i] = (byte)Math.Min(255, v + 8);
                    bgra[i + 1] = v;
                    bgra[i + 2] = v;
                    bgra[i + 3] = 255;
                }
            }
        }
    }

    /// <summary>
    /// Genera el gráfico clásico del signo de interrogación (?) dorado de sala de espera de Warcraft III.
    /// </summary>
    public static byte[] CreateQuestionMarkPreviewPixels(int size = 128)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, size, size);
            var bgBrush = new RadialGradientBrush(
                Color.FromRgb(26, 38, 62),
                Color.FromRgb(8, 12, 20))
            {
                RadiusX = 0.65,
                RadiusY = 0.65
            };
            dc.DrawRectangle(bgBrush, new Pen(new SolidColorBrush(Color.FromRgb(185, 146, 52)), Math.Max(2, size / 32.0)), rect);

            double pad = size * 0.12;
            dc.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(90, 12, 18, 30)),
                new Pen(new SolidColorBrush(Color.FromRgb(140, 108, 38)), Math.Max(1.5, size / 48.0)),
                new Point(size * 0.5, size * 0.5),
                size * 0.36,
                size * 0.36);

            var ftShadow = new FormattedText(
                "?",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.62,
                new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                1.0);

            var goldGrad = new LinearGradientBrush(
                Color.FromRgb(255, 236, 130),
                Color.FromRgb(198, 132, 24),
                90.0);

            var ftMain = new FormattedText(
                "?",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.62,
                goldGrad,
                1.0);

            double tx = (size - ftMain.Width) * 0.5;
            double ty = (size - ftMain.Height) * 0.46;
            dc.DrawText(ftShadow, new Point(tx + 2, ty + 3));
            dc.DrawText(ftMain, new Point(tx, ty));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[size * size * 4];
        rtb.CopyPixels(px, size * 4, 0);
        for (int i = 3; i < px.Length; i += 4)
            px[i] = 255;
        return px;
    }

    /// <summary>
    /// Codifica un buffer BGRA en formato TGA True-Color sin compresión (24-bit BGR o 32-bit BGRA),
    /// estándar oficial de Warcraft III 1.27b para war3mapPreview.tga y FullScreen.tga.
    /// </summary>
    public static byte[] EncodeTga(byte[] bgra, int width, int height, bool use24Bit = true)
    {
        int bytesPerPx = use24Bit ? 3 : 4;
        var outBytes = new byte[18 + width * height * bytesPerPx];
        outBytes[2] = 2; // Uncompressed True-Color
        outBytes[12] = (byte)(width & 0xFF);
        outBytes[13] = (byte)((width >> 8) & 0xFF);
        outBytes[14] = (byte)(height & 0xFF);
        outBytes[15] = (byte)((height >> 8) & 0xFF);
        outBytes[16] = (byte)(bytesPerPx * 8);
        outBytes[17] = use24Bit ? (byte)0x00 : (byte)0x08; // Bottom-left origin

        int dst = 18;
        for (int y = height - 1; y >= 0; y--)
        {
            int rowStart = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = rowStart + x * 4;
                outBytes[dst++] = bgra[s];     // B
                outBytes[dst++] = bgra[s + 1]; // G
                outBytes[dst++] = bgra[s + 2]; // R
                if (!use24Bit)
                    outBytes[dst++] = bgra[s + 3]; // A
            }
        }
        return outBytes;
    }

    public static byte[] GetSingleTextureLoadingScreenMdx(bool useTgaExtension = false)
    {
        var bytes = Convert.FromBase64String(SingleMdxBase64);
        if (useTgaExtension)
        {
            ReplaceAsciiInBytes(bytes, "FullScreen.blp", "FullScreen.tga");
        }
        return bytes;
    }

    public static byte[] GetQuadTextureLoadingScreenMdx()
    {
        return Convert.FromBase64String(QuadMdxBase64);
    }

    private static void ReplaceAsciiInBytes(byte[] buffer, string oldStr, string newStr)
    {
        var oldBytes = Encoding.ASCII.GetBytes(oldStr);
        var newBytes = Encoding.ASCII.GetBytes(newStr);
        if (oldBytes.Length != newBytes.Length) return;

        for (int i = 0; i <= buffer.Length - oldBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < oldBytes.Length; j++)
            {
                if (buffer[i + j] != oldBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                Buffer.BlockCopy(newBytes, 0, buffer, i, newBytes.Length);
                return;
            }
        }
    }

    public static byte[] ExtractSubRegion(byte[] src, int srcW, int srcH, int rx, int ry, int rw, int rh)
    {
        var dst = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
        {
            int sy = Math.Clamp(ry + y, 0, srcH - 1);
            Buffer.BlockCopy(src, (sy * srcW + rx) * 4, dst, y * rw * 4, rw * 4);
        }
        return dst;
    }

    public static void UpsertMapImportFile(MapProject project, string mpqPath, byte[] data, string statusLabel)
    {
        project.RawMpqFiles[mpqPath] = data;
        var existing = project.Imports.FirstOrDefault(i => i.MpqPath.Equals(mpqPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Data = data;
            existing.SizeBytes = data.Length;
            existing.IsPendingWrite = true;
            existing.IsDeleted = false;
            existing.Status = statusLabel;
        }
        else
        {
            project.Imports.Add(new W3ImportedFile
            {
                MpqPath = mpqPath,
                Data = data,
                SizeBytes = data.Length,
                IsPendingWrite = true,
                IsDeleted = false,
                Status = statusLabel
            });
        }
    }

    public static void RemoveMapImportFileIfPresent(MapProject project, string mpqPath)
    {
        project.RawMpqFiles.Remove(mpqPath);
        var existing = project.Imports.FirstOrDefault(i => i.MpqPath.Equals(mpqPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsDeleted = true;
            existing.IsPendingWrite = false;
        }
    }

    private static void SampleBilinear(byte[] src, int w, int h, float x, float y, out byte b, out byte g, out byte r, out byte a)
    {
        x = Math.Clamp(x, 0f, w - 1f);
        y = Math.Clamp(y, 0f, h - 1f);
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int x1 = Math.Min(w - 1, x0 + 1);
        int y1 = Math.Min(h - 1, y0 + 1);
        float fx = x - x0;
        float fy = y - y0;

        int i00 = (y0 * w + x0) * 4;
        int i10 = (y0 * w + x1) * 4;
        int i01 = (y1 * w + x0) * 4;
        int i11 = (y1 * w + x1) * 4;

        float w00 = (1f - fx) * (1f - fy);
        float w10 = fx * (1f - fy);
        float w01 = (1f - fx) * fy;
        float w11 = fx * fy;

        b = (byte)Math.Clamp(src[i00] * w00 + src[i10] * w10 + src[i01] * w01 + src[i11] * w11, 0f, 255f);
        g = (byte)Math.Clamp(src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11, 0f, 255f);
        r = (byte)Math.Clamp(src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11, 0f, 255f);
        a = (byte)Math.Clamp(src[i00 + 3] * w00 + src[i10 + 3] * w10 + src[i01 + 3] * w01 + src[i11 + 3] * w11, 0f, 255f);
    }
}
