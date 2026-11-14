using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class MainWindow
{
    private sealed class PlayerOwnerOption
    {
        public int PlayerId { get; init; }
        public string Label { get; init; } = string.Empty;
        public override string ToString() => Label;
    }

    private sealed class CatalogPaletteItem
    {
        public string Rawcode { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ModelPath { get; init; } = string.Empty;
        public bool IsItem { get; init; }
        public bool IsBuilding { get; init; }
        public bool IsDestructible { get; init; }
        public bool IsDoodadKind { get; init; }
        public string PaletteDisplay => $"[{Rawcode}] {Name}";
    }

    private object? _activeSelectedEntity;
    private bool _suppressInspectorEvents;
    private bool _suppressPaletteModeEvents;

    private void InitializeEntityEditorControls()
    {
        var owners = new List<PlayerOwnerOption>();
        for (int i = 0; i < 12; i++)
        {
            string colorName = i switch
            {
                0 => "Rojo",
                1 => "Azul",
                2 => "Teal",
                3 => "Púrpura",
                4 => "Amarillo",
                5 => "Naranja",
                6 => "Verde",
                7 => "Rosa",
                8 => "Gris",
                9 => "Azul Claro",
                10 => "Verde Oscuro",
                _ => "Marrón"
            };
            owners.Add(new PlayerOwnerOption { PlayerId = i, Label = $"Jugador {i + 1} ({colorName})" });
        }
        owners.Add(new PlayerOwnerOption { PlayerId = 12, Label = "Neutral Hostil (Creeps)" });
        owners.Add(new PlayerOwnerOption { PlayerId = 13, Label = "Neutral Víctima" });
        owners.Add(new PlayerOwnerOption { PlayerId = 14, Label = "Neutral Extra" });
        owners.Add(new PlayerOwnerOption { PlayerId = 15, Label = "Neutral Pasivo" });

        CmbPlaceOwner.ItemsSource = owners;
        CmbPlaceOwner.DisplayMemberPath = nameof(PlayerOwnerOption.Label);
        CmbPlaceOwner.SelectedIndex = 0;

        CmbSelectedEntityOwner.ItemsSource = owners;
        CmbSelectedEntityOwner.DisplayMemberPath = nameof(PlayerOwnerOption.Label);
    }

    private void Wire3DEntityViewportEvents()
    {
        Map3DViewport.EntitySelected += Map3DViewport_EntitySelected;
        Map3DViewport.EntityMoved += Map3DViewport_EntityMoved;
        Map3DViewport.EntityPlaceRequested += Map3DViewport_EntityPlaceRequested;
        Map3DViewport.EntityDeleteRequested += Map3DViewport_EntityDeleteRequested;
        Map3DViewport.PlacementCancelled += Map3DViewport_PlacementCancelled;
    }

    // ==================== MENÚ ESCENARIO Y AVANZADO ====================

    private void BtnOpenScenarioDescription_Click(object sender, RoutedEventArgs e) => OpenScenarioWindow(0);
    private void BtnOpenScenarioPlayers_Click(object sender, RoutedEventArgs e) => OpenScenarioWindow(1);
    private void BtnOpenScenarioForces_Click(object sender, RoutedEventArgs e) => OpenScenarioWindow(2);

    private void OpenScenarioWindow(int tabIndex)
    {
        if (_currentProject == null)
        {
            MessageBox.Show("Abre primero un mapa (.w3x / .w3m) para editar su escenario.", "Escenario", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new ScenarioWindow(_currentProject, _blpIconService, () =>
        {
            UpdateMapHeaderOverlay();
            UpdateTestButtonState();
            Title = $"Warcraft III World Editor — [{_currentProject.FileName} - {StripColorCodes(_currentProject.MapName)}] (1.27b)";
            TxtStatusBarLeft.Text = "Propiedades de escenario actualizadas (recuerda Guardar Ctrl+S).";
        }, tabIndex)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void BtnOpenGameplayConstants_Click(object sender, RoutedEventArgs e) => OpenGameConstantsWindow(0);
    private void BtnOpenGameInterface_Click(object sender, RoutedEventArgs e) => OpenGameConstantsWindow(1);

    private void OpenGameConstantsWindow(int tabIndex)
    {
        if (_currentProject == null)
        {
            MessageBox.Show("Abre primero un mapa (.w3x / .w3m) para configurar Gameplay Constants / Game Interface.", "Avanzado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new GameConstantsWindow(_currentProject, _blpIconService, () =>
        {
            UpdateTestButtonState();
            TxtStatusBarLeft.Text = "Constantes / Interfaz de Juego actualizadas (war3mapMisc.txt / war3mapSkin.txt).";
        }, tabIndex)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    // ==================== PALETA DE COLOCACIÓN Y MODO 3D ====================

    private void BtnEntityToolMode_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPaletteModeEvents || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
            return;

        _suppressPaletteModeEvents = true;
        bool wantPlace = (btn.Tag as string) == "place";
        TglModeSelect.IsChecked = !wantPlace;
        TglModePlace.IsChecked = wantPlace;

        if (wantPlace && CmbPaletteSource.SelectedIndex == 0)
        {
            CmbPaletteSource.SelectedIndex = 1;
        }
        _suppressPaletteModeEvents = false;

        if (wantPlace)
        {
            RefreshToolPaletteList();
        }
        Sync3DEditorMode();
    }

    private void CmbPaletteSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPaletteModeEvents) return;
        if (CmbPaletteSource.SelectedIndex == 0 && TglModePlace != null && TglModePlace.IsChecked == true)
        {
            _suppressPaletteModeEvents = true;
            TglModePlace.IsChecked = false;
            TglModeSelect.IsChecked = true;
            _suppressPaletteModeEvents = false;
        }
        RefreshToolPaletteList();
        Sync3DEditorMode();
    }

    private void CmbPlaceOwner_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Sync3DEditorMode();
    }

    private void Sync3DEditorMode()
    {
        if (Map3DViewport == null || CmbPaletteMode == null)
            return;

        int mode = CmbPaletteMode.SelectedIndex;
        if (mode == 3)
        {
            SyncTerrainBrushToViewport();
            return;
        }

        bool isPlace = TglModePlace?.IsChecked == true &&
                       (mode == 0 || mode == 1) &&
                       _currentProject != null &&
                       FloatingToolPalette?.Visibility == Visibility.Visible;

        if (isPlace && LstToolPalette.SelectedItem is CatalogPaletteItem cat)
        {
            int owner = (CmbPlaceOwner?.SelectedItem as PlayerOwnerOption)?.PlayerId ?? 0;
            string jsMode = cat.IsDoodadKind ? "place_doodad" : "place_unit";
            Map3DViewport.SetEditorMode(jsMode, new
            {
                kind = cat.IsDoodadKind ? "doodad" : "unit",
                rawcode = cat.Rawcode,
                player = owner,
                angle = 270.0 * Math.PI / 180.0,
                scale = 1.0,
                variation = 0,
                life = 100,
                modelPath = cat.ModelPath
            });
            TxtStatusBarLeft.Text = $"Modo Colocar: [{cat.Rawcode}] {cat.Name} — Clic izquierdo en terreno para colocar, Esc o Clic derecho para salir.";
        }
        else
        {
            Map3DViewport.SetEditorMode("select", null);
        }
    }

    private List<CatalogPaletteItem> BuildPlacementCatalog(bool isDoodadMode, string filter)
    {
        var list = new List<CatalogPaletteItem>();
        var customByRaw = _currentProject?.Objects
            .Where(o => isDoodadMode
                ? (o.Category is W3ObjectCategory.Doodads or W3ObjectCategory.Destructables)
                : (o.Category is W3ObjectCategory.Units or W3ObjectCategory.Items))
            .GroupBy(o => o.Rawcode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, W3ObjectEntry>(StringComparer.OrdinalIgnoreCase);

        if (!isDoodadMode)
        {
            list.Add(new CatalogPaletteItem
            {
                Rawcode = "sloc",
                Name = "Start Location (Posición Inicial)",
                ModelPath = @"Objects\StartLocation\StartLocation.mdx",
                IsDoodadKind = false
            });
        }

        // Custom / modified map objects first
        foreach (var obj in customByRaw.Values)
        {
            string name = StripColorCodes(obj.Name);
            if (string.IsNullOrWhiteSpace(name)) name = obj.Rawcode;
            string model = ResolveModelPathFromEntry(obj) ?? _baseDataService.GetBaseObject(obj.Rawcode)?.ModelFile ?? string.Empty;
            var baseMeta = _baseDataService.GetBaseObject(obj.Rawcode);
            bool isBldg = baseMeta?.DefaultFields.TryGetValue("ubdg", out var ubdg) == true && (ubdg == "1" || ubdg == "true");

            list.Add(new CatalogPaletteItem
            {
                Rawcode = obj.Rawcode,
                Name = $"★ {name}",
                ModelPath = model,
                IsItem = obj.Category == W3ObjectCategory.Items,
                IsBuilding = isBldg,
                IsDestructible = obj.Category == W3ObjectCategory.Destructables,
                IsDoodadKind = isDoodadMode
            });
        }

        // Base Warcraft III objects
        foreach (var kvp in _baseDataService.AllBaseObjects)
        {
            var meta = kvp.Value;
            if (customByRaw.ContainsKey(meta.Rawcode))
                continue;

            bool matches = isDoodadMode
                ? (meta.Category is W3ObjectCategory.Doodads or W3ObjectCategory.Destructables)
                : (meta.Category is W3ObjectCategory.Units or W3ObjectCategory.Items);

            if (!matches || string.IsNullOrWhiteSpace(meta.ModelFile))
                continue;

            bool isBldg = meta.DefaultFields.TryGetValue("ubdg", out var ubdg) == true && (ubdg == "1" || ubdg == "true");
            list.Add(new CatalogPaletteItem
            {
                Rawcode = meta.Rawcode,
                Name = string.IsNullOrWhiteSpace(meta.Name) ? meta.Rawcode : meta.Name,
                ModelPath = meta.ModelFile,
                IsItem = meta.Category == W3ObjectCategory.Items,
                IsBuilding = isBldg,
                IsDestructible = meta.Category == W3ObjectCategory.Destructables,
                IsDoodadKind = isDoodadMode
            });
        }

        IEnumerable<CatalogPaletteItem> q = list;
        if (!string.IsNullOrEmpty(filter))
        {
            q = q.Where(c =>
                c.Rawcode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return q.OrderBy(c => c.Name.StartsWith("★") ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToList();
    }

    private static string? ResolveModelPathFromEntry(W3ObjectEntry entry)
    {
        var modelField = entry.Fields.FirstOrDefault(f => f.FieldId is "umdl" or "ifil" or "bfil" or "dfil");
        if (modelField == null) return null;
        var val = !string.IsNullOrWhiteSpace(modelField.RawValue) ? modelField.RawValue : modelField.ResolvedValue;
        return !string.IsNullOrWhiteSpace(val) ? val : null;
    }

    // ==================== EVENTOS DEL VISOR 3D (SELECCIONAR / MOVER / COLOCAR / ELIMINAR) ====================

    private void Map3DViewport_EntitySelected(string? kind, int creationNumber)
    {
        if (_currentProject == null || string.IsNullOrEmpty(kind))
        {
            MapViewport.SelectedEntity = null;
            MapViewport.InvalidateVisual();
            MapViewport_SelectionChanged(null);
            return;
        }

        object? found = kind == "unit"
            ? _currentProject.PlacedUnits.FirstOrDefault(u => u.CreationNumber == creationNumber)
            : _currentProject.PlacedDoodads.FirstOrDefault(d => d.CreationNumber == creationNumber);

        MapViewport.SelectedEntity = found;
        MapViewport.InvalidateVisual();
        MapViewport_SelectionChanged(found);
    }

    private void Map3DViewport_EntityMoved(string kind, int creationNumber, float x, float y, float z)
    {
        if (_currentProject == null) return;

        if (kind == "unit")
        {
            var u = _currentProject.PlacedUnits.FirstOrDefault(item => item.CreationNumber == creationNumber);
            if (u == null) return;
            u.X = x; u.Y = y; u.Z = z;
            if (u.IsStartLocation && _currentProject.MapInfo != null)
            {
                var p = _currentProject.MapInfo.Players.FirstOrDefault(pl => pl.Id == u.PlayerId);
                if (p != null)
                {
                    p.StartPosition = new System.Numerics.Vector2(x, y);
                    _currentProject.HasScenarioEdits = true;
                }
            }
            MapViewport_SelectionChanged(u);
        }
        else
        {
            var d = _currentProject.PlacedDoodads.FirstOrDefault(item => item.CreationNumber == creationNumber);
            if (d == null) return;
            d.X = x; d.Y = y; d.Z = z;
            MapViewport_SelectionChanged(d);
        }

        _currentProject.HasUnitDoodadEdits = true;
        MapViewport.MarkViewportDirty();
        UpdateTestButtonState();
    }

    private void Map3DViewport_EntityPlaceRequested(string kind, string rawcode, float x, float y, float z, float angle, float scale, int player, int variation)
    {
        if (_currentProject == null) return;

        string cleanName = rawcode;
        var customObj = _currentProject.Objects.FirstOrDefault(o => o.Rawcode.Equals(rawcode, StringComparison.OrdinalIgnoreCase));
        if (rawcode == "sloc")
            cleanName = $"Posición Inicial Jugador {player + 1}";
        else if (customObj != null && !string.IsNullOrWhiteSpace(customObj.Name))
            cleanName = StripColorCodes(customObj.Name);
        else
            cleanName = _baseDataService.GetBaseObject(rawcode)?.Name ?? rawcode;

        string modelPath = (customObj != null ? ResolveModelPathFromEntry(customObj) : null)
                           ?? _baseDataService.GetBaseObject(rawcode)?.ModelFile
                           ?? string.Empty;

        if (kind == "unit")
        {
            int nextNum = _currentProject.PlacedUnits.Count > 0
                ? _currentProject.PlacedUnits.Max(u => u.CreationNumber) + 1
                : 0;
            var baseMeta = _baseDataService.GetBaseObject(rawcode);
            bool isItem = (customObj?.Category ?? baseMeta?.Category) == W3ObjectCategory.Items;
            bool isBldg = baseMeta?.DefaultFields.TryGetValue("ubdg", out var ubdg) == true && (ubdg == "1" || ubdg == "true");

            var placed = new W3PlacedUnit
            {
                Rawcode = rawcode,
                Name = cleanName,
                CreationNumber = nextNum,
                PlayerId = player,
                X = x,
                Y = y,
                Z = z,
                AngleRadians = angle,
                Scale = scale,
                ScaleY = scale,
                ScaleZ = scale,
                Flags = 2,
                Level = 1,
                IsItem = isItem,
                IsBuilding = isBldg
            };

            _currentProject.PlacedUnits.Add(placed);
            _currentProject.HasUnitDoodadEdits = true;

            Map3DViewport.SpawnLiveEntity("unit", new
            {
                id = rawcode,
                creationNumber = nextNum,
                x, y, z,
                angle, scale, player,
                modelPath
            });

            MapViewport.SelectedEntity = placed;
            MapViewport.MarkViewportDirty();
            MapViewport_SelectionChanged(placed);
        }
        else
        {
            int nextNum = _currentProject.PlacedDoodads.Count > 0
                ? _currentProject.PlacedDoodads.Max(d => d.CreationNumber) + 1
                : 0;
            var baseMeta = _baseDataService.GetBaseObject(rawcode);
            bool isDest = (customObj?.Category ?? baseMeta?.Category) == W3ObjectCategory.Destructables;

            var placed = new W3PlacedDoodad
            {
                Rawcode = rawcode,
                Name = cleanName,
                CreationNumber = nextNum,
                Variation = variation,
                X = x,
                Y = y,
                Z = z,
                AngleRadians = angle,
                Scale = scale,
                ScaleY = scale,
                ScaleZ = scale,
                Flags = 2,
                Life = 100,
                IsDestructible = isDest
            };

            _currentProject.PlacedDoodads.Add(placed);
            _currentProject.HasUnitDoodadEdits = true;

            Map3DViewport.SpawnLiveEntity("doodad", new
            {
                id = rawcode,
                creationNumber = nextNum,
                x, y, z,
                angle, scale, variation, life = 100,
                modelPath
            });

            MapViewport.SelectedEntity = placed;
            MapViewport.MarkViewportDirty();
            MapViewport_SelectionChanged(placed);
        }

        UpdateMapHeaderOverlay();
        UpdateTestButtonState();
    }

    private void Map3DViewport_EntityDeleteRequested(string kind, int creationNumber)
    {
        if (_currentProject == null) return;

        if (kind == "unit")
        {
            var u = _currentProject.PlacedUnits.FirstOrDefault(x => x.CreationNumber == creationNumber);
            if (u != null)
            {
                _currentProject.PlacedUnits.Remove(u);
                Map3DViewport.DeleteLiveEntity("unit", creationNumber);
            }
        }
        else
        {
            var d = _currentProject.PlacedDoodads.FirstOrDefault(x => x.CreationNumber == creationNumber);
            if (d != null)
            {
                _currentProject.PlacedDoodads.Remove(d);
                Map3DViewport.DeleteLiveEntity("doodad", creationNumber);
            }
        }

        _currentProject.HasUnitDoodadEdits = true;
        MapViewport.SelectedEntity = null;
        MapViewport.MarkViewportDirty();
        MapViewport_SelectionChanged(null);
        if (CmbPaletteSource.SelectedIndex == 0)
            RefreshToolPaletteList();
        UpdateMapHeaderOverlay();
        UpdateTestButtonState();
        TxtStatusBarLeft.Text = "Elemento eliminado del mapa (recuerda Guardar Ctrl+S).";
    }

    private void Map3DViewport_PlacementCancelled()
    {
        _suppressPaletteModeEvents = true;
        if (TglModePlace != null) TglModePlace.IsChecked = false;
        if (TglModeSelect != null) TglModeSelect.IsChecked = true;
        _suppressPaletteModeEvents = false;
        TxtStatusBarLeft.Text = "Modo Selección activo (clic para seleccionar o arrastrar entidades).";
    }

    // ==================== INSPECTOR LATERAL IZQUIERDO ====================

    private void PopulateSelectedEntityEditor(object? entity)
    {
        _activeSelectedEntity = entity;
        if (PnlSelectedEntityEditor == null) return;

        _suppressInspectorEvents = true;
        if (entity is W3PlacedUnit u)
        {
            PnlSelectedEntityEditor.Visibility = Visibility.Visible;
            PnlUnitOwnerRow.Visibility = Visibility.Visible;
            PnlUnitExtraRow.Visibility = Visibility.Visible;
            PnlDoodadVarRow.Visibility = Visibility.Collapsed;
            LblSelectedEntityHp.Text = "Vida (% / -1):";

            var ownerMatch = (CmbSelectedEntityOwner.ItemsSource as IEnumerable<PlayerOwnerOption>)?.FirstOrDefault(o => o.PlayerId == u.PlayerId);
            CmbSelectedEntityOwner.SelectedItem = ownerMatch;
            TxtSelectedEntityAngle.Text = u.AngleDegrees.ToString("0.#", CultureInfo.InvariantCulture);
            TxtSelectedEntityScale.Text = u.Scale.ToString("0.##", CultureInfo.InvariantCulture);
            TxtSelectedEntityHp.Text = u.HP.ToString(CultureInfo.InvariantCulture);
            TxtSelectedEntityMp.Text = u.MP.ToString(CultureInfo.InvariantCulture);
            TxtSelectedEntityLevel.Text = Math.Max(1, u.Level).ToString(CultureInfo.InvariantCulture);
        }
        else if (entity is W3PlacedDoodad d)
        {
            PnlSelectedEntityEditor.Visibility = Visibility.Visible;
            PnlUnitOwnerRow.Visibility = Visibility.Collapsed;
            PnlUnitExtraRow.Visibility = Visibility.Collapsed;
            PnlDoodadVarRow.Visibility = Visibility.Visible;
            LblSelectedEntityHp.Text = "Vida (0=Muerto, 100):";

            TxtSelectedEntityAngle.Text = d.AngleDegrees.ToString("0.#", CultureInfo.InvariantCulture);
            TxtSelectedEntityScale.Text = d.Scale.ToString("0.##", CultureInfo.InvariantCulture);
            TxtSelectedEntityHp.Text = d.Life.ToString(CultureInfo.InvariantCulture);
            TxtSelectedEntityVariation.Text = d.Variation.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            PnlSelectedEntityEditor.Visibility = Visibility.Collapsed;
        }
        _suppressInspectorEvents = false;
    }

    private void CmbSelectedEntityOwner_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInspectorEvents || _activeSelectedEntity is not W3PlacedUnit u || _currentProject == null)
            return;

        if (CmbSelectedEntityOwner.SelectedItem is PlayerOwnerOption opt)
        {
            u.PlayerId = opt.PlayerId;
            _currentProject.HasUnitDoodadEdits = true;
            Map3DViewport.UpdateLiveEntity("unit", u.CreationNumber, new { player = u.PlayerId });
            TxtSelectedMapEntityOwner.Text = $"Dueño: {u.PlayerName}";
            MapViewport.MarkViewportDirty();
            UpdateTestButtonState();
        }
    }

    private void BtnRotateEntityLeft_Click(object sender, RoutedEventArgs e) => AdjustEntityAngle(-45.0);
    private void BtnRotateEntityRight_Click(object sender, RoutedEventArgs e) => AdjustEntityAngle(45.0);

    private void AdjustEntityAngle(double deltaDeg)
    {
        if (_activeSelectedEntity == null || _currentProject == null) return;
        double currentDeg = 270.0;
        if (double.TryParse(TxtSelectedEntityAngle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            currentDeg = parsed;

        double next = (currentDeg + deltaDeg) % 360.0;
        if (next < 0) next += 360.0;
        TxtSelectedEntityAngle.Text = next.ToString("0.#", CultureInfo.InvariantCulture);
        BtnApplyEntityProps_Click(this, new RoutedEventArgs());
    }

    private void BtnApplyEntityProps_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null || _activeSelectedEntity == null) return;

        if (_activeSelectedEntity is W3PlacedUnit u)
        {
            if (double.TryParse(TxtSelectedEntityAngle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
                u.AngleDegrees = deg;
            if (float.TryParse(TxtSelectedEntityScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sc))
                u.Scale = u.ScaleY = u.ScaleZ = Math.Clamp(sc, 0.1f, 20f);
            if (int.TryParse(TxtSelectedEntityHp.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp))
                u.HP = hp;
            if (int.TryParse(TxtSelectedEntityMp.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp))
                u.MP = mp;
            if (int.TryParse(TxtSelectedEntityLevel.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
                u.Level = Math.Clamp(lvl, 1, 100);
            if (CmbSelectedEntityOwner.SelectedItem is PlayerOwnerOption opt)
                u.PlayerId = opt.PlayerId;

            Map3DViewport.UpdateLiveEntity("unit", u.CreationNumber, new
            {
                angle = u.AngleRadians,
                scale = u.Scale,
                player = u.PlayerId
            });
            MapViewport_SelectionChanged(u);
        }
        else if (_activeSelectedEntity is W3PlacedDoodad d)
        {
            if (double.TryParse(TxtSelectedEntityAngle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
                d.AngleDegrees = deg;
            if (float.TryParse(TxtSelectedEntityScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sc))
                d.Scale = d.ScaleY = d.ScaleZ = Math.Clamp(sc, 0.1f, 20f);
            if (byte.TryParse(TxtSelectedEntityHp.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var life))
                d.Life = Math.Clamp(life, (byte)0, (byte)100);
            if (int.TryParse(TxtSelectedEntityVariation.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vr))
                d.Variation = Math.Max(0, vr);

            Map3DViewport.UpdateLiveEntity("doodad", d.CreationNumber, new
            {
                angle = d.AngleRadians,
                scale = d.Scale,
                life = (int)d.Life
            });
            MapViewport_SelectionChanged(d);
        }

        _currentProject.HasUnitDoodadEdits = true;
        MapViewport.MarkViewportDirty();
        UpdateTestButtonState();
    }

    private void BtnDeleteSelectedEntity_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSelectedEntity is W3PlacedUnit u)
            Map3DViewport_EntityDeleteRequested("unit", u.CreationNumber);
        else if (_activeSelectedEntity is W3PlacedDoodad d)
            Map3DViewport_EntityDeleteRequested("doodad", d.CreationNumber);
    }

    private void UpdateMapHeaderOverlay()
    {
        if (_currentProject == null) return;
        TxtMapOverlayHeader.Text = $"{StripColorCodes(_currentProject.MapName)} ({_currentProject.Terrain.GridWidth - 1}x{_currentProject.Terrain.GridHeight - 1} • {_currentProject.Terrain.TilesetName} • {_currentProject.PlacedUnits.Count} Unidades • {_currentProject.PlacedDoodads.Count:N0} Árboles/Doodads • {_currentProject.Regions.Count} Regiones)";
    }
}
