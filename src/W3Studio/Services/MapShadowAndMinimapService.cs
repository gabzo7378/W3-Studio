using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using War3Net.Build.Common;
using War3Net.Build.Environment;
using War3Net.Build.Extensions;
using War3Net.Build.Info;
using War3Net.Build.Widget;
using War3Net.IO.Mpq;
using W3Studio.Models;

namespace W3Studio.Services;

public sealed class NewMapCreationOptions
{
    public string FilePath { get; set; } = string.Empty;
    public string MapName { get; set; } = "Nuevo Mapa";
    public string Author { get; set; } = "W3 Studio";
    public string Description { get; set; } = "Mapa creado con W3 Studio (1.27b).";
    public int CellsWidth { get; set; } = 64;
    public int CellsHeight { get; set; } = 64;
    public char TilesetChar { get; set; } = 'L';
    public bool InitialDeepWater { get; set; }
    public int BaseCliffLayer { get; set; } = 2;
}

/// <summary>
/// Generates directional shadow maps (war3map.shd), paletted minimaps (war3mapMap.blp),
/// and clean blank Warcraft III 1.27b maps (.w3m).
/// </summary>
public static class MapShadowAndMinimapService
{
    private readonly record struct TilesetStarterSpec(
        Tileset EnumVal,
        string DisplayName,
        string[] GroundTiles,
        string[] CliffTiles);

    private static readonly Dictionary<char, TilesetStarterSpec> TilesetSpecs = new()
    {
        ['L'] = new TilesetStarterSpec(Tileset.LordaeronSummer, "Lordaeron Summer",
            new[] { "Ldrt", "Ldro", "Ldrg", "Lrok", "Lgrs", "Lgrd" }, new[] { "CLdi", "CLgr" }),
        ['A'] = new TilesetStarterSpec(Tileset.Ashenvale, "Ashenvale",
            new[] { "Adrt", "Adrd", "Agrs", "Arck", "Agrd", "Adrg" }, new[] { "CAdi", "CAgr" }),
        ['B'] = new TilesetStarterSpec(Tileset.Barrens, "Barrens",
            new[] { "Bdrt", "Bdrh", "Bdrr", "Bdrg", "Bdsr", "Bgrs" }, new[] { "CBde", "CBds" }),
        ['N'] = new TilesetStarterSpec(Tileset.Northrend, "Northrend",
            new[] { "Ndrt", "Ndrd", "Nrck", "Ngrs", "Nice", "Nsnw" }, new[] { "CNdi", "CNsn" }),
        ['F'] = new TilesetStarterSpec(Tileset.Felwood, "Felwood",
            new[] { "Fdrt", "Fdrg", "Frok", "Fgrs", "Flvd", "Fgrd" }, new[] { "CFdi", "CFgr" }),
        ['V'] = new TilesetStarterSpec(Tileset.Village, "Village",
            new[] { "Vdrt", "Vdrr", "Vcrp", "Vcbp", "Vstp", "Vgrs" }, new[] { "CVdi", "CVgr" }),
        ['Y'] = new TilesetStarterSpec(Tileset.Cityscape, "Cityscape",
            new[] { "Ydrt", "Ydtr", "Yblm", "Ybtl", "Ysqd", "Yrtl" }, new[] { "CYdi", "CYsq" }),
        ['I'] = new TilesetStarterSpec(Tileset.IcecrownGlacier, "Icecrown Glacier",
            new[] { "Idrt", "Idtr", "Idki", "Ibkb", "Irbk", "Isnw" }, new[] { "CIdi", "CIsn" })
    };

