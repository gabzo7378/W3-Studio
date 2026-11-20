using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class IconManagerWindow : Window
{
    private sealed class IconListItem
    {
        private readonly BlpIconService _blpService;
        private readonly MapProject? _project;
        private System.Windows.Media.Imaging.BitmapSource? _preview;

        public IconListItem(IconCatalogEntry entry, BlpIconService blpService, MapProject? project)
        {
            Entry = entry;
            _blpService = blpService;
            _project = project;
        }

        public IconCatalogEntry Entry { get; }
        public string MpqPath => Entry.MpqPath;
        public string DisplayName => Entry.FileName;
        public string StyleBadge => Entry.StyleBadge;
        public string Subtitle => !string.IsNullOrWhiteSpace(Entry.SearchableMetadata)
            ? Entry.SearchableMetadata
            : Entry.MpqPath;

        public Brush BadgeBg => Entry.StyleBadge switch
        {
            "Mapa" => new SolidColorBrush(Color.FromRgb(0xDD, 0xF4, 0xDF)),
            "DIS" => new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
            "PAS" => new SolidColorBrush(Color.FromRgb(0xF3, 0xE8, 0xFF)),
            "ATC" => new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xD6)),
            "UI" => new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xFE)),
            _ => new SolidColorBrush(Color.FromRgb(0xEB, 0xF1, 0xFA))
        };

        public Brush BadgeFg => Entry.StyleBadge switch
        {
            "Mapa" => new SolidColorBrush(Color.FromRgb(0x1B, 0x6B, 0x26)),
            "DIS" => new SolidColorBrush(Color.FromRgb(0x52, 0x56, 0x60)),
            "PAS" => new SolidColorBrush(Color.FromRgb(0x6B, 0x21, 0xA8)),
            "ATC" => new SolidColorBrush(Color.FromRgb(0x92, 0x56, 0x00)),
            "UI" => new SolidColorBrush(Color.FromRgb(0x03, 0x69, 0xA1)),
            _ => new SolidColorBrush(Color.FromRgb(0x1E, 0x4E, 0x8C))
        };

        public System.Windows.Media.Imaging.BitmapSource PreviewBitmap => _preview ??= _blpService.GetIcon(MpqPath, _project);
    }

    private readonly BlpIconService _blpService;
    private readonly WarcraftBaseDataService? _baseDataService;
    private MapProject? _project;
    private readonly Action<string>? _onSavedToImports;

    private List<IconCatalogEntry> _allCatalogEntries = new();
    private Dictionary<string, IconListItem> _itemVmByPath = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _sourcePixels;
    private byte[]? _nativeDisPixels;
    private bool _sourceIsWc3FramedIcon;
    private int _sourceWidth;
    private int _sourceHeight;
    private string _sourceLabel = "Sin imagen";

    private byte[]? _composedMain64;
    private byte[]? _composedDis64;
    private bool _suppressUiEvents;

    public IconManagerWindow(
        BlpIconService blpService,
        MapProject? project,
        Action<string>? onSavedToImports = null,
        WarcraftBaseDataService? baseDataService = null)
    {
        _suppressUiEvents = true;
        InitializeComponent();
        _blpService = blpService;
        _baseDataService = baseDataService;
        _project = project;
        _onSavedToImports = onSavedToImports;
        _suppressUiEvents = false;

        SetProject(project);
        UpdateTargetPathsText();
    }

    public void SetProject(MapProject? project)
    {
        _project = project;
        if (TxtMapContextBadge != null)
        {
            TxtMapContextBadge.Text = project != null
                ? $"Mapa activo: {(string.IsNullOrEmpty(project.MapName) ? project.FileName : project.MapName)}"
                : "Sin mapa abierto (solo exportación a disco)";
        }
        if (BtnSaveToImports != null)
        {
            BtnSaveToImports.IsEnabled = project != null;
        }
        LoadWc3IconCatalog();
    }

    public void LoadIconByMpqPath(string mpqPath)
    {
        if (string.IsNullOrWhiteSpace(mpqPath)) return;
        var bmp = _blpService.GetIcon(mpqPath, _project);
        var pixels = IconProcessorService.ExtractBgraPixels(bmp, out int w, out int h);
        if (pixels == null) return;

        _sourcePixels = pixels;
        _sourceWidth = w;
        _sourceHeight = h;
        _sourceLabel = $"MPQ (tal cual en el juego): {mpqPath}";

        var fileName = Path.GetFileName(mpqPath.Replace('/', '\\'));
        var disName = fileName.StartsWith("DIS", StringComparison.OrdinalIgnoreCase) ? fileName : "DIS" + fileName;
        var disPath = @"ReplaceableTextures\CommandButtonsDisabled\" + disName;
        _nativeDisPixels = null;
        if (_blpService.FindRawFileBytes(disPath, _project) != null)
        {
            var disBmp = _blpService.GetIcon(disPath, _project);
            var dp = IconProcessorService.ExtractBgraPixels(disBmp, out int dw, out int dh);
            if (dp != null && dw == IconProcessorService.IconSize && dh == IconProcessorService.IconSize)
                _nativeDisPixels = dp;
        }

        var baseName = Path.GetFileNameWithoutExtension(mpqPath);
        _sourceIsWc3FramedIcon = baseName.StartsWith("BTN", StringComparison.OrdinalIgnoreCase) ||
                                 baseName.StartsWith("PASBTN", StringComparison.OrdinalIgnoreCase) ||
                                 baseName.StartsWith("ATC", StringComparison.OrdinalIgnoreCase);

        _suppressUiEvents = true;
        TxtOutputName.Text = StripKnownPrefix(baseName);
        // Al seleccionar un icono del juego, mostrarlo tal cual está en Warcraft III (sin agregar ni recortar bordes)
        if (RbBorderNone != null) RbBorderNone.IsChecked = true;
        if (ChkStripOldBorder != null) ChkStripOldBorder.IsChecked = false;
        _suppressUiEvents = false;

        RenderLivePreview();
    }

    private void LoadWc3IconCatalog()
    {
        var importedSet = new HashSet<string>(
            _project?.Imports.Where(i => !i.IsDeleted).Select(i => i.MpqPath) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var paths = _blpService.GetAvailableIconPaths(_project);
        _allCatalogEntries = IconCatalogFilterHelper.BuildCatalog(paths, importedSet, _baseDataService);
        _itemVmByPath = _allCatalogEntries.ToDictionary(
            e => e.MpqPath,
            e => new IconListItem(e, _blpService, _project),
            StringComparer.OrdinalIgnoreCase);

        ApplyIconListFilter();
    }

    private void ApplyIconListFilter()
    {
        if (LstWc3Icons == null) return;
        var q = TxtSearchIcons?.Text?.Trim() ?? string.Empty;
        var style = Enum.TryParse<IconStyleFilter>((CmbIconStyle?.SelectedItem as ComboBoxItem)?.Tag as string, out var s)
            ? s
            : IconStyleFilter.NoDis;
        var race = Enum.TryParse<IconRaceFilter>((CmbIconRace?.SelectedItem as ComboBoxItem)?.Tag as string, out var r)
            ? r
            : IconRaceFilter.All;
        var usage = Enum.TryParse<IconUsageFilter>((CmbIconUsage?.SelectedItem as ComboBoxItem)?.Tag as string, out var u)
            ? u
            : IconUsageFilter.All;

        var matched = IconCatalogFilterHelper.Filter(_allCatalogEntries, style, race, usage, q);
        var viewItems = matched
            .Select(e => _itemVmByPath.TryGetValue(e.MpqPath, out var vm) ? vm : new IconListItem(e, _blpService, _project))
            .ToList();

        LstWc3Icons.ItemsSource = viewItems;
        if (TxtIconsCount != null)
            TxtIconsCount.Text = $"Mostrando {viewItems.Count:N0} de {_allCatalogEntries.Count:N0} iconos";
    }

    private void IconFilter_Changed(object sender, EventArgs e)
    {
        if (_suppressUiEvents) return;
        ApplyIconListFilter();
    }

    private void BtnClearIconFilters_Click(object sender, RoutedEventArgs e)
    {
        _suppressUiEvents = true;
        if (TxtSearchIcons != null) TxtSearchIcons.Text = string.Empty;
        if (CmbIconStyle != null) CmbIconStyle.SelectedIndex = 0;
        if (CmbIconRace != null) CmbIconRace.SelectedIndex = 0;
        if (CmbIconUsage != null) CmbIconUsage.SelectedIndex = 0;
        _suppressUiEvents = false;
        ApplyIconListFilter();
    }

    private void LstWc3Icons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstWc3Icons.SelectedItem is IconListItem item)
        {
            LoadIconByMpqPath(item.MpqPath);
        }
    }

    private void BtnImportExternal_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importar imagen para crear Icono (.blp)",
            Filter = "Imágenes e Iconos (*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.blp)|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.blp|Todos (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            LoadExternalImageFile(dlg.FileName);
        }
    }

    private void LoadExternalImageFile(string filePath)
    {
        try
        {
            var bmp = IconProcessorService.LoadExternalImage(filePath, _blpService);
            var pixels = IconProcessorService.ExtractBgraPixels(bmp, out int w, out int h);
            if (pixels == null)
            {
                MessageBox.Show(this, "No se pudo decodificar la imagen seleccionada.", "Icon Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _sourcePixels = pixels;
            _nativeDisPixels = null;
            _sourceIsWc3FramedIcon = false;
            _sourceWidth = w;
            _sourceHeight = h;
            _sourceLabel = $"Archivo externo: {Path.GetFileName(filePath)} ({w}x{h} -> 64x64)";

            _suppressUiEvents = true;
            TxtOutputName.Text = StripKnownPrefix(Path.GetFileNameWithoutExtension(filePath));
            if (RbBorderBtn != null) RbBorderBtn.IsChecked = true;
            ChkStripOldBorder.IsChecked = false;
            _suppressUiEvents = false;

            RenderLivePreview();
            TxtStatus.Text = $"Imagen cargada: {Path.GetFileName(filePath)} ({w}x{h})";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error abriendo imagen:\n{ex.Message}", "Icon Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            LoadExternalImageFile(files[0]);
        }
    }

    private void BorderPreset_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        // Si el usuario cambia de "Original (Juego)" a un nuevo marco (BTN/PAS/ATC/UPG/INF) sobre un icono de WC3,
        // activamos automáticamente el recorte del borde viejo para no duplicar marcos; si vuelve a Original, lo quitamos.
        if (_sourceIsWc3FramedIcon && ChkStripOldBorder != null)
        {
            _suppressUiEvents = true;
            ChkStripOldBorder.IsChecked = GetSelectedBorderStyle() != IconBorderStyle.None;
            _suppressUiEvents = false;
        }
        UpdateTargetPathsText();
        RenderLivePreview();
    }

    private void EditOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        RenderLivePreview();
    }

    private void IconOutputOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateTargetPathsText();
        RenderLivePreview();
    }

    private void SliderOption_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        if (TxtFrameThickness != null && SldFrameThickness != null)
            TxtFrameThickness.Text = $"{(int)SldFrameThickness.Value} px";
        if (TxtZoomVal != null && SldZoom != null)
            TxtZoomVal.Text = $"{(int)SldZoom.Value}%";
        if (TxtBrightnessVal != null && SldBrightness != null)
            TxtBrightnessVal.Text = ((int)SldBrightness.Value).ToString();
        if (TxtContrastVal != null && SldContrast != null)
            TxtContrastVal.Text = ((int)SldContrast.Value).ToString();
        RenderLivePreview();
    }

    private void BtnFrameColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        _suppressUiEvents = true;
        ChkUseColorFrame.IsChecked = true;
        TxtFrameColorHex.Text = hex;
        if (TryParseColor(hex, out var col))
            BorderSelectedFrameColor.Background = new SolidColorBrush(col);
        _suppressUiEvents = false;
        RenderLivePreview();
    }

    private void TxtFrameColorHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents || BorderSelectedFrameColor == null) return;
        if (TryParseColor(TxtFrameColorHex.Text, out var col))
        {
            BorderSelectedFrameColor.Background = new SolidColorBrush(col);
            RenderLivePreview();
        }
    }

    private void TxtTintColorHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents || BorderTintColor == null) return;
        if (TryParseColor(TxtTintColorHex.Text, out var col))
        {
            BorderTintColor.Background = new SolidColorBrush(col);
            RenderLivePreview();
        }
    }

    private void CmbColorFrameStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded) return;
        RenderLivePreview();
    }

    private void TxtOutputName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateTargetPathsText();
    }

    private void BtnResetAdjustments_Click(object sender, RoutedEventArgs e)
    {
        _suppressUiEvents = true;
        SldZoom.Value = 100;
        SldBrightness.Value = 0;
        SldContrast.Value = 0;
        ChkFlipH.IsChecked = false;
        ChkUseTint.IsChecked = false;
        SldTintStrength.Value = 35;
        TxtZoomVal.Text = "100%";
        TxtBrightnessVal.Text = "0";
        TxtContrastVal.Text = "0";
        _suppressUiEvents = false;
        RenderLivePreview();
    }

    private IconBorderStyle GetSelectedBorderStyle()
    {
        if (RbBorderPas?.IsChecked == true) return IconBorderStyle.Pas;
        if (RbBorderAtc?.IsChecked == true) return IconBorderStyle.Atc;
        if (RbBorderUpg?.IsChecked == true) return IconBorderStyle.Upg;
        if (RbBorderInf?.IsChecked == true) return IconBorderStyle.Inf;
        if (RbBorderNone?.IsChecked == true) return IconBorderStyle.None;
        return IconBorderStyle.Btn;
    }

    private IconEditOptions BuildEditOptions()
    {
        var borderStyle = GetSelectedBorderStyle();
        TryParseColor(TxtFrameColorHex?.Text, out var frameColor);
        TryParseColor(TxtTintColorHex?.Text, out var tintColor);

        var frameStyleTag = (CmbColorFrameStyle?.SelectedItem as ComboBoxItem)?.Tag as string;
        var colorFrameStyle = frameStyleTag switch
        {
            "GlowingBox" => IconColorFrameStyle.GlowingBox,
            "CornerBrackets" => IconColorFrameStyle.CornerBrackets,
            _ => IconColorFrameStyle.SolidBox
        };

        return new IconEditOptions
        {
            BorderStyle = borderStyle,
            UseColorFrame = ChkUseColorFrame?.IsChecked == true,
            FrameColor = frameColor,
            FrameThickness = (int)(SldFrameThickness?.Value ?? 3),
            ColorFrameStyle = colorFrameStyle,
            ZoomPercent = (int)(SldZoom?.Value ?? 100),
            CropMargin = ChkStripOldBorder?.IsChecked == true ? 5 : 0,
            Brightness = (int)(SldBrightness?.Value ?? 0),
            Contrast = (int)(SldContrast?.Value ?? 0),
            FlipHorizontal = ChkFlipH?.IsChecked == true,
            UseTint = ChkUseTint?.IsChecked == true,
            TintColor = tintColor,
            TintStrength = (int)(SldTintStrength?.Value ?? 35)
        };
    }

    private static bool IsUnmodifiedOriginal(IconEditOptions opts) =>
        opts.BorderStyle == IconBorderStyle.None &&
        !opts.UseColorFrame &&
        opts.ZoomPercent == 100 &&
        opts.CropMargin == 0 &&
        opts.Brightness == 0 &&
        opts.Contrast == 0 &&
        !opts.FlipHorizontal &&
        !opts.UseTint;

    private void RenderLivePreview()
    {
        if (_sourcePixels == null)
            return;

        var opts = BuildEditOptions();
        bool isExactOriginal = IsUnmodifiedOriginal(opts) &&
                               _sourceWidth == IconProcessorService.IconSize &&
                               _sourceHeight == IconProcessorService.IconSize &&
                               _sourcePixels.Length == IconProcessorService.IconSize * IconProcessorService.IconSize * 4;

        if (isExactOriginal)
        {
            _composedMain64 = (byte[])_sourcePixels.Clone();
            _composedDis64 = _nativeDisPixels != null
                ? (byte[])_nativeDisPixels.Clone()
                : IconProcessorService.CreateDisabledVariant64(_composedMain64, opts.BorderStyle);
        }
        else
        {
            _composedMain64 = IconProcessorService.ComposeIcon64(_sourcePixels, _sourceWidth, _sourceHeight, opts);
            _composedDis64 = IconProcessorService.CreateDisabledVariant64(_composedMain64, opts.BorderStyle);
        }

        var mainBmp = IconProcessorService.PixelsToBitmapSource(_composedMain64);
        var disBmp = IconProcessorService.PixelsToBitmapSource(_composedDis64);

        ImgPreviewMainBig.Source = mainBmp;
        ImgPreviewMain64.Source = mainBmp;
        ImgPreviewDisBig.Source = disBmp;
        ImgPreviewDis64.Source = disBmp;

        bool showDis = ChkAutoDisbtn?.IsChecked == true;
        PnlDisbtnPreview.Visibility = showDis ? Visibility.Visible : Visibility.Collapsed;
        TxtSourceInfo.Text = _sourceLabel;
        UpdateTargetPathsText();
    }

    private (string mainPath, string disPath, string mainFile, string disFile) ComputeTargetMpqPaths()
    {
        var style = GetSelectedBorderStyle();
        var rawName = StripKnownPrefix(TxtOutputName?.Text ?? "CustomIcon01");
        if (string.IsNullOrWhiteSpace(rawName))
            rawName = "CustomIcon01";

        // Sanitizar caracteres no válidos en nombres de archivo
        foreach (var c in Path.GetInvalidFileNameChars())
            rawName = rawName.Replace(c.ToString(), string.Empty);
        if (string.IsNullOrWhiteSpace(rawName))
            rawName = "CustomIcon01";

        string prefix = style switch
        {
            IconBorderStyle.Pas => "PASBTN",
            IconBorderStyle.Atc => "ATC",
            IconBorderStyle.Upg => "BTN",
            IconBorderStyle.Inf => "INF",
            _ => "BTN"
        };

        string folder = style == IconBorderStyle.Pas
            ? @"ReplaceableTextures\CommandButtons\"
            : @"ReplaceableTextures\CommandButtons\";

        string mainFile = $"{prefix}{rawName}.blp";
        string disFile = $"DIS{mainFile}";

        string mainPath = folder + mainFile;
        string disPath = @"ReplaceableTextures\CommandButtonsDisabled\" + disFile;
        return (mainPath, disPath, mainFile, disFile);
    }

    private void UpdateTargetPathsText()
    {
        if (TxtMainMpqPath == null || TxtDisMpqPath == null || PnlDisMpqPath == null)
            return;

        var (mainPath, disPath, mainFile, disFile) = ComputeTargetMpqPaths();
        var style = GetSelectedBorderStyle();
        if (TxtMainPreviewTitle != null)
        {
            TxtMainPreviewTitle.Text = style == IconBorderStyle.None
                ? "ICONO PRINCIPAL (TAL CUAL EN EL JUEGO)"
                : $"PRINCIPAL ({style.ToString().ToUpperInvariant()})";
        }
        if (TxtDisPreviewTitle != null)
            TxtDisPreviewTitle.Text = $"DESHABILITADO ({Path.GetFileNameWithoutExtension(disFile)})";

        TxtMainMpqPath.Text = $"Principal: {mainPath}";
        bool autoDis = ChkAutoDisbtn?.IsChecked == true;
        PnlDisMpqPath.Visibility = autoDis ? Visibility.Visible : Visibility.Collapsed;
        TxtDisMpqPath.Text = $"DISBTN:    {disPath}";
    }

    private static string StripKnownPrefix(string name)
    {
        var s = (name ?? string.Empty).Trim();
        foreach (var prefix in new[] { "DISPASBTN", "DISBTN", "PASBTN", "BTN", "ATC", "INF" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.Length > prefix.Length)
                return s.Substring(prefix.Length);
        }
        return s;
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Color.FromRgb(255, 210, 40);
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var clean = hex.Trim();
        if (!clean.StartsWith("#")) clean = "#" + clean;
        try
        {
            if (ColorConverter.ConvertFromString(clean) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("IconManagerWindow.TryParseHexColor", $"Invalid color string '{hex}'.", ex);
        }
        return false;
    }

    private void BtnCopyMainPath_Click(object sender, RoutedEventArgs e)
    {
        var (mainPath, _, _, _) = ComputeTargetMpqPaths();
        Clipboard.SetText(mainPath);
        TxtStatus.Text = $"Ruta copiada al portapapeles: {mainPath}";
    }

    private void BtnSaveToImports_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null)
        {
            MessageBox.Show(this, "Abre primero un mapa (.w3x / .w3m) para poder guardar el icono en sus Importaciones.", "Icon Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_composedMain64 == null)
        {
            MessageBox.Show(this, "Primero selecciona un icono de Warcraft III o importa una imagen.", "Icon Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (mainPath, disPath, _, _) = ComputeTargetMpqPaths();
        var mainBlp = IconProcessorService.EncodeBlp1Paletted(_composedMain64);
        UpsertImportedFile(_project, mainPath, mainBlp);

        int count = 1;
        if (ChkAutoDisbtn.IsChecked == true && _composedDis64 != null)
        {
            var disBlp = IconProcessorService.EncodeBlp1Paletted(_composedDis64);
            UpsertImportedFile(_project, disPath, disBlp);
            count++;
        }

        _onSavedToImports?.Invoke(mainPath);
        TxtStatus.Text = $"Guardado en Importaciones ({count} archivo(s)): {mainPath}" + (count > 1 ? " + DISBTN" : "");
        MessageBox.Show(this,
            $"¡Icono guardado correctamente en Importaciones!\n\nPrincipal: {mainPath}" +
            (count > 1 ? $"\nDeshabilitado: {disPath}" : "") +
            "\n\nRecuerda presionar Guardar (Ctrl+S) en el mapa para escribirlo al MPQ final.",
            "Icon Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void UpsertImportedFile(MapProject project, string mpqPath, byte[] bytes)
    {
        project.RawMpqFiles[mpqPath] = bytes;
        _blpService.InvalidateIcon(mpqPath);

        var existing = project.Imports.FirstOrDefault(i => i.MpqPath.Equals(mpqPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Data = bytes;
            existing.SizeBytes = bytes.Length;
            existing.IsPendingWrite = true;
            existing.IsDeleted = false;
            existing.Status = "Icono editado (Pendiente guardar)";
        }
        else
        {
            project.Imports.Add(new W3ImportedFile
            {
                MpqPath = mpqPath,
                SizeBytes = bytes.Length,
                Data = bytes,
                IsPendingWrite = true,
                Status = "Icono Custom (Pendiente guardar)"
            });
        }
    }

    private void BtnExportToDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_composedMain64 == null)
        {
            MessageBox.Show(this, "Primero selecciona o importa una imagen.", "Icon Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (_, _, mainFile, disFile) = ComputeTargetMpqPaths();
        var dlg = new SaveFileDialog
        {
            Title = "Exportar icono editado",
            FileName = mainFile,
            Filter = "Icono Warcraft III (*.blp)|*.blp|Imagen PNG (*.png)|*.png"
        };

        if (dlg.ShowDialog(this) != true)
            return;

        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        if (ext == ".png")
        {
            IconProcessorService.SaveAsPng(dlg.FileName, _composedMain64);
            if (ChkAutoDisbtn.IsChecked == true && _composedDis64 != null)
            {
                var disPngPath = Path.Combine(Path.GetDirectoryName(dlg.FileName) ?? string.Empty, Path.GetFileNameWithoutExtension(disFile) + ".png");
                IconProcessorService.SaveAsPng(disPngPath, _composedDis64);
            }
        }
        else
        {
            var mainBlp = IconProcessorService.EncodeBlp1Paletted(_composedMain64);
            File.WriteAllBytes(dlg.FileName, mainBlp);
            if (ChkAutoDisbtn.IsChecked == true && _composedDis64 != null)
            {
                var disBlpPath = Path.Combine(Path.GetDirectoryName(dlg.FileName) ?? string.Empty, "DIS" + Path.GetFileName(dlg.FileName));
                File.WriteAllBytes(disBlpPath, IconProcessorService.EncodeBlp1Paletted(_composedDis64));
            }
        }

        TxtStatus.Text = $"Exportado a disco: {dlg.FileName}";
    }

    internal Action? EmbeddedCloseHandler { get; set; }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (EmbeddedCloseHandler != null) EmbeddedCloseHandler();
            else Close();
        }
    }
}

