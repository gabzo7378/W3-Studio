using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio.Controls;

public class WorldMapViewport : FrameworkElement
{
    private MapProject? _project;
    private WarcraftBaseDataService? _baseDataService;
    private BlpIconService? _blpIconService;

    // Sidebar minimap bitmaps (198x198 preview in left panel)
    private WriteableBitmap? _minimapWithTreesBitmap;
    private WriteableBitmap? _cleanMinimapBitmap;

    // Live high-resolution viewport framebuffer (renders real 64x64 .BLP ground tiles + 2.5D trees/doodads)
    private WriteableBitmap? _viewportBitmap;
    private byte[]? _viewportPixels;
    private int _vpWidth;
    private int _vpHeight;
    private bool _viewportDirty = true;

    // Extracted 64x64 BGRA32 ground tile variations for each tileset slot in war3map.w3e
    private readonly List<List<byte[]>> _groundTileVariations = new();

    // Pre-rendered 2.5D volumetric tree & boulder sprites (96x112 BGRA32)
    private static readonly Dictionary<char, byte[][]> TreeSpritesByTileset = new();
    private static byte[][]? _rockSprites;

    // Camera state in Warcraft III world coordinates
    private double _cameraCenterX;
    private double _cameraCenterY;
    private double _zoomScale = 0.08;
    private bool _isPanning;
    private Point _lastMousePos;
    private int _startLocationCycleIndex;

    // Layer visibility flags matching World Editor sidebar & View menu
    public bool ShowDoodads { get; set; } = true;
    public bool ShowUnits { get; set; } = true;
    public bool ShowRegions { get; set; } = false;
    public bool ShowGrid { get; set; } = false;
    public bool ShowNeutralBuildingIcons { get; set; } = true;
    public bool ShowCreepCampIcons { get; set; } = true;
    public bool ShowUnitLabels { get; set; } = false;

    public object? SelectedEntity { get; set; }
    public W3PlacedUnit? HoveredUnit { get; private set; }
    public W3MapRegion? HoveredRegion { get; private set; }

    public event Action<float, float, float, int>? CursorMovedOnMap;
    public event Action<object?>? SelectionChanged;
    public event Action? CameraChanged;

    public BitmapSource? MinimapBitmap => ShowDoodads ? _minimapWithTreesBitmap : _cleanMinimapBitmap;

    public int CameraDistance
    {
        get => (int)Math.Round(560.0 / Math.Max(0.015, _zoomScale));
        set
        {
            var clamped = Math.Clamp(value, 600, 14000);
            _zoomScale = 560.0 / clamped;
            _viewportDirty = true;
            InvalidateVisual();
            CameraChanged?.Invoke();
        }
    }

    public WorldMapViewport()
    {
        ClipToBounds = true;
        Focusable = true;
        EnsureRockSpritesInitialized();
    }

    public void InitializeServices(WarcraftBaseDataService baseDataService, BlpIconService blpIconService)
    {
        _baseDataService = baseDataService;
        _blpIconService = blpIconService;
    }

    public void MarkViewportDirty()
    {
        _viewportDirty = true;
        InvalidateVisual();
    }

    public void LoadProject(MapProject? project)
    {
        _project = project;
        SelectedEntity = null;
        HoveredUnit = null;
        HoveredRegion = null;
        _startLocationCycleIndex = 0;

        if (project == null)
        {
            _viewportBitmap = null;
            _cleanMinimapBitmap = null;
            _minimapWithTreesBitmap = null;
            _viewportDirty = true;
            InvalidateVisual();
            return;
        }

        LoadRealTerrainTileTextures(project);
        BuildSidebarMinimapBitmaps(project);
        EnsureTreeSpritesForTileset(project.Terrain.Tileset);

        ResetCameraToStandardEditorView();
    }

    public void ResetCameraToStandardEditorView()
    {
        if (_project == null)
            return;

        var t = _project.Terrain;
        double targetX = t.OffsetX + (t.WorldWidth * 0.5);
        double targetY = t.OffsetY + (t.WorldHeight * 0.5);

        var scenicUnit = _project.PlacedUnits
            .OrderBy(u => Math.Abs(u.X - targetX) + Math.Abs(u.Y - targetY))
            .FirstOrDefault();

        if (scenicUnit != null)
        {
            targetX = scenicUnit.X;
            targetY = scenicUnit.Y;
        }

        _cameraCenterX = targetX;
        _cameraCenterY = targetY;
        _zoomScale = 0.22;
        _viewportDirty = true;

        InvalidateVisual();
        CameraChanged?.Invoke();
    }

    public void CycleStartLocationsOrUnits()
    {
        if (_project == null || _project.PlacedUnits.Count == 0)
            return;

        var startLocs = _project.PlacedUnits.Where(u => u.IsStartLocation).ToList();
        var pool = startLocs.Count > 0 ? startLocs : _project.PlacedUnits;

        var target = pool[_startLocationCycleIndex % pool.Count];
        _startLocationCycleIndex++;
        SelectAndFocusEntity(target);
    }

    public void ResetCameraToFit()
    {
        if (_project == null)
            return;

        var t = _project.Terrain;
        _cameraCenterX = t.OffsetX + (t.WorldWidth * 0.5);
        _cameraCenterY = t.OffsetY + (t.WorldHeight * 0.5);

        var viewW = ActualWidth > 100 ? ActualWidth : 960;
        var viewH = ActualHeight > 100 ? ActualHeight : 650;
        var scaleX = (viewW * 0.96) / t.WorldWidth;
        var scaleY = (viewH * 0.96) / (t.WorldHeight * 0.84);
        _zoomScale = Math.Clamp(Math.Min(scaleX, scaleY), 0.02, 0.85);
        _viewportDirty = true;

        InvalidateVisual();
        CameraChanged?.Invoke();
    }

    public void CenterOnWorldPoint(double worldX, double worldY, double? targetZoomScale = null)
    {
        _cameraCenterX = worldX;
        _cameraCenterY = worldY;
        if (targetZoomScale.HasValue)
            _zoomScale = Math.Clamp(targetZoomScale.Value, 0.02, 0.85);
        _viewportDirty = true;
        InvalidateVisual();
        CameraChanged?.Invoke();
    }

    public void SelectAndFocusEntity(object entity)
    {
        SelectedEntity = entity;
        switch (entity)
        {
            case W3PlacedUnit u:
                CenterOnWorldPoint(u.X, u.Y, Math.Max(_zoomScale, 0.28));
                break;
            case W3MapRegion r:
                CenterOnWorldPoint(r.CenterX, r.CenterY, Math.Max(_zoomScale, 0.16));
                break;
            case W3PlacedDoodad d:
                CenterOnWorldPoint(d.X, d.Y, Math.Max(_zoomScale, 0.30));
                break;
        }
        SelectionChanged?.Invoke(entity);
        _viewportDirty = true;
        InvalidateVisual();
    }

    public Rect GetVisibleWorldBounds()
    {
        var w = Math.Max(10, ActualWidth) / _zoomScale;
        var h = Math.Max(10, ActualHeight) / (_zoomScale * 0.84);
        return new Rect(_cameraCenterX - (w * 0.5), _cameraCenterY - (h * 0.5), w, h);
    }

    // ==================== 1. REAL .BLP TERRAIN TEXTURE EXTRACTION (ANY MAP / ANY TILESET) ====================

    private void LoadRealTerrainTileTextures(MapProject project)
    {
        _groundTileVariations.Clear();
        var groundTiles = project.Terrain.GroundTiles;
        var count = Math.Max(16, groundTiles.Count);

        for (var i = 0; i < count; i++)
        {
            var rawcode = i < groundTiles.Count ? groundTiles[i] : string.Empty;
            var variations = TryExtractTileVariationsFromMpq(rawcode, i, project);
            _groundTileVariations.Add(variations);
        }
    }

