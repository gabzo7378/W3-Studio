using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3Studio.Models;

namespace W3Studio.Services;

/// <summary>
/// High-level terrain utilities inspired by HiveWE / World Editor:
/// 16-bit grayscale heightmap import/export and war3map.w3e tileset editing.
/// </summary>
public static class TerrainOperationsService
{
    public static readonly (char Code, string Name)[] OfficialTilesets =
    {
        ('L', "Lordaeron Summer"),
        ('F', "Lordaeron Fall"),
        ('W', "Lordaeron Winter"),
        ('A', "Ashenvale"),
        ('B', "Barrens"),
        ('C', "Felwood"),
        ('D', "Dungeon"),
        ('G', "Underground"),
        ('I', "Icecrown Glacier"),
        ('J', "Dalaran Ruins"),
        ('K', "Black Citadel"),
        ('N', "Northrend"),
        ('O', "Outland"),
        ('Q', "Village Fall"),
        ('V', "Village"),
        ('X', "Dalaran"),
        ('Y', "Cityscape"),
        ('Z', "Sunken Ruins")
    };

    private static readonly TerrainTileDefinition[] FallbackGroundTiles =
    {
        new("Ldrt", "Lordaeron Dirt", @"TerrainArt\LordaeronSummer\Lords_Dirt.blp", true),
        new("Ldro", "Lordaeron Rough Dirt", @"TerrainArt\LordaeronSummer\Lords_DirtRough.blp", true),
        new("Ldrg", "Lordaeron Grassy Dirt", @"TerrainArt\LordaeronSummer\Lords_DirtGrass.blp", true),
        new("Lrok", "Lordaeron Rock", @"TerrainArt\LordaeronSummer\Lords_Rock.blp", false),
        new("Lgrs", "Lordaeron Grass", @"TerrainArt\LordaeronSummer\Lords_Grass.blp", true),
        new("Lgrd", "Lordaeron Dark Grass", @"TerrainArt\LordaeronSummer\Lords_GrassDark.blp", true),
        new("Fdrt", "Lordaeron Fall Dirt", @"TerrainArt\LordaeronFall\Lordf_Dirt.blp", true),
        new("Fgrs", "Lordaeron Fall Grass", @"TerrainArt\LordaeronFall\Lordf_Grass.blp", true),
        new("Wdrt", "Lordaeron Winter Dirt", @"TerrainArt\LordaeronWinter\Lordw_Dirt.blp", true),
        new("Wsnw", "Lordaeron Winter Snow", @"TerrainArt\LordaeronWinter\Lordw_Snow.blp", true),
        new("Adrt", "Ashenvale Dirt", @"TerrainArt\Ashenvale\Ashen_Dirt.blp", true),
        new("Agrs", "Ashenvale Grass", @"TerrainArt\Ashenvale\Ashen_Grass.blp", true),
        new("Alvd", "Ashenvale Leaves", @"TerrainArt\Ashenvale\Ashen_Leaves.blp", true),
        new("Bdrt", "Barrens Dirt", @"TerrainArt\Barrens\Barrens_Dirt.blp", true),
        new("Bdsr", "Barrens Desert", @"TerrainArt\Barrens\Barrens_Desert.blp", false),
        new("Bgrs", "Barrens Grass", @"TerrainArt\Barrens\Barrens_Grass.blp", true),
        new("Ndrt", "Northrend Dirt", @"TerrainArt\Northrend\North_Dirt.blp", true),
        new("Nsnw", "Northrend Snow", @"TerrainArt\Northrend\North_Snow.blp", true),
        new("Vdrt", "Village Dirt", @"TerrainArt\Village\Village_Dirt.blp", true),
        new("Vgrs", "Village Grass", @"TerrainArt\Village\Village_Grass.blp", true),
        new("Vcbb", "Village Cobble Path", @"TerrainArt\Village\Village_CobblePath.blp", true),
        new("Ydrt", "Cityscape Dirt", @"TerrainArt\Cityscape\City_Dirt.blp", true),
        new("Ysqd", "Cityscape Square Tiles", @"TerrainArt\Cityscape\City_SquareTiles.blp", false),
        new("Ygsb", "Cityscape Black Marble", @"TerrainArt\Cityscape\City_BlackMarble.blp", false),
        new("Idrt", "Icecrown Dirt", @"TerrainArt\Icecrown\Ice_Dirt.blp", true),
        new("Isnw", "Icecrown Snow", @"TerrainArt\Icecrown\Ice_Snow.blp", true)
    };

