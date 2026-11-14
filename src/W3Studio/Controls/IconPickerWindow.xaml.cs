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

        public IconItemVm(BlpIconService blpService, MapProject? project)
        {
            _blpService = blpService;
            _project = project;
        }

        public string MpqPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool IsImported { get; set; }

        public string SourceBadge => IsImported ? "Mapa" : "WC3";
        public Brush BadgeBg => IsImported
            ? new SolidColorBrush(Color.FromRgb(0xDD, 0xF4, 0xDF))
            : new SolidColorBrush(Color.FromRgb(0xEB, 0xF1, 0xFA));
        public Brush BadgeFg => IsImported
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x6B, 0x26))
            : new SolidColorBrush(Color.FromRgb(0x1E, 0x4E, 0x8C));

        public BitmapSource PreviewBitmap => _preview ??= _blpService.GetIcon(MpqPath, _project);
    }

    private readonly BlpIconService _blpService;
    private readonly MapProject? _project;
    private readonly Action<string?>? _onOpenIconManager;
    private readonly List<IconItemVm> _allIcons = new();
    private bool _suppressTextChanged;

    public string SelectedIconPath { get; private set; } = string.Empty;

    public IconPickerWindow(
        BlpIconService blpService,
        MapProject? project,
        string? currentPath,
        string fieldLabel,
        Action<string?>? onOpenIconManager = null)
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
        foreach (var p in paths)
        {
            _allIcons.Add(new IconItemVm(_blpService, _project)
            {
                MpqPath = p,
                FileName = Path.GetFileName(p),
                IsImported = importedSet.Contains(p)
            });
        }

        _suppressTextChanged = true;
        TxtSelectedPath.Text = currentPath ?? string.Empty;
        _suppressTextChanged = false;

        ApplyFilter();

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var existing = _allIcons.FirstOrDefault(i => i.MpqPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                           ?? _allIcons.FirstOrDefault(i => i.FileName.Equals(Path.GetFileName(currentPath), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                LstIcons.SelectedItem = existing;
                LstIcons.ScrollIntoView(existing);
            }
            UpdatePreviewAndVerification(currentPath);
        }
    }

    private void ApplyFilter()
    {
        if (LstIcons == null || CmbCategory == null || TxtSearch == null)
            return;

        var tag = (CmbCategory.SelectedItem as ComboBoxItem)?.Tag as string ?? "ALL";
        var q = TxtSearch.Text.Trim();

        IEnumerable<IconItemVm> query = _allIcons;
        query = tag switch
        {
            "IMPORTED" => query.Where(i => i.IsImported),
            "BTN" => query.Where(i => i.FileName.StartsWith("BTN", StringComparison.OrdinalIgnoreCase) || i.FileName.StartsWith("ATC", StringComparison.OrdinalIgnoreCase)),
            "PAS" => query.Where(i => i.FileName.StartsWith("PASBTN", StringComparison.OrdinalIgnoreCase)),
            "DIS" => query.Where(i => i.FileName.StartsWith("DIS", StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(i =>
                i.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                i.MpqPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var result = query.ToList();
        LstIcons.ItemsSource = result;
        TxtCount.Text = $"{result.Count:N0} de {_allIcons.Count:N0} iconos";
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded) return;
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

