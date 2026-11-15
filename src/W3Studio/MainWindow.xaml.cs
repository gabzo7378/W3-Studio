using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using W3Studio.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class MainWindow : Window
{
    private WorkspaceSettings _settings = new();
    private readonly WarcraftBaseDataService _baseDataService = new();
    private readonly BlpIconService _blpIconService = new();
    private readonly MapMpqService _mapMpqService;
    private readonly JassCompilerService _compilerService = new();

    private MapProject? _currentProject;
    private TriggerNode? _activeTriggerNode;
    private bool _suppressEditorEvents;

    private readonly ObservableCollection<CompilerDiagnostic> _diagnostics = new();
    private readonly ObservableCollection<CompilerDiagnostic> _globalSearchResults = new();

    private readonly TerrainBrushOptions _terrainBrush = new();
    private readonly TerrainStrokeState _terrainStroke = new();
    private readonly List<Button> _terrainTextureButtons = new();
    private readonly JassIdeAttachment _mainCodeIde;
    private bool _suppressTerrainUiEvents;

    public MainWindow()
    {
        InitializeComponent();
        _mapMpqService = new MapMpqService(_baseDataService);

        CodeEditor.SyntaxHighlighting = VjassSyntaxHighlighter.GetDefinition();
        _mainCodeIde = new JassIdeAttachment(CodeEditor, null, _settings);
        Closed += (_, _) => _mainCodeIde.Dispose();
        GridDiagnostics.ItemsSource = _diagnostics;
        GridGlobalSearchResults.ItemsSource = _globalSearchResults;

        MapViewport.InitializeServices(_baseDataService, _blpIconService);
        MapViewport.CursorMovedOnMap += MapViewport_CursorMovedOnMap;
        MapViewport.SelectionChanged += MapViewport_SelectionChanged;
        MapViewport.CameraChanged += MapViewport_CameraChanged;

        Map3DViewport.SetContext(_blpIconService, null);
        Map3DViewport.TerrainBrushDabReceived += Map3DViewport_TerrainBrushDabReceived;
        Map3DViewport.MapLoadedSuccess += () =>
        {
            SyncTerrainBrushToViewport();
            Sync3DEditorMode();
        };
        InitializeEntityEditorControls();
        Wire3DEntityViewportEvents();

        EmbeddedObjectEditor.InitializeServices(_baseDataService, _blpIconService);
        EmbeddedObjectEditor.RequestOpenStandaloneWindow += OpenStandaloneObjectEditorWindow;
        EmbeddedObjectEditor.RequestOpenIconManager += OpenOrFocusIconManagerWindow;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        var totalSw = Stopwatch.StartNew();
        _settings = WorkspaceSettings.Load();
        LocalizationService.SetLanguage(_settings.AppLanguage, notify: false);
        LocalizationService.LanguageChanged += OnAppLanguageChanged;
        WorkspaceSettings.AdvancedViewSettingsChanged += OnAdvancedViewSettingsChanged;
        ApplyMainWindowLocalization();

        TxtTopWar3Dir.Text = _settings.Warcraft3Dir;
        ChkWindowedTest.IsChecked = _settings.WindowedMode;
        if (MenuWindowedMode != null)
            MenuWindowedMode.IsChecked = _settings.WindowedMode;

        await InitializeBaseMpqAsync();

        // Arranque Limpio: No auto-cargar ningún mapa (solicitud explícita del usuario)
        _currentProject = null;
        _isMapCompiledAndReady = false;
        UpdateTestButtonState();
        EmbeddedObjectEditor.LoadProject(null);
        MapViewport.LoadProject(null);
        Map3DViewport.LoadMap(null);
        ImgSidebarMinimap.Source = null;
        Title = "Warcraft III World Editor (1.27b)";
        TxtStatusBarLeft.Text = "Ningún mapa cargado. Usa Archivo -> Abrir Mapa (Ctrl+O)";

        totalSw.Stop();
        try
        {
            var statusReport =
                $"Status: OK\n" +
                $"TotalLoadMs: {totalSw.ElapsedMilliseconds}\n" +
                $"BaseSlkObjects: {_baseDataService.LoadedBaseObjectsCount}\n" +
                $"MapLoaded: {_currentProject?.FileName ?? "None"}\n" +
                $"PlacedUnits: {_currentProject?.PlacedUnits.Count ?? 0}\n" +
                $"PlacedDoodads: {_currentProject?.PlacedDoodads.Count ?? 0}\n" +
                $"Regions: {_currentProject?.Regions.Count ?? 0}\n" +
                $"CustomObjects: {_currentProject?.Objects.Count ?? 0}\n" +
                $"Triggers: {_currentProject?.TriggerTree.Sum(c => c.Children.Count) ?? 0}\n";
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_status.log"), statusReport);
        }
        catch { }
    }

    private async Task InitializeBaseMpqAsync()
    {
        TxtStatusBarRight.Text = "Cargando SLKs e Iconos .BLP Base 1.27b...";
        _blpIconService.ConfigureBaseMpqs(_settings);
        await Task.Run(() => _baseDataService.LoadFromWarcraftFolder(_settings));

        if (_baseDataService.IsBaseMpqLoaded)
        {
            TxtStatusBarRight.Text = $"MPQ 1.27b ({_baseDataService.LoadedBaseObjectsCount:N0} base SLK) | JassHelper Listo";
        }
        else
        {
            TxtStatusBarRight.Text = "MPQ Base no detectado (Ver Config)";
        }
    }

    private async Task LoadMapFileAsync(string filePath)
    {
        MapProgressWindow? progressWin = null;
        try
        {
            var sw = Stopwatch.StartNew();
            var mapTitle = Path.GetFileName(filePath);
            TxtStatusBarLeft.Text = $"Cargando mapa: {mapTitle}...";

            if (IsLoaded && IsVisible)
            {
                progressWin = new MapProgressWindow("World Editor — Cargando Mapa...", mapTitle, "Abriendo archivo MPQ...")
                {
                    Owner = this
                };
                progressWin.Show();
                progressWin.UpdateProgress(2, "Abriendo archivo MPQ...");
                await Task.Yield();
            }

            var project = await Task.Run(() => _mapMpqService.LoadMap(filePath, (pct, msg) =>
            {
                progressWin?.UpdateProgress(pct, msg);
            }));
            sw.Stop();

            _currentProject = project;
            _settings.LastOpenedMapPath = filePath;
            _settings.Save();

            Title = $"Warcraft III World Editor — [{project.FileName} - {StripColorCodes(project.MapName)}] (1.27b)";

            progressWin?.UpdateProgress(74, "Construyendo minimapa, paleta de herramientas y módulos...");
            await Task.Yield();

            // Load Map Viewport & Minimap
            MapViewport.LoadProject(project);
            ImgSidebarMinimap.Source = MapViewport.MinimapBitmap;
            Map3DViewport.SetContext(_blpIconService, project);
            TxtSidebarLighting.Text = $"Noon ({project.Terrain.TilesetName})";
            UpdateMapHeaderOverlay();

            // Populate Tool Palette, Triggers, Object Editor (F6), Imports, Variables
            _mainCodeIde.SetContext(project, _settings);
            RefreshToolPaletteList();
            if (FloatingToolPalette != null)
                FloatingToolPalette.Visibility = Visibility.Visible;
            TreeTriggers.ItemsSource = project.TriggerTree;
            EmbeddedObjectEditor.LoadProject(project);
            if (_standaloneObjectEditorWindow != null && _standaloneObjectEditorWindow.IsLoaded)
            {
                _standaloneObjectEditorWindow.LoadProject(project);
            }
            if (_standaloneTriggerEditorWindow != null && _standaloneTriggerEditorWindow.IsLoaded)
            {
                _standaloneTriggerEditorWindow.LoadProject(project);
            }
            if (_standaloneIconManagerWindow != null && _standaloneIconManagerWindow.IsLoaded)
            {
                _standaloneIconManagerWindow.SetProject(project);
            }
            if (_standaloneOptimizerWindow != null && _standaloneOptimizerWindow.IsLoaded)
            {
                _standaloneOptimizerWindow.SetProject(project);
            }
            if (_standaloneMapMediaWindow != null && _standaloneMapMediaWindow.IsLoaded)
            {
                _standaloneMapMediaWindow.SetProject(project, MapViewport.MinimapBitmap);
            }
            RefreshImportsGrid();
            RefreshGlobalVariablesGrid();

            var firstNode = project.TriggerTree.FirstOrDefault();
            if (firstNode != null)
                SelectTriggerNode(firstNode);

            // Start in Official World Editor Gameplay Camera View (Distance 1650)
            MapViewport.ResetCameraToStandardEditorView();

            await Map3DViewport.LoadMapAsync(project, (pct, msg) =>
            {
                progressWin?.UpdateProgress(pct, msg);
            });

            progressWin?.UpdateProgress(100, "Mapa cargado con éxito.");
            progressWin?.Close();
            progressWin = null;

            _diagnostics.Clear();
            _diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Info",
                Line = 1,
                Source = "World Editor 1.27b",
                TriggerOrFile = project.FileName,
                Message = $"Mapa cargado en {sw.Elapsed.TotalSeconds:F2}s — Terreno {project.Terrain.GridWidth - 1}x{project.Terrain.GridHeight - 1}, {project.PlacedUnits.Count} unidades, {project.PlacedDoodads.Count:N0} doodads, {project.Regions.Count} regiones, {project.GlobalVariables.Count:N0} variables globales y {project.Objects.Count:N0} objetos personalizados."
            });

            TxtStatusBarLeft.Text = $"{project.FileName}  |  Terreno: {project.Terrain.GridWidth - 1}x{project.Terrain.GridHeight - 1} ({project.Terrain.TilesetName})  |  Unidades: {project.PlacedUnits.Count}  |  Doodads: {project.PlacedDoodads.Count:N0}  |  Regiones: {project.Regions.Count}";
            _isMapCompiledAndReady = false;
            UpdateTestButtonState();
        }
        catch (Exception ex)
        {
            progressWin?.Close();
            progressWin = null;
            MessageBox.Show(this, $"Error al abrir el mapa:\n{ex.Message}", "Error MPQ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progressWin?.Close();
        }
    }

    private ObjectEditorWindow? _standaloneObjectEditorWindow;
    private TriggerEditorWindow? _standaloneTriggerEditorWindow;
    private IconManagerWindow? _standaloneIconManagerWindow;
    private OptimizerWindow? _standaloneOptimizerWindow;
    private MapMediaManagerWindow? _standaloneMapMediaWindow;

    private void OpenOrFocusObjectEditorWindow(W3ObjectCategory cat = W3ObjectCategory.Units, string? rawcode = null)
    {
        if (_standaloneObjectEditorWindow == null || !_standaloneObjectEditorWindow.IsLoaded)
        {
            _standaloneObjectEditorWindow = new ObjectEditorWindow(_baseDataService, _blpIconService, _currentProject, cat, rawcode)
            {
                Owner = this
            };
            _standaloneObjectEditorWindow.RequestOpenIconManager += OpenOrFocusIconManagerWindow;
            _standaloneObjectEditorWindow.Closed += (_, _) => _standaloneObjectEditorWindow = null;
            _standaloneObjectEditorWindow.Show();
        }
        else
        {
            if (_standaloneObjectEditorWindow.WindowState == WindowState.Minimized)
                _standaloneObjectEditorWindow.WindowState = WindowState.Normal;
            _standaloneObjectEditorWindow.Activate();
            _standaloneObjectEditorWindow.Focus();
            _standaloneObjectEditorWindow.SelectCategoryAndSearch(cat, rawcode);
        }
    }

    private void OpenOrFocusTriggerEditorWindow(TriggerNode? initialNode = null)
    {
        if (_standaloneTriggerEditorWindow == null || !_standaloneTriggerEditorWindow.IsLoaded)
        {
            _standaloneTriggerEditorWindow = new TriggerEditorWindow(_compilerService, _settings, _currentProject, initialNode)
            {
                Owner = this
            };
            _standaloneTriggerEditorWindow.RequestOpenObjectEditor += () => OpenOrFocusObjectEditorWindow();
            _standaloneTriggerEditorWindow.RequestSaveAndCompile += SaveAndCompileMapAsync;
            _standaloneTriggerEditorWindow.Closed += (_, _) => _standaloneTriggerEditorWindow = null;
            _standaloneTriggerEditorWindow.Show();
        }
        else
        {
            if (_standaloneTriggerEditorWindow.WindowState == WindowState.Minimized)
                _standaloneTriggerEditorWindow.WindowState = WindowState.Normal;
            _standaloneTriggerEditorWindow.Activate();
            _standaloneTriggerEditorWindow.Focus();
            if (initialNode != null)
                _standaloneTriggerEditorWindow.SelectTriggerNode(initialNode);
        }
    }

    private void OpenStandaloneObjectEditorWindow()
    {
        OpenOrFocusObjectEditorWindow();
    }

    private void BtnOpenIconManager_Click(object sender, RoutedEventArgs e)
    {
        OpenOrFocusIconManagerWindow(null);
    }

    private void OpenOrFocusIconManagerWindow(string? initialIconPath = null)
    {
        if (_standaloneIconManagerWindow == null || !_standaloneIconManagerWindow.IsLoaded)
        {
            _standaloneIconManagerWindow = new IconManagerWindow(
                _blpIconService,
                _currentProject,
                savedMpqPath =>
                {
                    RefreshImportsGrid();
                    UpdateTestButtonState();
                    TxtStatusBarLeft.Text = $"Icono custom agregado a Importaciones: {savedMpqPath} (con DISBTN automático)";
                })
            {
                Owner = this
            };
            _standaloneIconManagerWindow.Closed += (_, _) => _standaloneIconManagerWindow = null;
            _standaloneIconManagerWindow.Show();
        }
        else
        {
            if (_standaloneIconManagerWindow.WindowState == WindowState.Minimized)
                _standaloneIconManagerWindow.WindowState = WindowState.Normal;
            _standaloneIconManagerWindow.SetProject(_currentProject);
            _standaloneIconManagerWindow.Activate();
            _standaloneIconManagerWindow.Focus();
        }

        if (!string.IsNullOrWhiteSpace(initialIconPath))
        {
            _standaloneIconManagerWindow.LoadIconByMpqPath(initialIconPath);
        }
    }

    private void BtnOpenOptimizer_Click(object sender, RoutedEventArgs e)
    {
        OpenOrFocusOptimizerWindow();
    }

    private void BtnOpenMapMedia_Click(object sender, RoutedEventArgs e)
    {
        OpenOrFocusMapMediaWindow(0);
    }

    public void OpenOrFocusMapMediaWindow(int initialTabIndex = 0)
    {
        if (_standaloneMapMediaWindow == null || !_standaloneMapMediaWindow.IsLoaded)
        {
            _standaloneMapMediaWindow = new MapMediaManagerWindow(
                _blpIconService,
                _currentProject,
                MapViewport?.MinimapBitmap,
                summaryMsg =>
                {
                    RefreshImportsGrid();
                    UpdateTestButtonState();
                    TxtStatusBarLeft.Text = summaryMsg;
                },
                initialTabIndex)
            {
                Owner = this
            };
            _standaloneMapMediaWindow.Closed += (_, _) => _standaloneMapMediaWindow = null;
            _standaloneMapMediaWindow.Show();
        }
        else
        {
            if (_standaloneMapMediaWindow.WindowState == WindowState.Minimized)
                _standaloneMapMediaWindow.WindowState = WindowState.Normal;
            _standaloneMapMediaWindow.SetProject(_currentProject, MapViewport?.MinimapBitmap);
            _standaloneMapMediaWindow.SelectTab(initialTabIndex);
            _standaloneMapMediaWindow.Activate();
            _standaloneMapMediaWindow.Focus();
        }
    }

    private void OpenOrFocusOptimizerWindow()
    {
        if (_standaloneOptimizerWindow == null || !_standaloneOptimizerWindow.IsLoaded)
        {
            _standaloneOptimizerWindow = new OptimizerWindow(_settings, _currentProject)
            {
                Owner = this
            };
            _standaloneOptimizerWindow.Closed += (_, _) => _standaloneOptimizerWindow = null;
            _standaloneOptimizerWindow.Show();
        }
        else
        {
            if (_standaloneOptimizerWindow.WindowState == WindowState.Minimized)
                _standaloneOptimizerWindow.WindowState = WindowState.Normal;
            _standaloneOptimizerWindow.SetProject(_currentProject);
            _standaloneOptimizerWindow.Activate();
            _standaloneOptimizerWindow.Focus();
        }
    }

    // ==================== MAP VIEWPORT, MINIMAP & FLOATING TOOL PALETTE ====================

    private void MapViewport_CursorMovedOnMap(float wx, float wy, float wz, int cliff)
    {
        TxtStatusCoords.Text = $"Point: ({wx:F0}, {wy:F0}, {wz:F0})  |  Cliff: {cliff}";
    }

    private void MapViewport_SelectionChanged(object? entity)
    {
        switch (entity)
        {
            case W3PlacedUnit u:
                TxtSelectedMapEntityTitle.Text = $"[{u.Rawcode}] {u.Name}";
                TxtSelectedMapEntityVar.Text = $"JASS: {u.JassVariableName}";
                TxtSelectedMapEntityPos.Text = $"Pos: ({u.X:F0}, {u.Y:F0}, {u.Z:F0})";
                TxtSelectedMapEntityOwner.Text = $"Dueño: {u.PlayerName}";
                TxtStatusSelected.Text = $"Selected: [{u.Rawcode}] {u.Name} ({u.JassVariableName})";
                break;

            case W3MapRegion r:
                TxtSelectedMapEntityTitle.Text = $"[Región #{r.CreationNumber}] {r.Name}";
                TxtSelectedMapEntityVar.Text = $"JASS: {r.JassVariableName}";
                TxtSelectedMapEntityPos.Text = $"Centro: ({r.CenterX:F0}, {r.CenterY:F0}) • Tam: {r.Width:F0}x{r.Height:F0}";
                TxtSelectedMapEntityOwner.Text = "Tipo: Rect / Región del Mapa";
                TxtStatusSelected.Text = $"Selected: Región '{r.Name}' ({r.JassVariableName})";
                break;

            case W3PlacedDoodad d:
                TxtSelectedMapEntityTitle.Text = $"[{d.Rawcode}] Doodad / Árbol #{d.CreationNumber}";
                TxtSelectedMapEntityVar.Text = $"Rawcode: '{d.Rawcode}' (Escala: {d.Scale:F2})";
                TxtSelectedMapEntityPos.Text = $"Pos: ({d.X:F0}, {d.Y:F0}, {d.Z:F0})";
                TxtSelectedMapEntityOwner.Text = $"Vida: {d.Life}%";
                TxtStatusSelected.Text = $"Selected: Doodad [{d.Rawcode}] #{d.CreationNumber}";
                break;

            default:
                TxtSelectedMapEntityTitle.Text = "Ninguno";
                TxtSelectedMapEntityVar.Text = "Var: -";
                TxtSelectedMapEntityPos.Text = "Pos: -";
                TxtSelectedMapEntityOwner.Text = "Dueño: -";
                TxtStatusSelected.Text = "Selected: Ninguno";
                break;
        }
        PopulateSelectedEntityEditor(entity);
    }

    private void MapViewport_CameraChanged()
    {
        TxtSidebarDistance.Text = MapViewport.CameraDistance.ToString();
        UpdateMinimapCameraRectangle();
    }

    private void UpdateMinimapCameraRectangle()
    {
        if (_currentProject == null)
            return;

        var t = _currentProject.Terrain;
        var vis = MapViewport.GetVisibleWorldBounds();

        const double miniSize = 198.0;
        var uLeft = (vis.Left - t.OffsetX) / t.WorldWidth;
        var uRight = (vis.Right - t.OffsetX) / t.WorldWidth;
        var vBottom = (vis.Top - t.OffsetY) / t.WorldHeight;
        var vTop = (vis.Bottom - t.OffsetY) / t.WorldHeight;

        var rx = Math.Clamp(uLeft * miniSize, 0, miniSize - 6);
        var ry = Math.Clamp((1.0 - vTop) * miniSize, 0, miniSize - 6);
        var rw = Math.Clamp((uRight - uLeft) * miniSize, 6, miniSize - rx);
        var rh = Math.Clamp((vTop - vBottom) * miniSize, 6, miniSize - ry);

        Canvas.SetLeft(RectMinimapCamera, rx);
        Canvas.SetTop(RectMinimapCamera, ry);
        RectMinimapCamera.Width = rw;
        RectMinimapCamera.Height = rh;
    }

    private void MinimapBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        PanCameraFromMinimapPoint(e.GetPosition(ImgSidebarMinimap));
    }

    private void MinimapBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            PanCameraFromMinimapPoint(e.GetPosition(ImgSidebarMinimap));
    }

    private void PanCameraFromMinimapPoint(Point pt)
    {
        if (_currentProject == null)
            return;

        var t = _currentProject.Terrain;
        var u = Math.Clamp(pt.X / 198.0, 0.0, 1.0);
        var v = Math.Clamp(1.0 - (pt.Y / 198.0), 0.0, 1.0);
        var wx = t.OffsetX + (u * t.WorldWidth);
        var wy = t.OffsetY + (v * t.WorldHeight);
        MapViewport.CenterOnWorldPoint(wx, wy);
    }

    private void LayerCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (MapViewport == null)
            return;

        MapViewport.ShowNeutralBuildingIcons = ChkNeutralBuildings.IsChecked == true;
        MapViewport.ShowCreepCampIcons = ChkCreepCamps.IsChecked == true;
        MapViewport.ShowDoodads = ChkShowDoodads.IsChecked == true;
        MapViewport.ShowRegions = ChkShowRegions.IsChecked == true;
        MapViewport.ShowGrid = ChkShowGrid.IsChecked == true;
        ImgSidebarMinimap.Visibility = ChkViewGameMinimap.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        ImgSidebarMinimap.Source = MapViewport.MinimapBitmap;
        MapViewport.MarkViewportDirty();
    }

    private void BtnDistanceDec_Click(object sender, RoutedEventArgs e)
    {
        MapViewport.CameraDistance = Math.Max(600, MapViewport.CameraDistance - 250);
    }

    private void BtnDistanceInc_Click(object sender, RoutedEventArgs e)
    {
        MapViewport.CameraDistance = Math.Min(12000, MapViewport.CameraDistance + 250);
    }

    private void BtnResetMapCamera_Click(object sender, RoutedEventArgs e)
    {
        TabMapViewport.IsSelected = true;
        MapViewport.ResetCameraToFit();
        Map3DViewport.ResetCamera();
    }

    private void BtnStandardEditorCamera_Click(object sender, RoutedEventArgs e)
    {
        TabMapViewport.IsSelected = true;
        MapViewport.ResetCameraToStandardEditorView();
    }

    private void BtnCycleStartLocations_Click(object sender, RoutedEventArgs e)
    {
        TabMapViewport.IsSelected = true;
        MapViewport.CycleStartLocationsOrUnits();
    }

    private void RefreshToolPaletteList()
    {
        if (CmbPaletteMode == null || LstToolPalette == null || PnlTerrainPalette == null)
            return;

        var mode = CmbPaletteMode.SelectedIndex;
        bool isTerrainMode = mode == 3;
        bool showEntityModeControls = mode == 0 || mode == 1;

        LstToolPalette.Visibility = isTerrainMode ? Visibility.Collapsed : Visibility.Visible;
        PnlTerrainPalette.Visibility = isTerrainMode ? Visibility.Visible : Visibility.Collapsed;
        if (PnlUnitDoodadModeControls != null)
            PnlUnitDoodadModeControls.Visibility = showEntityModeControls ? Visibility.Visible : Visibility.Collapsed;
        if (CmbPlaceOwner != null)
            CmbPlaceOwner.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (TxtPaletteFilter != null)
            TxtPaletteFilter.Visibility = isTerrainMode ? Visibility.Collapsed : Visibility.Visible;
        if (PnlPaletteActions != null)
            PnlPaletteActions.Visibility = isTerrainMode ? Visibility.Collapsed : Visibility.Visible;

        if (isTerrainMode)
        {
            PopulateTerrainTextureSwatches();
            SyncTerrainBrushToViewport();
            return;
        }

        SyncTerrainBrushToViewport();

        if (_currentProject == null)
        {
            LstToolPalette.ItemsSource = null;
            return;
        }

        var q = TxtPaletteFilter?.Text?.Trim() ?? string.Empty;

        if (mode == 0)
        {
            if (CmbPaletteSource?.SelectedIndex == 1)
            {
                LstToolPalette.ItemsSource = BuildPlacementCatalog(isDoodadMode: false, q);
            }
            else
            {
                IEnumerable<W3PlacedUnit> units = _currentProject.PlacedUnits;
                if (!string.IsNullOrEmpty(q))
                {
                    units = units.Where(u =>
                        u.Rawcode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        u.JassVariableName.Contains(q, StringComparison.OrdinalIgnoreCase));
                }
                LstToolPalette.ItemsSource = units.ToList();
            }
        }
        else if (mode == 1)
        {
            if (CmbPaletteSource?.SelectedIndex == 1)
            {
                LstToolPalette.ItemsSource = BuildPlacementCatalog(isDoodadMode: true, q);
            }
            else
            {
                IEnumerable<W3PlacedDoodad> doodads = _currentProject.PlacedDoodads;
                if (!string.IsNullOrEmpty(q))
                {
                    doodads = doodads.Where(d =>
                        d.Rawcode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                }
                LstToolPalette.ItemsSource = doodads.Take(500).ToList();
            }
        }
        else
        {
            IEnumerable<W3MapRegion> regs = _currentProject.Regions;
            if (!string.IsNullOrEmpty(q))
            {
                regs = regs.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            LstToolPalette.ItemsSource = regs.ToList();
        }

        Sync3DEditorMode();
    }

    private void MenuSelectTerrainPalette_Click(object sender, RoutedEventArgs e)
    {
        TabMapViewport.IsSelected = true;
        if (FloatingToolPalette != null)
            FloatingToolPalette.Visibility = Visibility.Visible;
        if (CmbPaletteMode != null)
            CmbPaletteMode.SelectedIndex = 3;
    }

    private void PopulateTerrainTextureSwatches()
    {
        if (WpTerrainTextures == null) return;
        WpTerrainTextures.Children.Clear();
        _terrainTextureButtons.Clear();

        if (_currentProject?.Terrain?.GroundTiles == null || _currentProject.Terrain.GroundTiles.Count == 0)
            return;

        if (_terrainBrush.TextureIndex >= _currentProject.Terrain.GroundTiles.Count)
            _terrainBrush.TextureIndex = 0;

        for (int i = 0; i < _currentProject.Terrain.GroundTiles.Count; i++)
        {
            int idx = i;
            string code = _currentProject.Terrain.GroundTiles[i];
            string displayName = TerrainBrushService.GetTilesetDisplayName(code);
            string hex = TerrainBrushService.GetTilesetSwatchHex(code);
            bool selected = idx == _terrainBrush.TextureIndex;

            var btn = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 3, 3),
                Tag = idx,
                ToolTip = $"[{code}] {displayName}",
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderThickness = new Thickness(selected ? 2.5 : 1),
                BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(0x16, 0x7A, 0xFF) : Color.FromRgb(0x60, 0x60, 0x60))
            };
            btn.Click += (_, _) =>
            {
                _terrainBrush.TextureIndex = idx;
                _suppressTerrainUiEvents = true;
                if (ChkApplyTexture != null)
                    ChkApplyTexture.IsChecked = true;
                _suppressTerrainUiEvents = false;
                UpdateTerrainTextureSwatchHighlights();
                SyncTerrainBrushToViewport();
            };
            _terrainTextureButtons.Add(btn);
            WpTerrainTextures.Children.Add(btn);
        }
    }

    private void UpdateTerrainTextureSwatchHighlights()
    {
        foreach (var btn in _terrainTextureButtons)
        {
            bool sel = btn.Tag is int i && i == _terrainBrush.TextureIndex;
            btn.BorderThickness = new Thickness(sel ? 2.5 : 1);
            btn.BorderBrush = new SolidColorBrush(sel ? Color.FromRgb(0x16, 0x7A, 0xFF) : Color.FromRgb(0x60, 0x60, 0x60));
        }
    }

    private void TerrainOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressTerrainUiEvents) return;
        SyncTerrainBrushToViewport();
    }

    private void BtnCliffTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tag) return;

        ToggleButton?[] allCliffBtns = { BtnCliffDec, BtnCliffSame, BtnCliffInc, BtnCliffWater, BtnCliffRamp };
        bool isCheckedNow = clicked.IsChecked == true;

        _suppressTerrainUiEvents = true;
        foreach (var btn in allCliffBtns)
        {
            if (btn != null && !ReferenceEquals(btn, clicked))
                btn.IsChecked = false;
        }

        if (isCheckedNow && Enum.TryParse<TerrainCliffTool>(tag, out var parsed))
        {
            _terrainBrush.CliffTool = parsed;
            if (ChkApplyCliff != null)
            {
                ChkApplyCliff.IsChecked = true;
                ChkApplyCliff.Content = parsed switch
                {
                    TerrainCliffTool.DecreaseOne => "Apply Cliff: Bajar 1",
                    TerrainCliffTool.SameLevel => "Apply Cliff: Mismo Nivel",
                    TerrainCliffTool.IncreaseOne => "Apply Cliff: Subir 1",
                    TerrainCliffTool.Water => "Apply Cliff: Agua",
                    TerrainCliffTool.Ramp => "Apply Cliff: Rampa",
                    _ => "Apply Cliff: NONE"
                };
            }
        }
        else
        {
            _terrainBrush.CliffTool = TerrainCliffTool.None;
            if (ChkApplyCliff != null)
            {
                ChkApplyCliff.IsChecked = false;
                ChkApplyCliff.Content = "Apply Cliff: NONE";
            }
        }
        _suppressTerrainUiEvents = false;
        SyncTerrainBrushToViewport();
    }

    private void BtnHeightTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tag) return;

        ToggleButton?[] allHeightBtns = { BtnHeightRaise, BtnHeightLower, BtnHeightPlateau, BtnHeightNoise, BtnHeightSmooth };
        bool isCheckedNow = clicked.IsChecked == true;

        _suppressTerrainUiEvents = true;
        foreach (var btn in allHeightBtns)
        {
            if (btn != null && !ReferenceEquals(btn, clicked))
                btn.IsChecked = false;
        }

        if (isCheckedNow && Enum.TryParse<TerrainHeightTool>(tag, out var parsed))
        {
            _terrainBrush.HeightTool = parsed;
            if (ChkApplyHeight != null)
            {
                ChkApplyHeight.IsChecked = true;
                ChkApplyHeight.Content = $"Apply Height: {parsed.ToString().ToUpperInvariant()}";
            }
        }
        else
        {
            _terrainBrush.HeightTool = TerrainHeightTool.None;
            if (ChkApplyHeight != null)
            {
                ChkApplyHeight.IsChecked = false;
                ChkApplyHeight.Content = "Apply Height: NONE";
            }
        }
        _suppressTerrainUiEvents = false;
        SyncTerrainBrushToViewport();
    }

    private void BtnBrushSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked || clicked.Tag is not string tag || !int.TryParse(tag, out int size))
            return;

        _terrainBrush.BrushSize = size;
        if (TxtBrushSizeLabel != null)
            TxtBrushSizeLabel.Text = $"Size: {size}";

        Button?[] allSizeBtns = { BtnSize1, BtnSize2, BtnSize3, BtnSize5, BtnSize8 };
        foreach (var b in allSizeBtns)
        {
            if (b == null) continue;
            bool active = b.Tag is string t && t == tag;
            b.SetResourceReference(Control.BackgroundProperty, active ? "ThemeSelectedBg" : "ThemeButtonBg");
            b.SetResourceReference(Control.ForegroundProperty, active ? "ThemeSelectedText" : "Win10Text");
            b.SetResourceReference(Control.BorderBrushProperty, active ? "Win10Accent" : "Win10Border");
        }
        SyncTerrainBrushToViewport();
    }

    private void BtnBrushShape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked || clicked.Tag is not string tag)
            return;

        _terrainBrush.Shape = tag == "Square" ? TerrainBrushShape.Square : TerrainBrushShape.Circle;
        if (TxtBrushShapeLabel != null)
            TxtBrushShapeLabel.Text = $"Shape: {(_terrainBrush.Shape == TerrainBrushShape.Square ? "Square" : "Circle")}";

        Button?[] allShapeBtns = { BtnShapeCircle, BtnShapeSquare };
        foreach (var b in allShapeBtns)
        {
            if (b == null) continue;
            bool active = b.Tag is string t && t == tag;
            b.SetResourceReference(Control.BackgroundProperty, active ? "ThemeSelectedBg" : "ThemeButtonBg");
            b.SetResourceReference(Control.ForegroundProperty, active ? "ThemeSelectedText" : "Win10Text");
            b.SetResourceReference(Control.BorderBrushProperty, active ? "Win10Accent" : "Win10Border");
        }
        SyncTerrainBrushToViewport();
    }

    private void SyncTerrainBrushToViewport()
    {
        if (Map3DViewport == null || CmbPaletteMode == null) return;

        _terrainBrush.ApplyTexture = ChkApplyTexture?.IsChecked == true;
        _terrainBrush.ApplyBlight = ChkApplyBlight?.IsChecked == true;
        _terrainBrush.ApplyCliff = ChkApplyCliff?.IsChecked == true;
        _terrainBrush.ApplyHeight = ChkApplyHeight?.IsChecked == true;

        if (!_terrainBrush.ApplyCliff && ChkApplyCliff != null)
            ChkApplyCliff.Content = "Apply Cliff: NONE";
        if (!_terrainBrush.ApplyHeight && ChkApplyHeight != null)
            ChkApplyHeight.Content = "Apply Height: NONE";

        bool hasTool =
            _terrainBrush.ApplyTexture ||
            _terrainBrush.ApplyBlight ||
            (_terrainBrush.ApplyCliff && _terrainBrush.CliffTool != TerrainCliffTool.None) ||
            (_terrainBrush.ApplyHeight && _terrainBrush.HeightTool != TerrainHeightTool.None);

        bool active = CmbPaletteMode.SelectedIndex == 3
            && _currentProject != null
            && (FloatingToolPalette?.Visibility == Visibility.Visible)
            && hasTool;

        Map3DViewport.SetTerrainBrush(active, _terrainBrush.BrushSize, _terrainBrush.Shape == TerrainBrushShape.Square);
    }

    private void DisableAllTerrainBrushTools()
    {
        _suppressTerrainUiEvents = true;
        if (ChkApplyTexture != null) ChkApplyTexture.IsChecked = false;
        if (ChkApplyBlight != null) ChkApplyBlight.IsChecked = false;
        if (ChkApplyCliff != null) { ChkApplyCliff.IsChecked = false; ChkApplyCliff.Content = "Apply Cliff: NONE"; }
        if (ChkApplyHeight != null) { ChkApplyHeight.IsChecked = false; ChkApplyHeight.Content = "Apply Height: NONE"; }
        foreach (var b in new[] { BtnCliffDec, BtnCliffSame, BtnCliffInc, BtnCliffWater, BtnCliffRamp, BtnHeightRaise, BtnHeightLower, BtnHeightPlateau, BtnHeightNoise, BtnHeightSmooth })
        {
            if (b != null) b.IsChecked = false;
        }
        _terrainBrush.CliffTool = TerrainCliffTool.None;
        _terrainBrush.HeightTool = TerrainHeightTool.None;
        _suppressTerrainUiEvents = false;
        SyncTerrainBrushToViewport();
    }

    private async void Map3DViewport_TerrainBrushDabReceived(float wx, float wy, bool isStrokeStart)
    {
        if (_currentProject?.Terrain?.RawW3eBytes == null) return;

        var dab = TerrainBrushService.ApplyDabDetailed(_currentProject, wx, wy, _terrainBrush, isStrokeStart, _terrainStroke);
        if (!dab.Modified) return;

        MapViewport.MarkViewportDirty();
        UpdateTestButtonState();
        await Map3DViewport.PatchW3eCornersAsync(dab);
    }

    private void CmbPaletteMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshToolPaletteList();
    private void TxtPaletteFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshToolPaletteList();

    private void LstToolPalette_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstToolPalette.SelectedItem == null)
            return;

        if (LstToolPalette.SelectedItem is CatalogPaletteItem)
        {
            _suppressPaletteModeEvents = true;
            if (TglModePlace != null) TglModePlace.IsChecked = true;
            if (TglModeSelect != null) TglModeSelect.IsChecked = false;
            _suppressPaletteModeEvents = false;
            Sync3DEditorMode();
            return;
        }

        MapViewport.SelectAndFocusEntity(LstToolPalette.SelectedItem);
        if (LstToolPalette.SelectedItem is W3PlacedUnit u)
        {
            Map3DViewport.SelectEntity("unit", u.CreationNumber, focus: true);
        }
        else if (LstToolPalette.SelectedItem is W3PlacedDoodad d)
        {
            Map3DViewport.SelectEntity("doodad", d.CreationNumber, focus: true);
        }
        else if (LstToolPalette.SelectedItem is W3MapRegion r)
        {
            Map3DViewport.FocusWorldPoint(r.CenterX, r.CenterY, 2200f);
        }
    }

    private void BtnPaletteCenterSelected_Click(object sender, RoutedEventArgs e)
    {
        var target = LstToolPalette.SelectedItem ?? MapViewport.SelectedEntity;
        if (target == null) return;
        MapViewport.SelectAndFocusEntity(target);
        if (target is W3PlacedUnit u)
            Map3DViewport.SelectEntity("unit", u.CreationNumber, focus: true);
        else if (target is W3PlacedDoodad d)
            Map3DViewport.SelectEntity("doodad", d.CreationNumber, focus: true);
        else if (target is W3MapRegion r)
            Map3DViewport.FocusWorldPoint(r.CenterX, r.CenterY, 2200f);
    }

    private void MenuPaletteUnits_Click(object sender, RoutedEventArgs e)
    {
        TabMapViewport.IsSelected = true;
        FloatingToolPalette.Visibility = Visibility.Visible;
        CmbPaletteMode.SelectedIndex = 0;
    }

    private void MenuPaletteDoodads_Click(object sender, RoutedEventArgs e)
    {
        TabMapViewport.IsSelected = true;
        FloatingToolPalette.Visibility = Visibility.Visible;
        CmbPaletteMode.SelectedIndex = 1;
    }

    private void MenuPaletteRegions_Click(object sender, RoutedEventArgs e)
    {
        TabMapViewport.IsSelected = true;
        FloatingToolPalette.Visibility = Visibility.Visible;
        CmbPaletteMode.SelectedIndex = 2;
    }

    private void MenuToggleToolPalette_Click(object sender, RoutedEventArgs e)
    {
        FloatingToolPalette.Visibility = FloatingToolPalette.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        SyncTerrainBrushToViewport();
    }

    private void MenuToggleGrid_Click(object sender, RoutedEventArgs e) => ChkShowGrid.IsChecked = !(ChkShowGrid.IsChecked == true);
    private void MenuToggleUnits_Click(object sender, RoutedEventArgs e)
    {
        MapViewport.ShowUnits = !MapViewport.ShowUnits;
        MapViewport.InvalidateVisual();
    }
    private void MenuToggleDoodads_Click(object sender, RoutedEventArgs e) => ChkShowDoodads.IsChecked = !(ChkShowDoodads.IsChecked == true);
    private void MenuToggleRegions_Click(object sender, RoutedEventArgs e) => ChkShowRegions.IsChecked = !(ChkShowRegions.IsChecked == true);

    private void BtnOpenSelectedUnitInF6_Click(object sender, RoutedEventArgs e)
    {
        var (cat, rawcode) = MapViewport.SelectedEntity switch
        {
            W3PlacedUnit u => (W3ObjectCategory.Units, u.Rawcode),
            W3PlacedDoodad d => (W3ObjectCategory.Destructables, d.Rawcode),
            _ => (W3ObjectCategory.Units, string.Empty)
        };

        OpenOrFocusObjectEditorWindow(cat, rawcode);
    }

    private void BtnCopySelectedJassVar_Click(object sender, RoutedEventArgs e)
    {
        var textToCopy = MapViewport.SelectedEntity switch
        {
            W3PlacedUnit u => u.JassVariableName,
            W3MapRegion r => r.JassVariableName,
            W3PlacedDoodad d => d.Rawcode,
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(textToCopy))
        {
            Clipboard.SetText(textToCopy);
            TxtStatusBarLeft.Text = $"Identificador copiado al portapapeles: {textToCopy}";
        }
    }

    // ==================== TRIGGERS & vJASS (F4) ====================

    private static string StripColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\|c[0-9a-fA-F]{8}|\|r|\|n", " ").Trim();
    }

    private void SelectTriggerNode(TriggerNode node)
    {
        if (node.Kind == TriggerNodeKind.Category && node.Children.Count > 0)
        {
            node = node.Children[0];
        }

        _activeTriggerNode = node;
        _suppressEditorEvents = true;
        CodeEditor.Text = node.Code ?? string.Empty;
        CodeEditor.ScrollToHome();
        _suppressEditorEvents = false;
        _mainCodeIde.RefreshFolding();

        TxtActiveTriggerTitle.Text = node.Name;
        TxtActiveTriggerMeta.Text = $"({node.CategoryName} • {CodeEditor.LineCount:N0} líneas)";
    }

    private void TreeTriggers_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TriggerNode node && node.Kind != TriggerNodeKind.Category)
        {
            SelectTriggerNode(node);
        }
    }

    private void CodeEditor_TextChanged(object sender, EventArgs e)
    {
        if (_suppressEditorEvents || _currentProject == null || _activeTriggerNode == null)
            return;

        var oldCode = _activeTriggerNode.Code;
        var newCode = CodeEditor.Text;
        if (oldCode == newCode)
            return;

        _activeTriggerNode.Code = newCode;
        _activeTriggerNode.IsModified = true;
        _mapMpqService.SynchronizeTriggerEditIntoScript(_currentProject, _activeTriggerNode, oldCode, newCode);
    }

    private void TxtTreeFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentProject == null)
            return;

        var q = TxtTreeFilter.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            TreeTriggers.ItemsSource = _currentProject.TriggerTree;
            return;
        }

        var filtered = _currentProject.AllTriggerNodes
            .Where(n => n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        n.Code.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        TreeTriggers.ItemsSource = filtered;
    }

    private void TxtCodeSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnFindInCode_Click(sender, e);
    }

    private void BtnFindInCode_Click(object sender, RoutedEventArgs e)
    {
        TabTriggers.IsSelected = true;
        var query = TxtCodeSearch.Text;
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(CodeEditor.Text))
            return;

        var startPos = CodeEditor.SelectionStart + CodeEditor.SelectionLength;
        if (startPos >= CodeEditor.Text.Length)
            startPos = 0;

        var idx = CodeEditor.Text.IndexOf(query, startPos, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 && startPos > 0)
            idx = CodeEditor.Text.IndexOf(query, 0, StringComparison.OrdinalIgnoreCase);

        if (idx >= 0)
        {
            CodeEditor.Select(idx, query.Length);
            var line = CodeEditor.Document.GetLineByOffset(idx).LineNumber;
            CodeEditor.ScrollToLine(line);
            CodeEditor.Focus();
        }
    }

    private void BtnNewVjassTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null)
            return;

        TabTriggers.IsSelected = true;
        var newName = $"NuevoSistema_{_currentProject.AllTriggerNodes.Count + 1}";
        var template = $@"// Trigger: {newName}