    private static readonly CliffTileDefinition[] FallbackCliffTiles =
    {
        new("CLdi", "Lordaeron Dirt Cliff", "Ldrt", @"ReplaceableTextures\Cliff\Cliff0.blp"),
        new("CLgr", "Lordaeron Grass Cliff", "Lgrs", @"ReplaceableTextures\Cliff\Cliff1.blp"),
        new("CAdi", "Ashenvale Dirt Cliff", "Adrt", @"ReplaceableTextures\Cliff\Cliff0.blp"),
        new("CAgr", "Ashenvale Grass Cliff", "Agrs", @"ReplaceableTextures\Cliff\Cliff1.blp"),
        new("CBde", "Barrens Desert Cliff", "Bdsr", @"ReplaceableTextures\Cliff\Cliff0.blp"),
        new("CNdi", "Northrend Dirt Cliff", "Ndrt", @"ReplaceableTextures\Cliff\Cliff0.blp"),
        new("CNsn", "Northrend Snow Cliff", "Nsnw", @"ReplaceableTextures\Cliff\Cliff1.blp"),
        new("CYdi", "Cityscape Dirt Cliff", "Ydrt", @"ReplaceableTextures\Cliff\Cliff0.blp"),
        new("CYsq", "Cityscape Square Cliff", "Ysqd", @"ReplaceableTextures\Cliff\Cliff1.blp")
    };

    public static IReadOnlyList<TerrainTileDefinition> GetGroundTileCatalog(WarcraftBaseDataService? baseData)
    {
        if (baseData != null && baseData.TerrainTileCatalog.Count > 0)
            return baseData.TerrainTileCatalog;
        return FallbackGroundTiles;
    }

    public static IReadOnlyList<CliffTileDefinition> GetCliffTileCatalog(WarcraftBaseDataService? baseData)
    {
        if (baseData != null && baseData.CliffTileCatalog.Count > 0)
            return baseData.CliffTileCatalog;
        return FallbackCliffTiles;
    }

    public static (float MinZ, float MaxZ) ExportHeightmapPng(MapProject project, string outputPath)
    {
        var t = project?.Terrain ?? throw new ArgumentNullException(nameof(project));
        var raw = t.RawW3eBytes ?? throw new InvalidOperationException("El mapa no tiene datos de terreno (war3map.w3e).");

        int w = t.GridWidth;
        int h = t.GridHeight;
        int headerLen = TerrainBrushService.GetW3eCornerDataOffset(raw);
        if (headerLen < 0 || raw.Length < headerLen + w * h * 7)
            throw new InvalidOperationException("Cabecera war3map.w3e inválida.");

        float minZ = float.MaxValue;
        float maxZ = float.MinValue;
        var heights = new float[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int off = headerLen + (y * w + x) * 7;
                ushort gRaw = BitConverter.ToUInt16(raw, off);
                int layer = raw[off + 6] & 0x0F;
                float z = ((gRaw - 8192) / 4f) + ((layer - 2) * 128f);
                heights[y * w + x] = z;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
        }

        float span = MathF.Max(1f, maxZ - minZ);
        var pixels = new ushort[w * h];
        for (int imgY = 0; imgY < h; imgY++)
        {
            // Flip vertically so North (+Y) is top of image
            int w3eY = h - 1 - imgY;
            for (int x = 0; x < w; x++)
            {
                float norm = Math.Clamp((heights[w3eY * w + x] - minZ) / span, 0f, 1f);
                pixels[imgY * w + x] = (ushort)MathF.Round(norm * 65535f);
            }
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray16, null, pixels, w * 2);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
        return (minZ, maxZ);
    }