    /// <summary>
    /// Computes war3map.shd (4x4 subcells per tile) from terrain slopes, cliffs, and placed buildings/trees.
    /// </summary>
    public static byte[] GenerateShadowMapShd(MapProject project)
    {
        var t = project.Terrain;
        int cellsW = Math.Max(1, t.GridWidth - 1);
        int cellsH = Math.Max(1, t.GridHeight - 1);
        int shdW = cellsW * 4;
        int shdH = cellsH * 4;
        var shadows = new byte[shdW * shdH];

        if (t.GroundHeights == null)
            return shadows;

        float SampleTerrainZ(float wx, float wy)
        {
            float fx = Math.Clamp((wx - t.OffsetX) / 128f, 0f, t.GridWidth - 1.001f);
            float fy = Math.Clamp((wy - t.OffsetY) / 128f, 0f, t.GridHeight - 1.001f);
            int x0 = (int)fx;
            int y0 = (int)fy;
            int x1 = Math.Min(t.GridWidth - 1, x0 + 1);
            int y1 = Math.Min(t.GridHeight - 1, y0 + 1);
            float tx = fx - x0;
            float ty = fy - y0;
            float z00 = t.GroundHeights[x0, y0];
            float z10 = t.GroundHeights[x1, y0];
            float z01 = t.GroundHeights[x0, y1];
            float z11 = t.GroundHeights[x1, y1];
            return (z00 * (1f - tx) + z10 * tx) * (1f - ty) + (z01 * (1f - tx) + z11 * tx) * ty;
        }

        // Sun direction in terrain shader: normalize(vec3(-0.35, -0.35, 0.5))
        const float rayDirX = -0.35f;
        const float rayDirY = -0.35f;
        const float rayDirZ = 0.50f;
        float invLenXY = 1f / MathF.Sqrt(rayDirX * rayDirX + rayDirY * rayDirY);
        float stepX = rayDirX * invLenXY * 64f;
        float stepY = rayDirY * invLenXY * 64f;
        float stepRiseZ = rayDirZ * invLenXY * 64f;

        // 1. Directional terrain/cliff raymarch shadow
        for (int sy = 0; sy < shdH; sy++)
        {
            float wy = t.OffsetY + (sy + 0.5f) * 32f;
            int cy = Math.Clamp(sy >> 2, 0, Math.Max(0, t.GridHeight - 2));
            for (int sx = 0; sx < shdW; sx++)
            {
                float wx = t.OffsetX + (sx + 0.5f) * 32f;
                int cx = Math.Clamp(sx >> 2, 0, Math.Max(0, t.GridWidth - 2));
                float baseZ = t.WaterFlags != null && t.WaterFlags[cx, cy] && t.WaterDepths != null
                    ? t.GroundHeights[cx, cy] + Math.Max(0f, t.WaterDepths[cx, cy])
                    : SampleTerrainZ(wx, wy);

                bool hit = false;
                for (int s = 1; s <= 12; s++)
                {
                    float rx = wx + stepX * s;
                    float ry = wy + stepY * s;
                    if (rx < t.OffsetX || rx > t.MaxX || ry < t.OffsetY || ry > t.MaxY)
                        break;
                    if (SampleTerrainZ(rx, ry) > baseZ + stepRiseZ * s + 10f)
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit)
                    shadows[sy * shdW + sx] = 0xFF;
            }
        }

        // 2. Soft oval shadows cast towards North-East (+32, +32) by buildings & trees/destructables
        void StampShadowEllipse(float ox, float oy, float radiusWorld)
        {
            float cx = ox + 36f;
            float cy = oy + 36f;
            int minSx = Math.Max(0, (int)MathF.Floor((cx - t.OffsetX - radiusWorld) / 32f));
            int maxSx = Math.Min(shdW - 1, (int)MathF.Ceiling((cx - t.OffsetX + radiusWorld) / 32f));
            int minSy = Math.Max(0, (int)MathF.Floor((cy - t.OffsetY - radiusWorld) / 32f));
            int maxSy = Math.Min(shdH - 1, (int)MathF.Ceiling((cy - t.OffsetY + radiusWorld) / 32f));
            float r2 = radiusWorld * radiusWorld;

            for (int sy = minSy; sy <= maxSy; sy++)
            {
                float py = t.OffsetY + (sy + 0.5f) * 32f;
                float dy = py - cy;
                for (int sx = minSx; sx <= maxSx; sx++)
                {
                    float px = t.OffsetX + (sx + 0.5f) * 32f;
                    float dx = px - cx;
                    if (dx * dx + dy * dy <= r2)
                        shadows[sy * shdW + sx] = 0xFF;
                }
            }
        }

        foreach (var d in project.PlacedDoodads)
        {
            if (d.IsTree || d.IsDestructible)
                StampShadowEllipse(d.X, d.Y, Math.Clamp(52f * d.Scale, 32f, 110f));
        }

        foreach (var u in project.PlacedUnits)
        {
            if (u.IsBuilding && !u.IsStartLocation)
                StampShadowEllipse(u.X, u.Y, Math.Clamp(115f * u.Scale, 75f, 210f));
        }

        return shadows;
    }