    private List<byte[]> TryExtractTileVariationsFromMpq(string tileRawcode, int slotIndex, MapProject project)
    {
        var result = new List<byte[]>();

        if (_blpIconService != null && !string.IsNullOrWhiteSpace(tileRawcode))
        {
            var blpPath = _baseDataService?.GetTerrainTileTexturePath(tileRawcode)
                          ?? ResolveBuiltInTerrainBlpPath(tileRawcode);

            if (!string.IsNullOrEmpty(blpPath) &&
                _blpIconService.TryGetTexturePixels(blpPath, project, out var sheetPixels, out var sheetW, out var sheetH) &&
                sheetW >= 128 && sheetH >= 128)
            {
                var cols = sheetW >= sheetH * 2 ? 8 : 4;
                var rows = 4;
                var cellW = sheetW / cols;
                var cellH = sheetH / rows;

                // Sub-tile (0, 0) in Warcraft III terrain .blp files is ALWAYS the 100% solid full-coverage base tile
                var primaryTile = ExtractSeamless64x64SubTile(sheetPixels, sheetW, sheetH, 0, 0, cellW, cellH);
                result.Add(primaryTile);

                // In 8x4 extended sheets, also include any cell in cols 4..7 that is 100% opaque
                if (cols == 8)
                {
                    for (var r = 0; r < rows; r++)
                    {
                        for (var c = 4; c < 8; c++)
                        {
                            if (IsCellFullyOpaque(sheetPixels, sheetW, sheetH, c * cellW, r * cellH, cellW, cellH))
                            {
                                result.Add(ExtractSeamless64x64SubTile(sheetPixels, sheetW, sheetH, c * cellW, r * cellH, cellW, cellH));
                            }
                        }
                    }
                }
            }
        }

        if (result.Count == 0)
        {
            result.Add(GenerateProcedural64x64TerrainTile(tileRawcode, slotIndex, project.Terrain.Tileset));
        }

        return result;
    }

    private static bool IsCellFullyOpaque(byte[] sheet, int sheetW, int sheetH, int startX, int startY, int cellW, int cellH)
    {
        for (var y = startY + 8; y < startY + cellH - 8; y += 8)
        {
            for (var x = startX + 8; x < startX + cellW - 8; x += 8)
            {
                var idx = ((y * sheetW) + x) * 4;
                if (idx + 3 < sheet.Length && sheet[idx + 3] < 240)
                    return false;
            }
        }
        return true;
    }

    private static byte[] ExtractSeamless64x64SubTile(byte[] sheet, int sheetW, int sheetH, int startX, int startY, int cellW, int cellH)
    {
        var raw = new byte[64 * 64 * 4];
        var marginX = cellW * 0.06;
        var marginY = cellH * 0.06;
        var usableW = cellW * 0.88;
        var usableH = cellH * 0.88;

        for (var y = 0; y < 64; y++)
        {
            var sy = (int)Math.Clamp(startY + marginY + ((y / 63.0) * usableH), 0, sheetH - 1);
            for (var x = 0; x < 64; x++)
            {
                var sx = (int)Math.Clamp(startX + marginX + ((x / 63.0) * usableW), 0, sheetW - 1);
                var sIdx = ((sy * sheetW) + sx) * 4;
                var dIdx = ((y * 64) + x) * 4;
                raw[dIdx] = sheet[sIdx];         // B
                raw[dIdx + 1] = sheet[sIdx + 1]; // G
                raw[dIdx + 2] = sheet[sIdx + 2]; // R
                raw[dIdx + 3] = 255;
            }
        }

        // Apply mirror-blend near edges so world-space tiling across cells is 100% seamless
        var seamless = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        {
            var my = 63 - y;
            var wy = Math.Min(y, 63 - y) < 8 ? Math.Min(y, 63 - y) / 8.0f : 1.0f;
            for (var x = 0; x < 64; x++)
            {
                var mx = 63 - x;
                var wx = Math.Min(x, 63 - x) < 8 ? Math.Min(x, 63 - x) / 8.0f : 1.0f;
                var w = wx * wy;
                var i0 = ((y * 64) + x) * 4;
                var iM = ((my * 64) + mx) * 4;
                var wM = (1.0f - w) * 0.45f;
                var wP = 1.0f - wM;

                seamless[i0] = (byte)((raw[i0] * wP) + (raw[iM] * wM));
                seamless[i0 + 1] = (byte)((raw[i0 + 1] * wP) + (raw[iM + 1] * wM));
                seamless[i0 + 2] = (byte)((raw[i0 + 2] * wP) + (raw[iM + 2] * wM));
                seamless[i0 + 3] = 255;
            }
        }

        return seamless;
    }

    private static string? ResolveBuiltInTerrainBlpPath(string rawcode)
    {
        if (string.IsNullOrWhiteSpace(rawcode) || rawcode.Length < 4)
            return null;

        return rawcode switch
        {
            // Ashenvale
            "Adrt" => @"TerrainArt\Ashenvale\Ashen_Dirt.blp",
            "Adrd" => @"TerrainArt\Ashenvale\Ashen_DirtRough.blp",
            "Agrs" => @"TerrainArt\Ashenvale\Ashen_Grass.blp",
            "Arck" => @"TerrainArt\Ashenvale\Ashen_Rock.blp",
            "Agrd" => @"TerrainArt\Ashenvale\Ashen_GrassDark.blp",
            "Alvd" => @"TerrainArt\Ashenvale\Ashen_Leaves.blp",
            "Avin" => @"TerrainArt\Ashenvale\Ashen_Vines.blp",
            "Agrt" => @"TerrainArt\Ashenvale\Ashen_GrassyDirt.blp",
            // Lordaeron Summer
            "Ldrt" => @"TerrainArt\LordaeronSummer\Lords_Dirt.blp",
            "Ldro" => @"TerrainArt\LordaeronSummer\Lords_DirtRough.blp",
            "Ldrg" => @"TerrainArt\LordaeronSummer\Lords_DirtGrass.blp",
            "Lrck" => @"TerrainArt\LordaeronSummer\Lords_Rock.blp",
            "Lgrs" => @"TerrainArt\LordaeronSummer\Lords_Grass.blp",
            "Lgrd" => @"TerrainArt\LordaeronSummer\Lords_GrassDark.blp",
            // Lordaeron Fall
            "Fdrt" => @"TerrainArt\LordaeronFall\Lordf_Dirt.blp",
            "Fdro" => @"TerrainArt\LordaeronFall\Lordf_DirtRough.blp",
            "Fdrg" => @"TerrainArt\LordaeronFall\Lordf_DirtGrass.blp",
            "Frck" => @"TerrainArt\LordaeronFall\Lordf_Rock.blp",
            "Fgrs" => @"TerrainArt\LordaeronFall\Lordf_Grass.blp",
            "Fgrd" => @"TerrainArt\LordaeronFall\Lordf_GrassDark.blp",
            // Lordaeron Winter
            "Wdrt" => @"TerrainArt\LordaeronWinter\Lordw_Dirt.blp",
            "Wdro" => @"TerrainArt\LordaeronWinter\Lordw_DirtRough.blp",
            "Wsng" => @"TerrainArt\LordaeronWinter\Lordw_SnowGrass.blp",
            "Wrck" => @"TerrainArt\LordaeronWinter\Lordw_Rock.blp",
            "Wgrs" => @"TerrainArt\LordaeronWinter\Lordw_Grass.blp",
            "Wsnw" => @"TerrainArt\LordaeronWinter\Lordw_Snow.blp",
            // Barrens
            "Bdrt" => @"TerrainArt\Barrens\Barrens_Dirt.blp",
            "Bdrh" => @"TerrainArt\Barrens\Barrens_DirtRough.blp",
            "Bdrr" => @"TerrainArt\Barrens\Barrens_Pebbles.blp",
            "Bdrg" => @"TerrainArt\Barrens\Barrens_DirtGrass.blp",
            "Bdsr" => @"TerrainArt\Barrens\Barrens_Desert.blp",
            "Bdsd" => @"TerrainArt\Barrens\Barrens_DesertDark.blp",
            "Bflr" => @"TerrainArt\Barrens\Barrens_Rock.blp",
            "Bgrr" => @"TerrainArt\Barrens\Barrens_Grass.blp",
            // Northrend / Icecrown
            "Ndrt" => @"TerrainArt\Northrend\North_Dirt.blp",
            "Ndrd" => @"TerrainArt\Northrend\North_DirtDark.blp",
            "Nrck" => @"TerrainArt\Northrend\North_Rock.blp",
            "Ngrs" => @"TerrainArt\Northrend\North_Grass.blp",
            "Nice" => @"TerrainArt\Northrend\North_Ice.blp",
            "Nsnw" => @"TerrainArt\Northrend\North_Snow.blp",
            "Nsnd" => @"TerrainArt\Northrend\North_RockySnow.blp",
            _ => null
        };
    }