    public static int ImportHeightmapFromImage(
        MapProject project,
        string imagePath,
        float targetMinZ,
        float targetMaxZ,
        bool smoothPass = false)
    {
        var t = project?.Terrain ?? throw new ArgumentNullException(nameof(project));
        var raw = t.RawW3eBytes ?? throw new InvalidOperationException("El mapa no tiene datos de terreno (war3map.w3e).");

        int w = t.GridWidth;
        int h = t.GridHeight;
        int headerLen = TerrainBrushService.GetW3eCornerDataOffset(raw);
        if (headerLen < 0 || raw.Length < headerLen + w * h * 7)
            throw new InvalidOperationException("Cabecera war3map.w3e inválida.");

        if (targetMaxZ < targetMinZ)
            (targetMinZ, targetMaxZ) = (targetMaxZ, targetMinZ);

        BitmapSource src;
        using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            src = decoder.Frames[0];
        }

        var gray16 = src.Format == PixelFormats.Gray16
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Gray16, null, 0);

        int sw = gray16.PixelWidth;
        int sh = gray16.PixelHeight;
        if (sw <= 0 || sh <= 0)
            throw new InvalidOperationException("La imagen de heightmap está vacía.");

        var srcPixels = new ushort[sw * sh];
        gray16.CopyPixels(srcPixels, sw * 2, 0);

        var gridNorm = new float[w, h];
        for (int y = 0; y < h; y++)
        {
            // North (+Y) corresponds to top row of image (imgY = 0)
            float v = h > 1 ? ((h - 1 - y) / (float)(h - 1)) * (sh - 1) : 0f;
            int y0 = Math.Clamp((int)MathF.Floor(v), 0, sh - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float fy = v - y0;

            for (int x = 0; x < w; x++)
            {
                float u = w > 1 ? (x / (float)(w - 1)) * (sw - 1) : 0f;
                int x0 = Math.Clamp((int)MathF.Floor(u), 0, sw - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sw - 1);
                float fx = u - x0;

                float p00 = srcPixels[y0 * sw + x0] / 65535f;
                float p10 = srcPixels[y0 * sw + x1] / 65535f;
                float p01 = srcPixels[y1 * sw + x0] / 65535f;
                float p11 = srcPixels[y1 * sw + x1] / 65535f;
                gridNorm[x, y] = (p00 * (1f - fx) + p10 * fx) * (1f - fy) + (p01 * (1f - fx) + p11 * fx) * fy;
            }
        }

        if (smoothPass)
        {
            var smoothed = new float[w, h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    int cnt = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if ((uint)ny >= (uint)h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if ((uint)nx >= (uint)w) continue;
                            sum += gridNorm[nx, ny];
                            cnt++;
                        }
                    }
                    smoothed[x, y] = cnt > 0 ? sum / cnt : gridNorm[x, y];
                }
            }
            gridNorm = smoothed;
        }

        t.GroundHeights ??= new float[w, h];
        float span = targetMaxZ - targetMinZ;
        int modified = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int off = headerLen + (y * w + x) * 7;
                int layer = raw[off + 6] & 0x0F;
                float worldZ = targetMinZ + gridNorm[x, y] * span;
                float relZ = worldZ - ((layer - 2) * 128f);
                ushort rawH = (ushort)Math.Clamp((int)MathF.Round(relZ * 4f + 8192f), 0, 16383);

                ushort prevH = BitConverter.ToUInt16(raw, off);
                if (prevH != rawH)
                {
                    raw[off] = (byte)(rawH & 0xFF);
                    raw[off + 1] = (byte)((rawH >> 8) & 0xFF);
                    modified++;
                }
                t.GroundHeights[x, y] = ((rawH - 8192) / 4f) + ((layer - 2) * 128f);
            }
        }

        project.HasTerrainEdits = true;
        project.RawMpqFiles["war3map.w3e"] = raw;
        return modified;
    }

    public static void ApplyTilesetConfiguration(
        MapProject project,
        char newTileset,
        IReadOnlyList<string> newGroundTiles,
        IReadOnlyList<string> newCliffTiles,
        IReadOnlyDictionary<int, int>? groundIndexRemap = null)
    {
        var t = project?.Terrain ?? throw new ArgumentNullException(nameof(project));
        var oldRaw = t.RawW3eBytes ?? throw new InvalidOperationException("El mapa no tiene datos de terreno (war3map.w3e).");
        if (newGroundTiles.Count == 0 || newGroundTiles.Count > 16)
            throw new ArgumentOutOfRangeException(nameof(newGroundTiles), "Debe haber entre 1 y 16 texturas de suelo.");
        if (newCliffTiles.Count == 0 || newCliffTiles.Count > 16)
            throw new ArgumentOutOfRangeException(nameof(newCliffTiles), "Debe haber entre 1 y 16 texturas de acantilado.");

        int oldHeaderLen = TerrainBrushService.GetW3eCornerDataOffset(oldRaw);
        int w = t.GridWidth;
        int h = t.GridHeight;
        int cornerBytesLen = w * h * 7;
        if (oldHeaderLen < 0 || oldRaw.Length < oldHeaderLen + cornerBytesLen)
            throw new InvalidOperationException("Cabecera war3map.w3e inválida.");

        using var ms = new MemoryStream(37 + 4 * newGroundTiles.Count + 4 * newCliffTiles.Count + cornerBytesLen);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("W3E!"));
        bw.Write(11); // version
        bw.Write((byte)newTileset);
        bw.Write(1); // custom tileset = true

        bw.Write(newGroundTiles.Count);
        foreach (var tile in newGroundTiles)
        {
            var code = (tile ?? "Ldrt").PadRight(4).Substring(0, 4);
            bw.Write(Encoding.ASCII.GetBytes(code));
        }

        bw.Write(newCliffTiles.Count);
        foreach (var cliff in newCliffTiles)
        {
            var code = (cliff ?? "CLdi").PadRight(4).Substring(0, 4);
            bw.Write(Encoding.ASCII.GetBytes(code));
        }

        bw.Write(w);
        bw.Write(h);
        bw.Write(t.OffsetX);
        bw.Write(t.OffsetY);

        var cornerData = new byte[cornerBytesLen];
        Buffer.BlockCopy(oldRaw, oldHeaderLen, cornerData, 0, cornerBytesLen);

        int maxGroundIdx = newGroundTiles.Count - 1;
        int maxCliffIdx = newCliffTiles.Count - 1;
        t.TextureIndices ??= new byte[w, h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int off = (y * w + x) * 7;
                int oldGround = cornerData[off + 4] & 0x0F;
                int mappedGround = groundIndexRemap != null && groundIndexRemap.TryGetValue(oldGround, out var remapped)
                    ? Math.Clamp(remapped, 0, maxGroundIdx)
                    : Math.Clamp(oldGround, 0, maxGroundIdx);
                cornerData[off + 4] = (byte)((cornerData[off + 4] & 0xF0) | mappedGround);
                t.TextureIndices[x, y] = (byte)mappedGround;

                int oldCliffTex = (cornerData[off + 6] >> 4) & 0x0F;
                if (oldCliffTex > maxCliffIdx)
                    cornerData[off + 6] = (byte)((maxCliffIdx << 4) | (cornerData[off + 6] & 0x0F));
            }
        }

        bw.Write(cornerData);
        bw.Flush();

        t.Tileset = newTileset;
        t.GroundTiles.Clear();
        t.GroundTiles.AddRange(newGroundTiles.Select(c => c.PadRight(4).Substring(0, 4)));
        t.CliffTiles.Clear();
        t.CliffTiles.AddRange(newCliffTiles.Select(c => c.PadRight(4).Substring(0, 4)));
        t.RawW3eBytes = ms.ToArray();

        project.HasTerrainEdits = true;
        project.RawMpqFiles["war3map.w3e"] = t.RawW3eBytes;
    }
}

