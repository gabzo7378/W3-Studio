using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using W3Studio.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public sealed class PlayerOwnerOption
{
    public int PlayerId { get; init; }
    public string Label { get; init; } = string.Empty;
    public override string ToString() => Label;
}

public partial class MainWindow
{
    private CancellationTokenSource? _paletteIconLoadCts;

    private sealed class CatalogPaletteItem : INotifyPropertyChanged
    {
        private ImageSource? _iconBitmap;

        public string Rawcode { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Race { get; init; } = string.Empty;
        public string ModelPath { get; init; } = string.Empty;
        public string IconPath { get; init; } = string.Empty;
        public bool IsCustom { get; init; }
        public bool IsHero { get; init; }
        public bool IsItem { get; init; }
        public bool IsBuilding { get; init; }
        public bool IsDestructible { get; init; }
        public bool IsDoodadKind { get; init; }
        public Visibility CustomBadgeVisibility => IsCustom ? Visibility.Visible : Visibility.Collapsed;
        public string PaletteDisplay => IsCustom ? $"[{Rawcode}] ★ {Name}" : $"[{Rawcode}] {Name}";

        public ImageSource? IconBitmap
        {
            get => _iconBitmap;
            set
            {
                if (!ReferenceEquals(_iconBitmap, value))
                {
                    _iconBitmap = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ClipboardEntityEntry
    {
        public bool IsUnit { get; init; }
        public W3PlacedUnit? UnitClone { get; init; }
        public W3PlacedDoodad? DoodadClone { get; init; }
        public float OrigX { get; init; }
        public float OrigY { get; init; }
    }

    private object? _activeSelectedEntity;
    private readonly List<object> _selectedEntities = new();
    private readonly List<ClipboardEntityEntry> _entityClipboard = new();
    private W3MapRegion? _selectedRegion;
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
        Map3DViewport.EntitiesSelected += Map3DViewport_EntitiesSelected;
        Map3DViewport.EntityPropertiesRequested += Map3DViewport_EntityPropertiesRequested;
        Map3DViewport.EntityMoved += Map3DViewport_EntityMoved;
        Map3DViewport.EntitiesMoved += Map3DViewport_EntitiesMoved;
        Map3DViewport.EntitiesRotated += Map3DViewport_EntitiesRotated;
        Map3DViewport.EntityPlaceRequested += Map3DViewport_EntityPlaceRequested;
        Map3DViewport.EntityDeleteRequested += Map3DViewport_EntityDeleteRequested;
        Map3DViewport.EntitiesDeleteRequested += Map3DViewport_EntitiesDeleteRequested;
        Map3DViewport.EntityClipboardRequested += Map3DViewport_EntityClipboardRequested;
        Map3DViewport.RegionSelected += Map3DViewport_RegionSelected;
        Map3DViewport.RegionUpdated += Map3DViewport_RegionUpdated;
        Map3DViewport.RegionCreateRequested += Map3DViewport_RegionCreateRequested;
        Map3DViewport.RegionDeleteRequested += Map3DViewport_RegionDeleteRequested;
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

    private void CmbUnitCatalogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPaletteModeEvents) return;
        RefreshToolPaletteList();
    }

    private void ChkIgnorePathing_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkIgnorePathing == null || _settings == null)
            return;

        _settings.IgnoreEntityPathing = ChkIgnorePathing.IsChecked == true;
        _settings.Save();
        Map3DViewport?.SetPathingOptions(_settings.IgnoreEntityPathing);
    }

    private void Sync3DPathingMetadata()
    {
        if (Map3DViewport == null)
            return;

        Map3DViewport.SetPathingOptions(_settings.IgnoreEntityPathing);
        if (_currentProject != null)
        {
            var overrides = PathingService.BuildClientOverrides(_currentProject, _baseDataService);
            Map3DViewport.SetCustomObjectOverrides(overrides);
        }
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

        if (mode == 2)
        {
            bool drawRegion = TglRegionPlace?.IsChecked == true &&
                              _currentProject != null &&
                              FloatingToolPalette?.Visibility == Visibility.Visible;
            Map3DViewport.SetEditorMode(drawRegion ? "place_region" : "select_region", null);
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
            string pathTex = cat.IsDoodadKind
                ? PathingService.ResolveObjectField(cat.Rawcode, true, _currentProject, _baseDataService, cat.IsDestructible ? "bptx" : "dptx", "pathTex")
                : PathingService.ResolveObjectField(cat.Rawcode, false, _currentProject, _baseDataService, "upat", "pathTex");
            string moveType = cat.IsDoodadKind ? string.Empty : PathingService.ResolveObjectField(cat.Rawcode, false, _currentProject, _baseDataService, "umvt", "movetp");
            string preventPlace = cat.IsDoodadKind ? string.Empty : PathingService.ResolveObjectField(cat.Rawcode, false, _currentProject, _baseDataService, "upap", "preventPlace");
            string requirePlace = cat.IsDoodadKind ? string.Empty : PathingService.ResolveObjectField(cat.Rawcode, false, _currentProject, _baseDataService, "upar", "requirePlace");

            Map3DViewport.SetEditorMode(jsMode, new
            {
                kind = cat.IsDoodadKind ? "doodad" : "unit",
                rawcode = cat.Rawcode,
                player = owner,
                angle = 270.0 * Math.PI / 180.0,
                scale = 1.0,
                variation = 0,
                life = 100,
                modelPath = cat.ModelPath,
                isBuilding = cat.IsBuilding,
                isDestructible = cat.IsDestructible,
                pathTex,
                moveType,
                preventPlace,
                requirePlace
            });
            TxtStatusBarLeft.Text = $"Modo Colocar: [{cat.Rawcode}] {cat.Name} — Clic izquierdo en terreno para colocar, Esc o Clic derecho para salir.";
        }
        else
        {
            Map3DViewport.SetEditorMode("select", null);
        }
    }