    private static byte[] GenerateProcedural64x64TerrainTile(string rawcode, int slotIndex, char tileset)
    {
        var tile = new byte[64 * 64 * 4];
        GetFallbackTileColor(rawcode, slotIndex, tileset, out var baseR, out var baseG, out var baseB);

        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var n1 = FastOrganicNoise(x * 2 + slotIndex * 17, y * 2 + slotIndex * 31);
                var n2 = FastOrganicNoise(x * 5 + 11, y * 5 + 23);
                var delta = (int)((n1 * 18f) + (n2 * 10f) - 14f);

                var idx = ((y * 64) + x) * 4;
                tile[idx] = (byte)Math.Clamp(baseB + delta, 0, 255);
                tile[idx + 1] = (byte)Math.Clamp(baseG + (int)(delta * 1.15f), 0, 255);
                tile[idx + 2] = (byte)Math.Clamp(baseR + delta, 0, 255);
                tile[idx + 3] = 255;
            }
        }
        return tile;
    }

    // ==================== 2. LIVE HIGH-RES VIEWPORT RENDERER (3D TILT + SEAMLESS WATER & TERRAIN + TREES) ====================

    private void RenderViewportFramebuffer()
    {
        if (_project == null)
            return;

        var viewW = (int)Math.Clamp(ActualWidth, 320, 1280);
        var viewH = (int)Math.Clamp(ActualHeight, 240, 820);

        if (_viewportBitmap == null || _vpWidth != viewW || _vpHeight != viewH || _viewportPixels == null)
        {
            _vpWidth = viewW;
            _vpHeight = viewH;
            _viewportPixels = new byte[viewW * viewH * 4];
            _viewportBitmap = new WriteableBitmap(viewW, viewH, 96, 96, PixelFormats.Bgra32, null);
        }

        var pixels = _viewportPixels;
        var t = _project.Terrain;
        var gw = Math.Max(2, t.GridWidth);
        var gh = Math.Max(2, t.GridHeight);
        var ox = t.OffsetX;
        var oy = t.OffsetY;

        var camX = _cameraCenterX;
        var camY = _cameraCenterY;
        var invScaleX = (1.0 / _zoomScale) * (ActualWidth > 10 ? ActualWidth / viewW : 1.0);
        // 0.84 vertical pitch aspect matches the 56° Angle-of-Attack tilt in the official Warcraft III World Editor
        var invScaleY = invScaleX / 0.84;
        var halfW = viewW * 0.5;
        var halfH = viewH * 0.5;

        var heights = t.GroundHeights;
        var waterDepths = t.WaterDepths;
        var texIndices = t.TextureIndices;
        var tileCatalog = _groundTileVariations;
        var tileCatalogCount = tileCatalog.Count;

        // Find rock texture buffer for steep cliff faces (usually slot 3 in WC3 tilesets: Lrck, Arck, Brck, etc.)
        var rockSlot = Math.Min(3, Math.Max(0, tileCatalogCount - 1));
        for (var i = 0; i < t.GroundTiles.Count; i++)
        {
            if (t.GroundTiles[i].EndsWith("rck", StringComparison.OrdinalIgnoreCase) ||
                t.GroundTiles[i].EndsWith("flr", StringComparison.OrdinalIgnoreCase))
            {
                rockSlot = i;
                break;
            }
        }
        var rockTileBuf = tileCatalog[rockSlot][0];

        // 1. Parallel high-resolution ground texture, smooth autotile, 3D slope lighting & bilinear water pass
        Parallel.For(0, viewH, py =>
        {
            // Subtle perspective foreshortening (top of screen sees slightly wider world span, like WC3 3D camera)
            var vScreenNorm = (halfH - py) / Math.Max(1.0, halfH); // +1 at top, -1 at bottom
            var perspFactor = 1.0 + (vScreenNorm * 0.08);
            var wy = camY + ((halfH - py) * invScaleY * perspFactor);

            var fy = (wy - oy) / 128.0;
            var gy = (int)Math.Floor(fy);
            var rowOffset = py * viewW * 4;

            if (gy < 0 || gy >= gh - 1 || texIndices == null)
            {
                for (var px = 0; px < viewW; px++)
                {
                    var idx = rowOffset + (px * 4);
                    pixels[idx] = 18;
                    pixels[idx + 1] = 20;
                    pixels[idx + 2] = 16;
                    pixels[idx + 3] = 255;
                }
                return;
            }

            var v = (float)(fy - gy);
            // Continuous world-space texture Y coordinate (tiles seamlessly every 2 world cells)
            var texY = ((int)Math.Floor((1.0 - (fy * 0.5)) * 64.0)) & 63;

            for (var px = 0; px < viewW; px++)
            {
                var wx = camX + ((px - halfW) * invScaleX * perspFactor);
                var fx = (wx - ox) / 128.0;
                var gx = (int)Math.Floor(fx);
                var outIdx = rowOffset + (px * 4);

                if (gx < 0 || gx >= gw - 1)
                {
                    pixels[outIdx] = 18;
                    pixels[outIdx + 1] = 20;
                    pixels[outIdx + 2] = 16;
                    pixels[outIdx + 3] = 255;
                    continue;
                }

                var u = (float)(fx - gx);
                var texX = ((int)Math.Floor(fx * 0.5 * 64.0)) & 63;
                var texelOffset = ((texY << 6) + texX) << 2;

                // 4 corner vertex textures of cell (gx, gy)
                var t00 = texIndices[gx, gy];
                var t10 = texIndices[gx + 1, gy];
                var t01 = texIndices[gx, gy + 1];
                var t11 = texIndices[gx + 1, gy + 1];

                float b, g, r;

                if (t00 == t10 && t00 == t01 && t00 == t11)
                {
                    var tileBuf = tileCatalog[t00 < tileCatalogCount ? t00 : 0][0];
                    b = tileBuf[texelOffset];
                    g = tileBuf[texelOffset + 1];
                    r = tileBuf[texelOffset + 2];
                }
                else
                {
                    // C0-continuous organic autotile blend: edgeEnvelope vanishes on cell borders (u=0,1 or v=0,1)
                    // so there are ZERO square grid lines between cells while transitions inside cells have grass tufts!
                    var edgeEnv = 16.0f * u * (1.0f - u) * v * (1.0f - v);
                    var n00 = (FastOrganicNoise((int)(wx * 0.18) + t00 * 31, (int)(wy * 0.18) + t00 * 17) - 0.5f) * edgeEnv;
                    var n10 = (FastOrganicNoise((int)(wx * 0.18) + t10 * 31, (int)(wy * 0.18) + t10 * 17) - 0.5f) * edgeEnv;
                    var n01 = (FastOrganicNoise((int)(wx * 0.18) + t01 * 31, (int)(wy * 0.18) + t01 * 17) - 0.5f) * edgeEnv;
                    var n11 = (FastOrganicNoise((int)(wx * 0.18) + t11 * 31, (int)(wy * 0.18) + t11 * 17) - 0.5f) * edgeEnv;

                    var w00 = Math.Max(0.001f, ((1f - u) * (1f - v)) * (1.0f + n00));
                    var w10 = Math.Max(0.001f, (u * (1f - v)) * (1.0f + n10));
                    var w01 = Math.Max(0.001f, ((1f - u) * v) * (1.0f + n01));
                    var w11 = Math.Max(0.001f, (u * v) * (1.0f + n11));

                    // Sharpen transition slightly so higher-index tiles (Grass/DarkGrass) form crisp organic patches
                    w00 *= w00 * (1.0f + t00 * 0.12f);
                    w10 *= w10 * (1.0f + t10 * 0.12f);
                    w01 *= w01 * (1.0f + t01 * 0.12f);
                    w11 *= w11 * (1.0f + t11 * 0.12f);
                    var invW = 1f / (w00 + w10 + w01 + w11);

                    var buf00 = tileCatalog[t00 < tileCatalogCount ? t00 : 0][0];
                    var buf10 = tileCatalog[t10 < tileCatalogCount ? t10 : 0][0];
                    var buf01 = tileCatalog[t01 < tileCatalogCount ? t01 : 0][0];
                    var buf11 = tileCatalog[t11 < tileCatalogCount ? t11 : 0][0];

                    b = ((buf00[texelOffset] * w00) + (buf10[texelOffset] * w10) + (buf01[texelOffset] * w01) + (buf11[texelOffset] * w11)) * invW;
                    g = ((buf00[texelOffset + 1] * w00) + (buf10[texelOffset + 1] * w10) + (buf01[texelOffset + 1] * w01) + (buf11[texelOffset + 1] * w11)) * invW;
                    r = ((buf00[texelOffset + 2] * w00) + (buf10[texelOffset + 2] * w10) + (buf01[texelOffset + 2] * w01) + (buf11[texelOffset + 2] * w11)) * invW;
                }

                // Smooth Bilinear 3D Terrain Slope & Cliff Rock Blending (Zero square cliff blocks!)
                if (heights != null)
                {
                    var h00 = heights[gx, gy];
                    var h10 = heights[gx + 1, gy];
                    var h01 = heights[gx, gy + 1];
                    var h11 = heights[gx + 1, gy + 1];

                    var dhx = ((h10 - h00) * (1f - v)) + ((h11 - h01) * v);
                    var dhy = ((h01 - h00) * (1f - u)) + ((h11 - h10) * u);
                    var slopeMag = (float)Math.Sqrt((dhx * dhx) + (dhy * dhy));

                    if (slopeMag > 42f)
                    {
                        var rockBlend = Math.Clamp((slopeMag - 42f) / 75f, 0f, 0.88f);
                        var invRock = 1f - rockBlend;
                        b = (b * invRock) + (rockTileBuf[texelOffset] * rockBlend);
                        g = (g * invRock) + (rockTileBuf[texelOffset + 1] * rockBlend);
                        r = (r * invRock) + (rockTileBuf[texelOffset + 2] * rockBlend);
                    }

                    // Noon directional sun lighting (top-left sun)
                    var light = Math.Clamp(1.04f + ((dhy - dhx) * 0.0032f), 0.62f, 1.32f);
                    r *= light;
                    g *= light;
                    b *= light;
                }

                // Smooth Bilinear Water Depth Rendering (Shallow Fordable Rivers + Deep Ocean)
                if (waterDepths != null)
                {
                    var wd00 = waterDepths[gx, gy];
                    var wd10 = waterDepths[gx + 1, gy];
                    var wd01 = waterDepths[gx, gy + 1];
                    var wd11 = waterDepths[gx + 1, gy + 1];

                    var wd = (wd00 * (1f - u) * (1f - v)) +
                             (wd10 * u * (1f - v)) +
                             (wd01 * (1f - u) * v) +
                             (wd11 * u * v);

                    if (wd > 0f)
                    {
                        if (wd < 68f)
                        {
                            // Shallow Fordable River Water (translucent violet-blue reflection over riverbed dirt/pebbles,
                            // matching the shallow rivers in the official World Editor screenshot!)
                            var tWater = wd / 68f;
                            var alphaW = 0.28f + (tWater * 0.38f);
                            var invA = 1.0f - alphaW;

                            // Subtle shoreline ripple highlight near wd = 0..10
                            var foam = wd < 10f ? (10f - wd) * 1.8f : 0f;
                            r = (r * invA) + ((118f + foam) * alphaW);
                            g = (g * invA) + ((116f + foam) * alphaW);
                            b = (b * invA) + ((172f + foam) * alphaW);
                        }
                        else
                        {
                            // Deep Ocean Water (rich dark royal/navy blue at the top edge of (2)Harrow.w3m)
                            var deepFactor = Math.Clamp((wd - 68f) / 90f, 0f, 1f);
                            var shR = (r * 0.34f) + 78f;
                            var shG = (g * 0.34f) + 76f;
                            var shB = (b * 0.34f) + 148f;

                            var dpR = 20f;
                            var dpG = 38f;
                            var dpB = 138f;

                            r = shR + ((dpR - shR) * deepFactor);
                            g = shG + ((dpG - shG) * deepFactor);
                            b = shB + ((dpB - shB) * deepFactor);
                        }
                    }
                }

                pixels[outIdx] = (byte)Math.Clamp((int)b, 0, 255);
                pixels[outIdx + 1] = (byte)Math.Clamp((int)g, 0, 255);
                pixels[outIdx + 2] = (byte)Math.Clamp((int)r, 0, 255);
                pixels[outIdx + 3] = 255;
            }
        });

        // 2. Painter's Algorithm 2.5D Volumetric Trees & Rocks Pass (sorted back-to-front by Y)
        if (ShowDoodads && _project.PlacedDoodads.Count > 0)
        {
            RenderVolumetricDoodadsIntoBuffer(pixels, viewW, viewH, camX, camY, invScaleX, invScaleY);
        }

        _viewportBitmap.WritePixels(new Int32Rect(0, 0, viewW, viewH), pixels, viewW * 4, 0);
        _viewportDirty = false;
    }

    private void RenderVolumetricDoodadsIntoBuffer(
        byte[] fb, int viewW, int viewH,
        double camX, double camY, double invScaleX, double invScaleY)
    {
        if (_project == null)
            return;

        var halfW = viewW * 0.5;
        var halfH = viewH * 0.5;
        var marginWorld = 400.0;
        var minX = camX - (halfW * invScaleX * 1.15) - marginWorld;
        var maxX = camX + (halfW * invScaleX * 1.15) + marginWorld;
        var minY = camY - (halfH * invScaleY * 1.15) - marginWorld;
        var maxY = camY + (halfH * invScaleY * 1.15) + marginWorld;

        var visible = new List<W3PlacedDoodad>(1024);
        foreach (var d in _project.PlacedDoodads)
        {
            if (d.X >= minX && d.X <= maxX && d.Y >= minY && d.Y <= maxY)
                visible.Add(d);
        }

        // Painter's algorithm: draw from top of screen (max Y) to bottom of screen (min Y)
        visible.Sort((a, b) => b.Y.CompareTo(a.Y));

        var treeSprites = EnsureTreeSpritesForTileset(_project.Terrain.Tileset);
        var rockSprites = EnsureRockSpritesInitialized();
        var scaleX = 1.0 / invScaleX;
        var scaleY = 1.0 / invScaleY;

        foreach (var d in visible)
        {
            var pt = WorldToViewportPixel(d.X, d.Y, halfW, halfH, camX, camY, invScaleX, invScaleY);
            var screenX = (int)pt.X;
            var screenY = (int)pt.Y;

            var doodadScale = Math.Clamp(d.Scale, 0.75f, 1.55f);
            if (d.IsTree)
            {
                // Full Warcraft III 3D forest canopy width (~310x350 world units) so trees overlap into dense, lush forests
                var dstW = (int)Math.Clamp(315.0 * doodadScale * scaleX, 7, 260);
                var dstH = (int)Math.Clamp(355.0 * doodadScale * scaleY, 8, 290);
                var sprite = treeSprites[Math.Abs(d.CreationNumber) % treeSprites.Length];

                var startX = screenX - (dstW / 2);
                var startY = screenY - (int)(dstH * 0.78);
                BlitScaledSpriteBgra32(fb, viewW, viewH, sprite, 96, 112, startX, startY, dstW, dstH);
            }
            else
            {
                var dstW = (int)Math.Clamp(165.0 * doodadScale * scaleX, 4, 150);
                var dstH = (int)Math.Clamp(145.0 * doodadScale * scaleY, 4, 130);
                var sprite = rockSprites[Math.Abs(d.CreationNumber) % rockSprites.Length];
                var startX = screenX - (dstW / 2);
                var startY = screenY - (int)(dstH * 0.65);
                BlitScaledSpriteBgra32(fb, viewW, viewH, sprite, 64, 64, startX, startY, dstW, dstH);
            }
        }
    }

    private static Point WorldToViewportPixel(
        double worldX, double worldY,
        double halfW, double halfH,
        double camX, double camY,
        double invScaleX, double invScaleY)
    {
        var dyWorld = worldY - camY;
        var rawPy = halfH - (dyWorld / invScaleY);
        var vScreenNorm = (halfH - rawPy) / Math.Max(1.0, halfH);
        var perspFactor = 1.0 + (vScreenNorm * 0.08);

        var py = halfH - (dyWorld / (invScaleY * perspFactor));
        var px = halfW + ((worldX - camX) / (invScaleX * perspFactor));
        return new Point(px, py);
    }

    private static void BlitScaledSpriteBgra32(
        byte[] fb, int fbW, int fbH,
        byte[] sprite, int spW, int spH,
        int dstX, int dstY, int dstW, int dstH)
    {
        if (dstW <= 0 || dstH <= 0)
            return;

        var x0 = Math.Max(0, dstX);
        var y0 = Math.Max(0, dstY);
        var x1 = Math.Min(fbW, dstX + dstW);
        var y1 = Math.Min(fbH, dstY + dstH);
        if (x0 >= x1 || y0 >= y1)
            return;

        var stepX = (spW << 16) / dstW;
        var stepY = (spH << 16) / dstH;

        for (var y = y0; y < y1; y++)
        {
            var sy = ((y - dstY) * stepY) >> 16;
            if (sy < 0 || sy >= spH) continue;
            var spRow = sy * spW;
            var fbRow = y * fbW;

            var sxFixed = (x0 - dstX) * stepX;
            for (var x = x0; x < x1; x++, sxFixed += stepX)
            {
                var sx = sxFixed >> 16;
                var spIdx = (spRow + sx) << 2;
                var a = sprite[spIdx + 3];
                if (a == 0) continue;

                var fbIdx = (fbRow + x) << 2;
                if (a == 255)
                {
                    fb[fbIdx] = sprite[spIdx];
                    fb[fbIdx + 1] = sprite[spIdx + 1];
                    fb[fbIdx + 2] = sprite[spIdx + 2];
                }
                else
                {
                    var invA = 255 - a;
                    fb[fbIdx] = (byte)(((sprite[spIdx] * a) + (fb[fbIdx] * invA)) >> 8);
                    fb[fbIdx + 1] = (byte)(((sprite[spIdx + 1] * a) + (fb[fbIdx + 1] * invA)) >> 8);
                    fb[fbIdx + 2] = (byte)(((sprite[spIdx + 2] * a) + (fb[fbIdx + 2] * invA)) >> 8);
                }
            }
        }
    }

    // ==================== 3. VOLUMETRIC 2.5D TREE & ROCK SPRITE GENERATOR ====================

    private static byte[][] EnsureTreeSpritesForTileset(char tileset)
    {
        if (TreeSpritesByTileset.TryGetValue(tileset, out var existing))
            return existing;

        (byte rDark, byte gDark, byte bDark, byte rMid, byte gMid, byte bMid, byte rLit, byte gLit, byte bLit) pal = tileset switch
        {
            'F' => (42, 28, 8, 138, 82, 18, 212, 142, 38),
            'W' or 'I' or 'N' => (12, 38, 28, 42, 98, 72, 182, 218, 212),
            'B' => (34, 48, 14, 86, 116, 32, 142, 170, 62),
            'C' => (16, 38, 22, 46, 92, 42, 96, 148, 58),
            _ => (4, 28, 6, 18, 96, 20, 56, 158, 36) // Deep rich Warcraft III pine emerald
        };

        var variations = new byte[4][];
        for (var v = 0; v < 4; v++)
        {
            variations[v] = BuildSingleVolumetricTreeSprite96x112(v, pal);
        }

        TreeSpritesByTileset[tileset] = variations;
        return variations;
    }

    private static byte[] BuildSingleVolumetricTreeSprite96x112(
        int variation,
        (byte rDark, byte gDark, byte bDark, byte rMid, byte gMid, byte bMid, byte rLit, byte gLit, byte bLit) pal)
    {
        const int w = 96, h = 112;
        var buf = new byte[w * h * 4];

        // 1. Deep dark forest-floor ambient shadow so adjacent trees merge into a lush dark canopy floor
        for (var y = 62; y < 108; y++)
        {
            for (var x = 8; x < 90; x++)
            {
                var dx = (x - 51) / 38.0;
                var dy = (y - 86) / 20.0;
                var dist = (dx * dx) + (dy * dy);
                if (dist < 1.0)
                {
                    var alpha = (byte)((1.0 - dist) * 195);
                    SetPixelAlpha(buf, w, x, y, 3, 12, 4, alpha);
                }
            }
        }

        // 2. Short shaded trunk at base (mostly tucked beneath lowest pine boughs just like official WE)
        for (var y = 76; y <= 93; y++)
        {
            for (var x = 44; x <= 52; x++)
            {
                var barkShade = x < 48 ? 0.95f : 0.62f;
                SetPixelAlpha(buf, w, x, y, (byte)(68 * barkShade), (byte)(44 * barkShade), (byte)(24 * barkShade), 255);
            }
        }

        // 3. 4 Full-Bodied Overlapping Conical Pine Bough Tiers (extending wide at base so forests have zero gaps)
        (double cx, double cy, double rx, double ry, int lobes)[] tiers =
        {
            (48 + ((variation % 2) * 2 - 1), 74, 42, 25, 12), // Wide bottom pine boughs covering base
            (48 - ((variation % 2) * 2 - 1), 55, 35, 22, 11), // Lower-mid bough tier
            (48, 38, 27, 18, 9),                               // Upper-mid tier
            (48, 22, 17, 14, 7)                                // Conical pine crown
        };

        for (var tIdx = 0; tIdx < tiers.Length; tIdx++)
        {
            var tier = tiers[tIdx];
            var minY = (int)Math.Max(2, tier.cy - tier.ry - 6);
            var maxY = (int)Math.Min(h - 2, tier.cy + tier.ry + 6);
            var minX = (int)Math.Max(2, tier.cx - tier.rx - 6);
            var maxX = (int)Math.Min(w - 2, tier.cx + tier.rx + 6);

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var dx = (x - tier.cx) / tier.rx;
                    var dy = (y - tier.cy) / tier.ry;
                    var angle = Math.Atan2(dy, dx);
                    var dist = Math.Sqrt((dx * dx) + (dy * dy));

                    var lobeWave = Math.Cos((angle * tier.lobes) + (variation * 1.3) + (tIdx * 0.9)) * 0.14;
                    var needleNoise = (FastOrganicNoise(x * 2 + variation * 19, y * 2 + tIdx * 29) - 0.5f) * 0.22;
                    var effectiveRadius = 1.0 + lobeWave + needleNoise;

                    if (dist > effectiveRadius)
                        continue;

                    var sunDot = (-dx * 0.50) + (-dy * 0.58) + ((tIdx / 3.0) * 0.24);
                    var needleDetail = (FastOrganicNoise(x * 4 + tIdx * 7, y * 4 + variation * 13) - 0.5) * 0.45;
                    var shade = Math.Clamp(0.48 + (sunDot * 0.55) + needleDetail, 0.0, 1.0);

                    if (dy > 0.30 && dist > effectiveRadius * 0.68)
                        shade *= 0.38;

                    byte r, g, b;
                    if (shade < 0.48)
                    {
                        var k = (float)(shade / 0.48);
                        r = (byte)(pal.rDark + ((pal.rMid - pal.rDark) * k));
                        g = (byte)(pal.gDark + ((pal.gMid - pal.gDark) * k));
                        b = (byte)(pal.bDark + ((pal.bMid - pal.bDark) * k));
                    }
                    else
                    {
                        var k = (float)((shade - 0.48) / 0.52);
                        r = (byte)(pal.rMid + ((pal.rLit - pal.rMid) * k));
                        g = (byte)(pal.gMid + ((pal.gLit - pal.gMid) * k));
                        b = (byte)(pal.bMid + ((pal.bLit - pal.bMid) * k));
                    }

                    var edgeSoftness = Math.Clamp((effectiveRadius - dist) * 6.5, 0.0, 1.0);
                    var alpha = (byte)(edgeSoftness * 255);
                    BlendPixelInBuffer(buf, w, x, y, r, g, b, alpha);
                }
            }
        }

        return buf;
    }

    private static byte[][] EnsureRockSpritesInitialized()
    {
        if (_rockSprites != null)
            return _rockSprites;

        _rockSprites = new byte[2][];
        for (var v = 0; v < 2; v++)
        {
            const int sz = 64;
            var buf = new byte[sz * sz * 4];
            for (var y = 8; y < 58; y++)
            {
                for (var x = 6; x < 58; x++)
                {
                    var dx = (x - 32) / 22.0;
                    var dy = (y - 34) / 18.0;
                    var n = (FastOrganicNoise(x * 2 + v * 17, y * 2) - 0.5) * 0.25;
                    var dist = Math.Sqrt((dx * dx) + (dy * dy)) + n;
                    if (dist > 1.0) continue;

                    var shade = Math.Clamp(0.60 - (dx * 0.35) - (dy * 0.40) + (FastOrganicNoise(x * 3, y * 3) * 0.25), 0.22, 1.10);
                    var gray = (byte)Math.Clamp(118 * shade, 36, 185);
                    var alpha = (byte)(Math.Clamp((1.0 - dist) * 5.0, 0.0, 1.0) * 255);
                    BlendPixelInBuffer(buf, sz, x, y, gray, (byte)(gray * 0.96), (byte)(gray * 0.88), alpha);
                }
            }
            _rockSprites[v] = buf;
        }
        return _rockSprites;
    }

    private static void SetPixelAlpha(byte[] buf, int w, int x, int y, byte r, byte g, byte b, byte a)
    {
        var idx = ((y * w) + x) * 4;
        buf[idx] = b;
        buf[idx + 1] = g;
        buf[idx + 2] = r;
        buf[idx + 3] = a;
    }

    private static void BlendPixelInBuffer(byte[] buf, int w, int x, int y, byte r, byte g, byte b, byte a)
    {
        var idx = ((y * w) + x) * 4;
        var dstA = buf[idx + 3];
        if (dstA == 0 || a == 255)
        {
            buf[idx] = b;
            buf[idx + 1] = g;
            buf[idx + 2] = r;
            buf[idx + 3] = Math.Max(dstA, a);
        }
        else
        {
            var invA = 255 - a;
            buf[idx] = (byte)(((b * a) + (buf[idx] * invA)) >> 8);
            buf[idx + 1] = (byte)(((g * a) + (buf[idx + 1] * invA)) >> 8);
            buf[idx + 2] = (byte)(((r * a) + (buf[idx + 2] * invA)) >> 8);
            buf[idx + 3] = (byte)Math.Min(255, dstA + a);
        }
    }

    // ==================== 4. SIDEBAR MINIMAP BITMAP BUILDER ====================

    private void BuildSidebarMinimapBitmaps(MapProject project)
    {
        const int bmpSize = 256;
        var pixels = new byte[bmpSize * bmpSize * 4];

        var t = project.Terrain;
        var tgaPixels = DecodeTgaIfAvailable(t.MinimapTgaBytes, out var tgaW, out var tgaH);
        var gw = Math.Max(2, t.GridWidth);
        var gh = Math.Max(2, t.GridHeight);

        for (var py = 0; py < bmpSize; py++)
        {
            var vNorm = 1.0 - (py / (double)(bmpSize - 1));
            var gy = (int)Math.Clamp(vNorm * (gh - 1), 0, gh - 1);

            for (var px = 0; px < bmpSize; px++)
            {
                var uNorm = px / (double)(bmpSize - 1);
                var gx = (int)Math.Clamp(uNorm * (gw - 1), 0, gw - 1);

                byte r = 46, g = 88, b = 42;
                if (tgaPixels != null && tgaW > 0 && tgaH > 0)
                {
                    var tx = (int)Math.Clamp(uNorm * (tgaW - 1), 0, tgaW - 1);
                    var ty = (int)Math.Clamp((1.0 - vNorm) * (tgaH - 1), 0, tgaH - 1);
                    var tIdx = ((ty * tgaW) + tx) * 4;
                    if (tIdx + 3 < tgaPixels.Length)
                    {
                        b = tgaPixels[tIdx];
                        g = tgaPixels[tIdx + 1];
                        r = tgaPixels[tIdx + 2];
                    }
                }
                else if (t.TextureIndices != null)
                {
                    var texIdx = t.TextureIndices[gx, gy];
                    var raw = texIdx < t.GroundTiles.Count ? t.GroundTiles[texIdx] : string.Empty;
                    GetFallbackTileColor(raw, texIdx, t.Tileset, out r, out g, out b);
                }

                var outIdx = ((py * bmpSize) + px) * 4;
                pixels[outIdx] = b;
                pixels[outIdx + 1] = g;
                pixels[outIdx + 2] = r;
                pixels[outIdx + 3] = 255;
            }
        }

        _cleanMinimapBitmap = new WriteableBitmap(bmpSize, bmpSize, 96, 96, PixelFormats.Bgra32, null);
        _cleanMinimapBitmap.WritePixels(new Int32Rect(0, 0, bmpSize, bmpSize), pixels, bmpSize * 4, 0);

        var treePixels = (byte[])pixels.Clone();
        foreach (var d in project.PlacedDoodads)
        {
            if (!d.IsTree) continue;
            var u = (d.X - t.OffsetX) / t.WorldWidth;
            var v = (d.Y - t.OffsetY) / t.WorldHeight;
            if (u < 0 || u > 1 || v < 0 || v > 1) continue;
            var cx = (int)(u * (bmpSize - 1));
            var cy = (int)((1.0 - v) * (bmpSize - 1));
            var idx = ((cy * bmpSize) + cx) * 4;
            treePixels[idx] = 18;
            treePixels[idx + 1] = 88;
            treePixels[idx + 2] = 16;
        }

        _minimapWithTreesBitmap = new WriteableBitmap(bmpSize, bmpSize, 96, 96, PixelFormats.Bgra32, null);
        _minimapWithTreesBitmap.WritePixels(new Int32Rect(0, 0, bmpSize, bmpSize), treePixels, bmpSize * 4, 0);
    }

    private static float FastOrganicNoise(int x, int y)
    {
        var n = x + (y * 57);
        n = (n << 13) ^ n;
        return (1.0f - (((n * ((n * n * 15731) + 789221) + 1376312589) & 0x7fffffff) / 1073741824.0f)) * 0.5f + 0.5f;
    }

    private static void GetFallbackTileColor(string rawcode, int texIdx, char tileset, out byte r, out byte g, out byte b)
    {
        if (!string.IsNullOrEmpty(rawcode) && rawcode.Length >= 4)
        {
            var suffix = rawcode.Substring(1, 3).ToLowerInvariant();
            if (suffix.Contains("grs")) { r = 46; g = 118; b = 34; return; }
            if (suffix.Contains("grd") || suffix.Contains("vin")) { r = 28; g = 86; b = 24; return; }
            if (suffix.Contains("drt") || suffix.Contains("dro")) { r = 132; g = 96; b = 58; return; }
            if (suffix.Contains("rck") || suffix.Contains("flr")) { r = 116; g = 112; b = 104; return; }
            if (suffix.Contains("lvd")) { r = 98; g = 114; b = 38; return; }
            if (suffix.Contains("snw") || suffix.Contains("ice")) { r = 215; g = 228; b = 238; return; }
        }

        (r, g, b) = (texIdx % 6) switch
        {
            0 => ((byte)128, (byte)94, (byte)58),
            1 => ((byte)108, (byte)82, (byte)52),
            2 => ((byte)48, (byte)118, (byte)36),
            3 => ((byte)112, (byte)110, (byte)104),
            4 => ((byte)28, (byte)88, (byte)24),
            _ => ((byte)68, (byte)108, (byte)42)
        };
    }

    private static byte[]? DecodeTgaIfAvailable(byte[]? tgaBytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (tgaBytes == null || tgaBytes.Length < 18)
            return null;

        var idLen = tgaBytes[0];
        var colorMapType = tgaBytes[1];
        var imageType = tgaBytes[2];
        if (colorMapType != 0 || imageType != 2)
            return null;

        width = BitConverter.ToUInt16(tgaBytes, 12);
        height = BitConverter.ToUInt16(tgaBytes, 14);
        var bpp = tgaBytes[16];
        var descriptor = tgaBytes[17];
        var bytesPerPixel = bpp / 8;

        if (width <= 0 || height <= 0 || (bytesPerPixel != 3 && bytesPerPixel != 4))
            return null;

        var dataOffset = 18 + idLen;
        if (dataOffset + (width * height * bytesPerPixel) > tgaBytes.Length)
            return null;

        var topOrigin = (descriptor & 0x20) != 0;
        var outBgra = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var srcRow = topOrigin ? y : (height - 1 - y);
            for (var x = 0; x < width; x++)
            {
                var srcIdx = dataOffset + (((srcRow * width) + x) * bytesPerPixel);
                var dstIdx = ((y * width) + x) * 4;
                outBgra[dstIdx] = tgaBytes[srcIdx];
                outBgra[dstIdx + 1] = tgaBytes[srcIdx + 1];
                outBgra[dstIdx + 2] = tgaBytes[srcIdx + 2];
                outBgra[dstIdx + 3] = bytesPerPixel == 4 ? tgaBytes[srcIdx + 3] : (byte)255;
            }
        }
        return outBgra;
    }

    // ==================== 5. WPF ONRENDER (HIGH-RES BITMAP + 2.5D UNITS, GOLD MINES & BUILDINGS) ====================

    public Point WorldToScreen(double worldX, double worldY)
    {
        var halfW = ActualWidth * 0.5;
        var halfH = ActualHeight * 0.5;
        var invScaleX = 1.0 / _zoomScale;
        var invScaleY = invScaleX / 0.84;
        return WorldToViewportPixel(worldX, worldY, halfW, halfH, _cameraCenterX, _cameraCenterY, invScaleX, invScaleY);
    }

    public Point ScreenToWorld(Point screenPt)
    {
        var halfW = ActualWidth * 0.5;
        var halfH = ActualHeight * 0.5;
        var vScreenNorm = (halfH - screenPt.Y) / Math.Max(1.0, halfH);
        var perspFactor = 1.0 + (vScreenNorm * 0.08);
        var wx = _cameraCenterX + (((screenPt.X - halfW) / _zoomScale) * perspFactor);
        var wy = _cameraCenterY + (((halfH - screenPt.Y) / (_zoomScale * 0.84)) * perspFactor);
        return new Point(wx, wy);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _viewportDirty = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var fullRect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 22, 16)), null, fullRect);

        if (_project == null)
        {
            var formattedText = new FormattedText(
                "Ningún mapa cargado\nUsa Archivo → Abrir Mapa (Ctrl+O)",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                15,
                new SolidColorBrush(Color.FromRgb(110, 120, 110)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                TextAlignment = TextAlignment.Center
            };
            var pt = new Point((ActualWidth - formattedText.Width) * 0.5, (ActualHeight - formattedText.Height) * 0.5);
            dc.DrawText(formattedText, pt);
            return;
        }

        if (_viewportDirty || _viewportBitmap == null)
        {
            RenderViewportFramebuffer();
        }

        if (_viewportBitmap != null)
        {
            dc.DrawImage(_viewportBitmap, fullRect);
        }

        var t = _project.Terrain;

        if (ShowGrid && _zoomScale >= 0.06)
        {
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(48, 255, 255, 200)), 0.8);
            gridPen.Freeze();
            var vis = GetVisibleWorldBounds();
            var startX = Math.Max(t.OffsetX, Math.Floor(vis.Left / 512.0) * 512.0);
            var endX = Math.Min(t.OffsetX + t.WorldWidth, vis.Right);
            for (var wx = startX; wx <= endX; wx += 512.0)
            {
                var p1 = WorldToScreen(wx, Math.Max(t.OffsetY, vis.Top));
                var p2 = WorldToScreen(wx, Math.Min(t.OffsetY + t.WorldHeight, vis.Bottom));
                dc.DrawLine(gridPen, p1, p2);
            }

            var startY = Math.Max(t.OffsetY, Math.Floor(vis.Top / 512.0) * 512.0);
            var endY = Math.Min(t.OffsetY + t.WorldHeight, vis.Bottom);
            for (var wy = startY; wy <= endY; wy += 512.0)
            {
                var p1 = WorldToScreen(Math.Max(t.OffsetX, vis.Left), wy);
                var p2 = WorldToScreen(Math.Min(t.OffsetX + t.WorldWidth, vis.Right), wy);
                dc.DrawLine(gridPen, p1, p2);
            }
        }

        if (ShowRegions)
        {
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            foreach (var r in _project.Regions)
            {
                var pTL = WorldToScreen(r.Left, r.Top);
                var pBR = WorldToScreen(r.Right, r.Bottom);
                var rect = new Rect(
                    Math.Min(pTL.X, pBR.X),
                    Math.Min(pTL.Y, pBR.Y),
                    Math.Max(2, Math.Abs(pBR.X - pTL.X)),
                    Math.Max(2, Math.Abs(pBR.Y - pTL.Y)));

                if (!rect.IntersectsWith(fullRect))
                    continue;

                var isSel = ReferenceEquals(SelectedEntity, r) || ReferenceEquals(HoveredRegion, r);
                var fill = new SolidColorBrush(Color.FromArgb(isSel ? (byte)75 : (byte)32, r.ColorR, r.ColorG, r.ColorB));
                var border = new Pen(new SolidColorBrush(isSel ? Colors.Yellow : Color.FromArgb(190, r.ColorR, r.ColorG, r.ColorB)), isSel ? 2.0 : 1.0);
                dc.DrawRectangle(fill, border, rect);

                if (rect.Width > 36 && rect.Height > 16)
                {
                    var ft = new FormattedText(
                        r.Name,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        10.5,
                        Brushes.White,
                        1.0);
                    dc.DrawText(ft, new Point(rect.Left + 4, rect.Top + 2));
                }
            }
        }

        if (ShowUnits)
        {
            RenderPlacedUnits25D(dc, fullRect);
        }

        if (SelectedEntity is W3PlacedDoodad selDoodad)
        {
            var pt = WorldToScreen(selDoodad.X, selDoodad.Y);
            var selPen = new Pen(Brushes.Lime, 2.0);
            dc.DrawEllipse(null, selPen, pt, 16, 11);
        }
    }

    private void RenderPlacedUnits25D(DrawingContext dc, Rect fullRect)
    {
        if (_project == null)
            return;

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var sortedUnits = _project.PlacedUnits.OrderByDescending(u => u.Y).ToList();

        foreach (var u in sortedUnits)
        {
            if (u.PlayerId == 12 && !ShowCreepCampIcons)
                continue;
            if (u.PlayerId == 15 && !ShowNeutralBuildingIcons)
                continue;

            var pt = WorldToScreen(u.X, u.Y);
            if (pt.X < -60 || pt.X > fullRect.Width + 60 || pt.Y < -60 || pt.Y > fullRect.Height + 60)
                continue;

            var isSel = ReferenceEquals(SelectedEntity, u);
            var isHov = ReferenceEquals(HoveredUnit, u);
            var pColor = GetPlayerColor(u.PlayerId);
            var unitVisualScale = Math.Clamp(_zoomScale * 95.0 * Math.Clamp(u.Scale, 0.7f, 1.5f), 7.5, 42.0);

            if (isSel || isHov)
            {
                var ringPen = new Pen(isSel ? Brushes.Lime : Brushes.Yellow, 2.2);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 50, 255, 80)), ringPen, pt, unitVisualScale * 1.15, unitVisualScale * 0.80);
            }

            if (u.IsStartLocation)
            {
                // Official Warcraft III Start Location: Ring of glowing team-colored runes on the ground
                var rx = unitVisualScale * 1.45;
                var ry = unitVisualScale * 1.05;
                var pen = new Pen(new SolidColorBrush(pColor), 2.6);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(38, pColor.R, pColor.G, pColor.B)), pen, pt, rx, ry);

                const int runeCount = 8;
                for (var r = 0; r < runeCount; r++)
                {
                    var ang = r * (Math.PI * 2.0 / runeCount);
                    var rPt = new Point(pt.X + (Math.Cos(ang) * rx), pt.Y + (Math.Sin(ang) * ry));
                    dc.DrawEllipse(new SolidColorBrush(pColor), new Pen(Brushes.Black, 0.7), rPt, 2.6, 2.2);
                }
                continue;
            }

            if (u.Rawcode.Equals("ngol", StringComparison.OrdinalIgnoreCase) ||
                u.Name.Contains("Gold Mine", StringComparison.OrdinalIgnoreCase))
            {
                // 2.5D Gold Mine (Rocky dome mound with golden ore veins & dark mine shaft entrance, just like official WE!)
                DrawGoldMine25D(dc, pt, unitVisualScale * 1.15);
            }
            else if (IsLikelyBuilding(u))
            {
                DrawBuilding25D(dc, pt, unitVisualScale * 1.15, pColor, u.PlayerId);
            }
            else
            {
                DrawCharacterUnit25D(dc, pt, unitVisualScale, pColor, u);
            }

            if (isSel || isHov || ShowUnitLabels)
            {
                var labelText = $"{u.Name} [{u.Rawcode}]";
                var ft = new FormattedText(
                    labelText,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    11.0,
                    isSel ? Brushes.Yellow : Brushes.White,
                    1.0);

                var lx = pt.X - (ft.Width * 0.5);
                var ly = pt.Y - unitVisualScale - 24;
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(200, 14, 18, 22)),
                    new Pen(new SolidColorBrush(pColor), 1.0),
                    new Rect(lx - 4, ly - 1, ft.Width + 8, ft.Height + 2));
                dc.DrawText(ft, new Point(lx, ly));
            }
        }
    }

    private static void DrawGoldMine25D(DrawingContext dc, Point pt, double s)
    {
        // 1. Ground shadow
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(140, 8, 14, 8)), null, new Point(pt.X + 3, pt.Y + 3), s * 1.20, s * 0.82);

        // 2. Rocky stone mine mound
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(128, 114, 92)),
            new Pen(new SolidColorBrush(Color.FromRgb(48, 40, 30)), 1.3),
            new Point(pt.X, pt.Y - (s * 0.32)),
            s * 1.05,
            s * 0.78);

        // 3. Golden ore crown & timber entrance arch
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(218, 178, 52)),
            new Pen(new SolidColorBrush(Color.FromRgb(110, 82, 18)), 1.0),
            new Point(pt.X - (s * 0.15), pt.Y - (s * 0.58)),
            s * 0.62,
            s * 0.42);

        dc.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(22, 18, 14)),
            new Pen(new SolidColorBrush(Color.FromRgb(158, 118, 62)), 1.2),
            new Rect(pt.X - (s * 0.32), pt.Y - (s * 0.22), s * 0.64, s * 0.46));
    }

    private static bool IsLikelyBuilding(W3PlacedUnit u)
    {
        if (string.IsNullOrEmpty(u.Rawcode)) return false;
        var n = u.Name.ToLowerInvariant();
        if (n.Contains("tower") || n.Contains("hall") || n.Contains("keep") || n.Contains("castle") ||
            n.Contains("altar") || n.Contains("barracks") || n.Contains("shop") || n.Contains("tavern") ||
            n.Contains("camp") || n.Contains("laboratory") || n.Contains("fountain") || n.Contains("tree") ||
            n.Contains("gate") || n.Contains("wall") || n.Contains("well") || n.Contains("sanctum") ||
            n.Contains("lumber") || n.Contains("blacksmith") || n.Contains("burrow") || n.Contains("ziggurat") || n.Contains("crypt"))
        {
            return true;
        }
        return false;
    }

    private static void DrawBuilding25D(DrawingContext dc, Point pt, double s, Color teamColor, int playerId)
    {
        var roofColor = playerId >= 12 ? Color.FromRgb(205, 22, 26) : teamColor;
        var darkRoof = Color.FromRgb((byte)(roofColor.R * 0.60), (byte)(roofColor.G * 0.60), (byte)(roofColor.B * 0.60));

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(135, 8, 14, 8)), null, new Point(pt.X + 3, pt.Y + 3), s * 1.25, s * 0.85);

        var wallRect = new Rect(pt.X - (s * 0.90), pt.Y - (s * 0.50), s * 1.8, s * 0.80);
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(124, 96, 66)),
            new Pen(new SolidColorBrush(Color.FromRgb(52, 38, 24)), 1.3),
            wallRect);

        var roofGeo = new StreamGeometry();
        using (var ctx = roofGeo.Open())
        {
            ctx.BeginFigure(new Point(pt.X - (s * 1.05), pt.Y - (s * 0.42)), true, true);
            ctx.LineTo(new Point(pt.X, pt.Y - (s * 1.28)), true, false);
            ctx.LineTo(new Point(pt.X + (s * 1.05), pt.Y - (s * 0.42)), true, false);
            ctx.LineTo(new Point(pt.X, pt.Y - (s * 0.08)), true, false);
        }
        roofGeo.Freeze();

        var roofBrush = new LinearGradientBrush(roofColor, darkRoof, new Point(0, 0), new Point(1, 1));
        dc.DrawGeometry(roofBrush, new Pen(new SolidColorBrush(Color.FromRgb(45, 22, 14)), 1.3), roofGeo);
    }

    private static void DrawCharacterUnit25D(DrawingContext dc, Point pt, double s, Color teamColor, W3PlacedUnit u)
    {
        var isCreep = u.PlayerId == 12;
        var bodyColor = isCreep ? Color.FromRgb(28, 36, 98) : teamColor;
        var darkBody = Color.FromRgb((byte)(bodyColor.R * 0.55), (byte)(bodyColor.G * 0.55), (byte)(bodyColor.B * 0.55));

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(135, 6, 12, 6)), null, new Point(pt.X + 2, pt.Y + 2), s * 0.65, s * 0.42);

        dc.DrawEllipse(
            new SolidColorBrush(darkBody),
            new Pen(new SolidColorBrush(Color.FromRgb(12, 16, 28)), 1.1),
            new Point(pt.X, pt.Y - (s * 0.25)),
            s * 0.50,
            s * 0.40);

        dc.DrawEllipse(
            new SolidColorBrush(bodyColor),
            new Pen(Brushes.Black, 0.9),
            new Point(pt.X, pt.Y - (s * 0.55)),
            s * 0.26,
            s * 0.24);

        if (isCreep && s >= 7.0)
        {
            var ringCenterY = pt.Y - (s * 0.92);
            var rx = s * 0.55;
            var ry = s * 0.26;
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(185, 240, 248, 255)), 0.9), new Point(pt.X, ringCenterY), rx, ry);

            const int starCount = 6;
            for (var i = 0; i < starCount; i++)
            {
                var ang = i * (Math.PI * 2.0 / starCount);
                var sx = pt.X + (Math.Cos(ang) * rx);
                var sy = ringCenterY + (Math.Sin(ang) * ry);
                dc.DrawEllipse(Brushes.White, null, new Point(sx, sy), 1.9, 1.9);
            }
        }
    }

    private static Color GetPlayerColor(int playerId)
    {
        return playerId switch
        {
            0 => Color.FromRgb(255, 3, 3),     // Red
            1 => Color.FromRgb(0, 66, 255),    // Blue
            2 => Color.FromRgb(28, 230, 185),  // Teal
            3 => Color.FromRgb(84, 0, 129),    // Purple
            4 => Color.FromRgb(255, 252, 1),   // Yellow
            5 => Color.FromRgb(254, 138, 14),  // Orange
            6 => Color.FromRgb(32, 192, 0),    // Green
            7 => Color.FromRgb(229, 91, 176),  // Pink
            8 => Color.FromRgb(149, 150, 151), // Gray
            9 => Color.FromRgb(126, 191, 241), // Light Blue
            10 => Color.FromRgb(16, 98, 70),   // Dark Green
            11 => Color.FromRgb(78, 42, 4),    // Brown
            12 => Color.FromRgb(42, 52, 128),  // Neutral Hostile
            _ => Color.FromRgb(220, 185, 70)   // Neutral Passive
        };
    }

    // ==================== 6. MOUSE & CAMERA INTERACTION ====================

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_project == null)
            return;

        var mousePos = e.GetPosition(this);
        var worldBefore = ScreenToWorld(mousePos);

        var factor = e.Delta > 0 ? 1.22 : (1.0 / 1.22);
        _zoomScale = Math.Clamp(_zoomScale * factor, 0.02, 0.85);

        var worldAfter = ScreenToWorld(mousePos);
        _cameraCenterX += worldBefore.X - worldAfter.X;
        _cameraCenterY += worldBefore.Y - worldAfter.Y;
        _viewportDirty = true;

        InvalidateVisual();
        CameraChanged?.Invoke();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        var pos = e.GetPosition(this);
        _lastMousePos = pos;

        if (e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed)
        {
            _isPanning = true;
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed && _project != null)
        {
            var hit = HitTestWorldEntity(pos);
            if (hit != null)
            {
                SelectedEntity = hit;
                SelectionChanged?.Invoke(hit);
                InvalidateVisual();
            }
            else
            {
                _isPanning = true;
                CaptureMouse();
                Cursor = Cursors.Hand;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_project == null)
            return;

        var pos = e.GetPosition(this);
        var wPt = ScreenToWorld(pos);

        var t = _project.Terrain;
        var gx = (int)Math.Clamp((wPt.X - t.OffsetX) / 128.0, 0, Math.Max(0, t.GridWidth - 1));
        var gy = (int)Math.Clamp((wPt.Y - t.OffsetY) / 128.0, 0, Math.Max(0, t.GridHeight - 1));
        var wz = t.GroundHeights != null ? t.GroundHeights[gx, gy] : 0f;
        var cliff = t.CliffLevels != null ? t.CliffLevels[gx, gy] : 2;

        CursorMovedOnMap?.Invoke((float)wPt.X, (float)wPt.Y, wz, cliff);

        if (_isPanning)
        {
            var dx = pos.X - _lastMousePos.X;
            var dy = pos.Y - _lastMousePos.Y;
            _lastMousePos = pos;

            _cameraCenterX -= dx / _zoomScale;
            _cameraCenterY += dy / (_zoomScale * 0.84);
            _viewportDirty = true;

            InvalidateVisual();
            CameraChanged?.Invoke();
            return;
        }

        var prevUnit = HoveredUnit;
        var prevReg = HoveredRegion;
        HoveredUnit = FindNearestUnit(pos, 18.0);
        HoveredRegion = HoveredUnit == null && ShowRegions ? FindRegionAt(wPt) : null;

        if (!ReferenceEquals(prevUnit, HoveredUnit) || !ReferenceEquals(prevReg, HoveredRegion))
            InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isPanning)
        {
            _isPanning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }

    private object? HitTestWorldEntity(Point screenPos)
    {
        if (_project == null)
            return null;

        var unit = FindNearestUnit(screenPos, 20.0);
        if (unit != null)
            return unit;

        var wPt = ScreenToWorld(screenPos);
        if (ShowDoodads)
        {
            W3PlacedDoodad? bestDoodad = null;
            var bestDist = 110.0;
            foreach (var d in _project.PlacedDoodads)
            {
                var dist = Math.Sqrt(((d.X - wPt.X) * (d.X - wPt.X)) + ((d.Y - wPt.Y) * (d.Y - wPt.Y)));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestDoodad = d;
                }
            }
            if (bestDoodad != null)
                return bestDoodad;
        }

        if (ShowRegions)
        {
            var reg = FindRegionAt(wPt);
            if (reg != null)
                return reg;
        }

        return null;
    }

    private W3PlacedUnit? FindNearestUnit(Point screenPos, double maxPixelRadius)
    {
        if (_project == null || !ShowUnits)
            return null;

        W3PlacedUnit? best = null;
        var bestDist = maxPixelRadius;
        foreach (var u in _project.PlacedUnits)
        {
            var pt = WorldToScreen(u.X, u.Y);
            var dx = pt.X - screenPos.X;
            var dy = pt.Y - screenPos.Y;
            var d = Math.Sqrt((dx * dx) + (dy * dy));
            if (d < bestDist)
            {
                bestDist = d;
                best = u;
            }
        }
        return best;
    }

    private W3MapRegion? FindRegionAt(Point worldPt)
    {
        if (_project == null)
            return null;

        foreach (var r in _project.Regions)
        {
            if (worldPt.X >= r.Left && worldPt.X <= r.Right && worldPt.Y >= r.Bottom && worldPt.Y <= r.Top)
                return r;
        }
        return null;
    }
}
