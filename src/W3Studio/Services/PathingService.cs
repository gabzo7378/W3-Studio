using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using W3Studio.Models;

namespace W3Studio.Services;

public sealed record PathTextureGrid(int Width, int Height, byte[] Cells);

public sealed record ClientObjectPathingOverride(
    string Rawcode,
    bool IsBuilding,
    bool IsDestructible,
    string PathTex,
    string PathTexDeath,
    string MoveType,
    float Collision,
    string PreventPlace,
    string RequirePlace,
    string UberSplat,
    string ShadowTex,
    string AnimProps = "");

/// <summary>
/// Decodes WC3 PathTextures (*.tga), computes official 32/64-unit grid snapping,
/// determines building/unit pathability, and regenerates binary war3map.wpm (MP3W) on save.
/// </summary>
public static class PathingService
{
    public const byte FlagWalkBlocked = 0x02;
    public const byte FlagFlyBlocked = 0x04;
    public const byte FlagBuildBlocked = 0x08;
    public const byte FlagBlight = 0x20;
    public const byte FlagNoWater = 0x40;
    public const byte SubcellWorldSize = 32;

    private static readonly HashSet<string> UnbuildableGroundTiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lrok", "Arck", "Frok", "Drck", "Grck", "Nrck", "Crck", "Vrck", "Orck", "Krck", "Jrck", "Xrck",
        "Idki", "Ibkb", "Irbk", "Nice", "Bdsr", "Bdrh", "Daby", "Kaby", "Oaby", "Jaby", "Gaby", "Ybtl"
    };

    private static readonly ConcurrentDictionary<string, PathTextureGrid?> PathTextureCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clears cached decoded TGA path textures (primarily for tests or MPQ path changes).
    /// </summary>
    public static void ClearCache() => PathTextureCache.Clear();

    /// <summary>
    /// Resolves whether a unit rawcode (base or customized in the map) is a building.
    /// </summary>
    public static bool IsUnitBuilding(string? rawcode, MapProject? project, WarcraftBaseDataService? baseData)
    {
        if (string.IsNullOrWhiteSpace(rawcode))
            return false;

        var custom = project?.Objects.FirstOrDefault(o =>
            o.Category == W3ObjectCategory.Units &&
            o.Rawcode.Equals(rawcode, StringComparison.OrdinalIgnoreCase));

        if (custom != null)
        {
            var customField = custom.Fields.FirstOrDefault(f =>
                f.FieldId.Equals("ubdg", StringComparison.OrdinalIgnoreCase));
            if (customField != null)
            {
                var val = !string.IsNullOrWhiteSpace(customField.RawValue)
                    ? customField.RawValue
                    : customField.ResolvedValue;
                if (!string.IsNullOrWhiteSpace(val))
                    return val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        var baseMeta = baseData?.GetBaseObject(rawcode);
        if (baseMeta != null)
        {
            if (baseMeta.DefaultFields.TryGetValue("isbldg", out var isBldg) &&
                (isBldg == "1" || isBldg.Equals("true", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (baseMeta.DefaultFields.TryGetValue("ubdg", out var ubdg) &&
                (ubdg == "1" || ubdg.Equals("true", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves an object metadata value (custom override in project.Objects first, then base SLK default field).
    /// </summary>
    public static string ResolveObjectField(
        string rawcode,
        bool isDoodadOrDestructible,
        MapProject? project,
        WarcraftBaseDataService? baseData,
        string objectFieldId,
        string slkColumnName)
    {
        if (string.IsNullOrWhiteSpace(rawcode))
            return string.Empty;

        var custom = project?.Objects.FirstOrDefault(o =>
            o.Rawcode.Equals(rawcode, StringComparison.OrdinalIgnoreCase) &&
            (isDoodadOrDestructible
                ? o.Category is W3ObjectCategory.Doodads or W3ObjectCategory.Destructables
                : o.Category is W3ObjectCategory.Units or W3ObjectCategory.Items));

        if (custom != null)
        {
            var f = custom.Fields.FirstOrDefault(x => x.FieldId.Equals(objectFieldId, StringComparison.OrdinalIgnoreCase));
            if (f != null)
            {
                var val = !string.IsNullOrWhiteSpace(f.RawValue) ? f.RawValue : f.ResolvedValue;
                if (!string.IsNullOrWhiteSpace(val))
                    return CleanSlkSentinel(val);
            }
        }

        var baseMeta = baseData?.GetBaseObject(rawcode);
        if (baseMeta != null && baseMeta.DefaultFields.TryGetValue(slkColumnName, out var slkVal))
            return CleanSlkSentinel(slkVal);

        return string.Empty;
    }

    private static string CleanSlkSentinel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim();
        return trimmed is "_" or "-" or "0" or "NULL" or "null" ? string.Empty : trimmed;
    }

    /// <summary>
    /// Loads and caches a WC3 PathTextures\*.tga file from MPQ or map imports.
    /// </summary>
    public static PathTextureGrid? GetPathTexture(string? pathTex, MapProject? project, BlpIconService? blpService)
    {
        if (string.IsNullOrWhiteSpace(pathTex))
            return null;

        var clean = CleanSlkSentinel(pathTex);
        if (string.IsNullOrEmpty(clean))
            return null;

        var normalized = clean.Replace('/', '\\').Trim();
        if (!normalized.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            normalized += ".tga";

        if (PathTextureCache.TryGetValue(normalized, out var cached))
            return cached;

        byte[]? bytes = blpService?.FindRawFileBytes(normalized, project);
        if (bytes == null && blpService != null && !normalized.StartsWith("PathTextures\\", StringComparison.OrdinalIgnoreCase))
            bytes = blpService.FindRawFileBytes("PathTextures\\" + normalized, project);

        if (bytes == null && project != null)
        {
            var imp = project.Imports.FirstOrDefault(i =>
                !i.IsDeleted && i.Data != null && i.MpqPath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            bytes = imp?.Data;
            if (bytes == null && project.RawMpqFiles.TryGetValue(normalized, out var raw))
                bytes = raw;
        }

        var decoded = bytes != null ? DecodePathTextureTga(bytes) : null;
        PathTextureCache[normalized] = decoded;
        return decoded;
    }

    /// <summary>
    /// Decodes a WC3 PathTextures TGA (uncompressed 2/3 or RLE 10; 8/24/32 bpp) into MP3W subcell flags
    /// oriented bottom-to-top in world space (+Y up): Red=NoWalk(0x02), Green=NoFly(0x04), Blue=NoBuild(0x08).
    /// </summary>
    public static PathTextureGrid? DecodePathTextureTga(byte[] tga)
    {
        if (tga == null || tga.Length < 18)
            return null;

        int idLen = tga[0];
        int colorMapType = tga[1];
        int imageType = tga[2];
        if (colorMapType != 0 || (imageType != 2 && imageType != 3 && imageType != 10))
            return null;

        int width = BitConverter.ToUInt16(tga, 12);
        int height = BitConverter.ToUInt16(tga, 14);
        int bpp = tga[16];
        int desc = tga[17];
        bool topDown = (desc & 0x20) != 0;
        int bytesPerPx = bpp / 8;

        if (width <= 0 || height <= 0 || width > 256 || height > 256)
            return null;
        if (bytesPerPx is not (1 or 3 or 4))
            return null;

        int offset = 18 + idLen;
        int pixelCount = width * height;
        var pixels = new byte[pixelCount * 3]; // B, G, R in file order

        if (imageType is 2 or 3)
        {
            if (offset + pixelCount * bytesPerPx > tga.Length)
                return null;

            for (int i = 0; i < pixelCount; i++)
            {
                int s = offset + i * bytesPerPx;
                if (bytesPerPx == 1)
                {
                    byte g = tga[s];
                    pixels[i * 3 + 0] = g;
                    pixels[i * 3 + 1] = g;
                    pixels[i * 3 + 2] = g;
                }
                else
                {
                    pixels[i * 3 + 0] = tga[s];
                    pixels[i * 3 + 1] = tga[s + 1];
                    pixels[i * 3 + 2] = tga[s + 2];
                }
            }
        }
        else // imageType == 10 (RLE)
        {
            int p = offset;
            int outIdx = 0;
            while (outIdx < pixelCount && p < tga.Length)
            {
                byte packet = tga[p++];
                int runLen = (packet & 0x7F) + 1;
                bool isRle = (packet & 0x80) != 0;

                if (isRle)
                {
                    if (p + bytesPerPx > tga.Length) break;
                    byte b = tga[p];
                    byte g = bytesPerPx >= 3 ? tga[p + 1] : b;
                    byte r = bytesPerPx >= 3 ? tga[p + 2] : b;
                    p += bytesPerPx;
                    for (int k = 0; k < runLen && outIdx < pixelCount; k++, outIdx++)
                    {
                        pixels[outIdx * 3 + 0] = b;
                        pixels[outIdx * 3 + 1] = g;
                        pixels[outIdx * 3 + 2] = r;
                    }
                }
                else
                {
                    for (int k = 0; k < runLen && outIdx < pixelCount; k++, outIdx++)
                    {
                        if (p + bytesPerPx > tga.Length) break;
                        byte b = tga[p];
                        byte g = bytesPerPx >= 3 ? tga[p + 1] : b;
                        byte r = bytesPerPx >= 3 ? tga[p + 2] : b;
                        p += bytesPerPx;
                        pixels[outIdx * 3 + 0] = b;
                        pixels[outIdx * 3 + 1] = g;
                        pixels[outIdx * 3 + 2] = r;
                    }
                }
            }
        }

        var cells = new byte[pixelCount];
        for (int wy = 0; wy < height; wy++)
        {
            // Map world row (wy=0 is min Y) from TGA row order
            int srcRow = topDown ? (height - 1 - wy) : wy;
            for (int wx = 0; wx < width; wx++)
            {
                int srcOff = (srcRow * width + wx) * 3;
                byte blue = pixels[srcOff + 0];
                byte green = pixels[srcOff + 1];
                byte red = pixels[srcOff + 2];

                byte flags = 0;
                if (red >= 128) flags |= FlagWalkBlocked;
                if (green >= 128) flags |= FlagFlyBlocked;
                if (blue >= 128) flags |= FlagBuildBlocked;
                cells[wy * width + wx] = flags;
            }
        }

        return new PathTextureGrid(width, height, cells);
    }

    /// <summary>
    /// Computes the official Warcraft III World Editor 1.27b snapped placement center (X, Y)
    /// for a footprint in subcells (32 world units each).
    /// Even-sized footprints snap their origin to 64-unit grid (centering 4x4/8x8 on 64-multiples and 2x2/6x6/10x10 on +32 centers);
    /// odd-sized footprints snap their origin to 32-unit subcells.
    /// </summary>
    public static (float X, float Y) SnapPositionForFootprint(
        float worldX,
        float worldY,
        int fpWidth,
        int fpHeight,
        float terrainOffsetX,
        float terrainOffsetY)
    {
        float SnapAxis(float val, float offset, int sizeSubcells)
        {
            if (sizeSubcells <= 0)
                return val;

            float halfExtent = sizeSubcells * (SubcellWorldSize * 0.5f);
            float step = (sizeSubcells % 2 == 0) ? 64f : 32f;
            float relOrigin = (val - offset) - halfExtent;
            float snappedOrigin = MathF.Round(relOrigin / step) * step;
            return offset + snappedOrigin + halfExtent;
        }

        return (
            SnapAxis(worldX, terrainOffsetX, fpWidth),
            SnapAxis(worldY, terrainOffsetY, fpHeight)
        );
    }

    /// <summary>
    /// Captures the initial footprint list from an opened map so later edits can clear old entity footprints
    /// while preserving custom hand-painted pathing elsewhere in war3map.wpm.
    /// </summary>
    public static void CaptureInitialSnapshots(
        MapProject project,
        byte[]? originalWpmBytes,
        WarcraftBaseDataService baseData)
    {
        project.Terrain.InitialWpmBytes = originalWpmBytes != null ? (byte[])originalWpmBytes.Clone() : null;
        project.InitialPathingFootprints.Clear();

        foreach (var u in project.PlacedUnits)
        {
            if (!u.IsBuilding || u.IsStartLocation)
                continue;
            var pathTex = ResolveObjectField(u.Rawcode, false, project, baseData, "upat", "pathTex");
            if (!string.IsNullOrEmpty(pathTex))
            {
                project.InitialPathingFootprints.Add(new InitialEntityPathingSnapshot
                {
                    Rawcode = u.Rawcode,
                    IsDoodad = false,
                    IsBuilding = true,
                    X = u.X,
                    Y = u.Y,
                    PathTexture = pathTex
                });
            }
        }

        foreach (var d in project.PlacedDoodads)
        {
            bool isDead = d.Life == 0;
            var pathTex = ResolveObjectField(
                d.Rawcode,
                true,
                project,
                baseData,
                isDead ? "bptd" : "bptx",
                isDead ? "pathTexDeath" : "pathTex");
            if (string.IsNullOrEmpty(pathTex) && !isDead)
                pathTex = ResolveObjectField(d.Rawcode, true, project, baseData, "dptx", "pathTex");

            if (!string.IsNullOrEmpty(pathTex))
            {
                project.InitialPathingFootprints.Add(new InitialEntityPathingSnapshot
                {
                    Rawcode = d.Rawcode,
                    IsDoodad = true,
                    IsDead = isDead,
                    X = d.X,
                    Y = d.Y,
                    PathTexture = pathTex
                });
            }
        }
    }

    /// <summary>
    /// Regenerates or incrementally patches war3map.wpm (MP3W v0) from terrain + placed buildings/destructibles.
    /// </summary>
    public static byte[] BuildUpdatedWpmBytes(
        MapProject project,
        WarcraftBaseDataService baseData,
        BlpIconService? blpService)
    {
        var t = project.Terrain;
        int cellsW = Math.Max(1, t.GridWidth - 1);
        int cellsH = Math.Max(1, t.GridHeight - 1);
        int pw = cellsW * 4;
        int ph = cellsH * 4;
        var cells = new byte[pw * ph];

        bool canPatchExisting = !project.HasTerrainEdits &&
                                t.InitialWpmBytes != null &&
                                t.InitialWpmBytes.Length >= 16 + pw * ph &&
                                t.InitialWpmBytes[0] == (byte)'M' &&
                                t.InitialWpmBytes[1] == (byte)'P' &&
                                t.InitialWpmBytes[2] == (byte)'3' &&
                                t.InitialWpmBytes[3] == (byte)'W' &&
                                BitConverter.ToInt32(t.InitialWpmBytes, 8) == pw &&
                                BitConverter.ToInt32(t.InitialWpmBytes, 12) == ph;

        if (canPatchExisting)
        {
            Buffer.BlockCopy(t.InitialWpmBytes!, 16, cells, 0, pw * ph);

            // Clear old entity footprints back to terrain-derived pathing so moved/deleted buildings don't leave ghost blocks
            foreach (var snap in project.InitialPathingFootprints)
            {
                var grid = GetPathTexture(snap.PathTexture, project, blpService);
                if (grid == null) continue;
                RestoreTerrainSubcellsUnderFootprint(cells, pw, ph, t, snap.X, snap.Y, grid);
            }
        }
        else
        {
            for (int sy = 0; sy < ph; sy++)
            {
                for (int sx = 0; sx < pw; sx++)
                {
                    cells[sy * pw + sx] = ComputeBaseTerrainCellFlag(t, sx, sy);
                }
            }
        }

        // Stamp ONLY static non-destructible doodads with a PathTexture.
        // Buildings (CreateUnit) and Destructibles (CreateDestructable, trees/gates) must NEVER be baked into
        // war3map.wpm: WC3 checks war3map.wpm BEFORE CreateUnit/CreateDestructable and stamps their runtime pathing
        // dynamically. If a building's footprint is already baked as 0x08/0x02 in war3map.wpm, WC3 treats the
        // target spot as blocked and pushes the Gold Mine / building away to the nearest free coordinates.
        foreach (var d in project.PlacedDoodads)
        {
            if (IsPlacedDoodadDestructible(d, project, baseData))
                continue;

            var pathTex = ResolveObjectField(d.Rawcode, true, project, baseData, "dptx", "pathTex");
            if (string.IsNullOrEmpty(pathTex))
                pathTex = ResolveObjectField(d.Rawcode, true, project, baseData, "bptx", "pathTex");

            var grid = GetPathTexture(pathTex, project, blpService);
            if (grid != null)
                StampFootprint(cells, pw, ph, t, d.X, d.Y, grid);
        }

        using var ms = new MemoryStream(16 + cells.Length);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("MP3W"));
        bw.Write(0);
        bw.Write(pw);
        bw.Write(ph);
        bw.Write(cells);
        bw.Flush();
        return ms.ToArray();
    }

    public static void SyncWpm(MapProject project, WarcraftBaseDataService baseData, BlpIconService? blpService)
    {
        project.RawMpqFiles["war3map.wpm"] = BuildUpdatedWpmBytes(project, baseData, blpService);
    }

    /// <summary>
    /// Computes the terrain-only pathing byte (no-walk/no-fly/no-build) for a 32x32 subcell from W3E data.
    /// </summary>
    public static byte ComputeBaseTerrainCellFlag(W3TerrainData t, int sx, int sy)
    {
        int gx = Math.Clamp(sx >> 2, 0, Math.Max(0, t.GridWidth - 2));
        int gy = Math.Clamp(sy >> 2, 0, Math.Max(0, t.GridHeight - 2));

        // Boundary corners around the tile
        if (t.BoundaryFlags != null &&
            (t.BoundaryFlags[gx, gy] || t.BoundaryFlags[gx + 1, gy] ||
             t.BoundaryFlags[gx, gy + 1] || t.BoundaryFlags[gx + 1, gy + 1]))
        {
            return (byte)(FlagWalkBlocked | FlagFlyBlocked | FlagBuildBlocked);
        }

        byte flag = 0;

        // Water check at subcell
        bool hasWater = t.WaterFlags != null &&
                        (t.WaterFlags[gx, gy] || t.WaterFlags[gx + 1, gy] ||
                         t.WaterFlags[gx, gy + 1] || t.WaterFlags[gx + 1, gy + 1]);
        if (hasWater && t.WaterDepths != null)
        {
            float tx = ((sx & 3) + 0.5f) * 0.25f;
            float ty = ((sy & 3) + 0.5f) * 0.25f;
            float d00 = t.WaterDepths[gx, gy];
            float d10 = t.WaterDepths[gx + 1, gy];
            float d01 = t.WaterDepths[gx, gy + 1];
            float d11 = t.WaterDepths[gx + 1, gy + 1];
            float depth = (d00 * (1f - tx) + d10 * tx) * (1f - ty) + (d01 * (1f - tx) + d11 * tx) * ty;
            if (depth > 24f)
                flag |= FlagWalkBlocked | FlagBuildBlocked;
            else if (depth > 0f)
                flag |= FlagBuildBlocked;
        }

        // Cliff & ramp check
        bool isRamp = t.RampFlags != null &&
                      (t.RampFlags[gx, gy] || t.RampFlags[gx + 1, gy] ||
                       t.RampFlags[gx, gy + 1] || t.RampFlags[gx + 1, gy + 1]);
        if (!isRamp && t.CliffLevels != null)
        {
            byte c00 = t.CliffLevels[gx, gy];
            byte c10 = t.CliffLevels[gx + 1, gy];
            byte c01 = t.CliffLevels[gx, gy + 1];
            byte c11 = t.CliffLevels[gx + 1, gy + 1];
            if (c00 != c10 || c00 != c01 || c00 != c11)
                flag |= FlagWalkBlocked | FlagBuildBlocked;
        }

        if (!isRamp && t.GroundHeights != null)
        {
            float z00 = t.GroundHeights[gx, gy];
            float z10 = t.GroundHeights[gx + 1, gy];
            float z01 = t.GroundHeights[gx, gy + 1];
            float z11 = t.GroundHeights[gx + 1, gy + 1];
            float minZ = MathF.Min(MathF.Min(z00, z10), MathF.Min(z01, z11));
            float maxZ = MathF.Max(MathF.Max(z00, z10), MathF.Max(z01, z11));
            if (maxZ - minZ > 64f)
                flag |= FlagWalkBlocked | FlagBuildBlocked;
        }

        // Unbuildable tile textures
        if (t.TextureIndices != null && t.GroundTiles.Count > 0)
        {
            int cornerX = (sx & 3) >= 2 ? gx + 1 : gx;
            int cornerY = (sy & 3) >= 2 ? gy + 1 : gy;
            byte texIdx = t.TextureIndices[Math.Clamp(cornerX, 0, t.GridWidth - 1), Math.Clamp(cornerY, 0, t.GridHeight - 1)];
            if (texIdx < t.GroundTiles.Count && UnbuildableGroundTiles.Contains(t.GroundTiles[texIdx]))
                flag |= FlagBuildBlocked;
        }

        return flag;
    }

    private static bool IsPlacedDoodadDestructible(
        W3PlacedDoodad d,
        MapProject project,
        WarcraftBaseDataService? baseData)
    {
        if (d.IsDestructible)
            return true;

        var custom = project.Objects.FirstOrDefault(
            o => string.Equals(o.Rawcode, d.Rawcode, StringComparison.OrdinalIgnoreCase));
        if (custom != null)
            return custom.Category == W3ObjectCategory.Destructables;

        var baseMeta = baseData?.GetBaseObject(d.Rawcode);
        return baseMeta?.Category == W3ObjectCategory.Destructables;
    }

    private static void RestoreTerrainSubcellsUnderFootprint(
        byte[] cells, int pw, int ph, W3TerrainData t, float worldX, float worldY, PathTextureGrid grid)
    {
        int minIx = (int)MathF.Floor((worldX - t.OffsetX - grid.Width * 16f) / SubcellWorldSize);
        int minIy = (int)MathF.Floor((worldY - t.OffsetY - grid.Height * 16f) / SubcellWorldSize);

        for (int gy = 0; gy < grid.Height; gy++)
        {
            int sy = minIy + gy;
            if ((uint)sy >= (uint)ph) continue;
            for (int gx = 0; gx < grid.Width; gx++)
            {
                int sx = minIx + gx;
                if ((uint)sx >= (uint)pw) continue;
                if (grid.Cells[gy * grid.Width + gx] != 0)
                {
                    int idx = sy * pw + sx;
                    byte preservedHighBits = (byte)(cells[idx] & ~(FlagWalkBlocked | FlagFlyBlocked | FlagBuildBlocked));
                    cells[idx] = (byte)(preservedHighBits | ComputeBaseTerrainCellFlag(t, sx, sy));
                }
            }
        }
    }

    private static void StampFootprint(
        byte[] cells, int pw, int ph, W3TerrainData t, float worldX, float worldY, PathTextureGrid grid)
    {
        int minIx = (int)MathF.Floor((worldX - t.OffsetX - grid.Width * 16f) / SubcellWorldSize);
        int minIy = (int)MathF.Floor((worldY - t.OffsetY - grid.Height * 16f) / SubcellWorldSize);

        for (int gy = 0; gy < grid.Height; gy++)
        {
            int sy = minIy + gy;
            if ((uint)sy >= (uint)ph) continue;
            for (int gx = 0; gx < grid.Width; gx++)
            {
                int sx = minIx + gx;
                if ((uint)sx >= (uint)pw) continue;
                byte pf = grid.Cells[gy * grid.Width + gx];
                if (pf != 0)
                    cells[sy * pw + sx] |= (byte)(pf & (FlagWalkBlocked | FlagFlyBlocked | FlagBuildBlocked));
            }
        }
    }

    /// <summary>
    /// Builds custom object pathing/splat/shadow overrides for the WebView2 3D viewport.
    /// </summary>
    public static List<ClientObjectPathingOverride> BuildClientOverrides(
        MapProject project,
        WarcraftBaseDataService baseData)
    {
        var list = new List<ClientObjectPathingOverride>();
        var relevantCats = new HashSet<W3ObjectCategory>
        {
            W3ObjectCategory.Units,
            W3ObjectCategory.Items,
            W3ObjectCategory.Destructables,
            W3ObjectCategory.Doodads
        };

        foreach (var obj in project.Objects.Where(o => relevantCats.Contains(o.Category)))
        {
            bool isDoodadOrDest = obj.Category is W3ObjectCategory.Doodads or W3ObjectCategory.Destructables;
            bool isDest = obj.Category == W3ObjectCategory.Destructables;
            bool isBldg = !isDoodadOrDest && obj.Category == W3ObjectCategory.Units && IsUnitBuilding(obj.Rawcode, project, baseData);

            string pathTex = isDoodadOrDest
                ? ResolveObjectField(obj.Rawcode, true, project, baseData, isDest ? "bptx" : "dptx", "pathTex")
                : ResolveObjectField(obj.Rawcode, false, project, baseData, "upat", "pathTex");

            string pathTexDeath = isDest
                ? ResolveObjectField(obj.Rawcode, true, project, baseData, "bptd", "pathTexDeath")
                : string.Empty;

            string moveType = isDoodadOrDest
                ? string.Empty
                : ResolveObjectField(obj.Rawcode, false, project, baseData, "umvt", "movetp");

            string colStr = isDoodadOrDest
                ? string.Empty
                : ResolveObjectField(obj.Rawcode, false, project, baseData, "ucol", "collision");
            float.TryParse(colStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var collision);

            string preventPlace = isDoodadOrDest
                ? string.Empty
                : ResolveObjectField(obj.Rawcode, false, project, baseData, "upap", "preventPlace");

            string requirePlace = isDoodadOrDest
                ? string.Empty
                : ResolveObjectField(obj.Rawcode, false, project, baseData, "upar", "requirePlace");

            string uberSplat = !isDoodadOrDest
                ? ResolveObjectField(obj.Rawcode, false, project, baseData, "uubs", "uberSplat")
                : string.Empty;

            string shadowTex = isBldg
                ? ResolveObjectField(obj.Rawcode, false, project, baseData, "ushb", "buildingShadow")
                : (!isDoodadOrDest ? ResolveObjectField(obj.Rawcode, false, project, baseData, "ushu", "unitShadow") : string.Empty);

            string animProps = !isDoodadOrDest && obj.Category == W3ObjectCategory.Units
                ? ResolveObjectField(obj.Rawcode, false, project, baseData, "uani", "animProps")
                : string.Empty;

            list.Add(new ClientObjectPathingOverride(
                obj.Rawcode,
                isBldg,
                isDest,
                pathTex,
                pathTexDeath,
                moveType,
                collision,
                preventPlace,
                requirePlace,
                uberSplat,
                shadowTex,
                animProps));
        }

        return list;
    }
}
