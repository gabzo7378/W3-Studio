using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio.Controls;

public partial class IconPickerWindow : Window
{
    private sealed class IconItemVm
    {
        private readonly BlpIconService _blpService;
        private readonly MapProject? _project;
        private BitmapSource? _preview;

        public IconItemVm(IconCatalogEntry entry, BlpIconService blpService, MapProject? project)
        {
            Entry = entry;
            _blpService = blpService;
            _project = project;
        }

        public IconCatalogEntry Entry { get; }
        public string MpqPath => Entry.MpqPath;
        public string FileName => Entry.FileName;
        public bool IsImported => Entry.IsImported;

        public string SourceBadge => Entry.StyleBadge;
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

        public BitmapSource PreviewBitmap => _preview ??= _blpService.GetIcon(MpqPath, _project);
    }

    private readonly BlpIconService _blpService;
    private readonly MapProject? _project;
    private readonly Action<string?>? _onOpenIconManager;
    private readonly List<IconCatalogEntry> _allEntries;
    private readonly Dictionary<string, IconItemVm> _vmByPath;
    private bool _suppressTextChanged;
    private bool _suppressFilterEvents;

    public string SelectedIconPath { get; private set; } = string.Empty;

    public IconPickerWindow(
        BlpIconService blpService,
        MapProject? project,
        string? currentPath,
        string fieldLabel,
        Action<string?>? onOpenIconManager = null,
        WarcraftBaseDataService? baseDataService = null)
    {
        InitializeComponent();
        _blpService = blpService;
        _project = project;
        _onOpenIconManager = onOpenIconManager;

        if (BtnOpenIconManager != null)
            BtnOpenIconManager.Visibility = onOpenIconManager != null ? Visibility.Visible : Visibility.Collapsed;

        TxtHeaderTitle.Text = $"Seleccionar Icono — {fieldLabel}";

        var importedSet = new HashSet<string>(
            project?.Imports.Where(i => !i.IsDeleted).Select(i => i.MpqPath) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var paths = _blpService.GetAvailableIconPaths(project);
        _allEntries = IconCatalogFilterHelper.BuildCatalog(paths, importedSet, baseDataService);
        _vmByPath = _allEntries.ToDictionary(
            e => e.MpqPath,
            e => new IconItemVm(e, _blpService, _project),
            StringComparer.OrdinalIgnoreCase);

        _suppressTextChanged = true;
        TxtSelectedPath.Text = currentPath ?? string.Empty;
        _suppressTextChanged = false;

        // If the current icon is a DIS icon, switch initial filter to All so it is visible
        if (!string.IsNullOrWhiteSpace(currentPath) &&
            (Path.GetFileName(currentPath).StartsWith("DIS", StringComparison.OrdinalIgnoreCase) ||
             currentPath.Contains(@"\CommandButtonsDisabled\", StringComparison.OrdinalIgnoreCase)))
        {
            _suppressFilterEvents = true;
            CmbCategory.SelectedIndex = 7;
            _suppressFilterEvents = false;
        }

        ApplyFilter();

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var existingEntry = _allEntries.FirstOrDefault(i => i.MpqPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                                ?? _allEntries.FirstOrDefault(i => i.FileName.Equals(Path.GetFileName(currentPath), StringComparison.OrdinalIgnoreCase));
            if (existingEntry != null && _vmByPath.TryGetValue(existingEntry.MpqPath, out var existingVm))
            {
                LstIcons.SelectedItem = existingVm;
                LstIcons.ScrollIntoView(existingVm);
            }
            UpdatePreviewAndVerification(currentPath);
        }
    }

    private void ApplyFilter()
    {
        if (LstIcons == null || CmbCategory == null || TxtSearch == null)
            return;

        var style = Enum.TryParse<IconStyleFilter>((CmbCategory.SelectedItem as ComboBoxItem)?.Tag as string, out var s)
            ? s
            : IconStyleFilter.NoDis;
        var race = Enum.TryParse<IconRaceFilter>((CmbRace?.SelectedItem as ComboBoxItem)?.Tag as string, out var r)
            ? r
            : IconRaceFilter.All;
        var usage = Enum.TryParse<IconUsageFilter>((CmbUsage?.SelectedItem as ComboBoxItem)?.Tag as string, out var u)
            ? u
            : IconUsageFilter.All;
        var q = TxtSearch.Text.Trim();

        var matched = IconCatalogFilterHelper.Filter(_allEntries, style, race, usage, q);
        var result = matched
            .Select(e => _vmByPath.TryGetValue(e.MpqPath, out var vm) ? vm : new IconItemVm(e, _blpService, _project))
            .ToList();

        LstIcons.ItemsSource = result;
        TxtCount.Text = $"{result.Count:N0} de {_allEntries.Count:N0} iconos";
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents) return;
        ApplyFilter();
    }

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilterEvents = true;
        TxtSearch.Text = string.Empty;
        CmbCategory.SelectedIndex = 0;
        if (CmbRace != null) CmbRace.SelectedIndex = 0;
        if (CmbUsage != null) CmbUsage.SelectedIndex = 0;
        _suppressFilterEvents = false;
        ApplyFilter();
    }

    private void LstIcons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstIcons.SelectedItem is IconItemVm vm)
        {
            _suppressTextChanged = true;
            TxtSelectedPath.Text = vm.MpqPath;
            _suppressTextChanged = false;
            UpdatePreviewAndVerification(vm.MpqPath);
        }
    }

