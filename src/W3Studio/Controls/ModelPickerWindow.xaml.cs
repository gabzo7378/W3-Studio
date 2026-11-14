using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio.Controls;

public partial class ModelPickerWindow : Window
{
    private sealed class ModelItemVm
    {
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
    }

    private readonly BlpIconService _blpService;
    private readonly MapProject? _project;
    private readonly List<ModelItemVm> _allModels = new();
    private bool _suppressTextChanged;

    public string SelectedModelPath { get; private set; } = string.Empty;

    public ModelPickerWindow(BlpIconService blpService, MapProject? project, string? currentPath, string fieldLabel)
    {
        InitializeComponent();
        _blpService = blpService;
        _project = project;

        TxtHeaderTitle.Text = $"Seleccionar Modelo — {fieldLabel}";
        PreviewViewer.SetContext(_blpService, _project);

        var importedSet = new HashSet<string>(
            project?.Imports.Where(i => !i.IsDeleted).Select(i => i.MpqPath) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var paths = _blpService.GetAvailableModelPaths(project);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in paths)
        {
            if (seen.Add(p))
            {
                _allModels.Add(new ModelItemVm
                {
                    MpqPath = p,
                    FileName = Path.GetFileName(p),
                    IsImported = importedSet.Contains(p)
                });
            }
        }

        _suppressTextChanged = true;
        TxtSelectedPath.Text = currentPath ?? string.Empty;
        _suppressTextChanged = false;

        ApplyFilter();

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var normalized = NormalizeToMdx(currentPath);
            var existing = _allModels.FirstOrDefault(m =>
                m.MpqPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase) ||
                NormalizeToMdx(m.MpqPath).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                LstModels.SelectedItem = existing;
                LstModels.ScrollIntoView(existing);
            }
            UpdateVerificationAndPreview(currentPath);
        }
    }

    private void ApplyFilter()
    {
        if (LstModels == null || CmbCategory == null || TxtSearch == null)
            return;

        var tag = (CmbCategory.SelectedItem as ComboBoxItem)?.Tag as string ?? "ALL";
        var q = TxtSearch.Text.Trim();

        IEnumerable<ModelItemVm> query = _allModels;

        query = tag switch
        {
            "IMPORTED" => query.Where(m => m.IsImported),
            "UNITS" => query.Where(m => m.MpqPath.StartsWith(@"Units\", StringComparison.OrdinalIgnoreCase)),
            "BUILDINGS" => query.Where(m => m.MpqPath.StartsWith(@"Buildings\", StringComparison.OrdinalIgnoreCase)),
            "ABILITIES" => query.Where(m => m.MpqPath.StartsWith(@"Abilities\", StringComparison.OrdinalIgnoreCase)),
            "OBJECTS" => query.Where(m => m.MpqPath.StartsWith(@"Objects\", StringComparison.OrdinalIgnoreCase)),
            "DOODADS" => query.Where(m => m.MpqPath.StartsWith(@"Doodads\", StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(m =>
                m.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.MpqPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var result = query.ToList();
        LstModels.ItemsSource = result;
        TxtCount.Text = $"{result.Count:N0} de {_allModels.Count:N0} modelos";
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void LstModels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstModels.SelectedItem is ModelItemVm vm)
        {
            _suppressTextChanged = true;
            TxtSelectedPath.Text = vm.MpqPath;
            _suppressTextChanged = false;
            UpdateVerificationAndPreview(vm.MpqPath);
        }
    }

    private void TxtSelectedPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || !IsLoaded) return;
        UpdateVerificationAndPreview(TxtSelectedPath.Text.Trim());
    }

    private void UpdateVerificationAndPreview(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            TxtVerification.Text = "Sin modelo asignado";
            TxtVerification.Foreground = Brushes.LightGray;
            PreviewViewer.ClearModel();
            return;
        }

        var mdx = NormalizeToMdx(path);
        var tileset = _project?.Terrain != null ? _project.Terrain.Tileset.ToString() : null;
        bool exists = _blpService.FindRawFileBytes(mdx, _project, tileset) != null;

        if (!exists && mdx.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
        {
            var var0 = mdx.Substring(0, mdx.Length - 4) + "0.mdx";
            if (_blpService.FindRawFileBytes(var0, _project, tileset) != null)
            {
                mdx = var0;
                exists = true;
            }
        }

        if (exists)
        {
            TxtVerification.Text = "✓ Modelo verificado en MPQ / Importaciones";
            TxtVerification.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xE2, 0x7A));
            PreviewViewer.LoadModel(mdx);
        }
        else
        {
            TxtVerification.Text = "⚠ Archivo no encontrado en MPQ ni Importaciones";
            TxtVerification.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x44));
            PreviewViewer.ClearModel();
        }
    }

    private static string NormalizeToMdx(string path)
    {
        var clean = path.Trim().Trim('"').Replace('/', '\\');
        if (clean.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(0, clean.Length - 4) + ".mdx";
        else if (!clean.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(clean))
            clean += ".mdx";
        return clean;
    }

    private void LstModels_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstModels.SelectedItem is ModelItemVm vm)
        {
            SelectedModelPath = vm.MpqPath;
            DialogResult = true;
        }
    }

    private void BtnClearModel_Click(object sender, RoutedEventArgs e)
    {
        SelectedModelPath = string.Empty;
        DialogResult = true;
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        SelectedModelPath = TxtSelectedPath.Text.Trim();
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
        else if (e.Key == Key.Enter && TxtSearch.IsFocused && LstModels.Items.Count > 0)
        {
            LstModels.SelectedIndex = 0;
            LstModels.Focus();
            e.Handled = true;
        }
    }
}

