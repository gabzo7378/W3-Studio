using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class ModifyTilesetWindow : Window
{
    public sealed record TileEntry(string Code, string Name, string SwatchHex, int OriginalIndex = -1)
    {
        public string DisplayLabel => $"[{Code}] {Name}";
    }

    public sealed record CliffEntry(string Code, string Name)
    {
        public string DisplayLabel => $"[{Code}] {Name}";
    }

    private readonly MapProject _project;
    private readonly WarcraftBaseDataService _baseData;
    private readonly ObservableCollection<TileEntry> _currentTiles = new();
    private readonly List<TileEntry> _allGroundCatalog = new();
    private readonly ObservableCollection<CliffEntry> _currentCliffs = new();
    private readonly List<CliffEntry> _allCliffCatalog = new();

    public bool Applied { get; private set; }

    public ModifyTilesetWindow(MapProject project, WarcraftBaseDataService baseData)
    {
        _project = project;
        _baseData = baseData;
        InitializeComponent();

        foreach (var (code, name) in TerrainOperationsService.OfficialTilesets)
        {
            var item = new ComboBoxItem { Content = $"[{code}] {name}", Tag = code };
            CmbBaseTileset.Items.Add(item);
            if (code == project.Terrain.Tileset)
                CmbBaseTileset.SelectedItem = item;
        }
        if (CmbBaseTileset.SelectedItem == null && CmbBaseTileset.Items.Count > 0)
            CmbBaseTileset.SelectedIndex = 0;

        var groundCatalog = TerrainOperationsService.GetGroundTileCatalog(baseData);
        var groundByCode = groundCatalog.GroupBy(t => t.TileId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var t in groundCatalog)
        {
            _allGroundCatalog.Add(new TileEntry(t.TileId, t.DisplayName, TerrainBrushService.GetTilesetSwatchHex(t.TileId)));
        }

        for (int i = 0; i < project.Terrain.GroundTiles.Count; i++)
        {
            var code = project.Terrain.GroundTiles[i];
            var name = groundByCode.TryGetValue(code, out var def)
                ? def.DisplayName
                : TerrainBrushService.GetTilesetDisplayName(code);
            _currentTiles.Add(new TileEntry(code, name, TerrainBrushService.GetTilesetSwatchHex(code), i));
        }
        LstCurrentTiles.ItemsSource = _currentTiles;

        var cliffCatalog = TerrainOperationsService.GetCliffTileCatalog(baseData);
        var cliffByCode = cliffCatalog.GroupBy(c => c.CliffId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var c in cliffCatalog)
        {
            _allCliffCatalog.Add(new CliffEntry(c.CliffId, c.DisplayName));
        }

        foreach (var code in project.Terrain.CliffTiles)
        {
            var name = cliffByCode.TryGetValue(code, out var def) ? def.DisplayName : code;
            _currentCliffs.Add(new CliffEntry(code, name));
        }
        LstCurrentCliffs.ItemsSource = _currentCliffs;
        LstCatalogCliffs.ItemsSource = _allCliffCatalog;

        RefreshGroundCatalogFilter();
        UpdateSlotsCountText();
    }

    private void RefreshGroundCatalogFilter()
    {
        var q = TxtGroundSearch?.Text?.Trim() ?? string.Empty;
        LstCatalogTiles.ItemsSource = string.IsNullOrEmpty(q)
            ? _allGroundCatalog
            : _allGroundCatalog.Where(t =>
                t.Code.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void UpdateSlotsCountText()
    {
        TxtSlotsCount.Text = $"Suelo: {_currentTiles.Count} / 16 slots  |  Acantilados: {_currentCliffs.Count} / 16";
    }

    private void TxtGroundSearch_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshGroundCatalogFilter();

    private void LstCatalogTiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => BtnAddGroundTile_Click(sender, e);

    private void BtnAddGroundTile_Click(object sender, RoutedEventArgs e)
    {
        if (LstCatalogTiles.SelectedItem is not TileEntry picked) return;
        if (_currentTiles.Count >= 16)
        {
            MessageBox.Show(this, "Warcraft III admite un máximo de 16 texturas de suelo por mapa.", "Límite alcanzado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_currentTiles.Any(t => t.Code.Equals(picked.Code, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"La textura [{picked.Code}] ya está en el mapa.", "Textura duplicada", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _currentTiles.Add(picked with { OriginalIndex = -1 });
        LstCurrentTiles.SelectedIndex = _currentTiles.Count - 1;
        UpdateSlotsCountText();
    }

    private void BtnReplaceGroundTile_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstCurrentTiles.SelectedIndex;
        if (idx < 0 || LstCatalogTiles.SelectedItem is not TileEntry picked)
        {
            MessageBox.Show(this, "Selecciona primero una textura actual del mapa (izquierda) y una nueva del catálogo (derecha).", "Reemplazar Textura", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_currentTiles.Where((_, i) => i != idx).Any(t => t.Code.Equals(picked.Code, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"La textura [{picked.Code}] ya está asignada a otro slot.", "Textura duplicada", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int origIdx = _currentTiles[idx].OriginalIndex;
        _currentTiles[idx] = picked with { OriginalIndex = origIdx };
        LstCurrentTiles.SelectedIndex = idx;
    }

    private void BtnRemoveGroundTile_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstCurrentTiles.SelectedIndex;
        if (idx < 0) return;
        if (_currentTiles.Count <= 1)
        {
            MessageBox.Show(this, "El mapa debe conservar al menos 1 textura de suelo.", "Modify Tileset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _currentTiles.RemoveAt(idx);
        LstCurrentTiles.SelectedIndex = Math.Min(idx, _currentTiles.Count - 1);
        UpdateSlotsCountText();
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstCurrentTiles.SelectedIndex;
        if (idx <= 0) return;
        var item = _currentTiles[idx];
        _currentTiles.RemoveAt(idx);
        _currentTiles.Insert(idx - 1, item);
        LstCurrentTiles.SelectedIndex = idx - 1;
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstCurrentTiles.SelectedIndex;
        if (idx < 0 || idx >= _currentTiles.Count - 1) return;
        var item = _currentTiles[idx];
        _currentTiles.RemoveAt(idx);
        _currentTiles.Insert(idx + 1, item);
        LstCurrentTiles.SelectedIndex = idx + 1;
    }

    private void BtnAddCliff_Click(object sender, RoutedEventArgs e)
    {
        if (LstCatalogCliffs.SelectedItem is not CliffEntry picked) return;
        if (_currentCliffs.Count >= 16)
        {
            MessageBox.Show(this, "Máximo 16 cliff tilesets permitidos.", "Límite alcanzado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_currentCliffs.Any(c => c.Code.Equals(picked.Code, StringComparison.OrdinalIgnoreCase))) return;
        _currentCliffs.Add(picked);
        UpdateSlotsCountText();
    }

    private void BtnRemoveCliff_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstCurrentCliffs.SelectedIndex;
        if (idx < 0) return;
        if (_currentCliffs.Count <= 1)
        {
            MessageBox.Show(this, "El mapa debe conservar al menos 1 cliff tileset.", "Modify Tileset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _currentCliffs.RemoveAt(idx);
        UpdateSlotsCountText();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            char newTileset = CmbBaseTileset.SelectedItem is ComboBoxItem cbi && cbi.Tag is char ch
                ? ch
                : _project.Terrain.Tileset;

            var remap = new Dictionary<int, int>();
            for (int newIdx = 0; newIdx < _currentTiles.Count; newIdx++)
            {
                int orig = _currentTiles[newIdx].OriginalIndex;
                if (orig >= 0 && !remap.ContainsKey(orig))
                    remap[orig] = newIdx;
            }

            TerrainOperationsService.ApplyTilesetConfiguration(
                _project,
                newTileset,
                _currentTiles.Select(t => t.Code).ToList(),
                _currentCliffs.Select(c => c.Code).ToList(),
                remap);

            Applied = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo aplicar el tileset:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