    /// <summary>
    /// Renders a 256x256 terrain + trees minimap encoded as BLP1 paletted image for war3mapMap.blp.
    /// </summary>
    public static byte[] GenerateMinimapBlp(MapProject project)
    {
        const int size = 256;
        var bgra = new byte[size * size * 4];
        var t = project.Terrain;
        int gw = Math.Max(2, t.GridWidth);
        int gh = Math.Max(2, t.GridHeight);

        for (int py = 0; py < size; py++)
        {
            double vNorm = 1.0 - (py / (double)(size - 1));
            int gy = (int)Math.Clamp(vNorm * (gh - 1), 0, gh - 1);

            for (int px = 0; px < size; px++)
            {
                double uNorm = px / (double)(size - 1);
                int gx = (int)Math.Clamp(uNorm * (gw - 1), 0, gw - 1);

                byte r = 46, g = 88, b = 42;
                if (t.TextureIndices != null)
                {
                    var texIdx = t.TextureIndices[gx, gy];
                    var code = texIdx < t.GroundTiles.Count ? t.GroundTiles[texIdx] : string.Empty;
                    GetTerrainTileColor(code, texIdx, out r, out g, out b);
                }

                if (t.WaterFlags != null && t.WaterFlags[gx, gy])
                {
                    float depth = t.WaterDepths != null ? Math.Clamp(t.WaterDepths[gx, gy] / 128f, 0.15f, 1f) : 0.6f;
                    r = (byte)(r * (1f - 0.65f * depth) + 22f * depth);
                    g = (byte)(g * (1f - 0.55f * depth) + 74f * depth);
                    b = (byte)(b * (1f - 0.35f * depth) + 148f * depth);
                }
                else if (t.GroundHeights != null && gx > 0 && gy > 0)
                {
                    float slope = (t.GroundHeights[gx, gy] - t.GroundHeights[gx - 1, gy - 1]) * 0.14f;
                    int shade = Math.Clamp((int)slope, -28, 28);
                    r = (byte)Math.Clamp(r + shade, 0, 255);
                    g = (byte)Math.Clamp(g + shade, 0, 255);
                    b = (byte)Math.Clamp(b + shade, 0, 255);
                }

                int o = (py * size + px) * 4;
                bgra[o] = b;
                bgra[o + 1] = g;
                bgra[o + 2] = r;
                bgra[o + 3] = 255;
            }
        }

        foreach (var d in project.PlacedDoodads)
        {
            if (!d.IsTree) continue;
            double u = (d.X - t.OffsetX) / t.WorldWidth;
            double v = (d.Y - t.OffsetY) / t.WorldHeight;
            if (u < 0 || u > 1 || v < 0 || v > 1) continue;
            int cx = (int)(u * (size - 1));
            int cy = (int)((1.0 - v) * (size - 1));
            int idx = (cy * size + cx) * 4;
            bgra[idx] = 18;
            bgra[idx + 1] = 86;
            bgra[idx + 2] = 16;
        }

        return IconProcessorService.EncodeBlp1Paletted(bgra, size, size, includeMipmaps: true);
    }