    private List<CatalogPaletteItem> BuildPlacementCatalog(
        bool isDoodadMode,
        string filter,
        string? raceFilter = null,
        string? typeFilter = null)
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
                Race = "other",
                ModelPath = @"Objects\StartLocation\StartLocation.mdx",
                IconPath = ResolvePaletteIconPath("sloc", null, null),
                IsDoodadKind = false
            });
        }

        // Custom / modified map objects first
        foreach (var obj in customByRaw.Values)
        {
            string name = StripColorCodes(obj.Name);
            if (string.IsNullOrWhiteSpace(name)) name = obj.Rawcode;
            var baseLookupKey = !string.IsNullOrEmpty(obj.BaseRawcode) ? obj.BaseRawcode : obj.Rawcode;
            var baseMeta = _baseDataService.GetBaseObject(baseLookupKey) ?? _baseDataService.GetBaseObject(obj.Rawcode);
            string model = ResolveModelPathFromEntry(obj) ?? baseMeta?.ModelFile ?? string.Empty;
            bool isItem = obj.Category == W3ObjectCategory.Items;
            bool isBldg = obj.Category == W3ObjectCategory.Units &&
                          PathingService.IsUnitBuilding(obj.Rawcode, _currentProject, _baseDataService);
            bool isHero = UnitPaletteFilterHelper.IsHeroUnit(obj.Category, obj.Rawcode, obj.BaseRawcode);
            string race = UnitPaletteFilterHelper.ResolveUnitRace(obj, baseMeta, isItem);

            list.Add(new CatalogPaletteItem
            {
                Rawcode = obj.Rawcode,
                Name = name,
                Race = race,
                ModelPath = model,
                IconPath = ResolvePaletteIconPath(obj.Rawcode, obj, baseMeta),
                IsCustom = true,
                IsHero = isHero,
                IsItem = isItem,
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

            bool isItem = meta.Category == W3ObjectCategory.Items;
            bool isBldg = meta.Category == W3ObjectCategory.Units &&
                          PathingService.IsUnitBuilding(meta.Rawcode, _currentProject, _baseDataService);
            bool isHero = UnitPaletteFilterHelper.IsHeroUnit(meta.Category, meta.Rawcode, null);
            string race = UnitPaletteFilterHelper.ResolveUnitRace(null, meta, isItem);

            list.Add(new CatalogPaletteItem
            {
                Rawcode = meta.Rawcode,
                Name = string.IsNullOrWhiteSpace(meta.Name) ? meta.Rawcode : meta.Name,
                Race = race,
                ModelPath = meta.ModelFile,
                IconPath = ResolvePaletteIconPath(meta.Rawcode, null, meta),
                IsCustom = false,
                IsHero = isHero,
                IsItem = isItem,
                IsBuilding = isBldg,
                IsDestructible = meta.Category == W3ObjectCategory.Destructables,
                IsDoodadKind = isDoodadMode
            });
        }

        IEnumerable<CatalogPaletteItem> q = list;
        if (!isDoodadMode)
        {
            q = q.Where(c =>
                UnitPaletteFilterHelper.MatchesRaceFilter(raceFilter, c.Race, c.IsCustom, c.IsItem, typeFilter) &&
                UnitPaletteFilterHelper.MatchesTypeFilter(typeFilter, c.IsHero, c.IsBuilding, c.IsItem, c.Rawcode));
        }

        if (!string.IsNullOrEmpty(filter))
        {
            q = q.Where(c =>
                c.Rawcode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return q.OrderBy(c => c.IsCustom ? 0 : 1)
                .ThenBy(c => c.Rawcode == "sloc" ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(isDoodadMode ? 500 : 2500)
                .ToList();
    }

    private static string ResolvePaletteIconPath(string rawcode, W3ObjectEntry? customObj, BaseObjectMetadata? baseMeta)
    {
        if (string.Equals(rawcode, "sloc", StringComparison.OrdinalIgnoreCase))
            return @"ReplaceableTextures\CommandButtons\BTNSelectHeroOn.blp";

        string? rawIcon = null;
        if (customObj != null)
        {
            var artField = customObj.Fields.FirstOrDefault(f => f.FieldId is "uico" or "iico" or "bico");
            if (artField != null)
            {
                var candidate = !string.IsNullOrWhiteSpace(artField.RawValue) ? artField.RawValue : artField.ResolvedValue;
                if (!string.IsNullOrWhiteSpace(candidate) && candidate != "_")
                    rawIcon = candidate;
            }
            if (string.IsNullOrWhiteSpace(rawIcon) && !string.IsNullOrWhiteSpace(customObj.IconPath))
                rawIcon = customObj.IconPath;
        }

        if (string.IsNullOrWhiteSpace(rawIcon))
            rawIcon = baseMeta?.IconPath;

        if (string.IsNullOrWhiteSpace(rawIcon) && baseMeta != null &&
            baseMeta.DefaultFields.TryGetValue("Art", out var slkArt) &&
            !string.IsNullOrWhiteSpace(slkArt) && slkArt != "_")
        {
            rawIcon = slkArt;
        }

        if (string.IsNullOrWhiteSpace(rawIcon))
            return string.Empty;

        int comma = rawIcon.IndexOf(',');
        if (comma >= 0)
            rawIcon = rawIcon[..comma];

        return rawIcon.Trim();
    }

    private void CancelPaletteIconHydration()
    {
        try
        {
            _paletteIconLoadCts?.Cancel();
            _paletteIconLoadCts?.Dispose();
        }
        catch { }
        _paletteIconLoadCts = null;
    }

    private void HydratePaletteIconsAsync(IReadOnlyList<CatalogPaletteItem> items)
    {
        CancelPaletteIconHydration();
        if (items.Count == 0) return;

        var project = _currentProject;
        var cts = new CancellationTokenSource();
        _paletteIconLoadCts = cts;
        var token = cts.Token;

        // Touch FallbackIcon on UI thread so any fallback reference is initialized and frozen.
        var fallback = _blpIconService.FallbackIcon;

        int immediateCount = Math.Min(items.Count, 60);
        for (int i = 0; i < immediateCount; i++)
        {
            var item = items[i];
            item.IconBitmap = string.IsNullOrWhiteSpace(item.IconPath)
                ? fallback
                : _blpIconService.GetIcon(item.IconPath, project);
        }

        if (items.Count <= immediateCount)
            return;

        _ = Task.Run(() =>
        {
            var batch = new List<(CatalogPaletteItem Item, ImageSource Img)>(48);
            for (int i = immediateCount; i < items.Count; i++)
            {
                if (token.IsCancellationRequested)
                    return;

                var item = items[i];
                var bmp = string.IsNullOrWhiteSpace(item.IconPath)
                    ? fallback
                    : _blpIconService.GetIcon(item.IconPath, project);
                batch.Add((item, bmp));

                if (batch.Count >= 48 || i == items.Count - 1)
                {
                    var snapshot = batch.ToArray();
                    batch.Clear();
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        foreach (var pair in snapshot)
                            pair.Item.IconBitmap = pair.Img;
                    }));
                }
            }
        }, token);
    }

    private static string? ResolveModelPathFromEntry(W3ObjectEntry entry)
    {
        var modelField = entry.Fields.FirstOrDefault(f => f.FieldId is "umdl" or "ifil" or "bfil" or "dfil");
        if (modelField == null) return null;
        var val = !string.IsNullOrWhiteSpace(modelField.RawValue) ? modelField.RawValue : modelField.ResolvedValue;
        return !string.IsNullOrWhiteSpace(val) ? val : null;
    }

    // ==================== EVENTOS DEL VISOR 3D (SELECCIONAR / MOVER / COLOCAR / ELIMINAR) ====================

    private object? ResolveEntityByKey(string kind, int creationNumber)
    {
        if (_currentProject == null) return null;
        return kind == "unit"
            ? _currentProject.PlacedUnits.FirstOrDefault(u => u.CreationNumber == creationNumber)
            : _currentProject.PlacedDoodads.FirstOrDefault(d => d.CreationNumber == creationNumber);
    }

    private void Map3DViewport_EntitySelected(string? kind, int creationNumber)
    {
        _selectedEntities.Clear();
        if (_currentProject == null || string.IsNullOrEmpty(kind))
        {
            MapViewport.SelectedEntity = null;
            MapViewport.InvalidateVisual();
            MapViewport_SelectionChanged(null);
            return;
        }

        object? found = ResolveEntityByKey(kind, creationNumber);
        if (found != null)
            _selectedEntities.Add(found);

        MapViewport.SelectedEntity = found;
        MapViewport.InvalidateVisual();
        MapViewport_SelectionChanged(found);
    }

    private void Map3DViewport_EntitiesSelected(IReadOnlyList<ViewportEntityKey> keys)
    {
        _selectedEntities.Clear();
        if (_currentProject != null)
        {
            foreach (var k in keys)
            {
                var ent = ResolveEntityByKey(k.Kind, k.CreationNumber);
                if (ent != null && !_selectedEntities.Contains(ent))
                    _selectedEntities.Add(ent);
            }
        }

        object? primary = _selectedEntities.FirstOrDefault();
        MapViewport.SelectedEntity = primary;
        MapViewport.InvalidateVisual();
        MapViewport_SelectionChanged(primary);
        if (_selectedEntities.Count > 1 && TxtSelectedMapEntityTitle != null)
        {
            TxtSelectedMapEntityTitle.Text = $"{_selectedEntities.Count} entidades seleccionadas";
            TxtStatusBarLeft.Text = $"Selección múltiple: {_selectedEntities.Count} entidades (Arrastra para mover grupo, Ctrl+Drag/[ / ] rotar, Supr eliminar, Ctrl+C/X/D).";
        }
    }

    private void Map3DViewport_EntityPropertiesRequested(string kind, int creationNumber)
    {
        var target = ResolveEntityByKey(kind, creationNumber);
        if (target == null) return;

        if (!_selectedEntities.Contains(target))
        {
            _selectedEntities.Clear();
            _selectedEntities.Add(target);
        }
        _activeSelectedEntity = target;
        OpenSelectedEntityPropertiesDialog();
    }

    private void Map3DViewport_EntitiesMoved(IReadOnlyList<ViewportEntityMoveItem> items)
    {
        if (_currentProject == null || items.Count == 0) return;

        var records = new List<(object Entity, string Kind, int CreationNumber, float OldX, float OldY, float OldZ, float NewX, float NewY, float NewZ)>();
        foreach (var it in items)
        {
            var ent = ResolveEntityByKey(it.Kind, it.CreationNumber);
            if (ent is W3PlacedUnit u)
                records.Add((u, "unit", u.CreationNumber, u.X, u.Y, u.Z, it.X, it.Y, it.Z));
            else if (ent is W3PlacedDoodad d)
                records.Add((d, "doodad", d.CreationNumber, d.X, d.Y, d.Z, it.X, it.Y, it.Z));
        }

        if (records.Count == 0) return;

        void ApplyAll(bool useNew)
        {
            foreach (var r in records)
            {
                float tx = useNew ? r.NewX : r.OldX;
                float ty = useNew ? r.NewY : r.OldY;
                float tz = useNew ? r.NewZ : r.OldZ;
                if (r.Entity is W3PlacedUnit u)
                    ApplyUnitPosition(u, tx, ty, tz);
                else if (r.Entity is W3PlacedDoodad d)
                {
                    d.X = tx; d.Y = ty; d.Z = tz;
                }
                Map3DViewport.UpdateLiveEntity(r.Kind, r.CreationNumber, new { x = tx, y = ty, z = tz });
            }
            RefreshEntityAfterEdit(records[0].Entity);
        }

        ApplyAll(true);
        _undoService.Record(new DelegateUndoEdit(
            $"Mover {records.Count} entidades",
            () => ApplyAll(false),
            () => ApplyAll(true)));
    }

    private void Map3DViewport_EntitiesRotated(IReadOnlyList<ViewportEntityRotateItem> items)
    {
        if (_currentProject == null || items.Count == 0) return;

        var records = new List<(object Entity, string Kind, int CreationNumber, float OldAngle, float NewAngle)>();
        foreach (var it in items)
        {
            var ent = ResolveEntityByKey(it.Kind, it.CreationNumber);
            if (ent is W3PlacedUnit u)
                records.Add((u, "unit", u.CreationNumber, u.AngleRadians, it.AngleRadians));
            else if (ent is W3PlacedDoodad d)
                records.Add((d, "doodad", d.CreationNumber, d.AngleRadians, it.AngleRadians));
        }

        if (records.Count == 0) return;

        void ApplyAll(bool useNew)
        {
            foreach (var r in records)
            {
                float ang = useNew ? r.NewAngle : r.OldAngle;
                if (r.Entity is W3PlacedUnit u) u.AngleRadians = ang;
                else if (r.Entity is W3PlacedDoodad d) d.AngleRadians = ang;
                Map3DViewport.UpdateLiveEntity(r.Kind, r.CreationNumber, new { angle = ang });
            }
            RefreshEntityAfterEdit(records[0].Entity);
        }

        ApplyAll(true);
        string desc = records.Count == 1 ? "Rotar entidad" : $"Rotar {records.Count} entidades";
        _undoService.Record(new DelegateUndoEdit(desc, () => ApplyAll(false), () => ApplyAll(true)));
    }

    private void Map3DViewport_EntityMoved(string kind, int creationNumber, float x, float y, float z)
    {
        if (_currentProject == null) return;

        if (kind == "unit")
        {
            var u = _currentProject.PlacedUnits.FirstOrDefault(item => item.CreationNumber == creationNumber);
            if (u == null) return;
            float ox = u.X, oy = u.Y, oz = u.Z;
            ApplyUnitPosition(u, x, y, z);
            _undoService.Record(new DelegateUndoEdit(
                $"Mover {u.Name}",
                () => { ApplyUnitPosition(u, ox, oy, oz); Map3DViewport.UpdateLiveEntity("unit", u.CreationNumber, new { x = ox, y = oy, z = oz }); RefreshEntityAfterEdit(u); },
                () => { ApplyUnitPosition(u, x, y, z); Map3DViewport.UpdateLiveEntity("unit", u.CreationNumber, new { x, y, z }); RefreshEntityAfterEdit(u); }));
        }
        else
        {
            var d = _currentProject.PlacedDoodads.FirstOrDefault(item => item.CreationNumber == creationNumber);
            if (d == null) return;
            float ox = d.X, oy = d.Y, oz = d.Z;
            d.X = x; d.Y = y; d.Z = z;
            MapViewport_SelectionChanged(d);
            _undoService.Record(new DelegateUndoEdit(
                $"Mover {d.Name}",
                () => { d.X = ox; d.Y = oy; d.Z = oz; Map3DViewport.UpdateLiveEntity("doodad", d.CreationNumber, new { x = ox, y = oy, z = oz }); RefreshEntityAfterEdit(d); },
                () => { d.X = x; d.Y = y; d.Z = z; Map3DViewport.UpdateLiveEntity("doodad", d.CreationNumber, new { x, y, z }); RefreshEntityAfterEdit(d); }));
        }

        _currentProject.HasUnitDoodadEdits = true;
        MapViewport.MarkViewportDirty();
        UpdateTestButtonState();
    }

    private void ApplyUnitPosition(W3PlacedUnit u, float x, float y, float z)
    {
        u.X = x; u.Y = y; u.Z = z;
        if (u.IsStartLocation && _currentProject?.MapInfo != null)
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

    private void RefreshEntityAfterEdit(object entity)
    {
        if (_currentProject == null) return;
        _currentProject.HasUnitDoodadEdits = true;
        MapViewport.MarkViewportDirty();
        MapViewport_SelectionChanged(entity);
        UpdateMapHeaderOverlay();
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
            bool isBldg = !isItem && PathingService.IsUnitBuilding(rawcode, _currentProject, _baseDataService);

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

            var spawnOpts = new
            {
                id = rawcode,
                creationNumber = nextNum,
                x, y, z,
                angle, scale, player,
                modelPath
            };
            Map3DViewport.SpawnLiveEntity("unit", spawnOpts);

            _undoService.Record(new DelegateUndoEdit(
                $"Colocar {cleanName}",
                () =>
                {
                    _currentProject.PlacedUnits.Remove(placed);
                    Map3DViewport.DeleteLiveEntity("unit", placed.CreationNumber);
                    RefreshAfterEntityListChange(null);
                },
                () =>
                {
                    if (!_currentProject.PlacedUnits.Contains(placed))
                        _currentProject.PlacedUnits.Add(placed);
                    Map3DViewport.SpawnLiveEntity("unit", spawnOpts);
                    RefreshAfterEntityListChange(placed);
                }));

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

            var spawnOpts = new
            {
                id = rawcode,
                creationNumber = nextNum,
                x, y, z,
                angle, scale, variation, life = 100,
                modelPath
            };
            Map3DViewport.SpawnLiveEntity("doodad", spawnOpts);

            _undoService.Record(new DelegateUndoEdit(
                $"Colocar {cleanName}",
                () =>
                {
                    _currentProject.PlacedDoodads.Remove(placed);
                    Map3DViewport.DeleteLiveEntity("doodad", placed.CreationNumber);
                    RefreshAfterEntityListChange(null);
                },
                () =>
                {
                    if (!_currentProject.PlacedDoodads.Contains(placed))
                        _currentProject.PlacedDoodads.Add(placed);
                    Map3DViewport.SpawnLiveEntity("doodad", spawnOpts);
                    RefreshAfterEntityListChange(placed);
                }));

            MapViewport.SelectedEntity = placed;
            MapViewport.MarkViewportDirty();
            MapViewport_SelectionChanged(placed);
        }

        UpdateMapHeaderOverlay();
        UpdateTestButtonState();
    }

    private void RefreshAfterEntityListChange(object? selected)
    {
        if (_currentProject == null) return;
        _currentProject.HasUnitDoodadEdits = true;
        MapViewport.SelectedEntity = selected;
        MapViewport.MarkViewportDirty();
        MapViewport_SelectionChanged(selected);
        if (CmbPaletteSource.SelectedIndex == 0)
            RefreshToolPaletteList();
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
                string modelPath = ResolveModelPathForRawcode(u.Rawcode);
                var spawnOpts = new
                {
                    id = u.Rawcode,
                    creationNumber = u.CreationNumber,
                    x = u.X, y = u.Y, z = u.Z,
                    angle = u.AngleRadians, scale = u.Scale, player = u.PlayerId,
                    modelPath
                };
                _currentProject.PlacedUnits.Remove(u);
                Map3DViewport.DeleteLiveEntity("unit", creationNumber);
                _undoService.Record(new DelegateUndoEdit(
                    $"Eliminar {u.Name}",
                    () =>
                    {
                        if (!_currentProject.PlacedUnits.Contains(u))
                            _currentProject.PlacedUnits.Add(u);
                        Map3DViewport.SpawnLiveEntity("unit", spawnOpts);
                        RefreshAfterEntityListChange(u);
                    },
                    () =>
                    {
                        _currentProject.PlacedUnits.Remove(u);
                        Map3DViewport.DeleteLiveEntity("unit", u.CreationNumber);
                        RefreshAfterEntityListChange(null);
                    }));
            }
        }
        else
        {
            var d = _currentProject.PlacedDoodads.FirstOrDefault(x => x.CreationNumber == creationNumber);
            if (d != null)
            {
                string modelPath = ResolveModelPathForRawcode(d.Rawcode);
                var spawnOpts = new
                {
                    id = d.Rawcode,
                    creationNumber = d.CreationNumber,
                    x = d.X, y = d.Y, z = d.Z,
                    angle = d.AngleRadians, scale = d.Scale, variation = d.Variation, life = (int)d.Life,
                    modelPath
                };
                _currentProject.PlacedDoodads.Remove(d);
                Map3DViewport.DeleteLiveEntity("doodad", creationNumber);
                _undoService.Record(new DelegateUndoEdit(
                    $"Eliminar {d.Name}",
                    () =>
                    {
                        if (!_currentProject.PlacedDoodads.Contains(d))
                            _currentProject.PlacedDoodads.Add(d);
                        Map3DViewport.SpawnLiveEntity("doodad", spawnOpts);
                        RefreshAfterEntityListChange(d);
                    },
                    () =>
                    {
                        _currentProject.PlacedDoodads.Remove(d);
                        Map3DViewport.DeleteLiveEntity("doodad", d.CreationNumber);
                        RefreshAfterEntityListChange(null);
                    }));
            }
        }

        RefreshAfterEntityListChange(null);
        TxtStatusBarLeft.Text = "Elemento eliminado del mapa (recuerda Guardar Ctrl+S).";
    }

    private string ResolveModelPathForRawcode(string rawcode)
    {
        var customObj = _currentProject?.Objects.FirstOrDefault(o => o.Rawcode.Equals(rawcode, StringComparison.OrdinalIgnoreCase));
        return (customObj != null ? ResolveModelPathFromEntry(customObj) : null)
               ?? _baseDataService.GetBaseObject(rawcode)?.ModelFile
               ?? string.Empty;
    }

    private void Map3DViewport_PlacementCancelled()
    {
        _suppressPaletteModeEvents = true;
        if (TglModePlace != null) TglModePlace.IsChecked = false;
        if (TglModeSelect != null) TglModeSelect.IsChecked = true;
        if (TglRegionPlace != null) TglRegionPlace.IsChecked = false;
        if (TglRegionSelect != null) TglRegionSelect.IsChecked = true;
        _suppressPaletteModeEvents = false;
        TxtStatusBarLeft.Text = "Modo Selección activo (clic para seleccionar o arrastrar elementos).";
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
            PnlDoodadVarRow.Visibility = Visibility.Collapsed;

            var arch = EntityArchetypeHelper.Analyze(u, _currentProject, _baseDataService);

            var ownerMatch = (CmbSelectedEntityOwner.ItemsSource as IEnumerable<PlayerOwnerOption>)?.FirstOrDefault(o => o.PlayerId == u.PlayerId);
            CmbSelectedEntityOwner.SelectedItem = ownerMatch;
            TxtSelectedEntityAngle.Text = u.AngleDegrees.ToString("0.#", CultureInfo.InvariantCulture);
            TxtSelectedEntityScale.Text = u.Scale.ToString("0.##", CultureInfo.InvariantCulture);
            TxtSelectedEntityHp.Text = u.HP.ToString(CultureInfo.InvariantCulture);
            TxtSelectedEntityMp.Text = u.MP.ToString(CultureInfo.InvariantCulture);
            TxtSelectedEntityLevel.Text = Math.Max(1, u.Level).ToString(CultureInfo.InvariantCulture);
            TxtSelectedEntityGold.Text = u.GoldAmount.ToString(CultureInfo.InvariantCulture);

            if (arch.IsItem)
            {
                PnlUnitOwnerRow.Visibility = Visibility.Collapsed;
                PnlSelectedFacingRow.Visibility = Visibility.Collapsed;
                PnlSelectedGoldRow.Visibility = Visibility.Collapsed;
                PnlSelectedScaleHpRow.Visibility = Visibility.Collapsed;
                PnlUnitExtraRow.Visibility = Visibility.Collapsed;
            }
            else if (arch.IsGoldMine)
            {
                PnlUnitOwnerRow.Visibility = Visibility.Visible;
                PnlSelectedFacingRow.Visibility = Visibility.Collapsed;
                PnlSelectedGoldRow.Visibility = Visibility.Visible;
                PnlSelectedScaleHpRow.Visibility = Visibility.Collapsed;
                PnlUnitExtraRow.Visibility = Visibility.Collapsed;
            }
            else if (arch.IsBuilding && arch.IsInvulnerable)
            {
                PnlUnitOwnerRow.Visibility = Visibility.Visible;
                PnlSelectedFacingRow.Visibility = Visibility.Collapsed;
                PnlSelectedGoldRow.Visibility = Visibility.Collapsed;
                PnlSelectedScaleHpRow.Visibility = Visibility.Collapsed;
                PnlUnitExtraRow.Visibility = Visibility.Collapsed;
            }
            else if (arch.IsBuilding)
            {
                PnlUnitOwnerRow.Visibility = Visibility.Visible;
                PnlSelectedFacingRow.Visibility = Visibility.Collapsed;
                PnlSelectedGoldRow.Visibility = Visibility.Collapsed;
                PnlSelectedScaleHpRow.Visibility = Visibility.Visible;
                PnlSelectedScaleCol.Visibility = Visibility.Visible;
                PnlSelectedHpCol.Visibility = Visibility.Visible;
                LblSelectedEntityHp.Text = "Vida (% / -1):";

                var showMp = arch.BaseMp > 0 || u.MP >= 0;
                PnlUnitExtraRow.Visibility = showMp ? Visibility.Visible : Visibility.Collapsed;
                PnlSelectedMpCol.Visibility = showMp ? Visibility.Visible : Visibility.Collapsed;
                PnlSelectedLevelCol.Visibility = Visibility.Collapsed;
            }
            else
            {
                PnlUnitOwnerRow.Visibility = Visibility.Visible;
                PnlSelectedFacingRow.Visibility = Visibility.Visible;
                PnlSelectedGoldRow.Visibility = Visibility.Collapsed;
                PnlSelectedScaleHpRow.Visibility = Visibility.Visible;
                PnlSelectedScaleCol.Visibility = Visibility.Visible;
                PnlSelectedHpCol.Visibility = Visibility.Visible;
                LblSelectedEntityHp.Text = "Vida (% / -1):";

                var showMp = arch.IsHero || arch.BaseMp > 0 || u.MP >= 0;
                var showLvl = arch.IsHero;
                PnlUnitExtraRow.Visibility = (showMp || showLvl) ? Visibility.Visible : Visibility.Collapsed;
                PnlSelectedMpCol.Visibility = showMp ? Visibility.Visible : Visibility.Collapsed;
                PnlSelectedLevelCol.Visibility = showLvl ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        else if (entity is W3PlacedDoodad d)
        {
            PnlSelectedEntityEditor.Visibility = Visibility.Visible;
            PnlUnitOwnerRow.Visibility = Visibility.Collapsed;
            PnlSelectedFacingRow.Visibility = Visibility.Visible;
            PnlSelectedGoldRow.Visibility = Visibility.Collapsed;
            PnlSelectedScaleHpRow.Visibility = Visibility.Visible;
            PnlSelectedScaleCol.Visibility = Visibility.Visible;
            PnlSelectedHpCol.Visibility = d.IsDestructible ? Visibility.Visible : Visibility.Collapsed;
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
            if (PnlSelectedFacingRow.Visibility == Visibility.Visible &&
                double.TryParse(TxtSelectedEntityAngle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
                u.AngleDegrees = deg;
            if (PnlSelectedScaleHpRow.Visibility == Visibility.Visible && PnlSelectedScaleCol.Visibility == Visibility.Visible &&
                float.TryParse(TxtSelectedEntityScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sc))
                u.Scale = u.ScaleY = u.ScaleZ = Math.Clamp(sc, 0.1f, 20f);
            if (PnlSelectedGoldRow.Visibility == Visibility.Visible &&
                int.TryParse(TxtSelectedEntityGold.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gold))
                u.GoldAmount = Math.Max(0, gold);
            if (PnlSelectedScaleHpRow.Visibility == Visibility.Visible && PnlSelectedHpCol.Visibility == Visibility.Visible &&
                int.TryParse(TxtSelectedEntityHp.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp))
                u.HP = hp;
            if (PnlUnitExtraRow.Visibility == Visibility.Visible && PnlSelectedMpCol.Visibility == Visibility.Visible &&
                int.TryParse(TxtSelectedEntityMp.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp))
                u.MP = mp;
            if (PnlUnitExtraRow.Visibility == Visibility.Visible && PnlSelectedLevelCol.Visibility == Visibility.Visible &&
                int.TryParse(TxtSelectedEntityLevel.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
                u.Level = Math.Clamp(lvl, 1, 100);
            if (PnlUnitOwnerRow.Visibility == Visibility.Visible && CmbSelectedEntityOwner.SelectedItem is PlayerOwnerOption opt)
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

    // ==================== REGIONES 3D (war3map.w3r) Y CÁMARAS (war3map.w3c) ====================

    private void BtnRegionToolMode_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressPaletteModeEvents || sender is not System.Windows.Controls.Primitives.ToggleButton btn)
            return;

        _suppressPaletteModeEvents = true;
        bool wantPlace = (btn.Tag as string) == "place";
        if (TglRegionSelect != null) TglRegionSelect.IsChecked = !wantPlace;
        if (TglRegionPlace != null) TglRegionPlace.IsChecked = wantPlace;
        _suppressPaletteModeEvents = false;
        Sync3DEditorMode();
    }

    private void Map3DViewport_RegionSelected(int creationNumber)
    {
        if (_currentProject == null) return;
        _selectedRegion = creationNumber >= 0
            ? _currentProject.Regions.FirstOrDefault(r => r.CreationNumber == creationNumber)
            : null;

        if (_selectedRegion != null)
        {
            if (TxtSelectedRegionName != null) TxtSelectedRegionName.Text = _selectedRegion.Name;
            TxtStatusBarLeft.Text = $"Región seleccionada: {_selectedRegion.Name} [{_selectedRegion.Width:F0}×{_selectedRegion.Height:F0}] ({_selectedRegion.JassVariableName})";
            if (CmbPaletteMode.SelectedIndex == 2)
                LstToolPalette.SelectedItem = _selectedRegion;
        }
        else if (TxtSelectedRegionName != null)
        {
            TxtSelectedRegionName.Text = string.Empty;
        }
    }

    private void Map3DViewport_RegionUpdated(int creationNumber, float left, float bottom, float right, float top)
    {
        if (_currentProject == null) return;
        var reg = _currentProject.Regions.FirstOrDefault(r => r.CreationNumber == creationNumber);
        if (reg == null) return;

        float ol = reg.Left, ob = reg.Bottom, orr = reg.Right, ot = reg.Top;
        ApplyRegionRect(reg, left, bottom, right, top);
        _undoService.Record(new DelegateUndoEdit(
            $"Ajustar región {reg.Name}",
            () => ApplyRegionRect(reg, ol, ob, orr, ot),
            () => ApplyRegionRect(reg, left, bottom, right, top)));
    }

    private void ApplyRegionRect(W3MapRegion reg, float left, float bottom, float right, float top)
    {
        if (_currentProject == null) return;
        reg.Left = Math.Min(left, right);
        reg.Right = Math.Max(left, right);
        reg.Bottom = Math.Min(bottom, top);
        reg.Top = Math.Max(bottom, top);
        _currentProject.HasRegionEdits = true;
        Map3DViewport.SyncRegions(_currentProject.Regions);
        MapViewport.MarkViewportDirty();
        if (CmbPaletteMode.SelectedIndex == 2)
            RefreshToolPaletteList();
        UpdateTestButtonState();
    }

    private void Map3DViewport_RegionCreateRequested(float left, float bottom, float right, float top)
    {
        if (_currentProject == null) return;
        int nextNum = _currentProject.Regions.Count > 0
            ? _currentProject.Regions.Max(r => r.CreationNumber) + 1
            : 0;

        var reg = new W3MapRegion
        {
            CreationNumber = nextNum,
            Name = $"Region {nextNum:D3}",
            Left = Math.Min(left, right),
            Right = Math.Max(left, right),
            Bottom = Math.Min(bottom, top),
            Top = Math.Max(bottom, top),
            ColorR = 80,
            ColorG = 190,
            ColorB = 255
        };

        AddOrRemoveRegion(reg, add: true);
        _undoService.Record(new DelegateUndoEdit(
            $"Crear región {reg.Name}",
            () => AddOrRemoveRegion(reg, add: false),
            () => AddOrRemoveRegion(reg, add: true)));

        Map3DViewport_RegionSelected(reg.CreationNumber);
        Map3DViewport.SelectRegion(reg.CreationNumber, focus: false);
    }

    private void Map3DViewport_RegionDeleteRequested(int creationNumber)
    {
        if (_currentProject == null) return;
        var reg = _currentProject.Regions.FirstOrDefault(r => r.CreationNumber == creationNumber);
        if (reg == null) return;

        AddOrRemoveRegion(reg, add: false);
        _undoService.Record(new DelegateUndoEdit(
            $"Eliminar región {reg.Name}",
            () => AddOrRemoveRegion(reg, add: true),
            () => AddOrRemoveRegion(reg, add: false)));
    }

    private void AddOrRemoveRegion(W3MapRegion reg, bool add)
    {
        if (_currentProject == null) return;
        if (add)
        {
            if (!_currentProject.Regions.Contains(reg))
                _currentProject.Regions.Add(reg);
            _selectedRegion = reg;
        }
        else
        {
            _currentProject.Regions.Remove(reg);
            if (ReferenceEquals(_selectedRegion, reg))
                _selectedRegion = null;
        }
        _currentProject.HasRegionEdits = true;
        Map3DViewport.SyncRegions(_currentProject.Regions);
        MapViewport.MarkViewportDirty();
        if (CmbPaletteMode.SelectedIndex == 2)
            RefreshToolPaletteList();
        UpdateMapHeaderOverlay();
        UpdateTestButtonState();
    }

    private void BtnRenameSelectedRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null) return;
        var reg = _selectedRegion ?? LstToolPalette.SelectedItem as W3MapRegion;
        if (reg == null) return;

        string newName = TxtSelectedRegionName?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName) || newName == reg.Name)
            return;

        string oldName = reg.Name;
        reg.Name = newName;
        _currentProject.HasRegionEdits = true;
        Map3DViewport.SyncRegions(_currentProject.Regions);
        RefreshToolPaletteList();
        UpdateTestButtonState();

        _undoService.Record(new DelegateUndoEdit(
            $"Renombrar región {oldName}",
            () => { reg.Name = oldName; if (TxtSelectedRegionName != null) TxtSelectedRegionName.Text = oldName; Map3DViewport.SyncRegions(_currentProject.Regions); RefreshToolPaletteList(); },
            () => { reg.Name = newName; if (TxtSelectedRegionName != null) TxtSelectedRegionName.Text = newName; Map3DViewport.SyncRegions(_currentProject.Regions); RefreshToolPaletteList(); }));
    }

    private void BtnDeleteSelectedRegion_Click(object sender, RoutedEventArgs e)
    {
        var reg = _selectedRegion ?? LstToolPalette.SelectedItem as W3MapRegion;
        if (reg != null)
            Map3DViewport_RegionDeleteRequested(reg.CreationNumber);
    }

    private async void BtnCaptureCamera_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null) return;
        var cam = await Map3DViewport.CaptureCurrentCameraAsync();
        if (cam == null) return;

        cam.Name = $"Camera {_currentProject.Cameras.Count + 1:D3}";
        _currentProject.Cameras.Add(cam);
        _currentProject.HasCameraEdits = true;
        RefreshToolPaletteList();
        LstToolPalette.SelectedItem = cam;
        UpdateTestButtonState();

        _undoService.Record(new DelegateUndoEdit(
            $"Capturar cámara {cam.Name}",
            () => { _currentProject.Cameras.Remove(cam); _currentProject.HasCameraEdits = true; RefreshToolPaletteList(); UpdateTestButtonState(); },
            () => { if (!_currentProject.Cameras.Contains(cam)) _currentProject.Cameras.Add(cam); _currentProject.HasCameraEdits = true; RefreshToolPaletteList(); UpdateTestButtonState(); }));
    }

    private void BtnFocusSelectedCamera_Click(object sender, RoutedEventArgs e)
    {
        if (LstToolPalette.SelectedItem is W3MapCamera cam)
            Map3DViewport.FocusCameraPreset(cam);
    }

    private void BtnDeleteSelectedCamera_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null || LstToolPalette.SelectedItem is not W3MapCamera cam)
            return;

        int idx = _currentProject.Cameras.IndexOf(cam);
        _currentProject.Cameras.Remove(cam);
        _currentProject.HasCameraEdits = true;
        RefreshToolPaletteList();
        UpdateTestButtonState();

        _undoService.Record(new DelegateUndoEdit(
            $"Eliminar cámara {cam.Name}",
            () => { _currentProject.Cameras.Insert(Math.Clamp(idx, 0, _currentProject.Cameras.Count), cam); _currentProject.HasCameraEdits = true; RefreshToolPaletteList(); UpdateTestButtonState(); },
            () => { _currentProject.Cameras.Remove(cam); _currentProject.HasCameraEdits = true; RefreshToolPaletteList(); UpdateTestButtonState(); }));
    }

    private void BtnDeleteSelectedEntity_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntities.Count > 1)
        {
            DeleteEntitiesSelection(_selectedEntities);
        }
        else if (_activeSelectedEntity is W3PlacedUnit u)
        {
            Map3DViewport_EntityDeleteRequested("unit", u.CreationNumber);
        }
        else if (_activeSelectedEntity is W3PlacedDoodad d)
        {
            Map3DViewport_EntityDeleteRequested("doodad", d.CreationNumber);
        }
    }

    private void BtnOpenEntityProperties_Click(object sender, RoutedEventArgs e) => OpenSelectedEntityPropertiesDialog();

    private void OpenSelectedEntityPropertiesDialog()
    {
        if (_currentProject == null || _activeSelectedEntity == null)
            return;

        if (_activeSelectedEntity is W3PlacedUnit u)
        {
            int oldPlayer = u.PlayerId, oldCustomColor = u.CustomPlayerColorId, oldGold = u.GoldAmount;
            int oldHp = u.HP, oldMp = u.MP, oldLevel = u.Level, oldStr = u.HeroStrength, oldAgi = u.HeroAgility, oldInt = u.HeroIntelligence;
            int oldWaygate = u.WaygateDestinationRegionId, oldItemTablePtr = u.ItemTablePointer;
            float oldX = u.X, oldY = u.Y, oldZ = u.Z, oldAng = u.AngleRadians, oldScale = u.Scale, oldAcq = u.TargetAcquisition;
            var oldDrops = CloneItemDrops(u.ItemDropSets);

            var owners = (CmbSelectedEntityOwner?.ItemsSource as IEnumerable<PlayerOwnerOption>) ?? Array.Empty<PlayerOwnerOption>();
            var dlg = new EntityPropertiesWindow(_currentProject, _baseDataService, u, owners, _blpIconService) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            int newPlayer = u.PlayerId, newCustomColor = u.CustomPlayerColorId, newGold = u.GoldAmount;
            int newHp = u.HP, newMp = u.MP, newLevel = u.Level, newStr = u.HeroStrength, newAgi = u.HeroAgility, newInt = u.HeroIntelligence;
            int newWaygate = u.WaygateDestinationRegionId, newItemTablePtr = u.ItemTablePointer;
            float newX = u.X, newY = u.Y, newZ = u.Z, newAng = u.AngleRadians, newScale = u.Scale, newAcq = u.TargetAcquisition;
            var newDrops = CloneItemDrops(u.ItemDropSets);

            void ApplyUnitState(
                int player, int customColor, int gold, int hp, int mp, int lvl, int str, int agi, int intel,
                int waygate, int itemPtr, float x, float y, float z, float ang, float scale, float acq,
                List<List<W3UnitItemDrop>> drops)
            {
                u.PlayerId = player;
                u.CustomPlayerColorId = customColor;
                u.GoldAmount = gold;
                u.HP = hp;
                u.MP = mp;
                u.Level = lvl;
                u.HeroStrength = str;
                u.HeroAgility = agi;
                u.HeroIntelligence = intel;
                u.WaygateDestinationRegionId = waygate;
                u.ItemTablePointer = itemPtr;
                u.Scale = u.ScaleY = u.ScaleZ = scale;
                u.AngleRadians = ang;
                u.TargetAcquisition = acq;
                u.ItemDropSets.Clear();
                u.ItemDropSets.AddRange(CloneItemDrops(drops));
                ApplyUnitPosition(u, x, y, z);
                Map3DViewport.UpdateLiveEntity("unit", u.CreationNumber, new { x, y, z, angle = ang, scale, player });
                RefreshEntityAfterEdit(u);
            }

            ApplyUnitState(newPlayer, newCustomColor, newGold, newHp, newMp, newLevel, newStr, newAgi, newInt, newWaygate, newItemTablePtr, newX, newY, newZ, newAng, newScale, newAcq, newDrops);
            _undoService.Record(new DelegateUndoEdit(
                $"Propiedades de {u.Name}",
                () => ApplyUnitState(oldPlayer, oldCustomColor, oldGold, oldHp, oldMp, oldLevel, oldStr, oldAgi, oldInt, oldWaygate, oldItemTablePtr, oldX, oldY, oldZ, oldAng, oldScale, oldAcq, oldDrops),
                () => ApplyUnitState(newPlayer, newCustomColor, newGold, newHp, newMp, newLevel, newStr, newAgi, newInt, newWaygate, newItemTablePtr, newX, newY, newZ, newAng, newScale, newAcq, newDrops)));
        }
        else if (_activeSelectedEntity is W3PlacedDoodad d)
        {
            float oldX = d.X, oldY = d.Y, oldZ = d.Z, oldAng = d.AngleRadians, oldScale = d.Scale;
            byte oldLife = d.Life;
            int oldVar = d.Variation;

            var owners = (CmbSelectedEntityOwner?.ItemsSource as IEnumerable<PlayerOwnerOption>) ?? Array.Empty<PlayerOwnerOption>();
            var dlg = new EntityPropertiesWindow(_currentProject, _baseDataService, d, owners, _blpIconService) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            float newX = d.X, newY = d.Y, newZ = d.Z, newAng = d.AngleRadians, newScale = d.Scale;
            byte newLife = d.Life;
            int newVar = d.Variation;

            void ApplyDoodadState(float x, float y, float z, float ang, float scale, byte life, int variation)
            {
                d.X = x; d.Y = y; d.Z = z;
                d.AngleRadians = ang;
                d.Scale = d.ScaleY = d.ScaleZ = scale;
                d.Life = life;
                d.Variation = variation;
                Map3DViewport.UpdateLiveEntity("doodad", d.CreationNumber, new { x, y, z, angle = ang, scale, life = (int)life });
                RefreshEntityAfterEdit(d);
            }

            ApplyDoodadState(newX, newY, newZ, newAng, newScale, newLife, newVar);
            _undoService.Record(new DelegateUndoEdit(
                $"Propiedades de {d.Name}",
                () => ApplyDoodadState(oldX, oldY, oldZ, oldAng, oldScale, oldLife, oldVar),
                () => ApplyDoodadState(newX, newY, newZ, newAng, newScale, newLife, newVar)));
        }
    }

    private void Map3DViewport_EntitiesDeleteRequested(IReadOnlyList<ViewportEntityKey> keys)
    {
        var items = keys.Select(k => ResolveEntityByKey(k.Kind, k.CreationNumber)).Where(e => e != null).Cast<object>().ToList();
        if (items.Count > 0)
            DeleteEntitiesSelection(items);
    }

    private void DeleteEntitiesSelection(IEnumerable<object> selection)
    {
        if (_currentProject == null) return;
        var targets = selection.Distinct().ToList();
        if (targets.Count == 0) return;

        if (targets.Count == 1)
        {
            if (targets[0] is W3PlacedUnit u0) Map3DViewport_EntityDeleteRequested("unit", u0.CreationNumber);
            else if (targets[0] is W3PlacedDoodad d0) Map3DViewport_EntityDeleteRequested("doodad", d0.CreationNumber);
            return;
        }

        var unitEntries = new List<(W3PlacedUnit Unit, object SpawnOpts)>();
        var doodadEntries = new List<(W3PlacedDoodad Doodad, object SpawnOpts)>();
        foreach (var item in targets)
        {
            if (item is W3PlacedUnit u)
            {
                unitEntries.Add((u, BuildUnitSpawnOpts(u)));
            }
            else if (item is W3PlacedDoodad d)
            {
                doodadEntries.Add((d, BuildDoodadSpawnOpts(d)));
            }
        }

        void PerformDelete()
        {
            foreach (var (u, _) in unitEntries)
            {
                _currentProject.PlacedUnits.Remove(u);
                Map3DViewport.DeleteLiveEntity("unit", u.CreationNumber);
            }
            foreach (var (d, _) in doodadEntries)
            {
                _currentProject.PlacedDoodads.Remove(d);
                Map3DViewport.DeleteLiveEntity("doodad", d.CreationNumber);
            }
            _selectedEntities.Clear();
            RefreshAfterEntityListChange(null);
        }

        void PerformRestore()
        {
            var keys = new List<ViewportEntityKey>();
            foreach (var (u, opts) in unitEntries)
            {
                if (!_currentProject.PlacedUnits.Contains(u)) _currentProject.PlacedUnits.Add(u);
                Map3DViewport.SpawnLiveEntity("unit", opts);
                keys.Add(new ViewportEntityKey("unit", u.CreationNumber));
            }
            foreach (var (d, opts) in doodadEntries)
            {
                if (!_currentProject.PlacedDoodads.Contains(d)) _currentProject.PlacedDoodads.Add(d);
                Map3DViewport.SpawnLiveEntity("doodad", opts);
                keys.Add(new ViewportEntityKey("doodad", d.CreationNumber));
            }
            Map3DViewport.SelectEntities(keys);
            Map3DViewport_EntitiesSelected(keys);
            RefreshAfterEntityListChange(_selectedEntities.FirstOrDefault());
        }

        PerformDelete();
        _undoService.Record(new DelegateUndoEdit(
            $"Eliminar {unitEntries.Count + doodadEntries.Count} entidades",
            PerformRestore,
            PerformDelete));
        TxtStatusBarLeft.Text = $"{unitEntries.Count + doodadEntries.Count} entidades eliminadas (Ctrl+Z para deshacer).";
    }

    private void RotateEntitiesSelection(double deltaDeg)
    {
        if (_currentProject == null || _selectedEntities.Count == 0) return;
        float deltaRad = (float)(deltaDeg * Math.PI / 180.0);
        var items = new List<ViewportEntityRotateItem>();
        foreach (var ent in _selectedEntities)
        {
            if (ent is W3PlacedUnit u)
                items.Add(new ViewportEntityRotateItem("unit", u.CreationNumber, u.AngleRadians + deltaRad));
            else if (ent is W3PlacedDoodad d)
                items.Add(new ViewportEntityRotateItem("doodad", d.CreationNumber, d.AngleRadians + deltaRad));
        }
        Map3DViewport_EntitiesRotated(items);
    }

    private void Map3DViewport_EntityClipboardRequested(string action, float? wx, float? wy)
    {
        HandleEntityClipboardAction(action, wx, wy);
    }

    private void HandleEntityClipboardAction(string action, float? targetWx, float? targetWy)
    {
        if (_currentProject == null) return;

        switch (action)
        {
            case "copy":
                if (CopySelectedEntitiesToClipboard())
                    TxtStatusBarLeft.Text = $"{_entityClipboard.Count} entidad(es) copiada(s). Usa Ctrl+V para pegar.";
                break;

            case "cut":
                if (CopySelectedEntitiesToClipboard())
                {
                    int cutCount = _selectedEntities.Count;
                    DeleteEntitiesSelection(_selectedEntities);
                    TxtStatusBarLeft.Text = $"{cutCount} entidad(es) cortada(s). Usa Ctrl+V para pegar.";
                }
                break;

            case "paste":
                PasteEntitiesFromClipboard(targetWx, targetWy, offsetFallback: false);
                break;

            case "duplicate":
                if (CopySelectedEntitiesToClipboard())
                    PasteEntitiesFromClipboard(null, null, offsetFallback: true);
                break;
        }
    }

    private bool CopySelectedEntitiesToClipboard()
    {
        if (_selectedEntities.Count == 0) return false;
        _entityClipboard.Clear();
        foreach (var ent in _selectedEntities)
        {
            if (ent is W3PlacedUnit u)
            {
                _entityClipboard.Add(new ClipboardEntityEntry
                {
                    IsUnit = true,
                    OrigX = u.X,
                    OrigY = u.Y,
                    UnitClone = CloneUnitForPlacement(u, u.CreationNumber, u.X, u.Y, u.Z)
                });
            }
            else if (ent is W3PlacedDoodad d)
            {
                _entityClipboard.Add(new ClipboardEntityEntry
                {
                    IsUnit = false,
                    OrigX = d.X,
                    OrigY = d.Y,
                    DoodadClone = CloneDoodadForPlacement(d, d.CreationNumber, d.X, d.Y, d.Z)
                });
            }
        }
        return _entityClipboard.Count > 0;
    }

    private void PasteEntitiesFromClipboard(float? targetWx, float? targetWy, bool offsetFallback)
    {
        if (_currentProject == null || _entityClipboard.Count == 0) return;

        float centerX = _entityClipboard.Average(e => e.OrigX);
        float centerY = _entityClipboard.Average(e => e.OrigY);
        float destCenterX = targetWx ?? (centerX + (offsetFallback || targetWx == null ? 128f : 0f));
        float destCenterY = targetWy ?? (centerY - (offsetFallback || targetWy == null ? 128f : 0f));
        float dx = destCenterX - centerX;
        float dy = destCenterY - centerY;

        int nextUnitNum = _currentProject.PlacedUnits.Count > 0 ? _currentProject.PlacedUnits.Max(u => u.CreationNumber) + 1 : 0;
        int nextDoodadNum = _currentProject.PlacedDoodads.Count > 0 ? _currentProject.PlacedDoodads.Max(d => d.CreationNumber) + 1 : 0;

        var addedUnits = new List<(W3PlacedUnit Unit, object SpawnOpts)>();
        var addedDoodads = new List<(W3PlacedDoodad Doodad, object SpawnOpts)>();
        var pastedKeys = new List<ViewportEntityKey>();

        foreach (var entry in _entityClipboard)
        {
            string rawcode = entry.IsUnit ? entry.UnitClone!.Rawcode : entry.DoodadClone!.Rawcode;
            var (px, py) = SnapEntityPositionIfEnabled(rawcode, !entry.IsUnit, entry.OrigX + dx, entry.OrigY + dy);
            float pz = SampleTerrainHeightAt(px, py);

            if (entry.IsUnit && entry.UnitClone != null)
            {
                var copy = CloneUnitForPlacement(entry.UnitClone, nextUnitNum++, px, py, pz);
                addedUnits.Add((copy, BuildUnitSpawnOpts(copy)));
                pastedKeys.Add(new ViewportEntityKey("unit", copy.CreationNumber));
            }
            else if (!entry.IsUnit && entry.DoodadClone != null)
            {
                var copy = CloneDoodadForPlacement(entry.DoodadClone, nextDoodadNum++, px, py, pz);
                addedDoodads.Add((copy, BuildDoodadSpawnOpts(copy)));
                pastedKeys.Add(new ViewportEntityKey("doodad", copy.CreationNumber));
            }
        }

        if (addedUnits.Count == 0 && addedDoodads.Count == 0) return;

        void PerformPaste()
        {
            foreach (var (u, opts) in addedUnits)
            {
                if (!_currentProject.PlacedUnits.Contains(u)) _currentProject.PlacedUnits.Add(u);
                Map3DViewport.SpawnLiveEntity("unit", opts);
            }
            foreach (var (d, opts) in addedDoodads)
            {
                if (!_currentProject.PlacedDoodads.Contains(d)) _currentProject.PlacedDoodads.Add(d);
                Map3DViewport.SpawnLiveEntity("doodad", opts);
            }
            Map3DViewport.SelectEntities(pastedKeys);
            Map3DViewport_EntitiesSelected(pastedKeys);
            RefreshAfterEntityListChange(_selectedEntities.FirstOrDefault());
        }

        void PerformUndoPaste()
        {
            foreach (var (u, _) in addedUnits)
            {
                _currentProject.PlacedUnits.Remove(u);
                Map3DViewport.DeleteLiveEntity("unit", u.CreationNumber);
            }
            foreach (var (d, _) in addedDoodads)
            {
                _currentProject.PlacedDoodads.Remove(d);
                Map3DViewport.DeleteLiveEntity("doodad", d.CreationNumber);
            }
            _selectedEntities.Clear();
            RefreshAfterEntityListChange(null);
        }

        PerformPaste();
        int total = addedUnits.Count + addedDoodads.Count;
        _undoService.Record(new DelegateUndoEdit(
            offsetFallback ? $"Duplicar {total} entidad(es)" : $"Pegar {total} entidad(es)",
            PerformUndoPaste,
            PerformPaste));
        TxtStatusBarLeft.Text = $"{total} entidad(es) {(offsetFallback ? "duplicada(s)" : "pegada(s)")}.";
    }

    private object BuildUnitSpawnOpts(W3PlacedUnit u) => new
    {
        id = u.Rawcode,
        creationNumber = u.CreationNumber,
        x = u.X,
        y = u.Y,
        z = u.Z,
        angle = u.AngleRadians,
        scale = u.Scale,
        player = u.PlayerId,
        modelPath = ResolveModelPathForRawcode(u.Rawcode)
    };

    private object BuildDoodadSpawnOpts(W3PlacedDoodad d) => new
    {
        id = d.Rawcode,
        creationNumber = d.CreationNumber,
        x = d.X,
        y = d.Y,
        z = d.Z,
        angle = d.AngleRadians,
        scale = d.Scale,
        variation = d.Variation,
        life = (int)d.Life,
        modelPath = ResolveModelPathForRawcode(d.Rawcode)
    };

    private static List<List<W3UnitItemDrop>> CloneItemDrops(IEnumerable<List<W3UnitItemDrop>>? src)
    {
        var result = new List<List<W3UnitItemDrop>>();
        if (src == null) return result;
        foreach (var set in src)
        {
            var clonedSet = new List<W3UnitItemDrop>();
            foreach (var d in set)
                clonedSet.Add(new W3UnitItemDrop { ItemRawcode = d.ItemRawcode, Chance = d.Chance });
            result.Add(clonedSet);
        }
        return result;
    }

    private static W3PlacedUnit CloneUnitForPlacement(W3PlacedUnit src, int newCreationNumber, float x, float y, float z)
    {
        var copy = new W3PlacedUnit
        {
            Rawcode = src.Rawcode,
            Name = src.Name,
            Variation = src.Variation,
            X = x,
            Y = y,
            Z = z,
            AngleRadians = src.AngleRadians,
            Scale = src.Scale,
            ScaleY = src.ScaleY,
            ScaleZ = src.ScaleZ,
            Flags = src.Flags,
            PlayerId = src.PlayerId,
            CustomPlayerColorId = src.CustomPlayerColorId,
            HP = src.HP,
            MP = src.MP,
            ItemTablePointer = src.ItemTablePointer,
            GoldAmount = src.GoldAmount,
            TargetAcquisition = src.TargetAcquisition,
            Level = src.Level,
            HeroStrength = src.HeroStrength,
            HeroAgility = src.HeroAgility,
            HeroIntelligence = src.HeroIntelligence,
            CreationNumber = newCreationNumber,
            WaygateDestinationRegionId = src.WaygateDestinationRegionId,
            IsItem = src.IsItem,
            IsBuilding = src.IsBuilding
        };
        copy.ItemDropSets.AddRange(CloneItemDrops(src.ItemDropSets));
        return copy;
    }

    private static W3PlacedDoodad CloneDoodadForPlacement(W3PlacedDoodad src, int newCreationNumber, float x, float y, float z) => new()
    {
        Rawcode = src.Rawcode,
        Name = src.Name,
        Variation = src.Variation,
        X = x,
        Y = y,
        Z = z,
        AngleRadians = src.AngleRadians,
        Scale = src.Scale,
        ScaleY = src.ScaleY,
        ScaleZ = src.ScaleZ,
        Flags = src.Flags,
        Life = src.Life,
        CreationNumber = newCreationNumber,
        IsDestructible = src.IsDestructible
    };

    private (float X, float Y) SnapEntityPositionIfEnabled(string rawcode, bool isDoodadOrDestructible, float wx, float wy)
    {
        if (_currentProject == null || _settings.IgnoreEntityPathing)
            return (wx, wy);

        var t = _currentProject.Terrain;
        string pathTex = PathingService.ResolveObjectField(
            rawcode,
            isDoodadOrDestructible,
            _currentProject,
            _baseDataService,
            isDoodadOrDestructible ? "bptx" : "upat",
            "pathTex");
        var grid = PathingService.GetPathTexture(pathTex, _currentProject, _blpIconService);
        if (grid == null || grid.Width <= 0 || grid.Height <= 0)
            return (wx, wy);

        return PathingService.SnapPositionForFootprint(wx, wy, grid.Width, grid.Height, t.OffsetX, t.OffsetY);
    }

    private float SampleTerrainHeightAt(float wx, float wy)
    {
        var t = _currentProject?.Terrain;
        if (t?.GroundHeights == null || t.GridWidth < 2 || t.GridHeight < 2)
            return 0f;

        float fx = Math.Clamp((wx - t.OffsetX) / 128f, 0f, t.GridWidth - 1.001f);
        float fy = Math.Clamp((wy - t.OffsetY) / 128f, 0f, t.GridHeight - 1.001f);
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, t.GridWidth - 1);
        int y1 = Math.Min(y0 + 1, t.GridHeight - 1);
        float tx = fx - x0;
        float ty = fy - y0;
        float h00 = t.GroundHeights[x0, y0];
        float h10 = t.GroundHeights[x1, y0];
        float h01 = t.GroundHeights[x0, y1];
        float h11 = t.GroundHeights[x1, y1];
        float hx0 = h00 + (h10 - h00) * tx;
        float hx1 = h01 + (h11 - h01) * tx;
        return hx0 + (hx1 - hx0) * ty;
    }

    private void UpdateMapHeaderOverlay()
    {
        if (_currentProject == null) return;
        TxtStatusBarLeft.Text = $"{_currentProject.FileName}  |  Terreno: {_currentProject.Terrain.GridWidth - 1}x{_currentProject.Terrain.GridHeight - 1} ({_currentProject.Terrain.TilesetName})  |  Unidades: {_currentProject.PlacedUnits.Count}  |  Doodads: {_currentProject.PlacedDoodads.Count:N0}  |  Regiones: {_currentProject.Regions.Count}";
    }
}
