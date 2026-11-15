using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using War3Net.IO.Mpq;
using W3Studio.Models;

namespace W3Studio.Services;

public class BlpIconService
{
    private readonly ConcurrentDictionary<string, BitmapSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MpqArchive> _openArchives = new();
    private readonly Dictionary<string, List<MpqArchive>> _tilesetSubArchives = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mpqLock = new();
    private List<string>? _cachedBaseIconPaths;
    private List<string>? _cachedBaseModelPaths;
    private List<(MpqArchive Archive, Dictionary<string, string> ListfileMap)>? _cachedListfileMaps;
    private BitmapSource? _fallbackIcon;

    public BitmapSource FallbackIcon => _fallbackIcon ??= CreateFallbackIcon();

    public void ConfigureBaseMpqs(WorkspaceSettings settings)
    {
        lock (_mpqLock)
        {
            foreach (var list in _tilesetSubArchives.Values)
            {
                foreach (var sub in list)
                {
                    try { sub.Dispose(); } catch { }
                }
            }
            _tilesetSubArchives.Clear();

            foreach (var old in _openArchives)
            {
                try { old.Dispose(); } catch { }
            }
            _openArchives.Clear();
            _cachedBaseIconPaths = null;
            _cachedBaseModelPaths = null;
            _cachedListfileMaps = null;
            _iconCache.Clear();

            // Search priority: War3Patch.mpq -> War3xlocal.mpq -> War3x.mpq -> War3Local.mpq -> war3.mpq
            var paths = new[]
            {
                settings.War3PatchMpqPath,
                settings.War3xLocalMpqPath,
                settings.War3xMpqPath,
                settings.War3LocalMpqPath,
                settings.War3MpqPath
            };
            foreach (var mpqFile in paths)
            {
                if (!File.Exists(mpqFile))
                    continue;
                try
                {
                    var fs = new FileStream(mpqFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var archive = MpqArchive.Open(fs, false);
                    _openArchives.Add(archive);
                }
                catch
                {
                    // Skip if locked
                }
            }
        }
    }

    public BitmapSource GetIcon(string? iconPath, MapProject? currentProject = null)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return FallbackIcon;

        var normalized = NormalizeIconPath(iconPath);
        if (_iconCache.TryGetValue(normalized, out var cached))
            return cached;

        try
        {
            var rawBytes = FindIconBytes(normalized, currentProject);
            if (rawBytes != null && rawBytes.Length > 20)
            {
                var decoded = DecodeBlpOrTga(rawBytes);
                if (decoded != null)
                {
                    decoded.Freeze();
                    _iconCache[normalized] = decoded;
                    return decoded;
                }
            }
        }
        catch
        {
            // Return fallback icon if texture is malformed
        }

        _iconCache[normalized] = FallbackIcon;
        return FallbackIcon;
    }

    public void InvalidateIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return;

        var normalized = NormalizeIconPath(iconPath);
        _iconCache.TryRemove(normalized, out _);

        var fileName = Path.GetFileName(normalized);
        if (!string.IsNullOrEmpty(fileName))
        {
            foreach (var key in _iconCache.Keys)
            {
                if (key.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    _iconCache.TryRemove(key, out _);
            }
        }
    }

    public List<string> GetAvailableIconPaths(MapProject? project = null)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (project != null)
        {
            foreach (var imp in project.Imports)
            {
                if (imp.IsDeleted || string.IsNullOrWhiteSpace(imp.MpqPath))
                    continue;
                if (imp.MpqPath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ||
                    imp.MpqPath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(imp.MpqPath);
                }
            }
        }