library {newName} initializer Init
    globals
        private constant integer SPELL_ID = 'A000'
    endglobals

    private function OnAction takes nothing returns nothing
        local unit caster = GetTriggerUnit()
        // Lógica compatible con Warcraft III 1.27b
        set caster = null
    endfunction

    private function Init takes nothing returns nothing
        local trigger t = CreateTrigger()
        call TriggerRegisterAnyUnitEventBJ(t, EVENT_PLAYER_UNIT_SPELL_EFFECT)
        call TriggerAddAction(t, function OnAction)
        set t = null
    endfunction
endlibrary
";
        var node = new TriggerNode
        {
            Name = newName,
            Kind = TriggerNodeKind.LibraryOrScope,
            CategoryName = "Scripts vJASS (war3map.wct)",
            Code = template,
            IsModified = true,
            StartLineInMapJ = 1
        };

        _currentProject.TriggerTree.Add(node);
        _currentProject.AllTriggerNodes.Add(node);
        _currentProject.RawMapScript += "\r\n" + template;
        SelectTriggerNode(node);
    }

    // ==================== GESTOR DE IMPORTACIONES (F12) ====================

    private void RefreshImportsGrid()
    {
        if (_currentProject == null)
            return;

        IEnumerable<W3ImportedFile> list = _currentProject.Imports.Where(i => !i.IsDeleted);
        if (!_settings.ShowInternalMapFilesInImports)
        {
            list = list.Where(i => !i.IsInternalSystemFile);
        }

        var filter = TxtImportFilter.Text.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            list = list.Where(i =>
                i.MpqPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                i.FileCategory.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        GridImports.ItemsSource = list.ToList();
    }

    private void TxtImportFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshImportsGrid();

    private void ImportsGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ImportsGrid_Drop(object sender, DragEventArgs e)
    {
        if (_currentProject == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ImportExternalFiles(files);
        }
    }

    private void BtnImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null)
        {
            MessageBox.Show(this, "Abre primero un mapa (.w3x / .w3m).", "Importaciones", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Importar archivos al mapa (.mdx, .blp, .tga, .mp3, .wav)",
            Multiselect = true,
            Filter = "Recursos de Warcraft III (*.mdx;*.mdl;*.blp;*.tga;*.mp3;*.wav;*.txt;*.slk)|*.mdx;*.mdl;*.blp;*.tga;*.mp3;*.wav;*.txt;*.slk|Todos los archivos (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            ImportExternalFiles(dlg.FileNames);
        }
    }

    private void ImportExternalFiles(IEnumerable<string> paths)
    {
        if (_currentProject == null)
            return;

        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;

            var bytes = File.ReadAllBytes(path);
            var fileName = Path.GetFileName(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var defaultMpqPath = ext is ".blp" or ".tga" && fileName.StartsWith("BTN", StringComparison.OrdinalIgnoreCase)
                ? @"ReplaceableTextures\CommandButtons\" + fileName
                : ext is ".blp" or ".tga" && fileName.StartsWith("DISBTN", StringComparison.OrdinalIgnoreCase)
                    ? @"ReplaceableTextures\CommandButtonsDisabled\" + fileName
                    : @"war3mapImported\" + fileName;

            var existing = _currentProject.Imports.FirstOrDefault(i => i.MpqPath.Equals(defaultMpqPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Data = bytes;
                existing.SizeBytes = bytes.Length;
                existing.IsPendingWrite = true;
                existing.IsDeleted = false;
                existing.Status = "Reemplazado (Pendiente guardar)";
            }
            else
            {
                _currentProject.Imports.Add(new W3ImportedFile
                {
                    MpqPath = defaultMpqPath,
                    SizeBytes = bytes.Length,
                    Data = bytes,
                    IsPendingWrite = true,
                    Status = "Nuevo (Pendiente guardar)"
                });
            }
            added++;
        }

        RefreshImportsGrid();
        TxtStatusBarLeft.Text = $"Se importaron {added} archivo(s). Presiona Ctrl+S para guardar en el MPQ.";
    }

    private void BtnExportSelectedFile_Click(object sender, RoutedEventArgs e)
    {
        if (GridImports.SelectedItem is not W3ImportedFile item || item.Data == null)
            return;

        var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(item.MpqPath),
            Filter = "Todos los archivos (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            File.WriteAllBytes(dlg.FileName, item.Data);
            TxtStatusBarLeft.Text = $"Archivo extraído a: {dlg.FileName}";
        }
    }

    private void BtnExportAllFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null)
            return;

        var baseDir = Path.Combine(
            Path.GetDirectoryName(_currentProject.FilePath) ?? _settings.DefaultMapsDir,
            Path.GetFileNameWithoutExtension(_currentProject.FilePath) + "_Extraido");

        Directory.CreateDirectory(baseDir);
        var count = 0;
        foreach (var imp in _currentProject.Imports.Where(i => !i.IsDeleted && i.Data != null))
        {
            var safeRelPath = imp.MpqPath.Replace('/', Path.DirectorySeparatorChar);
            var fullDest = Path.Combine(baseDir, safeRelPath);
            var dir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(fullDest, imp.Data!);
            count++;
        }

        MessageBox.Show(this, $"Se extrajeron {count} archivos del MPQ en:\n{baseDir}", "Extracción Completa", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnDeleteImportedFile_Click(object sender, RoutedEventArgs e)
    {
        if (GridImports.SelectedItem is not W3ImportedFile item)
            return;

        if (item.IsInternalSystemFile)
        {
            MessageBox.Show(
                this,
                LocalizationService.L(
                    $"El archivo '{item.MpqPath}' es un archivo interno esencial del sistema de Warcraft III y está protegido contra eliminación para evitar corromper el mapa.",
                    $"The file '{item.MpqPath}' is an essential internal Warcraft III system file and is protected from deletion to prevent map corruption."),
                LocalizationService.L("Archivo del Sistema Protegido", "Protected System File"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        item.IsDeleted = true;
        RefreshImportsGrid();
    }

    // ==================== VARIABLES GLOBALES Y BÚSQUEDA GLOBAL ====================

    private void RefreshGlobalVariablesGrid()
    {
        if (_currentProject == null)
            return;

        IEnumerable<W3GlobalVariable> vars = _currentProject.GlobalVariables;
        var q = TxtVarFilter.Text.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            vars = vars.Where(v =>
                v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                v.Type.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        GridGlobalVars.ItemsSource = vars.ToList();
    }

    private void TxtVarFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshGlobalVariablesGrid();

    private void TxtGlobalSearchQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnExecuteGlobalSearch_Click(sender, e);
    }

    private void BtnExecuteGlobalSearch_Click(object sender, RoutedEventArgs e)
    {
        _globalSearchResults.Clear();
        if (_currentProject == null)
            return;

        var query = TxtGlobalSearchQuery.Text.Trim();
        if (string.IsNullOrEmpty(query))
            return;

        foreach (var trig in _currentProject.AllTriggerNodes.Where(n => n.Kind != TriggerNodeKind.RootScript))
        {
            var lines = trig.Code.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    _globalSearchResults.Add(new CompilerDiagnostic
                    {
                        Source = "Trigger / vJASS",
                        TriggerOrFile = trig.Name,
                        Line = i + 1,
                        Message = lines[i].Trim()
                    });
                }
            }
        }

        foreach (var obj in _currentProject.Objects)
        {
            if (obj.Rawcode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                obj.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                obj.TooltipBasic.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                obj.TooltipExtended.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _globalSearchResults.Add(new CompilerDiagnostic
                {
                    Source = $"Objeto ({obj.CategoryLabel})",
                    TriggerOrFile = $"[{obj.Rawcode}] {StripColorCodes(obj.Name)}",
                    Line = 0,
                    Message = StripColorCodes(obj.TooltipExtended)
                });
            }
        }

        TxtGlobalSearchCount.Text = $"{_globalSearchResults.Count} coincidencia(s)";
    }

    private void GridGlobalSearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_currentProject == null || GridGlobalSearchResults.SelectedItem is not CompilerDiagnostic item)
            return;

        if (item.Source.StartsWith("Trigger", StringComparison.OrdinalIgnoreCase))
        {
            var node = _currentProject.AllTriggerNodes.FirstOrDefault(n => n.Name == item.TriggerOrFile);
            if (node != null)
            {
                SelectTriggerNode(node);
                TabTriggers.IsSelected = true;
                if (item.Line > 0 && item.Line <= CodeEditor.LineCount)
                {
                    var docLine = CodeEditor.Document.GetLineByNumber(item.Line);
                    CodeEditor.Select(docLine.Offset, docLine.Length);
                    CodeEditor.ScrollToLine(item.Line);
                    CodeEditor.Focus();
                }
            }
        }
        else if (item.TriggerOrFile.StartsWith("[") && item.TriggerOrFile.Length >= 6)
        {
            var rawcode = item.TriggerOrFile.Substring(1, 4);
            var obj = _currentProject?.Objects.FirstOrDefault(o => o.Rawcode == rawcode);
            if (obj != null)
            {
                OpenOrFocusObjectEditorWindow(obj.Category, rawcode);
            }
        }
    }

    // ==================== VALIDACIÓN JASSHELPER / PJASS Y TESTEO F9 ====================

    private async void BtnValidateJass_Click(object sender, RoutedEventArgs e)
    {
        await SaveAndCompileMapAsync();
    }

    private void GridDiagnostics_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridDiagnostics.SelectedItem is not CompilerDiagnostic diag || diag.Line <= 0)
            return;

        if (_currentProject != null)
        {
            var rootNode = _currentProject.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
            if (rootNode != null && _activeTriggerNode != rootNode)
            {
                SelectTriggerNode(rootNode);
            }
        }

        TabTriggers.IsSelected = true;
        if (diag.Line <= CodeEditor.LineCount)
        {
            var lineObj = CodeEditor.Document.GetLineByNumber(diag.Line);
            CodeEditor.Select(lineObj.Offset, lineObj.Length);
            CodeEditor.ScrollToLine(diag.Line);
            CodeEditor.Focus();
        }
    }

    // ==================== ABRIR, GUARDAR, COMPILAR Y TESTEAR MAPA ====================

    private bool _isClosingConfirmed;
    private bool _isMapCompiledAndReady;

    public void UpdateTestButtonState()
    {
        bool canTest = _currentProject != null && _isMapCompiledAndReady && !_currentProject.HasUnsavedChanges;
        if (BtnTestMap != null)
            BtnTestMap.IsEnabled = canTest;
        if (MenuTestMap != null)
            MenuTestMap.IsEnabled = canTest;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosingConfirmed)
            return;

        if (_currentProject != null && _currentProject.HasUnsavedChanges)
        {
            var res = MessageBox.Show(
                this,
                $"¿Desea guardar y compilar los cambios realizados en el mapa '{_currentProject.FileName}' antes de salir?",
                "Warcraft III World Editor",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (res == MessageBoxResult.Yes)
            {
                bool saved = false;
                try
                {
                    saved = Task.Run(async () => await SaveAndCompileMapAsync()).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Error al guardar el mapa: {ex.Message}", "Error al guardar", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Cancel = true;
                    return;
                }

                if (!saved)
                {
                    e.Cancel = true;
                    return;
                }
            }
            // Si seleccionó No o el guardado fue exitoso:
            // e.Cancel permanece false para que WPF continúe el cierre con normalidad.
            // NO se debe llamar a Close() de nuevo aquí.
        }

        _isClosingConfirmed = true;

        try
        {
            if (_standaloneObjectEditorWindow != null)
            {
                try { _standaloneObjectEditorWindow.Close(); } catch { }
                _standaloneObjectEditorWindow = null;
            }

            if (_standaloneTriggerEditorWindow != null)
            {
                try { _standaloneTriggerEditorWindow.Close(); } catch { }
                _standaloneTriggerEditorWindow = null;
            }

            if (_standaloneIconManagerWindow != null)
            {
                try { _standaloneIconManagerWindow.Close(); } catch { }
                _standaloneIconManagerWindow = null;
            }
            if (_standaloneOptimizerWindow != null)
            {
                try { _standaloneOptimizerWindow.Close(); } catch { }
                _standaloneOptimizerWindow = null;
            }
            if (_standaloneMapMediaWindow != null)
            {
                try { _standaloneMapMediaWindow.Close(); } catch { }
                _standaloneMapMediaWindow = null;
            }
        }
        catch { }
    }

    private async void BtnCloseMap_Click(object sender, RoutedEventArgs e)
    {
        if (!await PromptSaveIfModifiedAsync("antes de cerrar el mapa"))
            return;

        CloseCurrentMap();
    }

    private void CloseCurrentMap()
    {
        _currentProject = null;
        _isMapCompiledAndReady = false;
        UpdateTestButtonState();
        Title = "Warcraft III World Editor (1.27b) - [Sin mapa cargado]";

        try
        {
            if (_standaloneObjectEditorWindow != null && _standaloneObjectEditorWindow.IsLoaded)
            {
                _standaloneObjectEditorWindow.LoadProject(null);
            }
            if (_standaloneTriggerEditorWindow != null && _standaloneTriggerEditorWindow.IsLoaded)
            {
                _standaloneTriggerEditorWindow.LoadProject(null);
            }
            if (_standaloneIconManagerWindow != null && _standaloneIconManagerWindow.IsLoaded)
            {
                _standaloneIconManagerWindow.SetProject(null);
            }
            if (_standaloneOptimizerWindow != null && _standaloneOptimizerWindow.IsLoaded)
            {
                _standaloneOptimizerWindow.SetProject(null);
            }
            if (_standaloneMapMediaWindow != null && _standaloneMapMediaWindow.IsLoaded)
            {
                _standaloneMapMediaWindow.SetProject(null, null);
            }

            EmbeddedObjectEditor.LoadProject(null);
            TreeTriggers.ItemsSource = null;
            GridDiagnostics.ItemsSource = null;
            GridImports.ItemsSource = null;
            GridGlobalVars.ItemsSource = null;
            ImgSidebarMinimap.Source = null;
            MapViewport.LoadProject(null);
            Map3DViewport.LoadMap(null);
            RefreshToolPaletteList();

            TxtStatusBarLeft.Text = "Mapa cerrado. Usa Archivo -> Abrir Mapa (Ctrl+O) para cargar otro mapa.";
            TxtStatusBarRight.Text = "Sin mapa";
        }
        catch { }
    }

    private async Task<bool> PromptSaveIfModifiedAsync(string actionContext)
    {
        if (_currentProject == null || !_currentProject.HasUnsavedChanges)
            return true;

        var res = MessageBox.Show(
            this,
            $"¿Desea guardar y compilar los cambios realizados en el mapa '{_currentProject.FileName}' {actionContext}?",
            "Warcraft III World Editor",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Cancel)
            return false;

        if (res == MessageBoxResult.Yes)
        {
            return await SaveAndCompileMapAsync();
        }

        return true; // Descartar cambios y continuar
    }

    private async void BtnOpenMap_Click(object sender, RoutedEventArgs e)
    {
        if (!await PromptSaveIfModifiedAsync("antes de abrir otro mapa"))
            return;

        var dlg = new OpenFileDialog
        {
            Title = "Abrir Mapa de Warcraft III 1.27b (.w3x / .w3m)",
            InitialDirectory = Directory.Exists(_settings.DefaultMapsDir) ? _settings.DefaultMapsDir : @"F:\WARCRAFT",
            Filter = "Mapas de Warcraft III (*.w3x;*.w3m)|*.w3x;*.w3m|Todos los archivos (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            await LoadMapFileAsync(dlg.FileName);
        }
    }

    private static void EnsureGlobalTriggerDeclaration(ref string script, string triggerVarName)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
                script,
                $@"(?m)^\s*trigger\s+{System.Text.RegularExpressions.Regex.Escape(triggerVarName)}\b"))
        {
            return;
        }

        var decl = $"    trigger {triggerVarName} = null\r\n";
        var idx = script.IndexOf("endglobals", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            script = script.Insert(idx, decl);
        }
        else
        {
            script = "globals\r\n" + decl + "endglobals\r\n\r\n" + script;
        }
    }

    public async Task<bool> SaveAndCompileMapAsync()
    {
        if (_currentProject == null)
        {
            MessageBox.Show(this, "No hay ningún mapa cargado para guardar.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        MapProgressWindow? progressWin = null;
        try
        {
            var sw = Stopwatch.StartNew();
            TxtStatusBarLeft.Text = "Compilando y validando código JASS/vJASS del mapa (JassHelper / PJass)...";

            if (IsLoaded && IsVisible)
            {
                progressWin = new MapProgressWindow("World Editor — Guardando Mapa...", _currentProject.FileName, "Sincronizando variables globales y triggers GUI...")
                {
                    Owner = this
                };
                progressWin.Show();
                progressWin.UpdateProgress(10, "Sincronizando variables globales (udg_*) y triggers GUI...");
                await Task.Yield();
            }

            // 0. Sincronizar variables globales udg_* con globals ... endglobals
            JassKnowledgeService.SyncGlobalVariablesToScript(_currentProject);

            // 1. Inyectar y sincronizar todos los disparadores GUI en el script de mapa
            var guiTriggers = _currentProject.AllTriggerNodes.Where(n => n.Kind == TriggerNodeKind.GuiTrigger).ToList();
            if (guiTriggers.Count > 0)
            {
                var sbGui = new StringBuilder();
                var sbInits = new StringBuilder();

                foreach (var g in guiTriggers)
                {
                    var cleanName = System.Text.RegularExpressions.Regex.Replace(g.Name, @"[^A-Za-z0-9_]", "_");
                    if (string.IsNullOrWhiteSpace(cleanName)) cleanName = "Untitled";

                    // Asegurar declaración de la variable global de trigger
                    var scriptRef = _currentProject.RawMapScript;
                    EnsureGlobalTriggerDeclaration(ref scriptRef, $"gg_trg_{cleanName}");
                    _currentProject.RawMapScript = scriptRef;

                    bool alreadyInScript = System.Text.RegularExpressions.Regex.IsMatch(
                        _currentProject.RawMapScript,
                        $@"function\s+InitTrig_{System.Text.RegularExpressions.Regex.Escape(cleanName)}\b");

                    if (!alreadyInScript)
                    {
                        var code = string.IsNullOrWhiteSpace(g.Code) || g.IsModified ? g.GenerateJassFromGui() : g.Code;
                        g.Code = code;

                        sbGui.AppendLine($"// Trigger: {g.Name}");
                        sbGui.AppendLine(code);
                        sbGui.AppendLine();

                        sbInits.AppendLine($"    call InitTrig_{cleanName}()");
                    }
                }

                if (sbGui.Length > 0)
                {
                    if (_currentProject.RawMapScript.Contains("function InitCustomTriggers takes nothing returns nothing"))
                    {
                        _currentProject.RawMapScript = _currentProject.RawMapScript.Replace(
                            "function InitCustomTriggers takes nothing returns nothing",
                            sbGui.ToString() + "\nfunction InitCustomTriggers takes nothing returns nothing\n" + sbInits.ToString());
                    }
                    else if (_currentProject.RawMapScript.Contains("function main takes nothing returns nothing"))
                    {
                        _currentProject.RawMapScript = _currentProject.RawMapScript.Replace(
                            "function main takes nothing returns nothing",
                            sbGui.ToString() + "\nfunction main takes nothing returns nothing\n" + sbInits.ToString());
                    }
                    else
                    {
                        _currentProject.RawMapScript += "\n" + sbGui.ToString();
                    }
                }
            }

            // 2. Ejecutar compilación y validación del script
            progressWin?.UpdateProgress(28, "Compilando y validando código JASS / vJASS (JassHelper / PJass)...");
            await Task.Yield();

            var currentScript = _currentProject.RawMapScript;
            var compileRes = await _compilerService.ValidateAndCompileAsync(currentScript, _settings, _currentProject.FileName);

            _diagnostics.Clear();
            foreach (var d in compileRes.Diagnostics)
                _diagnostics.Add(d);

            _mainCodeIde.UpdateDiagnostics(_diagnostics);

            if (_standaloneTriggerEditorWindow != null && _standaloneTriggerEditorWindow.IsLoaded)
            {
                _standaloneTriggerEditorWindow.UpdateDiagnostics(_diagnostics);
            }

            var hasErrors = compileRes.Diagnostics.Any(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)) || !compileRes.Success;

            if (hasErrors)
            {
                progressWin?.Close();
                progressWin = null;

                _isMapCompiledAndReady = false;
                UpdateTestButtonState();

                TxtStatusBarLeft.Text = $"❌ Compilación fallida: se encontraron errores de sintaxis en '{_currentProject.FileName}'.";
                TxtStatusBarRight.Text = "Errores detectados";

                var errorCount = compileRes.Diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                var errorSample = string.Join("\n", compileRes.Diagnostics
                    .Where(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .Select(d => $"• Línea {d.DisplayLine} [{d.TriggerOrFile}]: {d.Message}"));

                MessageBox.Show(
                    this,
                    $"Se encontraron {errorCount} error(es) de compilación en el mapa:\n\n{errorSample}\n\n" +
                    "Debe corregir los errores de código en el Editor de Triggers antes de poder guardar y probar el mapa en Warcraft III.",
                    "Compilador Warcraft III — Errores Encontrados",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return false;
            }

            // 3. Compilación limpia: guardar archivo MPQ
            if (!string.IsNullOrWhiteSpace(compileRes.CompiledScript))
            {
                _currentProject.RawMapScript = compileRes.CompiledScript;
            }

            TxtStatusBarLeft.Text = "Empaquetando Triggers (F4) y Objetos (F6) en el archivo MPQ...";
            progressWin?.UpdateProgress(48, "Empaquetando datos en archivo MPQ...");
            await Task.Run(() => _mapMpqService.SaveMap(_currentProject, _currentProject.FilePath, (pct, msg) =>
            {
                progressWin?.UpdateProgress(pct, msg);
            }));
            _currentProject.RawMapScript = currentScript;
            sw.Stop();

            progressWin?.UpdateProgress(100, "Mapa guardado con éxito.");
            progressWin?.Close();
            progressWin = null;

            foreach (var node in _currentProject.AllTriggerNodes)
                node.IsModified = false;

            foreach (var obj in _currentProject.Objects)
            {
                obj.IsModified = false;
                foreach (var f in obj.Fields)
                    f.IsModified = false;
            }

            foreach (var imp in _currentProject.Imports)
                imp.IsPendingWrite = false;

            _isMapCompiledAndReady = true;
            UpdateTestButtonState();

            TxtStatusBarLeft.Text = $"✔ Mapa '{_currentProject.FileName}' guardado y compilado con éxito en {sw.Elapsed.TotalSeconds:F2}s. ¡Listo para probar (F9)!";
            TxtStatusBarRight.Text = "Compilación: 0 errores | Listo para probar";

            return true;
        }
        finally
        {
            progressWin?.Close();
        }
    }

    private async void BtnSaveAndCompile_Click(object sender, RoutedEventArgs e)
    {
        await SaveAndCompileMapAsync();
    }

    private async void BtnSaveMap_Click(object sender, RoutedEventArgs e)
    {
        await SaveAndCompileMapAsync();
    }

    private void BtnTestMap_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null)
            return;

        if (!_isMapCompiledAndReady || _currentProject.HasUnsavedChanges)
        {
            MessageBox.Show(
                this,
                "El mapa tiene cambios sin guardar o no ha sido compilado.\n\n" +
                "Por favor haga clic en '💾 Guardar (Ctrl+S / F7)' primero para compilar y guardar el mapa antes de probarlo.",
                "Compilación Requerida",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            TxtStatusBarLeft.Text = "Lanzando mapa en Warcraft III 1.27b...";
            _compilerService.LaunchMapInWarcraft3(_currentProject.FilePath, _settings);
            TxtStatusBarLeft.Text = $"Jugando '{_currentProject.FileName}' en Warcraft III 1.27b";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo lanzar Warcraft III 1.27b:\n{ex.Message}", "Error Test Runner", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnBrowseWar3Dir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta donde se encuentra Warcraft III 1.27b (war3.exe)",
            InitialDirectory = Directory.Exists(_settings.Warcraft3Dir) ? _settings.Warcraft3Dir : @"F:\WARCRAFT"
        };

        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            _settings.Warcraft3Dir = dlg.FolderName;
            _settings.Save();
            TxtTopWar3Dir.Text = _settings.Warcraft3Dir;
            await InitializeBaseMpqAsync();

            if (_currentProject != null)
            {
                MapViewport.LoadProject(_currentProject);
                ImgSidebarMinimap.Source = MapViewport.MinimapBitmap;
                EmbeddedObjectEditor.LoadProject(_currentProject);
            }
        }
    }

    private void ChkWindowedTest_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkWindowedTest == null)
            return;

        _settings.WindowedMode = ChkWindowedTest.IsChecked == true;
        if (MenuWindowedMode != null)
            MenuWindowedMode.IsChecked = _settings.WindowedMode;
        _settings.Save();
    }

    private void MenuTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string themeTag })
        {
            var theme = ThemeService.ParseTheme(themeTag);
            ThemeService.ApplyTheme(theme, _settings, saveSettings: true);
            UpdateThemeMenuCheckmarks();
        }
    }

    private void UpdateThemeMenuCheckmarks()
    {
        if (MenuThemeSub == null) return;
        foreach (var item in MenuThemeSub.Items.OfType<MenuItem>())
        {
            if (item.Tag is string tag)
            {
                item.IsChecked = ThemeService.ParseTheme(tag) == ThemeService.CurrentTheme;
            }
        }
    }

    private void MenuLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string langTag })
        {
            var normalized = LocalizationService.NormalizeLanguage(langTag);
            _settings.AppLanguage = normalized;
            _settings.Save();
            LocalizationService.SetLanguage(normalized, notify: true);
        }
    }

    private void OnAppLanguageChanged(string lang)
    {
        Dispatcher.Invoke(() =>
        {
            _baseDataService.RefreshLocalization(_currentProject);
            ApplyMainWindowLocalization();
            RefreshToolPaletteList();
            RefreshImportsGrid();
        });
    }

    private void OnAdvancedViewSettingsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            RefreshImportsGrid();
            MapMpqService.RefreshTriggerTreeAdvancedVisibility(_currentProject, _settings.ShowCompiledScriptInTriggers);
            if (_currentProject != null)
            {
                TreeTriggers.ItemsSource = null;
                TreeTriggers.ItemsSource = _currentProject.TriggerTree;
            }
            if (_standaloneTriggerEditorWindow != null && _standaloneTriggerEditorWindow.IsLoaded)
            {
                _standaloneTriggerEditorWindow.LoadProject(_currentProject);
            }
        });
    }

    private void ApplyMainWindowLocalization()
    {
        UpdateThemeMenuCheckmarks();
        if (MenuLangEs != null) MenuLangEs.IsChecked = LocalizationService.IsSpanish;
        if (MenuLangEn != null) MenuLangEn.IsChecked = LocalizationService.IsEnglish;
        if (MenuConfigRoot != null) MenuConfigRoot.Header = LocalizationService.L("_Configuración", "_Settings");
        if (MenuLanguageSub != null) MenuLanguageSub.Header = LocalizationService.L("🌐 _Idioma", "🌐 _Language");
        if (MenuThemeSub != null) MenuThemeSub.Header = LocalizationService.L("_Tema Visual", "_Visual Theme");
        if (MenuWar3Dir != null) MenuWar3Dir.Header = LocalizationService.L("Directorio de Warcraft III...", "Warcraft III Directory...");
        if (MenuWindowedMode != null) MenuWindowedMode.Header = LocalizationService.L("Probar Mapa en Modo Ventana (-window)", "Test Map in Windowed Mode (-window)");
        if (MenuSettingsItem != null) MenuSettingsItem.Header = LocalizationService.L("Opciones y Preferencias...", "Options & Preferences...");
        if (ChkWindowedTest != null) ChkWindowedTest.Content = LocalizationService.L("Ventana", "Windowed");
    }

    private void MenuToggleWindowed_Click(object sender, RoutedEventArgs e)
    {
        if (MenuWindowedMode != null && ChkWindowedTest != null)
        {
            ChkWindowedTest.IsChecked = MenuWindowedMode.IsChecked;
        }
    }

    private async void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        var prevDir = _settings.Warcraft3Dir;
        var dlg = new SettingsWindow(_settings)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true || !string.Equals(prevDir, _settings.Warcraft3Dir, StringComparison.OrdinalIgnoreCase))
        {
            TxtTopWar3Dir.Text = _settings.Warcraft3Dir;
            ChkWindowedTest.IsChecked = _settings.WindowedMode;
            if (MenuWindowedMode != null)
                MenuWindowedMode.IsChecked = _settings.WindowedMode;

            if (!string.Equals(prevDir, _settings.Warcraft3Dir, StringComparison.OrdinalIgnoreCase))
            {
                await InitializeBaseMpqAsync();
                if (_currentProject != null)
                {
                    MapViewport.LoadProject(_currentProject);
                    ImgSidebarMinimap.Source = MapViewport.MinimapBitmap;
                    EmbeddedObjectEditor.LoadProject(_currentProject);
                }
            }
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    // ==================== NAVEGACIÓN Y ATAJOS DE TECLADO ====================

    private void BtnFocusMapTab_Click(object sender, RoutedEventArgs e) => TabMapViewport.IsSelected = true;
    private void BtnFocusTriggers_Click(object sender, RoutedEventArgs e) => OpenOrFocusTriggerEditorWindow();
    private void BtnFocusObjects_Click(object sender, RoutedEventArgs e) => OpenOrFocusObjectEditorWindow(W3ObjectCategory.Units);
    private void BtnFocusImports_Click(object sender, RoutedEventArgs e) => TabImports.IsSelected = true;
    private void BtnFocusGlobalSearch_Click(object sender, RoutedEventArgs e)
    {
        TabGlobalSearch.IsSelected = true;
        TxtGlobalSearchQuery.Focus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F3)
        {
            TabMapViewport.IsSelected = true;
            e.Handled = true;
        }
        else if (e.Key == Key.F4)
        {
            OpenOrFocusTriggerEditorWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            OpenOrFocusObjectEditorWindow(W3ObjectCategory.Units);
            e.Handled = true;
        }
        else if (e.Key == Key.F7)
        {
            BtnSaveAndCompile_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F9)
        {
            BtnTestMap_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            TabImports.IsSelected = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Home && TabMapViewport.IsSelected)
        {
            MapViewport.ResetCameraToFit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && CmbPaletteMode?.SelectedIndex == 3)
        {
            DisableAllTerrainBrushTools();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            BtnOpenMap_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            BtnSaveAndCompile_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B)
        {
            TabGlobalSearch.IsSelected = true;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F)
        {
            BtnFocusGlobalSearch_Click(sender, e);
            e.Handled = true;
        }
    }
}