    /// <summary>
    /// Creates a complete blank WC3 1.27b map (.w3m) with valid terrain, pathing, shadows, minimap, widgets, and JASS script.
    /// </summary>
    public static void CreateBlankMap(NewMapCreationOptions opts, WarcraftBaseDataService baseData)
    {
        if (!TilesetSpecs.TryGetValue(opts.TilesetChar, out var spec))
            spec = TilesetSpecs['L'];

        int cellsW = Math.Clamp(opts.CellsWidth, 32, 256);
        int cellsH = Math.Clamp(opts.CellsHeight, 32, 256);
        int gridW = cellsW + 1;
        int gridH = cellsH + 1;
        float offsetX = -(cellsW * 128f) * 0.5f;
        float offsetY = -(cellsH * 128f) * 0.5f;
        float halfW = cellsW * 64f;
        float halfH = cellsH * 64f;

        var project = new MapProject
        {
            FilePath = opts.FilePath,
            MapName = opts.MapName,
            MapAuthor = opts.Author,
            MapDescription = opts.Description,
            SuggestedPlayers = "1-2",
            FormatVersion = 25
        };

        // 1. Terrain (war3map.w3e)
        project.Terrain.Tileset = opts.TilesetChar;
        project.Terrain.GridWidth = gridW;
        project.Terrain.GridHeight = gridH;
        project.Terrain.OffsetX = offsetX;
        project.Terrain.OffsetY = offsetY;
        project.Terrain.GroundTiles.AddRange(spec.GroundTiles);
        project.Terrain.CliffTiles.AddRange(spec.CliffTiles);
        project.Terrain.RawW3eBytes = BuildW3eBytes(opts, spec, gridW, gridH, offsetX, offsetY);

        // Populate arrays cleanly for shd/minimap generation
        project.Terrain.GroundHeights = new float[gridW, gridH];
        project.Terrain.WaterDepths = new float[gridW, gridH];
        project.Terrain.TextureIndices = new byte[gridW, gridH];
        project.Terrain.CliffLevels = new byte[gridW, gridH];
        project.Terrain.WaterFlags = new bool[gridW, gridH];
        project.Terrain.RampFlags = new bool[gridW, gridH];

        int cornerHeader = TerrainBrushService.GetW3eCornerDataOffset(project.Terrain.RawW3eBytes);
        for (int y = 0; y < gridH; y++)
        {
            for (int x = 0; x < gridW; x++)
            {
                int off = cornerHeader + (y * gridW + x) * 7;
                TerrainBrushService.SyncCornerArraysFromRaw(project.Terrain, project.Terrain.RawW3eBytes, x, y, off);
            }
        }
        project.RawMpqFiles["war3map.w3e"] = project.Terrain.RawW3eBytes;

        // 2. MapInfo (war3map.w3i)
        var info = new MapInfo(MapInfoFormatVersion.v25)
        {
            MapVersion = 1,
            EditorVersion = EditorVersion.v6059,
            MapName = opts.MapName,
            MapAuthor = opts.Author,
            MapDescription = opts.Description,
            RecommendedPlayers = "1-2",
            CameraBoundsComplements = new RectangleMargins(4, 4, 4, 4),
            PlayableMapAreaWidth = cellsW - 8,
            PlayableMapAreaHeight = cellsH - 8,
            MapFlags = MapFlags.MaskedAreasArePartiallyVisible |
                       MapFlags.ShowWaterWavesOnCliffShores |
                       MapFlags.ShowWaterWavesOnRollingShores |
                       MapFlags.UseItemClassificationSystem,
            Tileset = spec.EnumVal,
            FogStyle = FogStyle.Linear,
            FogStartZ = 3000f,
            FogEndZ = 5000f,
            FogDensity = 0.5f,
            WaterTintingColor = System.Drawing.Color.FromArgb(255, 255, 255, 255)
        };
        info.CameraBounds ??= new Quadrilateral(offsetX + 512f, -offsetX - 512f, -offsetY - 512f, offsetY + 512f);
        info.CameraBounds.BottomLeft = new Vector2(offsetX + 512f, offsetY + 512f);
        info.CameraBounds.TopRight = new Vector2(-offsetX - 512f, -offsetY - 512f);
        info.CameraBounds.TopLeft = new Vector2(offsetX + 512f, -offsetY - 512f);
        info.CameraBounds.BottomRight = new Vector2(-offsetX - 512f, offsetY + 512f);

        float p1X = Math.Clamp(-halfW * 0.55f, offsetX + 1024f, -1024f);
        float p2X = Math.Clamp(halfW * 0.55f, 1024f, -offsetX - 1024f);
        info.Players.Add(new PlayerData
        {
            Id = 0,
            Controller = PlayerController.User,
            Race = PlayerRace.Selectable,
            Flags = PlayerFlags.FixedStartPosition | PlayerFlags.RaceSelectable,
            Name = "Player 1",
            StartPosition = new Vector2(p1X, 0f),
            AllyLowPriorityFlags = new Bitmask32(0),
            AllyHighPriorityFlags = new Bitmask32(0),
            EnemyLowPriorityFlags = new Bitmask32(0),
            EnemyHighPriorityFlags = new Bitmask32(0)
        });
        info.Players.Add(new PlayerData
        {
            Id = 1,
            Controller = PlayerController.Computer,
            Race = PlayerRace.Selectable,
            Flags = PlayerFlags.FixedStartPosition | PlayerFlags.RaceSelectable,
            Name = "Player 2",
            StartPosition = new Vector2(p2X, 0f),
            AllyLowPriorityFlags = new Bitmask32(0),
            AllyHighPriorityFlags = new Bitmask32(0),
            EnemyLowPriorityFlags = new Bitmask32(0),
            EnemyHighPriorityFlags = new Bitmask32(0)
        });

        var force1 = new ForceData
        {
            Name = "Force 1",
            Players = new Bitmask32(0b11)
        };
        info.Forces.Add(force1);
        project.MapInfo = info;

        // 3. Default Start Locations (war3mapUnits.doo + empty war3map.doo)
        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "sloc",
            Name = "Start Location (Jugador 1)",
            CreationNumber = 0,
            PlayerId = 0,
            X = p1X,
            Y = 0f,
            Z = project.Terrain.GroundHeights[gridW / 4, gridH / 2],
            AngleRadians = (float)(270.0 * Math.PI / 180.0),
            Flags = 2,
            Level = 1
        });
        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "sloc",
            Name = "Start Location (Jugador 2)",
            CreationNumber = 1,
            PlayerId = 1,
            X = p2X,
            Y = 0f,
            Z = project.Terrain.GroundHeights[(gridW * 3) / 4, gridH / 2],
            AngleRadians = (float)(270.0 * Math.PI / 180.0),
            Flags = 2,
            Level = 1
        });

        // 4. Blank JASS script + wts + wtg + wct + wpm + w3r + w3c + shd + blp
        project.RawMapScript = BuildStarterMeleeScript(opts.MapName, opts.Author, p1X, p2X);
        project.RawMpqFiles["war3map.wts"] = Encoding.UTF8.GetBytes("STRING 1\r\n{\r\n" + opts.MapName + "\r\n}\r\n");
        project.RawMpqFiles["war3map.wpm"] = BuildWpmBytes(cellsW, cellsH);
        project.RawMpqFiles["war3map.wtg"] = BuildEmptyWtgBytes();
        project.RawMpqFiles["war3map.wct"] = BuildEmptyWctBytes();
        project.RawMpqFiles["war3map.w3r"] = MapScriptSyncService.WriteRegionsW3r(project.Regions);
        project.RawMpqFiles["war3map.w3c"] = MapMpqService.WriteCamerasW3c(project);
        project.RawMpqFiles["war3map.shd"] = GenerateShadowMapShd(project);
        project.RawMpqFiles["war3mapMap.blp"] = GenerateMinimapBlp(project);

        MapScriptSyncService.SyncScenarioToW3iAndJass(project);
        MapScriptSyncService.SyncUnitsAndDoodadsToDooAndJass(project, baseData);
        project.RawMpqFiles["war3map.j"] = Encoding.UTF8.GetBytes(project.RawMapScript);

        // 5. Write HM3W 512-byte header + MPQ archive
        byte[] hm3w = BuildHm3wHeader(opts.MapName, (uint)info.MapFlags, maxPlayers: 2);
        var mpqFiles = new List<MpqFile>();
        foreach (var kvp in project.RawMpqFiles)
            mpqFiles.Add(MpqFile.New(new MemoryStream(kvp.Value), kvp.Key));

        var dir = Path.GetDirectoryName(opts.FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var outFs = new FileStream(opts.FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        outFs.Write(hm3w, 0, hm3w.Length);
        var createOpts = new MpqArchiveCreateOptions
        {
            ListFileCreateMode = MpqFileCreateMode.Overwrite,
            AttributesCreateMode = MpqFileCreateMode.None,
            BlockSize = 3,
            HashTableSize = 1024
        };
        using var archive = MpqArchive.Create(outFs, mpqFiles, createOpts, leaveOpen: true);
    }

    private static byte[] BuildW3eBytes(
        NewMapCreationOptions opts, TilesetStarterSpec spec,
        int gridW, int gridH, float offsetX, float offsetY)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("W3E!"));
        bw.Write(11); // version 11 (classic TFT/RoC)
        bw.Write((byte)opts.TilesetChar);
        bw.Write(1); // custom tileset flag
        bw.Write(spec.GroundTiles.Length);
        foreach (var gt in spec.GroundTiles)
            bw.Write(WarcraftBaseDataService.RawcodeToInt(gt));
        bw.Write(spec.CliffTiles.Length);
        foreach (var ct in spec.CliffTiles)
            bw.Write(WarcraftBaseDataService.RawcodeToInt(ct));
        bw.Write(gridW);
        bw.Write(gridH);
        bw.Write(offsetX);
        bw.Write(offsetY);

        int layer = opts.InitialDeepWater ? 0 : Math.Clamp(opts.BaseCliffLayer, 1, 12);
        byte flagsAndTex = opts.InitialDeepWater ? (byte)0x40 : (byte)0x00;
        ushort waterRaw = 8448;
        ushort groundRaw = 8192;

        for (int y = 0; y < gridH; y++)
        {
            for (int x = 0; x < gridW; x++)
            {
                bool isBoundary = x < 4 || x >= gridW - 4 || y < 4 || y >= gridH - 4;
                byte f = isBoundary ? (byte)(flagsAndTex | 0x80) : flagsAndTex;
                byte varByte = (byte)(((x * 7 + y * 13) & 0x03) << 4 | ((x + y) & 0x0F));
                byte cliffAndLayer = (byte)(layer & 0x0F);

                bw.Write(groundRaw);
                bw.Write(waterRaw);
                bw.Write(f);
                bw.Write(varByte);
                bw.Write(cliffAndLayer);
            }
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildWpmBytes(int cellsW, int cellsH)
    {
        int pw = cellsW * 4;
        int ph = cellsH * 4;
        using var ms = new MemoryStream(16 + pw * ph);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("MP3W"));
        bw.Write(0); // version 0
        bw.Write(pw);
        bw.Write(ph);
        bw.Write(new byte[pw * ph]);
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildEmptyWtgBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("WTG!"));
        bw.Write(7); // version 7 (TFT)
        bw.Write(1); // 1 default category
        bw.Write(0); // category id
        bw.Write(Encoding.UTF8.GetBytes("Inicialización"));
        bw.Write((byte)0);
        bw.Write(0); // isComment = false
        bw.Write(2); // unknown (2)
        bw.Write(0); // variables count
        bw.Write(0); // triggers count
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildEmptyWctBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(1); // WCT version 1
        bw.Write((byte)0); // custom script comment empty
        bw.Write(0); // custom script header len
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildHm3wHeader(string mapName, uint flags, int maxPlayers)
    {
        var buf = new byte[512];
        buf[0] = (byte)'H';
        buf[1] = (byte)'M';
        buf[2] = (byte)'3';
        buf[3] = (byte)'W';
        // Bytes 4..7 = 0 (unknown)
        var nameBytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(mapName) ? "New Map" : mapName);
        int copyLen = Math.Min(nameBytes.Length, 480);
        Buffer.BlockCopy(nameBytes, 0, buf, 8, copyLen);
        int pos = 8 + copyLen;
        buf[pos++] = 0;
        Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, buf, pos, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(maxPlayers), 0, buf, pos + 4, 4);
        return buf;
    }

    private static string BuildStarterMeleeScript(string mapName, string author, float p1X, float p2X)
    {
        string safeName = mapName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string safeAuthor = author.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $@"//===========================================================================
// Map: {safeName}
// Author: {safeAuthor}
// Generated by W3 Studio (1.27b)
//===========================================================================

globals
endglobals

function InitGlobals takes nothing returns nothing
endfunction

function CreateRegions takes nothing returns nothing
    local weathereffect we
endfunction

function CreateAllDestructables takes nothing returns nothing
endfunction

function CreateAllItems takes nothing returns nothing
endfunction

function CreateAllUnits takes nothing returns nothing
endfunction

function InitCustomPlayerSlots takes nothing returns nothing
    call SetPlayerStartLocation( Player(0), 0 )
    call SetPlayerColor( Player(0), ConvertPlayerColor(0) )
    call SetPlayerRacePreference( Player(0), RACE_PREF_RANDOM )
    call SetPlayerRaceSelectable( Player(0), true )
    call SetPlayerController( Player(0), MAP_CONTROL_USER )

    call SetPlayerStartLocation( Player(1), 1 )
    call SetPlayerColor( Player(1), ConvertPlayerColor(1) )
    call SetPlayerRacePreference( Player(1), RACE_PREF_RANDOM )
    call SetPlayerRaceSelectable( Player(1), true )
    call SetPlayerController( Player(1), MAP_CONTROL_COMPUTER )
endfunction

function InitCustomTeams takes nothing returns nothing
    call SetPlayerTeam( Player(0), 0 )
    call SetPlayerTeam( Player(1), 0 )
endfunction

function main takes nothing returns nothing
    call SetCameraBounds( -3328.0 + GetCameraMargin(CAMERA_MARGIN_LEFT), -3584.0 + GetCameraMargin(CAMERA_MARGIN_BOTTOM), 3328.0 - GetCameraMargin(CAMERA_MARGIN_RIGHT), 3072.0 - GetCameraMargin(CAMERA_MARGIN_TOP), -3328.0 + GetCameraMargin(CAMERA_MARGIN_LEFT), 3072.0 - GetCameraMargin(CAMERA_MARGIN_TOP), 3328.0 - GetCameraMargin(CAMERA_MARGIN_RIGHT), -3584.0 + GetCameraMargin(CAMERA_MARGIN_BOTTOM) )
    call SetDayNightModels( ""Environment\\DNC\\DNCLordaeron\\DNCLordaeronTerrain\\DNCLordaeronTerrain.mdl"", ""Environment\\DNC\\DNCLordaeron\\DNCLordaeronUnit\\DNCLordaeronUnit.mdl"" )
    call NewSoundEnvironment( ""Default"" )
    call SetAmbientDaySound( ""LordaeronSummerDay"" )
    call SetAmbientNightSound( ""LordaeronSummerNight"" )
    call SetMapMusic( ""Music"", true, 0 )
    call CreateRegions(  )
    call InitBlizzard(  )
    call InitGlobals(  )
    call MeleeStartingVisibility(  )
    call MeleeStartingHeroLimit(  )
    call MeleeGrantHeroItems(  )
    call MeleeStartingResources(  )
    call MeleeStartingUnits(  )
    call MeleeStartingAI(  )
    call MeleeInitVictoryDefeat(  )
endfunction

function config takes nothing returns nothing
    call SetMapName( ""{safeName}"" )
    call SetMapDescription( ""Creado con W3 Studio"" )
    call SetPlayers( 2 )
    call SetTeams( 1 )
    call SetGamePlacement( MAP_PLACEMENT_TEAMS_TOGETHER )
    call DefineStartLocation( 0, {p1X:0.0}, 0.0 )
    call DefineStartLocation( 1, {p2X:0.0}, 0.0 )
    call InitCustomPlayerSlots(  )
    call SetPlayerSlotAvailable( Player(0), MAP_CONTROL_USER )
    call SetPlayerSlotAvailable( Player(1), MAP_CONTROL_COMPUTER )
    call InitGenericPlayerSlots(  )
endfunction
";
    }

    private static void GetTerrainTileColor(string rawcode, int texIdx, out byte r, out byte g, out byte b)
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
}