        lock (_mpqLock)
        {
            if (_cachedBaseIconPaths == null)
            {
                var baseIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var archive in _openArchives)
                {
                    try
                    {
                        if (!archive.FileExists("(listfile)"))
                            continue;
                        using var s = archive.OpenFile("(listfile)");
                        using var sr = new StreamReader(s);
                        string? line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length == 0 || !trimmed.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (trimmed.StartsWith(@"ReplaceableTextures\CommandButtons\", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith(@"ReplaceableTextures\PassiveButtons\", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith(@"ReplaceableTextures\CommandButtonsDisabled\", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith(@"ReplaceableTextures\WorldEditArt\", StringComparison.OrdinalIgnoreCase))
                            {
                                baseIcons.Add(trimmed);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                _cachedBaseIconPaths = new List<string>(baseIcons);
            }

            foreach (var p in _cachedBaseIconPaths)
                result.Add(p);
        }

        return new List<string>(result);
    }

    public List<string> GetAvailableModelPaths(MapProject? project = null)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (project != null)
        {
            foreach (var imp in project.Imports)
            {
                if (imp.IsDeleted || string.IsNullOrWhiteSpace(imp.MpqPath))
                    continue;
                if (imp.MpqPath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
                    imp.MpqPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(imp.MpqPath);
                }
            }
        }

        lock (_mpqLock)
        {
            if (_cachedBaseModelPaths == null)
            {
                var baseModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var archive in _openArchives)
                {
                    try
                    {
                        if (!archive.FileExists("(listfile)"))
                            continue;
                        using var s = archive.OpenFile("(listfile)");
                        using var sr = new StreamReader(s);
                        string? line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length == 0)
                                continue;
                            if (trimmed.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                            {
                                baseModels.Add(trimmed);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                _cachedBaseModelPaths = new List<string>(baseModels);
            }

            foreach (var p in _cachedBaseModelPaths)
                result.Add(p);
        }

        return new List<string>(result);
    }

    public bool TryGetTexturePixels(string? texturePath, MapProject? currentProject, out byte[] pixels, out int width, out int height)
    {
        pixels = Array.Empty<byte>();
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(texturePath))
            return false;

        try
        {
            var normalized = NormalizeIconPath(texturePath);
            var rawBytes = FindIconBytes(normalized, currentProject);
            if (rawBytes == null || rawBytes.Length <= 20)
                return false;

            var bmp = DecodeBlpOrTga(rawBytes);
            if (bmp == null)
                return false;

            width = bmp.PixelWidth;
            height = bmp.PixelHeight;
            var stride = width * 4;
            pixels = new byte[height * stride];
            bmp.CopyPixels(pixels, stride, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeIconPath(string path)
    {
        var clean = path.Trim().Trim('"').Replace('/', '\\').Replace("\\\\", "\\");
        if (!clean.Contains('.'))
            clean += ".blp";
        else if (clean.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(0, clean.Length - 4) + ".blp";
        return clean;
    }

    private List<MpqArchive> GetOrOpenTilesetArchivesLocked(string tileset)
    {
        var key = char.ToUpperInvariant(tileset.Trim()[0]).ToString();
        if (_tilesetSubArchives.TryGetValue(key, out var cached))
            return cached;

        var subList = new List<MpqArchive>();
        var subMpqName = key + ".mpq";
        foreach (var archive in _openArchives)
        {
            try
            {
                if (!archive.FileExists(subMpqName))
                    continue;
                using var s = archive.OpenFile(subMpqName);
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                subList.Add(MpqArchive.Open(ms, false));
            }
            catch
            {
                // Ignore corrupt or unreadable nested tileset archive
            }
        }

        _tilesetSubArchives[key] = subList;
        return subList;
    }

    private static byte[]? TryReadCandidatesFromArchive(MpqArchive archive, List<string> candidates)
    {
        try
        {
            foreach (var cand in candidates)
            {
                if (archive.FileExists(cand))
                {
                    using var s = archive.OpenFile(cand);
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private List<(MpqArchive Archive, Dictionary<string, string> ListfileMap)> GetOrBuildListfileMapsLocked()
    {
        if (_cachedListfileMaps != null)
            return _cachedListfileMaps;

        var result = new List<(MpqArchive, Dictionary<string, string>)>();
        foreach (var archive in _openArchives)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (archive.FileExists("(listfile)"))
                {
                    using var s = archive.OpenFile("(listfile)");
                    using var sr = new StreamReader(s);
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                            map.TryAdd(trimmed, trimmed);
                    }
                }
            }
            catch
            {
            }
            result.Add((archive, map));
        }
        _cachedListfileMaps = result;
        return result;
    }

    public byte[]? FindRawFileBytes(string path, MapProject? project = null, string? tileset = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var clean = path.Trim().Trim('"').Replace('/', '\\');
        while (clean.Contains("\\\\", StringComparison.Ordinal))
            clean = clean.Replace("\\\\", "\\");
        clean = clean.TrimStart('\\');
        if (clean.Length == 0)
            return null;

        var candidates = new List<string> { clean };
        void AddCandidate(string p)
        {
            if (!string.IsNullOrWhiteSpace(p) && !candidates.Contains(p, StringComparer.OrdinalIgnoreCase))
                candidates.Add(p);
        }

        // Fix double extensions like ShadowCrates.tga.blp or Foo.mdl.mdx
        if (clean.EndsWith(".tga.blp", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".blp.blp", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".dds.blp", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(clean.Substring(0, clean.Length - 4));
        }
        if (clean.EndsWith(".mdl.mdx", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".mdx.mdx", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(clean.Substring(0, clean.Length - 4));
            AddCandidate(clean.Substring(0, clean.Length - 8) + ".mdx");
        }

        if (clean.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            AddCandidate(clean.Substring(0, clean.Length - 4) + ".mdx");
        else if (clean.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            AddCandidate(clean.Substring(0, clean.Length - 4) + ".mdl");
        else if (clean.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            AddCandidate(clean.Substring(0, clean.Length - 4) + ".tga");
        else if (clean.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            AddCandidate(clean.Substring(0, clean.Length - 4) + ".blp");

        // WC3 path quirk: e.g. Objects\Spawnmodels\Other\NPCBlood\NpcBloodVillagerWoman.mdx -> ...\NPCbloodVillagerWoman\NPCbloodVillagerWoman.mdx
        if (clean.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(clean);
            var stem = Path.GetFileNameWithoutExtension(clean);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(stem))
            {
                AddCandidate(Path.Combine(dir, stem, stem + ".mdx"));
                // Numbered variation fallback like Foo0.mdx -> Foo.mdx or Foo.mdx -> Foo0.mdx
                if (char.IsDigit(stem[^1]))
                    AddCandidate(Path.Combine(dir, stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') + ".mdx"));
            }
        }

        // Fallback for TeamColor16..27 / TeamGlow16..27 (WC3 1.27b only has 00..15)
        if (clean.StartsWith(@"ReplaceableTextures\TeamColor\TeamColor", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith(@"ReplaceableTextures\TeamGlow\TeamGlow", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(clean);
            if (stem.Length >= 2 && int.TryParse(stem.AsSpan(stem.Length - 2), out int teamIdx) && teamIdx > 15)
            {
                int mapped = teamIdx >= 24 ? 12 : (teamIdx % 16);
                var folder = Path.GetDirectoryName(clean) ?? @"ReplaceableTextures\TeamColor";
                var prefix = stem.StartsWith("TeamGlow", StringComparison.OrdinalIgnoreCase) ? "TeamGlow" : "TeamColor";
                AddCandidate(Path.Combine(folder, $"{prefix}{mapped:D2}.blp"));
            }
        }

        if (clean.StartsWith(@"war3mapImported\", StringComparison.OrdinalIgnoreCase))
            AddCandidate(clean.Substring(@"war3mapImported\".Length));

        var fileNameOnly = Path.GetFileName(clean);
        if (!string.IsNullOrEmpty(fileNameOnly))
        {
            AddCandidate(@"ReplaceableTextures\CommandButtons\" + fileNameOnly);
            AddCandidate(@"war3mapImported\" + fileNameOnly);
        }

        if (project != null)
        {
            foreach (var cand in candidates)
            {
                if (project.RawMpqFiles.TryGetValue(cand, out var mapBytes))
                    return mapBytes;
            }
        }

        lock (_mpqLock)
        {
            // Tileset nested MPQs (e.g., W.mpq inside War3Patch/War3x/war3) override cliff/water/splat textures.
            if (!string.IsNullOrWhiteSpace(tileset))
            {
                var subArchives = GetOrOpenTilesetArchivesLocked(tileset);
                foreach (var subArchive in subArchives)
                {
                    var found = TryReadCandidatesFromArchive(subArchive, candidates);
                    if (found != null)
                        return found;
                }
            }

            foreach (var archive in _openArchives)
            {
                var found = TryReadCandidatesFromArchive(archive, candidates);
                if (found != null)
                    return found;
            }

            // Fallback via (listfile) case-insensitive exact match (handles MPQ hash locale/case quirks)
            foreach (var (archive, map) in GetOrBuildListfileMapsLocked())
            {
                foreach (var cand in candidates)
                {
                    if (map.TryGetValue(cand, out var actualPath))
                    {
                        var found = TryReadCandidatesFromArchive(archive, new List<string> { actualPath });
                        if (found != null)
                            return found;
                    }
                }
            }
        }

        return null;
    }

    private byte[]? FindIconBytes(string normalizedPath, MapProject? project)
    {
        var candidatePaths = new List<string> { normalizedPath };
        if (normalizedPath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            candidatePaths.Add(normalizedPath.Substring(0, normalizedPath.Length - 4) + ".blp");
        else if (normalizedPath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            candidatePaths.Add(normalizedPath.Substring(0, normalizedPath.Length - 4) + ".tga");

        var fileNameOnly = Path.GetFileName(normalizedPath);
        if (!string.IsNullOrEmpty(fileNameOnly))
        {
            candidatePaths.Add(@"ReplaceableTextures\CommandButtons\" + fileNameOnly);
            candidatePaths.Add(@"war3mapImported\" + fileNameOnly);
        }

        // 1. Check current map MPQ files first
        if (project != null)
        {
            foreach (var cand in candidatePaths)
            {
                if (project.RawMpqFiles.TryGetValue(cand, out var mapBytes))
                    return mapBytes;
            }
        }

        // 2. Check already-opened base Warcraft III 1.27b MPQs (O(1) hash lookup)
        lock (_mpqLock)
        {
            foreach (var archive in _openArchives)
            {
                try
                {
                    foreach (var cand in candidatePaths)
                    {
                        if (archive.FileExists(cand))
                        {
                            using var s = archive.OpenFile(cand);
                            using var ms = new MemoryStream();
                            s.CopyTo(ms);
                            return ms.ToArray();
                        }
                    }
                }
                catch
                {
                    // Continue to next MPQ
                }
            }
        }

        return null;
    }

    public static BitmapSource? DecodeBlpOrTga(byte[] data)
    {
        if (data.Length >= 28 && data[0] == 'B' && data[1] == 'L' && data[2] == 'P' && data[3] == '1')
        {
            return DecodeBlp1(data);
        }
        return DecodeTga(data);
    }

    private static BitmapSource? DecodeBlp1(byte[] blp)
    {
        var compression = BitConverter.ToUInt32(blp, 4); // 0 = JPEG, 1 = Paletted
        var alphaBits = BitConverter.ToUInt32(blp, 8);
        var width = (int)BitConverter.ToUInt32(blp, 12);
        var height = (int)BitConverter.ToUInt32(blp, 16);
        var mip0Offset = (int)BitConverter.ToUInt32(blp, 28);
        var mip0Size = (int)BitConverter.ToUInt32(blp, 92);

        if (width <= 0 || height <= 0 || width > 2048 || height > 2048 || mip0Offset <= 0 || mip0Size <= 0)
            return null;

        if (compression == 0)
        {
            // JPEG BLP1
            if (blp.Length < 160) return null;
            var jpegHeaderSize = (int)BitConverter.ToUInt32(blp, 156);
            if (160 + jpegHeaderSize > blp.Length || mip0Offset + mip0Size > blp.Length)
                return null;

            var fullJpeg = new byte[jpegHeaderSize + mip0Size];
            Buffer.BlockCopy(blp, 160, fullJpeg, 0, jpegHeaderSize);
            Buffer.BlockCopy(blp, mip0Offset, fullJpeg, jpegHeaderSize, mip0Size);

            // In-place convert 18-byte JFIF APP0 (FF E0 00 10 "JFIF"...) into 16-byte Adobe APP14 (FF EE 00 0E "Adobe" ... transform=0)
            // followed by 2 standard 0xFF JPEG fill bytes so WIC sees exact length 14 (0x000E) and skips YCCK conversion!
            for (var m = 2; m + 18 < fullJpeg.Length && m < jpegHeaderSize + 32; m++)
            {
                if (fullJpeg[m] == 0xFF && fullJpeg[m + 1] == 0xE0 && fullJpeg[m + 2] == 0x00 && fullJpeg[m + 3] == 0x10)
                {
                    fullJpeg[m + 1] = 0xEE; // APP14
                    fullJpeg[m + 2] = 0x00;
                    fullJpeg[m + 3] = 0x0E; // Exact 14-byte APP14 length required by WIC
                    fullJpeg[m + 4] = (byte)'A';
                    fullJpeg[m + 5] = (byte)'d';
                    fullJpeg[m + 6] = (byte)'o';
                    fullJpeg[m + 7] = (byte)'b';
                    fullJpeg[m + 8] = (byte)'e';
                    fullJpeg[m + 9] = 0x00;
                    fullJpeg[m + 10] = 0x64; // Version 100
                    fullJpeg[m + 11] = 0x00;
                    fullJpeg[m + 12] = 0x00;
                    fullJpeg[m + 13] = 0x00;
                    fullJpeg[m + 14] = 0x00;
                    fullJpeg[m + 15] = 0x00; // transform = 0 (Raw 4-channel CMYK/BGRA)
                    fullJpeg[m + 16] = 0xFF; // Standard JPEG fill byte before next marker
                    fullJpeg[m + 17] = 0xFF; // Standard JPEG fill byte before next marker
                }
            }

            using var ms = new MemoryStream(fullJpeg);
            var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var w = frame.PixelWidth;
            var h = frame.PixelHeight;
            var stride = w * 4;
            var pixels = new byte[h * stride];

            if (frame.Format.BitsPerPixel == 32)
            {
                // WIC Cmyk32 represents 0%..100% ink as 255..0, so inverting (255 - byte) yields exact raw (B, G, R, A)
                frame.CopyPixels(pixels, stride, 0);
                for (var i = 0; i + 3 < pixels.Length; i += 4)
                {
                    pixels[i] = (byte)(255 - pixels[i]);         // Blue
                    pixels[i + 1] = (byte)(255 - pixels[i + 1]); // Green
                    pixels[i + 2] = (byte)(255 - pixels[i + 2]); // Red
                    pixels[i + 3] = alphaBits == 0 ? (byte)255 : (byte)(255 - pixels[i + 3]); // Alpha
                }
            }
            else
            {
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                converted.CopyPixels(pixels, stride, 0);
                for (var i = 0; i + 3 < pixels.Length; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                    pixels[i + 3] = 255;
                }
            }

            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            return wb;
        }

        if (compression == 1)
        {
            // Paletted 256-color BLP1
            if (blp.Length < 156 + 1024 || mip0Offset + (width * height) > blp.Length)
                return null;

            var outPixels = new byte[width * height * 4];
            var pixelCount = width * height;
            var alphaOffset = mip0Offset + pixelCount;

            for (var i = 0; i < pixelCount; i++)
            {
                var palIdx = blp[mip0Offset + i];
                var palPos = 156 + (palIdx * 4);
                var dstPos = i * 4;

                outPixels[dstPos] = blp[palPos];         // B
                outPixels[dstPos + 1] = blp[palPos + 1]; // G
                outPixels[dstPos + 2] = blp[palPos + 2]; // R

                byte a = 255;
                if (alphaBits == 8 && alphaOffset + i < blp.Length)
                    a = blp[alphaOffset + i];
                else if (alphaBits == 1 && alphaOffset + (i / 8) < blp.Length)
                    a = ((blp[alphaOffset + (i / 8)] >> (i % 8)) & 1) != 0 ? (byte)255 : (byte)0;

                outPixels[dstPos + 3] = a;
            }

            var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, width, height), outPixels, width * 4, 0);
            return wb;
        }

        return null;
    }

    private static BitmapSource? DecodeTga(byte[] tga)
    {
        if (tga.Length < 18) return null;
        var idLen = tga[0];
        var colorMap = tga[1];
        var imgType = tga[2];
        if (colorMap != 0 || imgType != 2) return null;

        var width = BitConverter.ToUInt16(tga, 12);
        var height = BitConverter.ToUInt16(tga, 14);
        var bpp = tga[16];
        var desc = tga[17];
        var flipTop = (desc & 0x20) != 0;
        var bytesPerPx = bpp / 8;
        if (width <= 0 || height <= 0 || (bytesPerPx != 3 && bytesPerPx != 4)) return null;

        var offset = 18 + idLen;
        if (offset + (width * height * bytesPerPx) > tga.Length) return null;

        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var srcRow = flipTop ? y : (height - 1 - y);
            for (var x = 0; x < width; x++)
            {
                var s = offset + (((srcRow * width) + x) * bytesPerPx);
                var d = ((y * width) + x) * 4;
                bgra[d] = tga[s];
                bgra[d + 1] = tga[s + 1];
                bgra[d + 2] = tga[s + 2];
                bgra[d + 3] = bytesPerPx == 4 ? tga[s + 3] : (byte)255;
            }
        }

        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        return wb;
    }

    private static BitmapSource CreateFallbackIcon()
    {
        const int sz = 16;
        var px = new byte[sz * sz * 4];
        for (var y = 0; y < sz; y++)
        {
            for (var x = 0; x < sz; x++)
            {
                var border = x == 0 || y == 0 || x == sz - 1 || y == sz - 1;
                var idx = ((y * sz) + x) * 4;
                px[idx] = border ? (byte)60 : (byte)130;     // B
                px[idx + 1] = border ? (byte)170 : (byte)95; // G
                px[idx + 2] = border ? (byte)215 : (byte)45; // R
                px[idx + 3] = 255;
            }
        }
        var wb = new WriteableBitmap(sz, sz, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, sz, sz), px, sz * 4, 0);
        wb.Freeze();
        return wb;
    }
}