    private void TxtSelectedPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || !IsLoaded) return;
        UpdatePreviewAndVerification(TxtSelectedPath.Text.Trim());
    }

    private void UpdatePreviewAndVerification(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ImgPreviewMain.Source = _blpService.FallbackIcon;
            ImgPreviewDis.Source = _blpService.FallbackIcon;
            TxtVerifyMain.Text = "Sin ruta seleccionada";
            TxtVerifyMain.Foreground = Brushes.LightGray;
            TxtVerifyDis.Text = string.Empty;
            TxtDisPathInfo.Text = string.Empty;
            return;
        }

        ImgPreviewMain.Source = _blpService.GetIcon(path, _project);
        bool mainExists = _blpService.FindRawFileBytes(path, _project) != null;
        TxtVerifyMain.Text = mainExists
            ? "✓ Icono encontrado en MPQ / Importaciones"
            : "⚠ Icono no encontrado en MPQ / Importaciones";
        TxtVerifyMain.Foreground = new SolidColorBrush(mainExists
            ? Color.FromRgb(0x66, 0xE2, 0x7A)
            : Color.FromRgb(0xFF, 0x9E, 0x44));

        var fileName = Path.GetFileName(path.Replace('/', '\\'));
        var disName = fileName.StartsWith("DIS", StringComparison.OrdinalIgnoreCase) ? fileName : "DIS" + fileName;
        var disPath = @"ReplaceableTextures\CommandButtonsDisabled\" + disName;
        TxtDisPathInfo.Text = disPath;

        bool disExists = _blpService.FindRawFileBytes(disPath, _project) != null;
        ImgPreviewDis.Source = disExists ? _blpService.GetIcon(disPath, _project) : _blpService.FallbackIcon;
        TxtVerifyDis.Text = disExists
            ? "✓ DISBTN correspondiente encontrado"
            : "⚠ Falta DISBTN (se verá verde al pausar juego)";
        TxtVerifyDis.Foreground = new SolidColorBrush(disExists
            ? Color.FromRgb(0x66, 0xE2, 0x7A)
            : Color.FromRgb(0xFF, 0xB8, 0x4D));
    }

    private void LstIcons_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstIcons.SelectedItem is IconItemVm vm)
        {
            SelectedIconPath = vm.MpqPath;
            DialogResult = true;
        }
    }

    private void BtnOpenIconManager_Click(object sender, RoutedEventArgs e)
    {
        var current = TxtSelectedPath.Text.Trim();
        _onOpenIconManager?.Invoke(current);
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        SelectedIconPath = TxtSelectedPath.Text.Trim();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && TxtSearch.IsFocused && LstIcons.Items.Count > 0)
        {
            LstIcons.SelectedIndex = 0;
            LstIcons.Focus();
            e.Handled = true;
        }
    }
}

