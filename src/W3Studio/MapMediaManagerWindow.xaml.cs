using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using War3Net.Build.Common;
using War3Net.Build.Info;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class MapMediaManagerWindow : Window
{
    private sealed class PreviewTextureItem
    {
        public string MpqPath { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }

    private readonly BlpIconService _blpService;
    private MapProject? _project;
    private BitmapSource? _liveMinimapBitmap;
    private readonly Action<string>? _onAppliedToMap;

    private readonly List<LoadingScreenPreset> _allPresets;
    private List<PreviewTextureItem> _allPreviewTextures = new();

    // Estado de imagen origen en Pestaña 1 (Loading Screen)
    private byte[]? _loadingSrcPixels;
    private int _loadingSrcW;
    private int _loadingSrcH;
    private string _loadingSrcLabel = "Predeterminada";
    private LoadingScreenPreset? _selectedOfficialPreset;

    // Estado de imagen origen en Pestaña 2 (MapPreview)
    private byte[]? _previewSrcPixels;
    private int _previewSrcW;
    private int _previewSrcH;
    private string _previewSrcLabel = "Minimapa / ? por defecto";
    private bool _isQuestionMarkSelected;

    private bool _suppressUi;
    private static FontFamily? _cachedWc3Font;

    public MapMediaManagerWindow(
        BlpIconService blpService,
        MapProject? project,
        BitmapSource? liveMinimapBitmap = null,
        Action<string>? onAppliedToMap = null,
        int initialTabIndex = 0)
    {
        _suppressUi = true;
        InitializeComponent();
        _blpService = blpService;
        _project = project;
        _liveMinimapBitmap = liveMinimapBitmap;
        _onAppliedToMap = onAppliedToMap;
        _allPresets = MapMediaService.GetOfficialLoadingScreenPresets();

        ApplyWc3FrizFontToPreview();

        if (initialTabIndex >= 0 && initialTabIndex < TabsMedia.Items.Count)
            TabsMedia.SelectedIndex = initialTabIndex;

        LstOfficialLoadingPresets.ItemsSource = _allPresets;
        _suppressUi = false;

        LoadWc3TextureCatalog();
        SetProject(project, liveMinimapBitmap);
    }

    private void ApplyWc3FrizFontToPreview()
    {
        try
        {
            if (_cachedWc3Font == null)
            {
                var fontBytes = _blpService.FindRawFileBytes(@"Fonts\FRIZQT__.TTF", _project)
                                ?? _blpService.FindRawFileBytes(@"Fonts\frizqt__.ttf", _project);
                if (fontBytes != null && fontBytes.Length > 1024)
                {
                    var fontDir = Path.Combine(Path.GetTempPath(), "W3Studio_Fonts");
                    Directory.CreateDirectory(fontDir);
                    var fontPath = Path.Combine(fontDir, "FRIZQT__.TTF");
                    if (!File.Exists(fontPath) || new FileInfo(fontPath).Length != fontBytes.Length)
                        File.WriteAllBytes(fontPath, fontBytes);

                    foreach (var fam in Fonts.GetFontFamilies(fontPath))
                    {
                        _cachedWc3Font = fam;
                        break;
                    }
                }
            }

            if (_cachedWc3Font != null)
            {
                TxtSimLoadingTitle.FontFamily = _cachedWc3Font;
                TxtSimLoadingSubtitle.FontFamily = _cachedWc3Font;
                TxtSimLoadingBody.FontFamily = _cachedWc3Font;
            }
        }
        catch
        {
            // Fallback font definido en XAML
        }
    }

    public void SelectTab(int tabIndex)
    {
        if (tabIndex >= 0 && tabIndex < TabsMedia.Items.Count)
            TabsMedia.SelectedIndex = tabIndex;
    }

    public void SetProject(MapProject? project, BitmapSource? liveMinimapBitmap = null)
    {
        _project = project;
        if (liveMinimapBitmap != null)
            _liveMinimapBitmap = liveMinimapBitmap;

        _suppressUi = true;
        bool hasMap = project != null;
        BtnApplyLoadingToMap.IsEnabled = hasMap;
        BtnApplyPreviewToMap.IsEnabled = hasMap;
        BtnRemoveCustomPreview.IsEnabled = hasMap;
        BtnUseMinimapForLoading.IsEnabled = hasMap || _liveMinimapBitmap != null;
        BtnUseMinimapForPreview.IsEnabled = hasMap || _liveMinimapBitmap != null;

        if (project != null)
        {
            var cleanMapName = StripColorCodes(string.IsNullOrWhiteSpace(project.MapName) ? project.FileName : project.MapName);
            TxtMapContextBadge.Text = $"Mapa activo: {cleanMapName} ({project.FileName})";
            TxtLobbySimMapName.Text = cleanMapName;
            TxtLobbySimPlayers.Text = string.IsNullOrWhiteSpace(project.SuggestedPlayers) ? "Cualquier número" : StripColorCodes(project.SuggestedPlayers);
            TxtLobbySimAuthor.Text = string.IsNullOrWhiteSpace(project.MapAuthor) ? "Desconocido" : StripColorCodes(project.MapAuthor);

            var info = project.MapInfo;
            TxtLoadingTitle.Text = info != null ? project.ResolveTrigStr(info.LoadingScreenTitle) : cleanMapName;
            TxtLoadingSubtitle.Text = info != null ? project.ResolveTrigStr(info.LoadingScreenSubtitle) : project.SuggestedPlayers;
            TxtLoadingDescription.Text = info != null ? project.ResolveTrigStr(info.LoadingScreenText) : project.MapDescription;

            if (string.IsNullOrWhiteSpace(TxtLoadingTitle.Text))
                TxtLoadingTitle.Text = cleanMapName;
            if (string.IsNullOrWhiteSpace(TxtLoadingDescription.Text))
                TxtLoadingDescription.Text = StripColorCodes(project.MapDescription);

            ChkHideMinimapFlag.IsChecked = info != null && info.MapFlags.HasFlag(MapFlags.HideMinimapInPreviewScreens);

            LoadInitialLoadingScreenFromMap(project);
            LoadInitialMapPreviewFromMap(project);
        }
        else
        {
            TxtMapContextBadge.Text = "Sin mapa abierto (Modo diseño y exportación a disco)";
            TxtLobbySimMapName.Text = "Mi Mapa Personalizado";
            TxtLobbySimPlayers.Text = "2 - 12 Jugadores";
            TxtLobbySimAuthor.Text = "Autor";
            TxtLoadingTitle.Text = "Mi Mapa de Warcraft III";
            TxtLoadingSubtitle.Text = "Versión 1.0";
            TxtLoadingDescription.Text = "Bienvenido al mapa. Prepárate para la batalla.";

            // Cargar preset genérico por defecto
            SelectOfficialPreset(_allPresets[0], switchRadioToPreset: false);
            UseQuestionMarkForPreview();
        }
        _suppressUi = false;

        UpdateSimulatedLoadingHudTexts();
        RenderLoadingScreenLivePreview();
        RenderMapPreviewLive();
    }

    private void LoadInitialLoadingScreenFromMap(MapProject project)
    {
        // 1. ¿Tiene FullScreen.blp o FullScreen.tga importado?
        if (_blpService.TryGetTexturePixels("FullScreen.blp", project, out var fsPx, out int fsW, out int fsH) ||
            _blpService.TryGetTexturePixels("FullScreen.tga", project, out fsPx, out fsW, out fsH) ||
            _blpService.TryGetTexturePixels("LoadingScreen.blp", project, out fsPx, out fsW, out fsH) ||
            _blpService.TryGetTexturePixels("LoadingScreen.tga", project, out fsPx, out fsW, out fsH))
        {
            _loadingSrcPixels = fsPx;
            _loadingSrcW = fsW;
            _loadingSrcH = fsH;
            _loadingSrcLabel = $"FullScreen actual del mapa ({fsW}×{fsH})";
            RbLoadCustomImage.IsChecked = true;
            CmbLoadingQuality.SelectedIndex = fsW >= 512 ? 1 : 2;
            return;
        }

        // 2. ¿Tiene 4 cuadrantes LoadingScreenTL/TR/BL/BR.blp importados?
        var stitched = MapMediaService.StitchQuadLoadingScreen(
            _blpService,
            project,
            "LoadingScreenTL.blp",
            "LoadingScreenTR.blp",
            "LoadingScreenBL.blp",
            "LoadingScreenBR.blp",
            out int stW,
            out int stH);
        if (stitched != null)
        {
            _loadingSrcPixels = stitched;
            _loadingSrcW = stW;
            _loadingSrcH = stH;
            _loadingSrcLabel = "Pantalla 4-Cuadrantes HQ actual del mapa (1024×768)";
            RbLoadCustomImage.IsChecked = true;
            CmbLoadingQuality.SelectedIndex = 0;
            return;
        }

        // 3. ¿Coincide con algún preset oficial por ruta custom o por índice de campaña en war3map.w3i?
        var info = project.MapInfo;
        var path = info?.LoadingScreenPath ?? string.Empty;
        LoadingScreenPreset? matchedPreset = null;

        if (!string.IsNullOrWhiteSpace(path))
        {
            matchedPreset = _allPresets.FirstOrDefault(p =>
                p.MdxPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(p.MdxPath).Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase));
        }
        else if (info != null)
        {
            int activeIdx = info.FormatVersion >= MapInfoFormatVersion.v25
                ? info.LoadingScreenBackgroundNumber
                : info.CampaignBackgroundNumber;

            if (activeIdx >= 0)
            {
                matchedPreset = _allPresets.FirstOrDefault(p => p.CampaignScreenIndex == activeIdx)
                    ?? _allPresets.FirstOrDefault(p => string.Equals(p.Id, MapCampaignIndexToPresetId(activeIdx), StringComparison.OrdinalIgnoreCase));
            }
        }

        SelectOfficialPreset(matchedPreset ?? _allPresets[0], switchRadioToPreset: matchedPreset != null);
    }

    private static string MapCampaignIndexToPresetId(int idx) => idx switch
    {
        0 or 1 => "Tutorial",
        2 or 3 or (>= 5 and <= 10) or 15 or 20 or 21 or 26 => "Lordaeron",
        4 or 14 or 22 or 23 => "Dalaran",
        >= 11 and <= 13 => "Northrend",
        >= 16 and <= 19 => "Quelthalas",
        24 or 25 or (>= 27 and <= 33) => "Barrens",
        >= 34 and <= 43 => "Ashenvale",
        44 => "Credits",
        45 => "Generic",
        46 or 50 => "AshenvaleExp",
        >= 47 and <= 49 or 51 => "DrownedRuins",
        (>= 52 and <= 60) or (>= 67 and <= 71) or 73 or 74 => "LordaeronExp",
        >= 61 and <= 66 => "OutlandExp",
        72 or (>= 75 and <= 80) => "IcecrownExp",
        >= 81 and <= 97 => "BarrensExp",
        _ => "Generic"
    };

    private void LoadInitialMapPreviewFromMap(MapProject project)
    {
        if (_blpService.TryGetTexturePixels("war3mapPreview.tga", project, out var pvPx, out int pvW, out int pvH) ||
            _blpService.TryGetTexturePixels("war3mapPreview.blp", project, out pvPx, out pvW, out pvH))
        {
            _previewSrcPixels = pvPx;
            _previewSrcW = pvW;
            _previewSrcH = pvH;
            _previewSrcLabel = $"war3mapPreview actual del mapa ({pvW}×{pvH})";
            _isQuestionMarkSelected = false;
            CmbPreviewFrameStyle.SelectedIndex = 0; // Ya viene con su propio marco si fue guardado antes
            return;
        }

        if (project.MapInfo != null && project.MapInfo.MapFlags.HasFlag(MapFlags.HideMinimapInPreviewScreens))
        {
            UseQuestionMarkForPreview();
            return;
        }

        if (!TryCaptureMinimapAsPreviewSource())
        {
            UseQuestionMarkForPreview();
        }
    }

    private void SelectOfficialPreset(LoadingScreenPreset preset, bool switchRadioToPreset)
    {
        _selectedOfficialPreset = preset;
        LstOfficialLoadingPresets.SelectedItem = preset;
        if (switchRadioToPreset)
            RbLoadOfficialPreset.IsChecked = true;

        var stitched = MapMediaService.StitchQuadLoadingScreen(
            _blpService,
            _project,
            preset.TopLeftBlp,
            preset.TopRightBlp,
            preset.BotLeftBlp,
            preset.BotRightBlp,
            out int w,
            out int h);

        if (stitched != null)
        {
            _loadingSrcPixels = stitched;
            _loadingSrcW = w;
            _loadingSrcH = h;
            _loadingSrcLabel = $"Warcraft III Oficial: {preset.DisplayName}";
        }
    }

    private void LoadWc3TextureCatalog()
    {
        var paths = _blpService.GetAvailableIconPaths(_project);
        _allPreviewTextures = paths
            .Select(p => new PreviewTextureItem
            {
                MpqPath = p,
                DisplayName = Path.GetFileName(p)
            })
            .ToList();
        ApplyPreviewTextureFilter();
    }

    private void ApplyPreviewTextureFilter()
    {
        if (LstWc3PreviewTextures == null) return;
        var q = TxtFilterPreviewIcons?.Text?.Trim() ?? string.Empty;
        IEnumerable<PreviewTextureItem> list = _allPreviewTextures;
        if (!string.IsNullOrEmpty(q))
        {
            list = list.Where(i =>
                i.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                i.MpqPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = list.Take(1000).ToList();
        LstWc3PreviewTextures.ItemsSource = filtered;
        if (TxtPreviewIconsCount != null)
            TxtPreviewIconsCount.Text = $"Mostrando {filtered.Count:N0} de {_allPreviewTextures.Count:N0} texturas";
    }

    // ==================== EVENTOS PESTAÑA 1: LOADING SCREEN ====================

    private void TxtFilterPresets_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = TxtFilterPresets.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            LstOfficialLoadingPresets.ItemsSource = _allPresets;
            return;
        }
        LstOfficialLoadingPresets.ItemsSource = _allPresets
            .Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void LstOfficialLoadingPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || LstOfficialLoadingPresets.SelectedItem is not LoadingScreenPreset preset)
            return;

        _suppressUi = true;
        RbLoadOfficialPreset.IsChecked = true;
        SelectOfficialPreset(preset, switchRadioToPreset: false);
        _suppressUi = false;

        UpdateLoadingUiModeState();
        RenderLoadingScreenLivePreview();
    }

    private void BtnConvertPresetToCustom_Click(object sender, RoutedEventArgs e)
    {
        if (LstOfficialLoadingPresets.SelectedItem is LoadingScreenPreset preset)
        {
            _suppressUi = true;
            SelectOfficialPreset(preset, switchRadioToPreset: false);
            RbLoadCustomImage.IsChecked = true;
            _loadingSrcLabel = $"Basado en: {preset.DisplayName} (Editable)";
            _suppressUi = false;

            UpdateLoadingUiModeState();
            RenderLoadingScreenLivePreview();
            TxtStatus.Text = $"Pantalla '{preset.DisplayName}' cargada como imagen editable. Ahora puedes ajustar zoom, brillo, contraste o calidad.";
        }
    }

    private void LoadingModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUi || !IsLoaded) return;
        UpdateLoadingUiModeState();
        RenderLoadingScreenLivePreview();
    }

    private void UpdateLoadingUiModeState()
    {
        if (PnlCustomLoadingQualityOptions == null) return;
        bool isCustom = RbLoadCustomImage.IsChecked == true;
        PnlCustomLoadingQualityOptions.IsEnabled = isCustom;
        PnlCustomLoadingQualityOptions.Opacity = isCustom ? 1.0 : 0.65;
    }

    private void BtnPickLoadingImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Elegir Imagen para Pantalla de Carga (Loading Screen)",
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.blp)|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.blp|Todos los archivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadExternalImageForLoadingScreen(dlg.FileName);
        }
    }

    private void LoadExternalImageForLoadingScreen(string filePath)
    {
        try
        {
            var bmp = IconProcessorService.LoadExternalImage(filePath, _blpService);
            var px = IconProcessorService.ExtractBgraPixels(bmp, out int w, out int h);
            if (px == null)
            {
                MessageBox.Show(this, "No se pudo leer la imagen seleccionada.", "Loading Screen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _loadingSrcPixels = px;
            _loadingSrcW = w;
            _loadingSrcH = h;
            _loadingSrcLabel = $"Imagen de usuario: {Path.GetFileName(filePath)} ({w}×{h})";

            _suppressUi = true;
            RbLoadCustomImage.IsChecked = true;
            _suppressUi = false;

            UpdateLoadingUiModeState();
            RenderLoadingScreenLivePreview();
            TxtStatus.Text = $"Imagen de pantalla de carga cargada: {Path.GetFileName(filePath)} ({w}×{h}).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al cargar la imagen:\n{ex.Message}", "Loading Screen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnUseMinimapForLoading_Click(object sender, RoutedEventArgs e)
    {
        if (TryExtractMinimapPixels(out var px, out int w, out int h))
        {
            _loadingSrcPixels = px;
            _loadingSrcW = w;
            _loadingSrcH = h;
            _loadingSrcLabel = $"Minimapa del Mapa Actual ({w}×{h})";
            _suppressUi = true;
            RbLoadCustomImage.IsChecked = true;
            _suppressUi = false;
            UpdateLoadingUiModeState();
            RenderLoadingScreenLivePreview();
            TxtStatus.Text = "Minimapa actual aplicado como base para la Pantalla de Carga.";
        }
        else
        {
            MessageBox.Show(this, "No hay un minimapa disponible en este momento. Abre primero un mapa.", "Loading Screen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void LoadingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || !IsLoaded) return;
        RenderLoadingScreenLivePreview();
    }

    private void LoadingEditOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUi || !IsLoaded) return;
        if (GridLoadingSimulatedHud != null)
            GridLoadingSimulatedHud.Visibility = ChkShowLoadingOverlayText.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RenderLoadingScreenLivePreview();
    }

    private void LoadingSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || !IsLoaded) return;
        if (TxtLoadingZoomVal != null && SldLoadingZoom != null)
            TxtLoadingZoomVal.Text = $"{(int)SldLoadingZoom.Value}%";
        if (TxtLoadingBrightVal != null && SldLoadingBrightness != null)
            TxtLoadingBrightVal.Text = ((int)SldLoadingBrightness.Value).ToString();
        if (TxtLoadingContrastVal != null && SldLoadingContrast != null)
            TxtLoadingContrastVal.Text = ((int)SldLoadingContrast.Value).ToString();
        RenderLoadingScreenLivePreview();
    }

    private void BtnResetLoadingAdjustments_Click(object sender, RoutedEventArgs e)
    {
        _suppressUi = true;
        CmbLoadingFitMode.SelectedIndex = 0;
        SldLoadingZoom.Value = 100;
        SldLoadingBrightness.Value = 0;
        SldLoadingContrast.Value = 0;
        ChkLoadingBottomVignette.IsChecked = true;
        TxtLoadingZoomVal.Text = "100%";
        TxtLoadingBrightVal.Text = "0";
        TxtLoadingContrastVal.Text = "0";
        _suppressUi = false;
        RenderLoadingScreenLivePreview();
    }

    private void LoadingText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUi || !IsLoaded) return;
        UpdateSimulatedLoadingHudTexts();
    }

    private void UpdateSimulatedLoadingHudTexts()
    {
        if (TxtSimLoadingTitle == null) return;
        var titleText = StripColorCodes(TxtLoadingTitle.Text);
        var subtitleText = StripColorCodes(TxtLoadingSubtitle.Text);
        TxtSimLoadingTitle.Text = titleText;
        TxtSimLoadingTitle.Visibility = string.IsNullOrWhiteSpace(titleText) ? Visibility.Collapsed : Visibility.Visible;
        TxtSimLoadingSubtitle.Text = subtitleText;
        TxtSimLoadingSubtitle.Visibility = string.IsNullOrWhiteSpace(subtitleText) ? Visibility.Collapsed : Visibility.Visible;
        TxtSimLoadingBody.Text = StripColorCodes(TxtLoadingDescription.Text);
    }

    private MediaComposeOptions BuildLoadingComposeOptions()
    {
        if (RbLoadOfficialPreset.IsChecked == true)
        {
            return new MediaComposeOptions
            {
                FitMode = MediaFitMode.Stretch,
                ZoomPercent = 100,
                Brightness = 0,
                Contrast = 0,
                BottomVignette = false,
                FrameStyle = MapPreviewFrameStyle.None
            };
        }

        return new MediaComposeOptions
        {
            FitMode = CmbLoadingFitMode.SelectedIndex switch
            {
                1 => MediaFitMode.FitLetterbox,
                2 => MediaFitMode.Stretch,
                _ => MediaFitMode.FillCrop
            },
            ZoomPercent = (int)(SldLoadingZoom?.Value ?? 100),
            Brightness = (int)(SldLoadingBrightness?.Value ?? 0),
            Contrast = (int)(SldLoadingContrast?.Value ?? 0),
            BottomVignette = ChkLoadingBottomVignette?.IsChecked == true,
            FrameStyle = MapPreviewFrameStyle.None
        };
    }

    private LoadingScreenQualityMode GetSelectedLoadingQuality() =>
        CmbLoadingQuality?.SelectedIndex switch
        {
            0 => LoadingScreenQualityMode.HighQuad1024x768,
            2 => LoadingScreenQualityMode.Compact256x256,
            3 => LoadingScreenQualityMode.UncompressedTga,
            _ => LoadingScreenQualityMode.Standard512x512
        };

    private void RenderLoadingScreenLivePreview()
    {
        if (_loadingSrcPixels == null || ImgLoadingPreview == null)
            return;

        bool isOfficial = RbLoadOfficialPreset.IsChecked == true;
        var quality = GetSelectedLoadingQuality();
        var opts = BuildLoadingComposeOptions();

        // Renderizar a la resolución real de salida para que la vista previa muestre la nitidez exacta
        int previewW = 512;
        int previewH = 512;
        if (isOfficial || quality == LoadingScreenQualityMode.HighQuad1024x768)
        {
            previewW = 800;
            previewH = 600;
        }
        else if (quality == LoadingScreenQualityMode.Compact256x256)
        {
            previewW = 256;
            previewH = 256;
        }

        var composed = MapMediaService.ComposeImage(_loadingSrcPixels, _loadingSrcW, _loadingSrcH, previewW, previewH, opts);
        ImgLoadingPreview.Source = IconProcessorService.PixelsToBitmapSource(composed, previewW, previewH);

        TxtLoadingSourceInfo.Text = $"Origen: {_loadingSrcLabel}";
        bool mips = ChkLoadingMipmaps?.IsChecked == true;

        if (isOfficial && _selectedOfficialPreset != null)
        {
            TxtLoadingOutputFilesInfo.Text = _selectedOfficialPreset.CampaignScreenIndex >= 0
                ? $"war3map.w3i CampaignScreen #{_selectedOfficialPreset.CampaignScreenIndex} (Nativo de Warcraft III, 0 archivos extra)"
                : $"Ruta en war3map.w3i: {_selectedOfficialPreset.MdxPath} (Nativo de Warcraft III, 0 archivos extra)";
            TxtLoadingEstimatedSize.Text = "0 KB (Nativo)";
        }
        else
        {
            switch (quality)
            {
                case LoadingScreenQualityMode.HighQuad1024x768:
                    TxtLoadingOutputFilesInfo.Text = "Archivos MPQ: LoadingScreen.mdx + LoadingScreenTL/TR (512×512) + BL/BR (512×256) .blp";
                    TxtLoadingEstimatedSize.Text = mips ? "~1,050 KB" : "~790 KB";
                    break;
                case LoadingScreenQualityMode.Standard512x512:
                    TxtLoadingOutputFilesInfo.Text = "Archivos MPQ: LoadingScreen.mdx (1.3 KB) + FullScreen.blp (512×512 Paletted)";
                    TxtLoadingEstimatedSize.Text = mips ? "~350 KB" : "~264 KB";
                    break;
                case LoadingScreenQualityMode.Compact256x256:
                    TxtLoadingOutputFilesInfo.Text = "Archivos MPQ: LoadingScreen.mdx (1.3 KB) + FullScreen.blp (256×256 Ligera)";
                    TxtLoadingEstimatedSize.Text = mips ? "~88 KB" : "~66 KB";
                    break;
                case LoadingScreenQualityMode.UncompressedTga:
                    TxtLoadingOutputFilesInfo.Text = "Archivos MPQ: LoadingScreen.mdx (1.3 KB) + FullScreen.tga (512×512 RGB 24-bit)";
                    TxtLoadingEstimatedSize.Text = "769 KB";
                    break;
            }
        }
    }

    private Dictionary<string, byte[]> GenerateCustomLoadingScreenFiles(out string summaryLabel)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var opts = BuildLoadingComposeOptions();
        var quality = GetSelectedLoadingQuality();
        bool includeMips = ChkLoadingMipmaps.IsChecked == true;

        if (_loadingSrcPixels == null)
        {
            summaryLabel = "Sin imagen";
            return files;
        }

        switch (quality)
        {
            case LoadingScreenQualityMode.HighQuad1024x768:
            {
                var composed1024x768 = MapMediaService.ComposeImage(_loadingSrcPixels, _loadingSrcW, _loadingSrcH, 1024, 768, opts);
                var tl = MapMediaService.ExtractSubRegion(composed1024x768, 1024, 768, 0, 0, 512, 512);
                var tr = MapMediaService.ExtractSubRegion(composed1024x768, 1024, 768, 512, 0, 512, 512);
                var bl = MapMediaService.ExtractSubRegion(composed1024x768, 1024, 768, 0, 512, 512, 256);
                var br = MapMediaService.ExtractSubRegion(composed1024x768, 1024, 768, 512, 512, 512, 256);

                files["LoadingScreen.mdx"] = MapMediaService.GetQuadTextureLoadingScreenMdx();
                files["LoadingScreenTL.blp"] = IconProcessorService.EncodeBlp1Paletted(tl, 512, 512, includeMips);
                files["LoadingScreenTR.blp"] = IconProcessorService.EncodeBlp1Paletted(tr, 512, 512, includeMips);
                files["LoadingScreenBL.blp"] = IconProcessorService.EncodeBlp1Paletted(bl, 512, 256, includeMips);
                files["LoadingScreenBR.blp"] = IconProcessorService.EncodeBlp1Paletted(br, 512, 256, includeMips);
                summaryLabel = "Pantalla de Carga HQ 1024×768 (LoadingScreen.mdx + 4 cuadrantes .blp)";
                break;
            }
            case LoadingScreenQualityMode.Compact256x256:
            {
                var composed256 = MapMediaService.ComposeImage(_loadingSrcPixels, _loadingSrcW, _loadingSrcH, 256, 256, opts);
                files["LoadingScreen.mdx"] = MapMediaService.GetSingleTextureLoadingScreenMdx(useTgaExtension: false);
                files["FullScreen.blp"] = IconProcessorService.EncodeBlp1Paletted(composed256, 256, 256, includeMips);
                summaryLabel = "Pantalla de Carga Compacta 256×256 (LoadingScreen.mdx + FullScreen.blp)";
                break;
            }
            case LoadingScreenQualityMode.UncompressedTga:
            {
                var composed512 = MapMediaService.ComposeImage(_loadingSrcPixels, _loadingSrcW, _loadingSrcH, 512, 512, opts);
                files["LoadingScreen.mdx"] = MapMediaService.GetSingleTextureLoadingScreenMdx(useTgaExtension: true);
                files["FullScreen.tga"] = MapMediaService.EncodeTga(composed512, 512, 512, use24Bit: true);
                summaryLabel = "Pantalla de Carga TGA 512×512 (LoadingScreen.mdx + FullScreen.tga)";
                break;
            }
            default:
            {
                var composed512 = MapMediaService.ComposeImage(_loadingSrcPixels, _loadingSrcW, _loadingSrcH, 512, 512, opts);
                files["LoadingScreen.mdx"] = MapMediaService.GetSingleTextureLoadingScreenMdx(useTgaExtension: false);
                files["FullScreen.blp"] = IconProcessorService.EncodeBlp1Paletted(composed512, 512, 512, includeMips);
                summaryLabel = "Pantalla de Carga Estándar 512×512 (LoadingScreen.mdx + FullScreen.blp)";
                break;
            }
        }

        return files;
    }

    private void BtnApplyLoadingToMap_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null)
        {
            MessageBox.Show(this, "Abre primero un mapa (.w3x / .w3m) para aplicarle la pantalla de carga.", "Loading Screen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var info = EnsureMapInfo(_project);
        info.LoadingScreenTitle = TxtLoadingTitle.Text.Trim();
        info.LoadingScreenSubtitle = TxtLoadingSubtitle.Text.Trim();
        info.LoadingScreenText = TxtLoadingDescription.Text;

        string summary;
        string w3iDetail;
        if (RbLoadOfficialPreset.IsChecked == true && _selectedOfficialPreset != null)
        {
            // Limpiar archivos custom previos si existían
            foreach (var old in new[] { "LoadingScreen.mdx", "FullScreen.blp", "FullScreen.tga", "LoadingScreenTL.blp", "LoadingScreenTR.blp", "LoadingScreenBL.blp", "LoadingScreenBR.blp" })
            {
                MapMediaService.RemoveMapImportFileIfPresent(_project, old);
            }

            if (_selectedOfficialPreset.CampaignScreenIndex >= 0)
            {
                // War3Net serializa CampaignBackgroundNumber en v18 (.w3m) y LoadingScreenBackgroundNumber en v25 (.w3x)
                info.CampaignBackgroundNumber = _selectedOfficialPreset.CampaignScreenIndex;
                info.LoadingScreenBackgroundNumber = _selectedOfficialPreset.CampaignScreenIndex;
                info.LoadingScreenNumber = 0;
                info.LoadingScreenPath = string.Empty;
                w3iDetail = $"CampaignScreen #{_selectedOfficialPreset.CampaignScreenIndex} (compatible .w3m/.w3x)";
            }
            else if (string.Equals(_selectedOfficialPreset.Id, "MultiRandom", StringComparison.OrdinalIgnoreCase))
            {
                info.CampaignBackgroundNumber = -1;
                info.LoadingScreenBackgroundNumber = -1;
                info.LoadingScreenNumber = -1;
                info.LoadingScreenPath = string.Empty;
                w3iDetail = "Pantalla por defecto / Melee (-1)";
            }
            else
            {
                UpgradeMapInfoToV25IfNeeded(info);
                info.CampaignBackgroundNumber = -1;
                info.LoadingScreenBackgroundNumber = -1;
                info.LoadingScreenNumber = -1;
                info.LoadingScreenPath = _selectedOfficialPreset.MdxPath;
                w3iDetail = $"LoadingScreenPath = '{info.LoadingScreenPath}' (formato v25)";
            }

            summary = $"Pantalla de carga oficial aplicada: {_selectedOfficialPreset.DisplayName}";
        }
        else
        {
            var generated = GenerateCustomLoadingScreenFiles(out summary);
            if (generated.Count == 0)
                return;

            // Limpiar variantes anteriores para no dejar archivos huérfanos
            foreach (var old in new[] { "FullScreen.blp", "FullScreen.tga", "LoadingScreenTL.blp", "LoadingScreenTR.blp", "LoadingScreenBL.blp", "LoadingScreenBR.blp" })
            {
                if (!generated.ContainsKey(old))
                    MapMediaService.RemoveMapImportFileIfPresent(_project, old);
            }

            foreach (var kvp in generated)
            {
                MapMediaService.UpsertMapImportFile(_project, kvp.Key, kvp.Value, "Loading Screen (Pendiente guardar)");
                _blpService.InvalidateIcon(kvp.Key);
            }

            // LoadingScreenPath solo existe en war3map.w3i >= v25
            UpgradeMapInfoToV25IfNeeded(info);
            info.CampaignBackgroundNumber = -1;
            info.LoadingScreenBackgroundNumber = -1;
            info.LoadingScreenNumber = -1;
            info.LoadingScreenPath = "LoadingScreen.mdx";
            w3iDetail = $"LoadingScreenPath = 'LoadingScreen.mdx' (war3map.w3i {(int)info.FormatVersion})";
        }

        _project.HasScenarioEdits = true;
        _onAppliedToMap?.Invoke(summary);
        TxtStatus.Text = $"✅ {summary}. Presiona Guardar (Ctrl+S) en la ventana principal para compilar el MPQ.";
        MessageBox.Show(
            this,
            $"{summary}\n\nSe configuró automáticamente war3map.w3i ({w3iDetail}) y se actualizaron los recursos en el Gestor de Importaciones.",
            "Pantalla de Carga Aplicada",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void UpgradeMapInfoToV25IfNeeded(MapInfo info)
    {
        if (info.FormatVersion >= MapInfoFormatVersion.v25)
            return;

        info.FormatVersion = MapInfoFormatVersion.v25;
        if (info.WaterTintingColor.ToArgb() == 0)
            info.WaterTintingColor = System.Drawing.Color.FromArgb(255, 255, 255, 255);
        if (info.FogEndZ <= 0f && info.FogStartZ <= 0f)
        {
            info.FogStartZ = 1000f;
            info.FogEndZ = 8000f;
            info.FogDensity = 0f;
        }
        if (info.FogColor.ToArgb() == 0)
            info.FogColor = System.Drawing.Color.FromArgb(255, 0, 0, 0);
        if (info.LightEnvironment == Tileset.Unspecified)
            info.LightEnvironment = info.Tileset;
    }

    private void BtnExportLoadingToDisk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Selecciona dónde exportar los archivos de Pantalla de Carga",
            FileName = "LoadingScreen.mdx",
            Filter = "Modelo Warcraft III (*.mdx)|*.mdx"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var targetDir = Path.GetDirectoryName(dlg.FileName);
        if (string.IsNullOrEmpty(targetDir))
            return;

        var generated = GenerateCustomLoadingScreenFiles(out string summary);
        foreach (var kvp in generated)
        {
            File.WriteAllBytes(Path.Combine(targetDir, kvp.Key), kvp.Value);
        }

        TxtStatus.Text = $"Archivos exportados en: {targetDir}";
        MessageBox.Show(
            this,
            $"Se exportaron {generated.Count} archivo(s) en:\n{targetDir}\n\n({string.Join(", ", generated.Keys)})",
            "Exportación Completa",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ==================== EVENTOS PESTAÑA 2: MAPPREVIEW (SALA DE ESPERA) ====================

    private void TxtFilterPreviewIcons_TextChanged(object sender, TextChangedEventArgs e) => ApplyPreviewTextureFilter();

    private void LstWc3PreviewTextures_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || LstWc3PreviewTextures.SelectedItem is not PreviewTextureItem item)
            return;

        if (_blpService.TryGetTexturePixels(item.MpqPath, _project, out var px, out int w, out int h))
        {
            _previewSrcPixels = px;
            _previewSrcW = w;
            _previewSrcH = h;
            _previewSrcLabel = $"Textura WC3: {item.DisplayName} ({w}×{h})";
            _isQuestionMarkSelected = false;
            ChkHideMinimapFlag.IsChecked = false;
            RenderMapPreviewLive();
        }
    }

    private void BtnPickPreviewImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Elegir Imagen para Sala de Espera (war3mapPreview.tga)",
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.blp)|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.blp|Todos los archivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadExternalImageForMapPreview(dlg.FileName);
        }
    }

    private void LoadExternalImageForMapPreview(string filePath)
    {
        try
        {
            var bmp = IconProcessorService.LoadExternalImage(filePath, _blpService);
            var px = IconProcessorService.ExtractBgraPixels(bmp, out int w, out int h);
            if (px == null)
            {
                MessageBox.Show(this, "No se pudo decodificar la imagen seleccionada.", "MapPreview", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _previewSrcPixels = px;
            _previewSrcW = w;
            _previewSrcH = h;
            _previewSrcLabel = $"Imagen de usuario: {Path.GetFileName(filePath)} ({w}×{h})";
            _isQuestionMarkSelected = false;
            ChkHideMinimapFlag.IsChecked = false;

            RenderMapPreviewLive();
            TxtStatus.Text = $"Imagen de Sala de Espera cargada: {Path.GetFileName(filePath)} ({w}×{h}).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error abriendo imagen:\n{ex.Message}", "MapPreview", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnUseMinimapForPreview_Click(object sender, RoutedEventArgs e)
    {
        if (TryCaptureMinimapAsPreviewSource())
        {
            _isQuestionMarkSelected = false;
            ChkHideMinimapFlag.IsChecked = false;
            RenderMapPreviewLive();
            TxtStatus.Text = "Minimapa actual del terreno capturado para el MapPreview.";
        }
        else
        {
            MessageBox.Show(this, "No hay un minimapa disponible. Abre primero un mapa.", "MapPreview", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool TryCaptureMinimapAsPreviewSource()
    {
        if (TryExtractMinimapPixels(out var px, out int w, out int h))
        {
            _previewSrcPixels = px;
            _previewSrcW = w;
            _previewSrcH = h;
            _previewSrcLabel = $"Minimapa del Terreno ({w}×{h})";
            return true;
        }
        return false;
    }

    private bool TryExtractMinimapPixels(out byte[] pixels, out int w, out int h)
    {
        pixels = Array.Empty<byte>();
        w = 0;
        h = 0;

        if (_liveMinimapBitmap != null)
        {
            var extracted = IconProcessorService.ExtractBgraPixels(_liveMinimapBitmap, out w, out h);
            if (extracted != null)
            {
                pixels = extracted;
                return true;
            }
        }

        if (_project != null)
        {
            if (_blpService.TryGetTexturePixels("war3mapMap.blp", _project, out pixels, out w, out h) ||
                _blpService.TryGetTexturePixels("war3mapMap.tga", _project, out pixels, out w, out h))
            {
                return true;
            }
        }

        return false;
    }

    private void BtnUseQuestionMarkPreview_Click(object sender, RoutedEventArgs e)
    {
        UseQuestionMarkForPreview();
        RenderMapPreviewLive();
        TxtStatus.Text = "Símbolo de Interrogación Clásico (?) seleccionado para la Sala de Espera.";
    }

    private void UseQuestionMarkForPreview()
    {
        _previewSrcPixels = MapMediaService.CreateQuestionMarkPreviewPixels(256);
        _previewSrcW = 256;
        _previewSrcH = 256;
        _previewSrcLabel = "Símbolo de Interrogación Clásico (?) de Warcraft III";
        _isQuestionMarkSelected = true;
        if (CmbPreviewFrameStyle != null)
            CmbPreviewFrameStyle.SelectedIndex = 0;
        if (ChkHideMinimapFlag != null)
            ChkHideMinimapFlag.IsChecked = true;
    }

    private void PreviewCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || !IsLoaded) return;
        RenderMapPreviewLive();
    }

    private void PreviewEditOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUi || !IsLoaded) return;
        RenderMapPreviewLive();
    }

    private void PreviewSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || !IsLoaded) return;
        if (TxtPreviewZoomVal != null && SldPreviewZoom != null)
            TxtPreviewZoomVal.Text = $"{(int)SldPreviewZoom.Value}%";
        if (TxtPreviewBrightVal != null && SldPreviewBrightness != null)
            TxtPreviewBrightVal.Text = ((int)SldPreviewBrightness.Value).ToString();
        if (TxtPreviewContrastVal != null && SldPreviewContrast != null)
            TxtPreviewContrastVal.Text = ((int)SldPreviewContrast.Value).ToString();
        RenderMapPreviewLive();
    }

    private void BtnResetPreviewAdjustments_Click(object sender, RoutedEventArgs e)
    {
        _suppressUi = true;
        CmbPreviewFitMode.SelectedIndex = 0;
        CmbPreviewFrameStyle.SelectedIndex = 1;
        SldPreviewZoom.Value = 100;
        SldPreviewBrightness.Value = 0;
        SldPreviewContrast.Value = 0;
        TxtPreviewZoomVal.Text = "100%";
        TxtPreviewBrightVal.Text = "0";
        TxtPreviewContrastVal.Text = "0";
        _suppressUi = false;
        RenderMapPreviewLive();
    }

    private int GetSelectedPreviewSize() =>
        CmbPreviewQuality?.SelectedIndex switch
        {
            1 => 256,
            2 => 64,
            _ => 128
        };

    private MediaComposeOptions BuildPreviewComposeOptions()
    {
        return new MediaComposeOptions
        {
            FitMode = CmbPreviewFitMode?.SelectedIndex switch
            {
                1 => MediaFitMode.FitLetterbox,
                2 => MediaFitMode.Stretch,
                _ => MediaFitMode.FillCrop
            },
            ZoomPercent = (int)(SldPreviewZoom?.Value ?? 100),
            Brightness = (int)(SldPreviewBrightness?.Value ?? 0),
            Contrast = (int)(SldPreviewContrast?.Value ?? 0),
            BottomVignette = false,
            FrameStyle = CmbPreviewFrameStyle?.SelectedIndex switch
            {
                1 => MapPreviewFrameStyle.ClassicGoldBevel,
                2 => MapPreviewFrameStyle.DarkSteelBevel,
                3 => MapPreviewFrameStyle.SoftVignette,
                _ => MapPreviewFrameStyle.None
            }
        };
    }

    private void RenderMapPreviewLive()
    {
        if (_previewSrcPixels == null || ImgMapPreviewLarge == null)
            return;

        int targetSize = GetSelectedPreviewSize();
        var opts = BuildPreviewComposeOptions();
        var composed = MapMediaService.ComposeImage(_previewSrcPixels, _previewSrcW, _previewSrcH, targetSize, targetSize, opts);
        var bmp = IconProcessorService.PixelsToBitmapSource(composed, targetSize, targetSize);

        ImgMapPreviewLarge.Source = bmp;
        ImgMapPreviewActual128.Source = bmp;

        TxtPreviewResolutionBadge.Text = $"Resolución Salida: {targetSize} × {targetSize} px (TGA 24-bit)";
        TxtPreviewSourceInfo.Text = $"Origen: {_previewSrcLabel}";

        int tgaBytes = 18 + targetSize * targetSize * 3;
        bool alsoBlp = ChkPreviewAlsoWriteBlp?.IsChecked == true;
        TxtPreviewOutputFilesInfo.Text = alsoBlp
            ? $"Archivos en raíz del MPQ: war3mapPreview.tga ({targetSize}×{targetSize}) + war3mapPreview.blp"
            : $"Archivo en raíz del MPQ: war3mapPreview.tga ({targetSize}×{targetSize} TGA 24-bit)";
        TxtPreviewEstimatedSize.Text = $"{tgaBytes / 1024.0:F1} KB";
    }

    private void BtnApplyPreviewToMap_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _previewSrcPixels == null)
        {
            MessageBox.Show(this, "Abre primero un mapa (.w3x / .w3m) para aplicar el MapPreview.", "MapPreview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int size = GetSelectedPreviewSize();
        var opts = BuildPreviewComposeOptions();
        var composed = MapMediaService.ComposeImage(_previewSrcPixels, _previewSrcW, _previewSrcH, size, size, opts);
        var tgaBytes = MapMediaService.EncodeTga(composed, size, size, use24Bit: true);

        MapMediaService.UpsertMapImportFile(_project, "war3mapPreview.tga", tgaBytes, "MapPreview Sala (Pendiente guardar)");
        _blpService.InvalidateIcon("war3mapPreview.tga");

        if (ChkPreviewAlsoWriteBlp.IsChecked == true)
        {
            var blpBytes = IconProcessorService.EncodeBlp1Paletted(composed, size, size, includeMipmaps: true);
            MapMediaService.UpsertMapImportFile(_project, "war3mapPreview.blp", blpBytes, "MapPreview BLP (Pendiente guardar)");
            _blpService.InvalidateIcon("war3mapPreview.blp");
        }
        else
        {
            MapMediaService.RemoveMapImportFileIfPresent(_project, "war3mapPreview.blp");
        }

        var info = EnsureMapInfo(_project);
        // En Warcraft III 1.27b, para que war3mapPreview.tga se muestre en la sala de espera,
        // HideMinimapInPreviewScreens debe estar desactivado (a menos que el usuario haya pedido solo el flag nativo '?')
        if (!_isQuestionMarkSelected)
            ChkHideMinimapFlag.IsChecked = false;

        if (ChkHideMinimapFlag.IsChecked == true)
            info.MapFlags |= MapFlags.HideMinimapInPreviewScreens;
        else
            info.MapFlags &= ~MapFlags.HideMinimapInPreviewScreens;

        _project.HasScenarioEdits = true;
        string summary = $"MapPreview war3mapPreview.tga ({size}×{size}) aplicado al mapa";
        _onAppliedToMap?.Invoke(summary);
        TxtStatus.Text = $"✅ {summary}. Presiona Guardar (Ctrl+S) en la ventana principal para actualizar el MPQ.";

        MessageBox.Show(
            this,
            $"Se inyectó 'war3mapPreview.tga' ({size}×{size} px, {tgaBytes.Length / 1024.0:F1} KB) en la raíz del mapa y se sincronizaron las banderas de previsualización en war3map.w3i.",
            "MapPreview Aplicado",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void BtnRemoveCustomPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;

        MapMediaService.RemoveMapImportFileIfPresent(_project, "war3mapPreview.tga");
        MapMediaService.RemoveMapImportFileIfPresent(_project, "war3mapPreview.blp");

        var info = EnsureMapInfo(_project);
        info.MapFlags &= ~MapFlags.HideMinimapInPreviewScreens;
        ChkHideMinimapFlag.IsChecked = false;
        _project.HasScenarioEdits = true;

        TryCaptureMinimapAsPreviewSource();
        RenderMapPreviewLive();

        const string msg = "Se eliminó war3mapPreview.tga personalizado; la sala de espera volverá a mostrar el minimapa normal del terreno";
        _onAppliedToMap?.Invoke(msg);
        TxtStatus.Text = msg;
    }

    private void BtnExportPreviewToDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_previewSrcPixels == null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Exportar war3mapPreview.tga",
            FileName = "war3mapPreview.tga",
            Filter = "Imagen TGA de Warcraft III (*.tga)|*.tga|Imagen BLP1 (*.blp)|*.blp|Imagen PNG (*.png)|*.png"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        int size = GetSelectedPreviewSize();
        var opts = BuildPreviewComposeOptions();
        var composed = MapMediaService.ComposeImage(_previewSrcPixels, _previewSrcW, _previewSrcH, size, size, opts);
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();

        if (ext == ".blp")
        {
            File.WriteAllBytes(dlg.FileName, IconProcessorService.EncodeBlp1Paletted(composed, size, size, includeMipmaps: true));
        }
        else if (ext == ".png")
        {
            IconProcessorService.SaveAsPng(dlg.FileName, composed, size, size);
        }
        else
        {
            File.WriteAllBytes(dlg.FileName, MapMediaService.EncodeTga(composed, size, size, use24Bit: true));
        }

        TxtStatus.Text = $"Archivo exportado: {dlg.FileName}";
    }

    // ==================== DRAG & DROP EN CUALQUIER PESTAÑA ====================

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        if (TabsMedia.SelectedIndex == 1)
            LoadExternalImageForMapPreview(files[0]);
        else
            LoadExternalImageForLoadingScreen(files[0]);
    }

    internal Action? EmbeddedCloseHandler { get; set; }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (EmbeddedCloseHandler != null) EmbeddedCloseHandler();
        else Close();
    }

    private static MapInfo EnsureMapInfo(MapProject project)
    {
        if (project.MapInfo != null)
            return project.MapInfo;

        var info = new MapInfo(MapInfoFormatVersion.v25)
        {
            MapName = project.MapName,
            MapAuthor = project.MapAuthor,
            MapDescription = project.MapDescription,
            RecommendedPlayers = string.IsNullOrWhiteSpace(project.SuggestedPlayers) ? "Any" : project.SuggestedPlayers,
            Tileset = Tileset.LordaeronSummer,
            FogStyle = FogStyle.Linear,
            GlobalWeather = WeatherType.None,
            MapFlags = MapFlags.MaskedAreasArePartiallyVisible | MapFlags.ShowWaterWavesOnCliffShores | MapFlags.ShowWaterWavesOnRollingShores | MapFlags.UseItemClassificationSystem
        };
        project.MapInfo = info;
        return info;
    }

    private static string StripColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return Regex.Replace(text, @"\|c[0-9a-fA-F]{8}|\|r|\|n", " ").Trim();
    }
}

