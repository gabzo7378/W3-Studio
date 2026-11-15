using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using War3Net.Build.Extensions;
using War3Net.Build.Object;
using War3Net.IO.Mpq;
using W3Studio.Models;

namespace W3Studio.Services;

public class MapMpqService
{
    private readonly WarcraftBaseDataService _baseDataService;

    private static readonly string[] StandardMpqFiles =
    {
        "(listfile)", "(attributes)", "(signature)",
        "war3map.j", @"scripts\war3map.j",
        "war3map.wts", "war3map.w3i", "war3map.wtg", "war3map.wct",
        "war3map.w3e", "war3map.shd", "war3map.wpm", "war3map.doo",
        "war3mapUnits.doo", "war3map.w3r", "war3map.w3c", "war3map.w3s",
        "war3map.w3u", "war3map.w3t", "war3map.w3a", "war3map.w3b",
        "war3map.w3d", "war3map.w3q", "war3map.w3h",
        "war3mapMap.blp", "war3mapMap.tga", "war3mapMap.b00",
        "war3mapPreview.tga", "war3mapPreview.blp",
        "war3mapPath.tga", "war3mapMisc.txt", "war3mapSkin.txt",
        "war3mapExtra.txt", "war3mapImp.txt", "war3map.imp",
        "LoadingScreen.mdx", "FullScreen.blp", "FullScreen.tga", "LoadingScreen.blp", "LoadingScreen.tga",
        "LoadingScreenBL.blp", "LoadingScreenBR.blp", "LoadingScreenTR.blp", "LoadingScreenTL.blp",
        "LoadingScreenBL.tga", "LoadingScreenBR.tga", "LoadingScreenTR.tga", "LoadingScreenTL.tga"
    };

    public MapMpqService(WarcraftBaseDataService baseDataService)
    {
        _baseDataService = baseDataService;
    }

    public MapProject LoadMap(string filePath, Action<int, string>? onProgress = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"No se encontró el mapa: {filePath}");

        onProgress?.Invoke(5, "Abriendo archivo MPQ del mapa...");

        var project = new MapProject
        {
            FilePath = filePath
        };

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = MpqArchive.Open(fs, true);

        var discoveredNames = new HashSet<string>(StandardMpqFiles, StringComparer.OrdinalIgnoreCase);

        // 1. Read (listfile)
        onProgress?.Invoke(10, "Leyendo catálogo e imports del MPQ...");
        if (archive.FileExists("(listfile)"))
        {
            try
            {
                using var lfStream = archive.OpenFile("(listfile)");
                using var reader = new StreamReader(lfStream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        discoveredNames.Add(trimmed);
                }
            }
            catch { }
        }

        // 2. Read war3map.imp, war3mapImp.txt & war3mapSkin.txt
        foreach (var impFile in new[] { "war3map.imp", "war3mapImp.txt", "war3mapSkin.txt", "war3mapMisc.txt" })
        {
            if (archive.FileExists(impFile))
            {
                try
                {
                    using var s = archive.OpenFile(impFile);
                    bool isBinaryImp = impFile.Equals("war3map.imp", StringComparison.OrdinalIgnoreCase)
                        || impFile.Equals("war3mapImp.txt", StringComparison.OrdinalIgnoreCase);
                    ReadImportedPathsFromStream(s, isBinaryImp, discoveredNames);
                }
                catch { }
            }
        }

        // 3. Read war3map.wts (TRIGSTR_xxx)
        if (archive.FileExists("war3map.wts"))
        {
            try
            {
                using var wtsStream = archive.OpenFile("war3map.wts");
                using var reader = new StreamReader(wtsStream, Encoding.UTF8);
                ParseWts(reader.ReadToEnd(), project.TriggerStrings);
            }
            catch { }
        }

        // 4. Read war3map.w3i metadata (full MapInfo + fallback header)
        onProgress?.Invoke(16, "Leyendo información del mapa (war3map.w3i / war3map.wts)...");
        if (archive.FileExists("war3map.w3i"))
        {
            try
            {
                using var w3iStream = archive.OpenFile("war3map.w3i");
                using var msW3i = new MemoryStream();
                w3iStream.CopyTo(msW3i);
                var w3iBytes = msW3i.ToArray();
                try
                {
                    using var msInfo = new MemoryStream(w3iBytes);
                    using var brInfo = new BinaryReader(msInfo, Encoding.UTF8, leaveOpen: true);
                    project.MapInfo = brInfo.ReadMapInfo();
                    project.FormatVersion = (int)project.MapInfo.FormatVersion;
                    project.MapName = project.ResolveTrigStr(project.MapInfo.MapName);
                    project.MapAuthor = project.ResolveTrigStr(project.MapInfo.MapAuthor);
                    project.MapDescription = project.ResolveTrigStr(project.MapInfo.MapDescription);
                    project.SuggestedPlayers = project.ResolveTrigStr(project.MapInfo.RecommendedPlayers);
                }
                catch
                {
                    using var msHdr = new MemoryStream(w3iBytes);
                    ParseW3iHeader(msHdr, project);
                }
            }
            catch { }
        }

        // 5. Read war3map.j
        string? jassFile = archive.FileExists("war3map.j") ? "war3map.j"
            : archive.FileExists(@"scripts\war3map.j") ? @"scripts\war3map.j"
            : null;

        if (jassFile != null)
        {
            using var jStream = archive.OpenFile(jassFile);
            using var reader = new StreamReader(jStream, Encoding.UTF8);
            project.RawMapScript = reader.ReadToEnd();
            DiscoverAssetPathsFromText(project.RawMapScript, discoveredNames);
        }

        // 6. Read Object Editor files (.w3u, .w3t, .w3a, .w3h, .w3b, .w3d, .w3q)
        onProgress?.Invoke(26, "Cargando datos de objetos (unidades, ítems, habilidades)...");
        LoadAllObjectData(archive, project, discoveredNames);

        // 7. Read Terrain (war3map.w3e) and Minimap TGA (war3mapMap.tga / war3mapPreview.tga)
        onProgress?.Invoke(38, "Cargando malla de terreno y alturas (war3map.w3e)...");
        if (archive.FileExists("war3map.w3e"))
        {
            try
            {
                using var w3eStream = archive.OpenFile("war3map.w3e");
                using var msW3e = new MemoryStream();
                w3eStream.CopyTo(msW3e);
                var w3eBytes = msW3e.ToArray();
                project.Terrain.RawW3eBytes = w3eBytes;
                using var parseStream = new MemoryStream(w3eBytes);
                ParseW3eTerrain(parseStream, project.Terrain);
            }
            catch { }
        }

        foreach (var tgaName in new[] { "war3mapMap.tga", "war3mapPreview.tga" })
        {
            if (archive.FileExists(tgaName))
            {
                try
                {
                    using var tgaStream = archive.OpenFile(tgaName);
                    using var ms = new MemoryStream();
                    tgaStream.CopyTo(ms);
                    project.Terrain.MinimapTgaBytes = ms.ToArray();
                    break;
                }
                catch { }
            }
        }

        // 8. Read Placed Units (war3mapUnits.doo), Doodads (war3map.doo), and Regions (war3map.w3r)
        onProgress?.Invoke(50, "Cargando unidades, árboles, doodads y regiones...");
        if (archive.FileExists("war3mapUnits.doo"))
        {
            try
            {
                using var uStream = archive.OpenFile("war3mapUnits.doo");
                using var msU = new MemoryStream();
                uStream.CopyTo(msU);
                var uBytes = msU.ToArray();
                try
                {
                    using var msRawU = new MemoryStream(uBytes);
                    using var brRawU = new BinaryReader(msRawU, Encoding.UTF8, leaveOpen: true);
                    project.RawMapUnitsData = brRawU.ReadMapUnits();
                }
                catch { }

                using var msParseU = new MemoryStream(uBytes);
                ParsePlacedUnits(msParseU, project);
            }
            catch { }
        }

        if (archive.FileExists("war3map.doo"))
        {
            try
            {
                using var dStream = archive.OpenFile("war3map.doo");
                using var msD = new MemoryStream();
                dStream.CopyTo(msD);
                var dBytes = msD.ToArray();
                try
                {
                    using var msRawD = new MemoryStream(dBytes);
                    using var brRawD = new BinaryReader(msRawD, Encoding.UTF8, leaveOpen: true);
                    project.RawMapDoodadsData = brRawD.ReadMapDoodads();
                }
                catch { }

                using var msParseD = new MemoryStream(dBytes);
                ParsePlacedDoodads(msParseD, project);
            }
            catch { }
        }

        if (archive.FileExists("war3map.w3r"))
        {
            try
            {
                using var rStream = archive.OpenFile("war3map.w3r");
                ParseRegions(rStream, project);
            }
            catch { }
        }

        // 9. Read WTG Categories + Variables and WCT Custom Text Triggers
        onProgress?.Invoke(62, "Cargando disparadores (triggers), variables y script JASS...");
        var wtgCategories = new List<(int Id, string Name, bool IsComment)>();
        if (archive.FileExists("war3map.wtg"))
        {
            try
            {
                using var wtgStream = archive.OpenFile("war3map.wtg");
                ParseWtgCategoriesAndVariables(wtgStream, wtgCategories, project.GlobalVariables);
            }
            catch { }
        }

        string? wctHeader = null;
        var wctBlocks = new List<string>();
        if (archive.FileExists("war3map.wct"))
        {
            try
            {
                using var wctStream = archive.OpenFile("war3map.wct");
                ParseWct(wctStream, out wctHeader, wctBlocks);
            }
            catch { }
        }

        BuildTriggerHierarchy(project, wtgCategories, wctHeader, wctBlocks);

        // 10. Register all discovered MPQ files into RawMpqFiles and Imports list
        onProgress?.Invoke(70, "Indexando archivos importados en el mapa...");
        archive.AddFileNames(discoveredNames);
        foreach (var name in discoveredNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Equals("(listfile)", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("(attributes)", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("(signature)", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!archive.FileExists(name))
                continue;

            try
            {
                using var stream = archive.OpenFile(name);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles[name] = bytes;

                project.Imports.Add(new W3ImportedFile
                {
                    MpqPath = name,
                    SizeBytes = bytes.Length,
                    Data = bytes,
                    Status = "En MPQ"
                });
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(project.MapName))
            project.MapName = Path.GetFileNameWithoutExtension(filePath);

        return project;
    }

    private static void ParseW3eTerrain(Stream stream, W3TerrainData terrain)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length < 36) return;

        var magic = br.ReadBytes(4); // W3E!
        if (magic[0] != 'W' || magic[1] != '3' || magic[2] != 'E')
            return;

        _ = br.ReadInt32(); // version (11)
        terrain.Tileset = (char)br.ReadByte();
        _ = br.ReadInt32(); // custom tileset flag

        var gtCount = br.ReadInt32();
        for (var i = 0; i < gtCount; i++)
            terrain.GroundTiles.Add(IntToRawcode(br.ReadInt32()));

        var ctCount = br.ReadInt32();
        for (var i = 0; i < ctCount; i++)
            terrain.CliffTiles.Add(IntToRawcode(br.ReadInt32()));

        terrain.GridWidth = br.ReadInt32();
        terrain.GridHeight = br.ReadInt32();
        terrain.OffsetX = br.ReadSingle();
        terrain.OffsetY = br.ReadSingle();

        var w = terrain.GridWidth;
        var h = terrain.GridHeight;
        if (w <= 0 || h <= 0 || w > 1025 || h > 1025)
            return;

        terrain.GroundHeights = new float[w, h];
        terrain.WaterDepths = new float[w, h];
        terrain.TextureIndices = new byte[w, h];
        terrain.CliffLevels = new byte[w, h];
        terrain.WaterFlags = new bool[w, h];
        terrain.RampFlags = new bool[w, h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (stream.Position + 7 > stream.Length)
                    return;

                var gHeightRaw = br.ReadUInt16();
                var waterAndBoundary = br.ReadUInt16();
                var flagsAndTex = br.ReadByte();
                _ = br.ReadByte(); // variation
                var cliffAndLayer = br.ReadByte();

                var layerHeight = cliffAndLayer & 0x0F;
                var realHeight = ((gHeightRaw - 8192f) / 4f) + ((layerHeight - 2) * 128f);
                var waterHeight = (((waterAndBoundary & 0x3FFF) - 8192f) / 4f) - 89.6f;
                var hasWaterFlag = (flagsAndTex & 0x40) != 0;
                var waterDepth = hasWaterFlag ? (waterHeight - realHeight) : -64f;

                terrain.GroundHeights[x, y] = realHeight;
                terrain.WaterDepths[x, y] = waterDepth;
                terrain.TextureIndices[x, y] = (byte)(flagsAndTex & 0x0F);
                terrain.WaterFlags[x, y] = hasWaterFlag && waterDepth > 0f;
                terrain.RampFlags[x, y] = (flagsAndTex & 0x10) != 0;
                terrain.CliffLevels[x, y] = (byte)layerHeight;
            }
        }
    }

    private void ParsePlacedUnits(Stream stream, MapProject project)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length < 16) return;

        _ = br.ReadBytes(4); // W3do
        var version = br.ReadInt32();
        var subVersion = br.ReadInt32();
        var count = br.ReadInt32();

        // Build fast lookup of custom unit names
        var customNames = project.Objects
            .Where(o => o.Category == W3ObjectCategory.Units)
            .GroupBy(o => o.Rawcode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < count && stream.Position + 40 <= stream.Length; i++)
        {
            var rawcode = IntToRawcode(br.ReadInt32());
            var variation = br.ReadInt32();
            var x = br.ReadSingle();
            var y = br.ReadSingle();
            var z = br.ReadSingle();
            var angle = br.ReadSingle();
            var sx = br.ReadSingle();
            var sy = br.ReadSingle();
            var sz = br.ReadSingle();
            var flags = br.ReadByte();
            var player = br.ReadInt32();
            _ = br.ReadByte(); // unk1
            _ = br.ReadByte(); // unk2
            var hp = br.ReadInt32();
            var mp = br.ReadInt32();

            if (subVersion >= 11)
                _ = br.ReadInt32(); // itemTablePtr

            var itemSetsCount = br.ReadInt32();
            for (var s = 0; s < itemSetsCount; s++)
            {
                var itemCount = br.ReadInt32();
                stream.Position += itemCount * 8;
            }

            var goldAmount = br.ReadInt32();
            var targetAcq = br.ReadSingle();
            var heroLevel = br.ReadInt32();
            int heroStr = 0, heroAgi = 0, heroInt = 0;
            if (subVersion >= 11)
            {
                heroStr = br.ReadInt32();
                heroAgi = br.ReadInt32();
                heroInt = br.ReadInt32();
            }

            var invCount = br.ReadInt32();
            stream.Position += invCount * 8;

            var abilCount = br.ReadInt32();
            stream.Position += abilCount * 12;

            var randType = br.ReadInt32();
            if (randType == 0)
                stream.Position += 4;
            else if (randType == 1)
                stream.Position += 8;
            else if (randType == 2)
            {
                var rcnt = br.ReadInt32();
                stream.Position += rcnt * 8;
            }

            _ = br.ReadInt32(); // customColor
            _ = br.ReadInt32(); // waygate
            var creationNum = br.ReadInt32();

            var baseObj = _baseDataService.GetBaseObject(rawcode);
            string unitName;
            if (rawcode == "sloc")
                unitName = $"Posición Inicial Jugador {player + 1}";
            else if (customNames.TryGetValue(rawcode, out var cName) && !string.IsNullOrWhiteSpace(cName))
                unitName = StripColorCodes(cName);
            else
                unitName = baseObj?.Name ?? rawcode;

            bool isItem = baseObj?.Category == W3ObjectCategory.Items;
            bool isBldg = baseObj?.DefaultFields.TryGetValue("ubdg", out var ubdg) == true && (ubdg == "1" || ubdg.Equals("true", StringComparison.OrdinalIgnoreCase));

            project.PlacedUnits.Add(new W3PlacedUnit
            {
                Rawcode = rawcode,
                Name = unitName,
                CreationNumber = creationNum,
                Variation = variation,
                PlayerId = player,
                X = x,
                Y = y,
                Z = z,
                AngleRadians = angle,
                Scale = sx,
                ScaleY = sy,
                ScaleZ = sz,
                Flags = flags,
                HP = hp,
                MP = mp,
                GoldAmount = goldAmount,
                TargetAcquisition = targetAcq,
                Level = heroLevel,
                HeroStrength = heroStr,
                HeroAgility = heroAgi,
                HeroIntelligence = heroInt,
                IsItem = isItem,
                IsBuilding = isBldg
            });
        }
    }

    private void ParsePlacedDoodads(Stream stream, MapProject project)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length < 16) return;

        _ = br.ReadBytes(4); // W3do
        var version = br.ReadInt32();
        var subVersion = br.ReadInt32();
        var count = br.ReadInt32();

        for (var i = 0; i < count && stream.Position + 42 <= stream.Length; i++)
        {
            var rawcode = IntToRawcode(br.ReadInt32());
            var variation = br.ReadInt32();
            var x = br.ReadSingle();
            var y = br.ReadSingle();
            var z = br.ReadSingle();
            var angle = br.ReadSingle();
            var sx = br.ReadSingle();
            var sy = br.ReadSingle();
            var sz = br.ReadSingle();
            var flags = br.ReadByte();
            var life = br.ReadByte();

            if (version >= 8)
            {
                _ = br.ReadInt32(); // itemTablePtr
                var itemSets = br.ReadInt32();
                for (var s = 0; s < itemSets; s++)
                {
                    var icnt = br.ReadInt32();
                    stream.Position += icnt * 8;
                }
            }

            var creationNum = br.ReadInt32();
            var baseObj = _baseDataService.GetBaseObject(rawcode);
            project.PlacedDoodads.Add(new W3PlacedDoodad
            {
                Rawcode = rawcode,
                Name = baseObj?.Name ?? string.Empty,
                CreationNumber = creationNum,
                Variation = variation,
                X = x,
                Y = y,
                Z = z,
                AngleRadians = angle,
                Scale = sx,
                ScaleY = sy,
                ScaleZ = sz,
                Flags = flags,
                Life = life,
                IsDestructible = baseObj?.Category == W3ObjectCategory.Destructables
            });
        }
    }

    private static void ParseRegions(Stream stream, MapProject project)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length < 8) return;

        _ = br.ReadInt32(); // version (5)
        var count = br.ReadInt32();
        for (var i = 0; i < count && stream.Position + 28 <= stream.Length; i++)
        {
            var left = br.ReadSingle();
            var right = br.ReadSingle();
            var bottom = br.ReadSingle();
            var top = br.ReadSingle();
            var name = ReadNullTerminatedString(br);
            var creationNum = br.ReadInt32();
            _ = br.ReadInt32(); // weatherId
            _ = ReadNullTerminatedString(br); // ambientSound
            var b = br.ReadByte();
            var g = br.ReadByte();
            var r = br.ReadByte();
            _ = br.ReadByte(); // alpha

            project.Regions.Add(new W3MapRegion
            {
                Name = name,
                CreationNumber = creationNum,
                Left = Math.Min(left, right),
                Right = Math.Max(left, right),
                Bottom = Math.Min(bottom, top),
                Top = Math.Max(bottom, top),
                ColorR = r,
                ColorG = g,
                ColorB = b
            });
        }
    }

    private static void ParseWtgCategoriesAndVariables(
        Stream stream,
        List<(int Id, string Name, bool IsComment)> categories,
        List<W3GlobalVariable> variables)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length < 16) return;

        var magic = br.ReadBytes(4);
        if (magic[0] != 'W' || magic[1] != 'T' || magic[2] != 'G')
            return;

        var version = br.ReadInt32(); // 7 in TFT 1.27b, 4 in RoC
        var catCount = br.ReadInt32();
        for (var i = 0; i < catCount && stream.Position + 8 <= stream.Length; i++)
        {
            var id = br.ReadInt32();
            var name = ReadNullTerminatedString(br);
            var isComment = version >= 7 && br.ReadInt32() != 0;
            categories.Add((id, name, isComment));
        }

        if (stream.Position + 8 > stream.Length)
            return;

        _ = br.ReadInt32(); // unknown (2)
        var varCount = br.ReadInt32();
        for (var i = 0; i < varCount && stream.Position + 16 <= stream.Length; i++)
        {
            var vName = ReadNullTerminatedString(br);
            var vType = ReadNullTerminatedString(br);
            _ = br.ReadInt32(); // unk
            var isArray = br.ReadInt32() != 0;
            var arraySize = version >= 7 ? br.ReadInt32() : 1;
            var isInit = br.ReadInt32() != 0;
            var initVal = ReadNullTerminatedString(br);

            variables.Add(new W3GlobalVariable
            {
                Name = vName,
                Type = vType,
                IsArray = isArray,
                ArraySize = arraySize,
                InitialValue = isInit ? initVal : string.Empty
            });
        }
    }

    private void ReadImportedPathsFromStream(Stream stream, bool isBinaryImp, HashSet<string> discoveredNames)
    {
        if (isBinaryImp)
        {
            using var br = new BinaryReader(stream, Encoding.UTF8, true);
            if (stream.Length < 8) return;
            _ = br.ReadInt32();
            var count = br.ReadInt32();
            for (var i = 0; i < count && stream.Position < stream.Length; i++)
            {
                var flag = br.ReadByte();
                var path = ReadNullTerminatedString(br);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    discoveredNames.Add(path);
                    if ((flag & 8) == 0 && !path.StartsWith(@"war3mapImported\", StringComparison.OrdinalIgnoreCase))
                        discoveredNames.Add(@"war3mapImported\" + path);
                }
            }
        }
        else
        {
            using var sr = new StreamReader(stream, Encoding.UTF8);
            DiscoverAssetPathsFromText(sr.ReadToEnd(), discoveredNames);
        }
    }

    private static void DiscoverAssetPathsFromText(string text, HashSet<string> discoveredNames)
    {
        var matches = Regex.Matches(text, @"([a-zA-Z0-9_\-\\/\. ]+\.(?:mdx|mdl|blp|tga|mp3|wav|slk|txt|fdf|toc))", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var raw = m.Groups[1].Value.Replace("\\\\", "\\").Trim();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                discoveredNames.Add(raw);
                if (raw.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    discoveredNames.Add(raw.Substring(0, raw.Length - 4) + ".mdx");
            }
        }
    }

    private static void ParseWts(string wtsContent, Dictionary<int, string> dict)
    {
        var regex = new Regex(@"STRING\s+(\d+)\s*(?://[^\r\n]*)?\r?\n\{\r?\n(.*?)\r?\n\}", RegexOptions.Singleline);
        foreach (Match match in regex.Matches(wtsContent))
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
            {
                dict[id] = match.Groups[2].Value;
            }
        }
    }

    private static string SerializeWts(Dictionary<int, string> dict)
    {
        var sb = new StringBuilder();
        foreach (var kvp in dict.OrderBy(k => k.Key))
        {
            sb.Append("STRING ").AppendLine(kvp.Key.ToString());
            sb.AppendLine("{");
            sb.AppendLine(kvp.Value);
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void ParseW3iHeader(Stream stream, MapProject project)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, true);
        project.FormatVersion = br.ReadInt32();
        _ = br.ReadInt32();
        _ = br.ReadInt32();
        if (project.FormatVersion >= 28)
        {
            _ = br.ReadInt32(); _ = br.ReadInt32(); _ = br.ReadInt32(); _ = br.ReadInt32();
        }
        project.MapName = project.ResolveTrigStr(ReadNullTerminatedString(br));
        project.MapAuthor = project.ResolveTrigStr(ReadNullTerminatedString(br));
        project.MapDescription = project.ResolveTrigStr(ReadNullTerminatedString(br));
        project.SuggestedPlayers = project.ResolveTrigStr(ReadNullTerminatedString(br));
    }

    private static void ParseWct(Stream stream, out string? headerCode, List<string> triggerCodes)
    {
        headerCode = null;
        using var br = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length < 8) return;

        var version = br.ReadInt32();
        if (version == 1 || version == 0)
        {
            if (version == 1)
            {
                _ = ReadNullTerminatedString(br);
                var headerLen = br.ReadInt32();
                if (headerLen > 0 && stream.Position + headerLen <= stream.Length)
                {
                    var bytes = br.ReadBytes(headerLen);
                    headerCode = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                }
            }
            if (stream.Position + 4 <= stream.Length)
            {
                var count = br.ReadInt32();
                for (var i = 0; i < count && stream.Position + 4 <= stream.Length; i++)
                {
                    var len = br.ReadInt32();
                    if (len > 0 && stream.Position + len <= stream.Length)
                    {
                        var bytes = br.ReadBytes(len);
                        var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                        if (!string.IsNullOrWhiteSpace(text))
                            triggerCodes.Add(text);
                    }
                }
            }
        }
    }

    private void BuildTriggerHierarchy(
        MapProject project,
        List<(int Id, string Name, bool IsComment)> wtgCategories,
        string? wctHeader,
        List<string> wctBlocks)
    {
        project.TriggerTree.Clear();
        project.AllTriggerNodes.Clear();

        var jass = project.RawMapScript ?? string.Empty;
        var settings = WorkspaceSettings.Load();

        // 1. Root Script (siempre en AllTriggerNodes, visible en TriggerTree solo si ShowCompiledScriptInTriggers está activo)
        var fullScriptNode = new TriggerNode
        {
            Name = LocalizationService.L("war3map.j (Script Compilado Completo)", "war3map.j (Full Compiled Script)"),
            Kind = TriggerNodeKind.RootScript,
            CategoryName = "Mapa",
            Code = jass,
            StartLineInMapJ = 1
        };
        project.AllTriggerNodes.Add(fullScriptNode);
        if (settings.ShowCompiledScriptInTriggers)
        {
            project.TriggerTree.Add(fullScriptNode);
        }

        // 2. Cabecera del Mapa (Custom Script Header)
        if (!string.IsNullOrWhiteSpace(wctHeader))
        {
            var headerNode = new TriggerNode
            {
                Name = LocalizationService.L("Cabecera del Mapa (Custom Script Header)", "Map Header (Custom Script Header)"),
                Kind = TriggerNodeKind.MapHeader,
                CategoryName = "Mapa",
                Code = wctHeader,
                StartLineInMapJ = 1
            };
            project.TriggerTree.Add(headerNode);
            project.AllTriggerNodes.Add(headerNode);
        }

        // 3. Crear carpetas de Categorías WTG directamente en la raíz del árbol (como en World Editor)
        var categoryNodes = new List<TriggerNode>();
        foreach (var c in wtgCategories)
        {
            var catNode = new TriggerNode
            {
                Name = c.Name,
                CategoryId = c.Id,
                Kind = TriggerNodeKind.Category,
                Code = $"// Categoría: {c.Name}"
            };
            categoryNodes.Add(catNode);
            project.TriggerTree.Add(catNode);
        }

        TriggerNode GetOrCreateDefaultCategory()
        {
            if (categoryNodes.Count > 0)
                return categoryNodes[0];

            var defCat = new TriggerNode
            {
                Name = "Initialization",
                CategoryId = 0,
                Kind = TriggerNodeKind.Category
            };
            categoryNodes.Add(defCat);
            project.TriggerTree.Add(defCat);
            return defCat;
        }

        // Detectar qué triggers se ejecutan en RunInitializationTriggers (Map Initialization)
        var mapInitTriggerVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runInitMatch = Regex.Match(jass, @"function\s+RunInitializationTriggers\s+takes\s+nothing\s+returns\s+nothing(.*?)endfunction", RegexOptions.Singleline);
        if (runInitMatch.Success)
        {
            foreach (Match mInit in Regex.Matches(runInitMatch.Groups[1].Value, @"gg_trg_[A-Za-z0-9_]+"))
            {
                mapInitTriggerVars.Add(mInit.Value);
            }
        }

        // 4. Extraer cada Trigger completo de war3map.j (sin cortar en //===== intermedios)
        var guiMatches = Regex.Matches(
            jass,
            @"// Trigger:\s*([^\r\n]+)\r?\n(.*?)(?=(?:\r?\n//={10,})?\r?\n// Trigger:|\r?\n//\*{10,}|\r?\n(?://={10,}\r?\n)?function\s+InitCustomTriggers|\r?\n(?://={10,}\r?\n)?function\s+main\b|$)",
            RegexOptions.Singleline);

        foreach (Match m in guiMatches)
        {
            var trigName = m.Groups[1].Value.Trim();
            var rawBody = m.Groups[2].Value.Trim();
            var fullBlock = $"// Trigger: {trigName}\r\n{rawBody}";
            var cleanId = Regex.Replace(trigName, @"[^A-Za-z0-9_]", "_");
            var ggVarName = $"gg_trg_{cleanId}";

            // Extraer comentario inicial debajo de // Trigger: <Name>
            var commentLines = new List<string>();
            using (var sr = new StringReader(rawBody))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("//===") || trimmed.StartsWith("function ") || trimmed.StartsWith("globals"))
                        break;
                    if (trimmed.StartsWith("//"))
                    {
                        var cText = trimmed.Length > 2 ? trimmed[2..].Trim() : string.Empty;
                        if (!string.IsNullOrEmpty(cText))
                            commentLines.Add(cText);
                    }
                }
            }

            bool isGuiTrigger = Regex.IsMatch(rawBody, $@"function\s+InitTrig_{Regex.Escape(cleanId)}\b") &&
                                rawBody.Contains("CreateTrigger", StringComparison.Ordinal);

            var targetCat = ChooseBestCategoryForTrigger(trigName, categoryNodes) ?? GetOrCreateDefaultCategory();

            var node = new TriggerNode
            {
                Name = trigName,
                Kind = isGuiTrigger ? TriggerNodeKind.GuiTrigger : TriggerNodeKind.CustomTextTrigger,
                IsCustomText = !isGuiTrigger,
                CategoryId = targetCat.CategoryId,
                CategoryName = targetCat.Name,
                Comment = string.Join(Environment.NewLine, commentLines),
                Code = fullBlock,
                StartLineInMapJ = GetLineNumber(jass, m.Index),
                IsEnabled = !rawBody.Contains($"DisableTrigger( {ggVarName} )", StringComparison.Ordinal) &&
                            !rawBody.Contains($"DisableTrigger({ggVarName})", StringComparison.Ordinal),
                IsInitiallyOn = true
            };

            if (isGuiTrigger)
            {
                PopulateGuiElementsFromTriggerJass(node, rawBody, cleanId, mapInitTriggerVars.Contains(ggVarName));
            }

            targetCat.Children.Add(node);
            project.AllTriggerNodes.Add(node);
        }

        // 5. Bloques vJASS / Custom Text adicionales en war3map.wct que no estén ya en el árbol
        if (wctBlocks.Count > 0)
        {
            TriggerNode? wctFolder = null;
            var idx = 1;
            foreach (var block in wctBlocks)
            {
                var nameMatch = Regex.Match(block, @"(?:library|scope)\s+([A-Za-z0-9_]+)|function\s+InitTrig_([A-Za-z0-9_]+)");
                var blockTitle = nameMatch.Success
                    ? (!string.IsNullOrEmpty(nameMatch.Groups[1].Value) ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value)
                    : $"vJASS_Script_{idx}";

                var normalizedTitle = blockTitle.Replace('_', ' ');
                if (project.AllTriggerNodes.Any(n =>
                    n.Name.Equals(blockTitle, StringComparison.OrdinalIgnoreCase) ||
                    n.Name.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    idx++;
                    continue;
                }

                wctFolder ??= new TriggerNode
                {
                    Name = "Scripts vJASS (war3map.wct)",
                    Kind = TriggerNodeKind.Category
                };

                var node = new TriggerNode
                {
                    Name = blockTitle,
                    Kind = TriggerNodeKind.LibraryOrScope,
                    CategoryName = wctFolder.Name,
                    Code = block,
                    StartLineInMapJ = 1
                };
                wctFolder.Children.Add(node);
                project.AllTriggerNodes.Add(node);
                idx++;
            }

            if (wctFolder != null && wctFolder.Children.Count > 0)
            {
                project.TriggerTree.Add(wctFolder);
            }
        }
    }

    public static void RefreshTriggerTreeAdvancedVisibility(MapProject? project, bool showCompiledScript)
    {
        if (project == null) return;
        var rootScript = project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
        if (rootScript == null) return;

        bool currentlyInTree = project.TriggerTree.Contains(rootScript);
        if (showCompiledScript && !currentlyInTree)
        {
            project.TriggerTree.Insert(0, rootScript);
        }
        else if (!showCompiledScript && currentlyInTree)
        {
            project.TriggerTree.Remove(rootScript);
        }
    }

    private static TriggerNode? ChooseBestCategoryForTrigger(string trigName, List<TriggerNode> categories)
    {
        if (categories.Count == 0) return null;
        if (categories.Count == 1) return categories[0];

        foreach (var cat in categories)
        {
            if (trigName.Contains(cat.Name, StringComparison.OrdinalIgnoreCase))
                return cat;
        }
        return categories[0];
    }

    private static void PopulateGuiElementsFromTriggerJass(
        TriggerNode node,
        string rawBody,
        string cleanId,
        bool isMapInit)
    {
        // 1. Eventos (Events)
        if (isMapInit)
        {
            node.Events.Add(new GuiTriggerElement
            {
                ElementType = GuiTriggerElementType.Event,
                Category = "Map",
                FunctionName = "MapInitialization",
                DisplayText = LocalizationService.L("Map Initialization (Al iniciar el mapa)", "Map Initialization"),
                IsEnabled = true
            });
        }

        var initMatch = Regex.Match(
            rawBody,
            $@"function\s+InitTrig_{Regex.Escape(cleanId)}\s+takes\s+nothing\s+returns\s+nothing(.*?)endfunction",
            RegexOptions.Singleline);

        if (initMatch.Success)
        {
            using var sr = new StringReader(initMatch.Groups[1].Value);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("call TriggerRegister", StringComparison.Ordinal))
                    continue;

                var ev = CreateGuiEventFromJassLine(trimmed);
                node.Events.Add(ev);
            }
        }

        // 2. Condiciones (Conditions)
        var condMatch = Regex.Match(
            rawBody,
            $@"function\s+Trig_{Regex.Escape(cleanId)}_Conditions\s+takes\s+nothing\s+returns\s+boolean(.*?)endfunction",
            RegexOptions.Singleline);

        if (condMatch.Success)
        {
            var condBody = condMatch.Groups[1].Value;
            foreach (Match ifMatch in Regex.Matches(condBody, @"if\s*\((.*?)\)\s*then\s*\r?\n\s*return\s+false\s*\r?\n\s*endif", RegexOptions.Singleline))
            {
                var rawIf = ifMatch.Value.Trim();
                var expr = ifMatch.Groups[1].Value.Trim();
                node.Conditions.Add(new GuiTriggerElement
                {
                    ElementType = GuiTriggerElementType.Condition,
                    Category = "Condition",
                    FunctionName = "CustomScript",
                    DisplayText = FormatJassConditionForGui(expr),
                    IsEnabled = true,
                    Parameters = { ["RawJass"] = rawIf, ["Script"] = expr }
                });
            }
        }

        // 3. Acciones (Actions)
        var actMatch = Regex.Match(
            rawBody,
            $@"function\s+Trig_{Regex.Escape(cleanId)}_Actions\s+takes\s+nothing\s+returns\s+nothing(.*?)endfunction",
            RegexOptions.Singleline);

        if (actMatch.Success)
        {
            using var sr = new StringReader(actMatch.Groups[1].Value);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
                    continue;

                node.Actions.Add(CreateGuiActionFromJassLine(trimmed));
            }
        }
    }

    private static GuiTriggerElement CreateGuiEventFromJassLine(string jassLine)
    {
        string category = "Event";
        string funcName = "CustomEventScript";
        string display = jassLine;

        if (jassLine.Contains("TriggerRegisterTimerEventPeriodic", StringComparison.Ordinal))
        {
            category = "Time";
            funcName = "PeriodicTimer";
            var m = Regex.Match(jassLine, @",\s*([0-9.]+)\s*\)");
            var sec = m.Success ? m.Groups[1].Value : "1.00";
            display = LocalizationService.L($"Tiempo - Cada {sec} segundos de juego", $"Time - Every {sec} seconds of game time");
        }
        else if (jassLine.Contains("TriggerRegisterTimerEvent", StringComparison.Ordinal))
        {
            category = "Time";
            funcName = "ElapsedTime";
            var m = Regex.Match(jassLine, @",\s*([0-9.]+)\s*,");
            var sec = m.Success ? m.Groups[1].Value : "0.00";
            display = LocalizationService.L($"Tiempo - Tiempo transcurrido es {sec} segundos", $"Time - Elapsed game time is {sec} seconds");
        }
        else if (jassLine.Contains("EVENT_PLAYER_UNIT_DEATH", StringComparison.Ordinal))
        {
            category = "Unit";
            funcName = "UnitDies";
            display = LocalizationService.L("Unidad - Una unidad muere", "Unit - A unit Dies");
        }
        else if (jassLine.Contains("EVENT_PLAYER_UNIT_ATTACKED", StringComparison.Ordinal))
        {
            category = "Unit";
            funcName = "UnitAttacked";
            display = LocalizationService.L("Unidad - Una unidad es atacada", "Unit - A unit Is attacked");
        }
        else if (jassLine.Contains("EVENT_PLAYER_UNIT_SPELL_EFFECT", StringComparison.Ordinal))
        {
            category = "Unit";
            funcName = "UnitSpell";
            display = LocalizationService.L("Unidad - Una unidad inicia el efecto de una habilidad", "Unit - A unit Starts the effect of an ability");
        }
        else if (jassLine.Contains("TriggerRegisterEnterRectSimple", StringComparison.Ordinal))
        {
            category = "Unit";
            funcName = "UnitEntersRegion";
            display = LocalizationService.L($"Unidad - Una unidad entra en región ({ExtractLastIdentifier(jassLine)})", $"Unit - A unit enters Region ({ExtractLastIdentifier(jassLine)})");
        }
        else if (jassLine.Contains("TriggerRegisterPlayerChatEvent", StringComparison.Ordinal))
        {
            category = "Player";
            funcName = "PlayerChat";
            display = LocalizationService.L($"Jugador - Mensaje de chat ({jassLine})", $"Player - Chat message ({jassLine})");
        }

        return new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Event,
            Category = category,
            FunctionName = funcName,
            DisplayText = display,
            IsEnabled = true,
            Parameters = { ["RawJass"] = jassLine, ["Code"] = jassLine }
        };
    }

    private static string FormatJassConditionForGui(string expr)
    {
        if (expr.StartsWith("not (", StringComparison.Ordinal) && expr.EndsWith(")"))
            expr = expr[5..^1].Trim();
        return expr;
    }

    private static GuiTriggerElement CreateGuiActionFromJassLine(string jassLine)
    {
        string category = "Action";
        string display = jassLine;

        if (jassLine.Contains("MeleeStartingVisibility", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Usar ciclo de día/noche melee (para todos los jugadores)", "Melee Game - Use melee time of day (for all players)");
        }
        else if (jassLine.Contains("MeleeStartingHeroLimit", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Limitar Héroes a 1 por tipo de Héroe (para todos los jugadores)", "Melee Game - Limit Heroes to 1 per Hero-type (for all players)");
        }
        else if (jassLine.Contains("MeleeGrantHeroItems", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Entregar pergamino de teletransporte inicial a Héroes (para todos los jugadores)", "Melee Game - Give heroicUnits starting items (for all players)");
        }
        else if (jassLine.Contains("MeleeStartingResources", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Establecer recursos iniciales (para todos los jugadores)", "Melee Game - Set starting resources (for all players)");
        }
        else if (jassLine.Contains("MeleeClearExcessUnits", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Eliminar creeps y criaturas cerca de posiciones iniciales (para todos los jugadores)", "Melee Game - Remove creeps and critters from used start locations (for all players)");
        }
        else if (jassLine.Contains("MeleeStartingUnits", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Crear unidades iniciales (para todos los jugadores)", "Melee Game - Create starting units (for all players)");
        }
        else if (jassLine.Contains("MeleeStartingAI", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Ejecutar scripts de IA melee (para jugadores de computadora)", "Melee Game - Run melee AI scripts (for all computer players)");
        }
        else if (jassLine.Contains("MeleeInitVictoryDefeat", StringComparison.Ordinal))
        {
            category = "Melee Game";
            display = LocalizationService.L("Partida Melee - Imponer condiciones de victoria/derrota melee (para todos los jugadores)", "Melee Game - Enforce victory/defeat conditions (for all players)");
        }
        else if (jassLine.StartsWith("call ", StringComparison.Ordinal))
        {
            var callExpr = jassLine[5..].Trim();
            var paren = callExpr.IndexOf('(');
            var fn = paren > 0 ? callExpr[..paren].Trim() : callExpr;
            category = GuessGuiCategoryFromFunction(fn);
            display = $"{category} - {callExpr}";
        }
        else if (jassLine.StartsWith("set ", StringComparison.Ordinal))
        {
            category = "Set Variable";
            display = $"Set {jassLine[4..].Trim()}";
        }

        return new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Action,
            Category = category,
            FunctionName = "CustomScript",
            DisplayText = display,
            IsEnabled = true,
            Parameters = { ["RawJass"] = jassLine, ["Code"] = jassLine }
        };
    }

    private static string GuessGuiCategoryFromFunction(string fn)
    {
        if (fn.Contains("Unit", StringComparison.OrdinalIgnoreCase)) return "Unit";
        if (fn.Contains("Hero", StringComparison.OrdinalIgnoreCase)) return "Hero";
        if (fn.Contains("Player", StringComparison.OrdinalIgnoreCase)) return "Player";
        if (fn.Contains("Item", StringComparison.OrdinalIgnoreCase)) return "Item";
        if (fn.Contains("Effect", StringComparison.OrdinalIgnoreCase)) return "Special Effect";
        if (fn.Contains("Sound", StringComparison.OrdinalIgnoreCase)) return "Sound";
        if (fn.Contains("Camera", StringComparison.OrdinalIgnoreCase)) return "Camera";
        if (fn.Contains("Trigger", StringComparison.OrdinalIgnoreCase)) return "Trigger";
        if (fn.Contains("Display", StringComparison.OrdinalIgnoreCase) || fn.Contains("Text", StringComparison.OrdinalIgnoreCase)) return "Game";
        return "Action";
    }

    private static string ExtractLastIdentifier(string line)
    {
        var m = Regex.Match(line, @"([A-Za-z0-9_]+)\s*\)\s*$");
        return m.Success ? m.Groups[1].Value : line;
    }

    private static int GetLineNumber(string text, int charIndex)
    {
        var line = 1;
        for (var i = 0; i < charIndex && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }

    private static readonly PropertyInfo? SanityCheckProperty =
        typeof(ObjectDataModification).GetProperty("SanityCheck", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static void SetSanityCheck(ObjectDataModification mod, int sanityValue)
    {
        SanityCheckProperty?.SetValue(mod, sanityValue);
    }

    private static object ParseFieldValue(W3ObjectField f)
    {
        var strVal = f.ResolvedValue ?? f.RawValue ?? string.Empty;
        switch (f.DataType)
        {
            case ObjectDataType.Int:
                if (int.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                    return iv;
                if (int.TryParse(f.RawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv2))
                    return iv2;
                return 0;

            case ObjectDataType.Real:
            case ObjectDataType.Unreal:
                var clean = strVal.Replace(',', '.');
                if (float.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                    return fv;
                var cleanRaw = (f.RawValue ?? "0").Replace(',', '.');
                if (float.TryParse(cleanRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv2))
                    return fv2;
                return 0f;

            default:
                return f.IsModified ? strVal : (f.RawValue ?? strVal);
        }
    }

    private void LoadAllObjectData(MpqArchive archive, MapProject project, HashSet<string> discoveredNames)
    {
        // 1. Units (.w3u)
        if (archive.FileExists("war3map.w3u"))
        {
            try
            {
                using var s = archive.OpenFile("war3map.w3u");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles["war3map.w3u"] = bytes;

                using var br = new BinaryReader(new MemoryStream(bytes));
                project.UnitData = br.ReadUnitObjectData();
                PopulateSimpleObjectData(project, W3ObjectCategory.Units, project.UnitData.BaseUnits, project.UnitData.NewUnits, discoveredNames);
            }
            catch { }
        }

        // 2. Items (.w3t)
        if (archive.FileExists("war3map.w3t"))
        {
            try
            {
                using var s = archive.OpenFile("war3map.w3t");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles["war3map.w3t"] = bytes;

                using var br = new BinaryReader(new MemoryStream(bytes));
                project.ItemData = br.ReadItemObjectData();
                PopulateSimpleObjectData(project, W3ObjectCategory.Items, project.ItemData.BaseItems, project.ItemData.NewItems, discoveredNames);
            }
            catch { }
        }

        // 3. Abilities (.w3a)
        if (archive.FileExists("war3map.w3a"))
        {
            try
            {
                using var s = archive.OpenFile("war3map.w3a");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles["war3map.w3a"] = bytes;

                using var br = new BinaryReader(new MemoryStream(bytes));
                project.AbilityData = br.ReadAbilityObjectData();
                PopulateLevelObjectData(project, W3ObjectCategory.Abilities, project.AbilityData.BaseAbilities, project.AbilityData.NewAbilities, discoveredNames);
            }
            catch { }
        }

        // 4. Buffs (.w3h)
        if (archive.FileExists("war3map.w3h"))
        {
            try
            {
                using var s = archive.OpenFile("war3map.w3h");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles["war3map.w3h"] = bytes;

                using var br = new BinaryReader(new MemoryStream(bytes));
                project.BuffData = br.ReadBuffObjectData();
                PopulateSimpleObjectData(project, W3ObjectCategory.Buffs, project.BuffData.BaseBuffs, project.BuffData.NewBuffs, discoveredNames);
            }
            catch { }
        }

        // 5. Destructables (.w3b)
        if (archive.FileExists("war3map.w3b"))
        {
            try
            {
                using var s = archive.OpenFile("war3map.w3b");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles["war3map.w3b"] = bytes;

                using var br = new BinaryReader(new MemoryStream(bytes));
                project.DestructableData = br.ReadDestructableObjectData();
                PopulateSimpleObjectData(project, W3ObjectCategory.Destructables, project.DestructableData.BaseDestructables, project.DestructableData.NewDestructables, discoveredNames);
            }
            catch { }
        }

        // 6. Doodads (.w3d)
        if (archive.FileExists("war3map.w3d"))
        {
            try
            {
                using var s = archive.OpenFile("war3map.w3d");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles["war3map.w3d"] = bytes;

                using var br = new BinaryReader(new MemoryStream(bytes));
                project.DoodadData = br.ReadDoodadObjectData();
                PopulateVariationObjectData(project, W3ObjectCategory.Doodads, project.DoodadData.BaseDoodads, project.DoodadData.NewDoodads, discoveredNames);
            }
            catch { }
        }

        // 7. Upgrades (.w3q)
        if (archive.FileExists("war3map.w3q"))
        {
            try
            {
                using var s = archive.OpenFile("war3map.w3q");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                project.RawMpqFiles["war3map.w3q"] = bytes;

                using var br = new BinaryReader(new MemoryStream(bytes));
                project.UpgradeData = br.ReadUpgradeObjectData();
                PopulateLevelObjectData(project, W3ObjectCategory.Upgrades, project.UpgradeData.BaseUpgrades, project.UpgradeData.NewUpgrades, discoveredNames);
            }
            catch { }
        }
    }

    private void PopulateSimpleObjectData(
        MapProject project,
        W3ObjectCategory category,
        IEnumerable<SimpleObjectModification> baseMods,
        IEnumerable<SimpleObjectModification> customMods,
        HashSet<string> discoveredNames)
    {
        foreach (var mod in baseMods)
            AddSimpleEntry(project, category, mod, isCustom: false, discoveredNames);
        foreach (var mod in customMods)
            AddSimpleEntry(project, category, mod, isCustom: true, discoveredNames);
    }

    private void AddSimpleEntry(
        MapProject project,
        W3ObjectCategory category,
        SimpleObjectModification mod,
        bool isCustom,
        HashSet<string> discoveredNames)
    {
        var baseRaw = IntToRawcode(mod.OldId);
        var customRaw = isCustom && mod.NewId != 0 ? IntToRawcode(mod.NewId) : baseRaw;
        var baseMeta = _baseDataService.GetBaseObject(baseRaw);

        var entry = new W3ObjectEntry
        {
            Category = category,
            SubcategoryFolder = baseMeta?.SubcategoryFolder ?? "General",
            OldId = mod.OldId,
            NewId = mod.NewId,
            BaseRawcode = baseRaw,
            Rawcode = customRaw,
            Name = baseMeta?.Name ?? customRaw,
            TooltipBasic = baseMeta?.Tooltip ?? string.Empty,
            TooltipExtended = baseMeta?.Ubertip ?? string.Empty,
            Hotkey = baseMeta?.Hotkey ?? string.Empty,
            IconPath = baseMeta?.Art ?? string.Empty,
            ButtonPosX = baseMeta?.ButtonPosX ?? 0,
            ButtonPosY = baseMeta?.ButtonPosY ?? 0
        };

        foreach (var m in mod.Modifications)
        {
            var fieldCode = IntToRawcode(m.Id);
            string rawValue = m.Type switch
            {
                ObjectDataType.Int => m.ValueAsInt.ToString(CultureInfo.InvariantCulture),
                ObjectDataType.Real or ObjectDataType.Unreal => m.ValueAsFloat.ToString("0.####", CultureInfo.InvariantCulture),
                _ => m.ValueAsString ?? string.Empty
            };
            var resolvedVal = project.ResolveTrigStr(rawValue);
            if (m.Type == ObjectDataType.String)
                DiscoverAssetPathsFromText(resolvedVal, discoveredNames);

            var field = new W3ObjectField
            {
                FieldId = fieldCode,
                FieldIntId = m.Id,
                FieldName = _baseDataService.GetFieldDisplayName(fieldCode),
                Level = 0,
                Variation = 0,
                DataPointer = 0,
                DataType = m.Type,
                RawValue = rawValue,
                ResolvedValue = resolvedVal,
                IsModified = false,
                ValueUpdatedCallback = _ =>
                {
                    entry.IsModified = true;
                    entry.RefreshSummaryFromFields();
                }
            };
            entry.Fields.Add(field);
        }

        entry.RefreshSummaryFromFields();
        project.Objects.Add(entry);
    }

    private void PopulateLevelObjectData(
        MapProject project,
        W3ObjectCategory category,
        IEnumerable<LevelObjectModification> baseMods,
        IEnumerable<LevelObjectModification> customMods,
        HashSet<string> discoveredNames)
    {
        foreach (var mod in baseMods)
            AddLevelEntry(project, category, mod, isCustom: false, discoveredNames);
        foreach (var mod in customMods)
            AddLevelEntry(project, category, mod, isCustom: true, discoveredNames);
    }

    private void AddLevelEntry(
        MapProject project,
        W3ObjectCategory category,
        LevelObjectModification mod,
        bool isCustom,
        HashSet<string> discoveredNames)
    {
        var baseRaw = IntToRawcode(mod.OldId);
        var customRaw = isCustom && mod.NewId != 0 ? IntToRawcode(mod.NewId) : baseRaw;
        var baseMeta = _baseDataService.GetBaseObject(baseRaw);

        var entry = new W3ObjectEntry
        {
            Category = category,
            SubcategoryFolder = baseMeta?.SubcategoryFolder ?? "General",
            OldId = mod.OldId,
            NewId = mod.NewId,
            BaseRawcode = baseRaw,
            Rawcode = customRaw,
            Name = baseMeta?.Name ?? customRaw,
            TooltipBasic = baseMeta?.Tooltip ?? string.Empty,
            TooltipExtended = baseMeta?.Ubertip ?? string.Empty,
            Hotkey = baseMeta?.Hotkey ?? string.Empty,
            IconPath = baseMeta?.Art ?? string.Empty,
            ButtonPosX = baseMeta?.ButtonPosX ?? 0,
            ButtonPosY = baseMeta?.ButtonPosY ?? 0
        };

        foreach (var m in mod.Modifications)
        {
            var fieldCode = IntToRawcode(m.Id);
            string rawValue = m.Type switch
            {
                ObjectDataType.Int => m.ValueAsInt.ToString(CultureInfo.InvariantCulture),
                ObjectDataType.Real or ObjectDataType.Unreal => m.ValueAsFloat.ToString("0.####", CultureInfo.InvariantCulture),
                _ => m.ValueAsString ?? string.Empty
            };
            var resolvedVal = project.ResolveTrigStr(rawValue);
            if (m.Type == ObjectDataType.String)
                DiscoverAssetPathsFromText(resolvedVal, discoveredNames);

            var field = new W3ObjectField
            {
                FieldId = fieldCode,
                FieldIntId = m.Id,
                FieldName = _baseDataService.GetFieldDisplayName(fieldCode),
                Level = m.Level,
                Variation = 0,
                DataPointer = m.Pointer,
                DataType = m.Type,
                RawValue = rawValue,
                ResolvedValue = resolvedVal,
                IsModified = false,
                ValueUpdatedCallback = _ =>
                {
                    entry.IsModified = true;
                    entry.RefreshSummaryFromFields();
                }
            };
            entry.Fields.Add(field);
        }

        entry.RefreshSummaryFromFields();
        project.Objects.Add(entry);
    }

    private void PopulateVariationObjectData(
        MapProject project,
        W3ObjectCategory category,
        IEnumerable<VariationObjectModification> baseMods,
        IEnumerable<VariationObjectModification> customMods,
        HashSet<string> discoveredNames)
    {
        foreach (var mod in baseMods)
            AddVariationEntry(project, category, mod, isCustom: false, discoveredNames);
        foreach (var mod in customMods)
            AddVariationEntry(project, category, mod, isCustom: true, discoveredNames);
    }

    private void AddVariationEntry(
        MapProject project,
        W3ObjectCategory category,
        VariationObjectModification mod,
        bool isCustom,
        HashSet<string> discoveredNames)
    {
        var baseRaw = IntToRawcode(mod.OldId);
        var customRaw = isCustom && mod.NewId != 0 ? IntToRawcode(mod.NewId) : baseRaw;
        var baseMeta = _baseDataService.GetBaseObject(baseRaw);

        var entry = new W3ObjectEntry
        {
            Category = category,
            SubcategoryFolder = baseMeta?.SubcategoryFolder ?? "General",
            OldId = mod.OldId,
            NewId = mod.NewId,
            BaseRawcode = baseRaw,
            Rawcode = customRaw,
            Name = baseMeta?.Name ?? customRaw,
            TooltipBasic = baseMeta?.Tooltip ?? string.Empty,
            TooltipExtended = baseMeta?.Ubertip ?? string.Empty,
            Hotkey = baseMeta?.Hotkey ?? string.Empty,
            IconPath = baseMeta?.Art ?? string.Empty,
            ButtonPosX = baseMeta?.ButtonPosX ?? 0,
            ButtonPosY = baseMeta?.ButtonPosY ?? 0
        };

        foreach (var m in mod.Modifications)
        {
            var fieldCode = IntToRawcode(m.Id);
            string rawValue = m.Type switch
            {
                ObjectDataType.Int => m.ValueAsInt.ToString(CultureInfo.InvariantCulture),
                ObjectDataType.Real or ObjectDataType.Unreal => m.ValueAsFloat.ToString("0.####", CultureInfo.InvariantCulture),
                _ => m.ValueAsString ?? string.Empty
            };
            var resolvedVal = project.ResolveTrigStr(rawValue);
            if (m.Type == ObjectDataType.String)
                DiscoverAssetPathsFromText(resolvedVal, discoveredNames);

            var field = new W3ObjectField
            {
                FieldId = fieldCode,
                FieldIntId = m.Id,
                FieldName = _baseDataService.GetFieldDisplayName(fieldCode),
                Level = 0,
                Variation = m.Variation,
                DataPointer = m.Pointer,
                DataType = m.Type,
                RawValue = rawValue,
                ResolvedValue = resolvedVal,
                IsModified = false,
                ValueUpdatedCallback = _ =>
                {
                    entry.IsModified = true;
                    entry.RefreshSummaryFromFields();
                }
            };
            entry.Fields.Add(field);
        }

        entry.RefreshSummaryFromFields();
        project.Objects.Add(entry);
    }

    private void SynchronizeAndSerializeAllObjects(MapProject project)
    {
        SynchronizeAndSerializeUnits(project);
        SynchronizeAndSerializeItems(project);
        SynchronizeAndSerializeAbilities(project);
        SynchronizeAndSerializeBuffs(project);
        SynchronizeAndSerializeDestructables(project);
        SynchronizeAndSerializeDoodads(project);
        SynchronizeAndSerializeUpgrades(project);
    }

    private void SynchronizeAndSerializeUnits(MapProject project)
    {
        var entries = project.Objects.Where(o => o.Category == W3ObjectCategory.Units).ToList();
        if (project.UnitData == null && !entries.Any(o => o.IsModified))
            return;

        project.UnitData ??= new UnitObjectData(ObjectDataFormatVersion.v2);
        SyncSimpleCategory(entries, project.UnitData.BaseUnits, project.UnitData.NewUnits);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(project.UnitData);
        project.RawMpqFiles["war3map.w3u"] = ms.ToArray();
    }

    private void SynchronizeAndSerializeItems(MapProject project)
    {
        var entries = project.Objects.Where(o => o.Category == W3ObjectCategory.Items).ToList();
        if (project.ItemData == null && !entries.Any(o => o.IsModified))
            return;

        project.ItemData ??= new ItemObjectData(ObjectDataFormatVersion.v2);
        SyncSimpleCategory(entries, project.ItemData.BaseItems, project.ItemData.NewItems);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(project.ItemData);
        project.RawMpqFiles["war3map.w3t"] = ms.ToArray();
    }

    private void SynchronizeAndSerializeBuffs(MapProject project)
    {
        var entries = project.Objects.Where(o => o.Category == W3ObjectCategory.Buffs).ToList();
        if (project.BuffData == null && !entries.Any(o => o.IsModified))
            return;

        project.BuffData ??= new BuffObjectData(ObjectDataFormatVersion.v2);
        SyncSimpleCategory(entries, project.BuffData.BaseBuffs, project.BuffData.NewBuffs);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(project.BuffData);
        project.RawMpqFiles["war3map.w3h"] = ms.ToArray();
    }

    private void SynchronizeAndSerializeDestructables(MapProject project)
    {
        var entries = project.Objects.Where(o => o.Category == W3ObjectCategory.Destructables).ToList();
        if (project.DestructableData == null && !entries.Any(o => o.IsModified))
            return;

        project.DestructableData ??= new DestructableObjectData(ObjectDataFormatVersion.v2);
        SyncSimpleCategory(entries, project.DestructableData.BaseDestructables, project.DestructableData.NewDestructables);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(project.DestructableData);
        project.RawMpqFiles["war3map.w3b"] = ms.ToArray();
    }

    private static void SyncSimpleCategory(
        List<W3ObjectEntry> entries,
        List<SimpleObjectModification> baseList,
        List<SimpleObjectModification> customList)
    {
        foreach (var entry in entries)
        {
            var targetList = entry.IsCustom ? customList : baseList;
            var targetMod = targetList.FirstOrDefault(m =>
                m.OldId == entry.OldId && (!entry.IsCustom || m.NewId == entry.NewId));

            if (targetMod == null)
            {
                targetMod = new SimpleObjectModification { OldId = entry.OldId, NewId = entry.NewId };
                targetList.Add(targetMod);
            }

            foreach (var f in entry.Fields)
            {
                var m = targetMod.Modifications.FirstOrDefault(x => x.Id == f.FieldIntId);
                if (m == null)
                {
                    m = new SimpleObjectDataModification { Id = f.FieldIntId };
                    SetSanityCheck(m, targetMod.NewId != 0 ? targetMod.NewId : targetMod.OldId);
                    targetMod.Modifications.Add(m);
                }
                m.Type = f.DataType;
                m.Value = ParseFieldValue(f);
            }
        }
    }

    private void SynchronizeAndSerializeAbilities(MapProject project)
    {
        var entries = project.Objects.Where(o => o.Category == W3ObjectCategory.Abilities).ToList();
        if (project.AbilityData == null && !entries.Any(o => o.IsModified))
            return;

        project.AbilityData ??= new AbilityObjectData(ObjectDataFormatVersion.v2);
        SyncLevelCategory(entries, project.AbilityData.BaseAbilities, project.AbilityData.NewAbilities);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(project.AbilityData);
        project.RawMpqFiles["war3map.w3a"] = ms.ToArray();
    }

    private void SynchronizeAndSerializeUpgrades(MapProject project)
    {
        var entries = project.Objects.Where(o => o.Category == W3ObjectCategory.Upgrades).ToList();
        if (project.UpgradeData == null && !entries.Any(o => o.IsModified))
            return;

        project.UpgradeData ??= new UpgradeObjectData(ObjectDataFormatVersion.v2);
        SyncLevelCategory(entries, project.UpgradeData.BaseUpgrades, project.UpgradeData.NewUpgrades);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(project.UpgradeData);
        project.RawMpqFiles["war3map.w3q"] = ms.ToArray();
    }

    private static void SyncLevelCategory(
        List<W3ObjectEntry> entries,
        List<LevelObjectModification> baseList,
        List<LevelObjectModification> customList)
    {
        foreach (var entry in entries)
        {
            var targetList = entry.IsCustom ? customList : baseList;
            var targetMod = targetList.FirstOrDefault(m =>
                m.OldId == entry.OldId && (!entry.IsCustom || m.NewId == entry.NewId));

            if (targetMod == null)
            {
                targetMod = new LevelObjectModification { OldId = entry.OldId, NewId = entry.NewId };
                targetList.Add(targetMod);
            }

            foreach (var f in entry.Fields)
            {
                var m = targetMod.Modifications.FirstOrDefault(x =>
                    x.Id == f.FieldIntId && x.Level == f.Level && x.Pointer == f.DataPointer);
                if (m == null)
                {
                    m = new LevelObjectDataModification
                    {
                        Id = f.FieldIntId,
                        Level = f.Level,
                        Pointer = f.DataPointer
                    };
                    SetSanityCheck(m, targetMod.NewId != 0 ? targetMod.NewId : targetMod.OldId);
                    targetMod.Modifications.Add(m);
                }
                m.Type = f.DataType;
                m.Value = ParseFieldValue(f);
            }
        }
    }

    private void SynchronizeAndSerializeDoodads(MapProject project)
    {
        var entries = project.Objects.Where(o => o.Category == W3ObjectCategory.Doodads).ToList();
        if (project.DoodadData == null && !entries.Any(o => o.IsModified))
            return;

        project.DoodadData ??= new DoodadObjectData(ObjectDataFormatVersion.v2);
        foreach (var entry in entries)
        {
            var targetList = entry.IsCustom ? project.DoodadData.NewDoodads : project.DoodadData.BaseDoodads;
            var targetMod = targetList.FirstOrDefault(m =>
                m.OldId == entry.OldId && (!entry.IsCustom || m.NewId == entry.NewId));

            if (targetMod == null)
            {
                targetMod = new VariationObjectModification { OldId = entry.OldId, NewId = entry.NewId };
                targetList.Add(targetMod);
            }

            foreach (var f in entry.Fields)
            {
                var m = targetMod.Modifications.FirstOrDefault(x =>
                    x.Id == f.FieldIntId && x.Variation == f.Variation && x.Pointer == f.DataPointer);
                if (m == null)
                {
                    m = new VariationObjectDataModification
                    {
                        Id = f.FieldIntId,
                        Variation = f.Variation,
                        Pointer = f.DataPointer
                    };
                    SetSanityCheck(m, targetMod.NewId != 0 ? targetMod.NewId : targetMod.OldId);
                    targetMod.Modifications.Add(m);
                }
                m.Type = f.DataType;
                m.Value = ParseFieldValue(f);
            }
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(project.DoodadData);
        project.RawMpqFiles["war3map.w3d"] = ms.ToArray();
    }

    public void SynchronizeTriggerEditIntoScript(MapProject project, TriggerNode editedNode, string oldCode, string newCode)
    {
        if (editedNode.Kind == TriggerNodeKind.RootScript)
        {
            project.RawMapScript = newCode;
            return;
        }

        if (!string.IsNullOrEmpty(oldCode) && project.RawMapScript.Contains(oldCode, StringComparison.Ordinal))
        {
            project.RawMapScript = project.RawMapScript.Replace(oldCode, newCode, StringComparison.Ordinal);
        }

        var root = project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
        if (root != null)
        {
            root.Code = project.RawMapScript;
        }
    }

    public void SaveMap(MapProject project, string targetPath, Action<int, string>? onProgress = null)
    {
        onProgress?.Invoke(50, "Sincronizando escenario, unidades y doodads...");
        if (project.HasScenarioEdits)
        {
            MapScriptSyncService.SyncScenarioToW3iAndJass(project);
        }

        if (project.HasUnitDoodadEdits)
        {
            MapScriptSyncService.SyncUnitsAndDoodadsToDooAndJass(project, _baseDataService);
        }

        project.RawMpqFiles["war3map.j"] = Encoding.UTF8.GetBytes(project.RawMapScript);

        if (project.Terrain.RawW3eBytes != null && project.HasTerrainEdits)
        {
            project.RawMpqFiles["war3map.w3e"] = project.Terrain.RawW3eBytes;
        }

        if (project.TriggerStrings.Count > 0 && project.RawMpqFiles.ContainsKey("war3map.wts"))
        {
            project.RawMpqFiles["war3map.wts"] = Encoding.UTF8.GetBytes(SerializeWts(project.TriggerStrings));
        }

        onProgress?.Invoke(65, "Serializando objetos personalizados (Object Editor)...");
        SynchronizeAndSerializeAllObjects(project);

        onProgress?.Invoke(75, "Actualizando archivos importados (war3map.imp)...");
        foreach (var imp in project.Imports)
        {
            if (imp.IsDeleted)
            {
                project.RawMpqFiles.Remove(imp.MpqPath);
            }
            else if (imp.IsPendingWrite && imp.Data != null)
            {
                project.RawMpqFiles[imp.MpqPath] = imp.Data;
                imp.IsPendingWrite = false;
                imp.Status = "Guardado en MPQ";
            }
        }

        SyncBinaryImpFile(project);

        byte[]? hm3wHeader = null;
        if (File.Exists(project.FilePath))
        {
            using var srcFs = new FileStream(project.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (srcFs.Length >= 512)
            {
                var buf = new byte[512];
                _ = srcFs.Read(buf, 0, 512);
                if (buf[0] == 'H' && buf[1] == 'M' && buf[2] == '3' && buf[3] == 'W')
                {
                    hm3wHeader = buf;
                    if (project.MapInfo != null)
                    {
                        UpdateHm3wHeaderBuffer(hm3wHeader, project);
                    }
                }
            }
        }

        onProgress?.Invoke(82, "Creando copia de seguridad (.bak)...");
        if (File.Exists(targetPath))
        {
            var backupPath = targetPath + ".bak";
            File.Copy(targetPath, backupPath, overwrite: true);
        }

        onProgress?.Invoke(88, "Empaquetando archivos en el contenedor MPQ...");
        var mpqFiles = new List<MpqFile>();
        foreach (var kvp in project.RawMpqFiles)
        {
            var memStream = new MemoryStream(kvp.Value);
            var mpqFile = MpqFile.New(memStream, kvp.Key);
            mpqFiles.Add(mpqFile);
        }

        using var outFs = new FileStream(targetPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (hm3wHeader != null)
        {
            outFs.Write(hm3wHeader, 0, hm3wHeader.Length);
        }

        var createOptions = new MpqArchiveCreateOptions
        {
            ListFileCreateMode = MpqFileCreateMode.Overwrite,
            AttributesCreateMode = MpqFileCreateMode.None,
            BlockSize = 3
        };

        ushort hashSize = 1024;
        while (hashSize < mpqFiles.Count * 2 && hashSize < 32768)
            hashSize <<= 1;
        createOptions.HashTableSize = hashSize;

        using var createdArchive = MpqArchive.Create(outFs, mpqFiles, createOptions, leaveOpen: true);
        onProgress?.Invoke(98, "Finalizando guardado...");

        // Reset dirty flags after successful save
        project.HasTerrainEdits = false;
        project.HasScenarioEdits = false;
        project.HasMiscEdits = false;
        project.HasSkinEdits = false;
        project.HasUnitDoodadEdits = false;
        foreach (var obj in project.Objects)
        {
            obj.IsModified = false;
            foreach (var f in obj.Fields)
                f.IsModified = false;
        }
    }

    private static string IntToRawcode(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (IsPrintableAscii(bytes[0]) && IsPrintableAscii(bytes[1]) && IsPrintableAscii(bytes[2]) && IsPrintableAscii(bytes[3]))
        {
            return Encoding.ASCII.GetString(bytes);
        }
        return value.ToString("X8");
    }

    private static bool IsPrintableAscii(byte b) => b >= 32 && b <= 126;

    private static string StripColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return Regex.Replace(text, @"\|c[0-9a-fA-F]{8}|\|r|\|n", " ").Trim();
    }

    private static string ReadNullTerminatedString(BinaryReader br)
    {
        var bytes = new List<byte>(64);
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            var b = br.ReadByte();
            if (b == 0)
                break;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void WriteNullTerminatedString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(bytes);
        bw.Write((byte)0);
    }

    private static void UpdateHm3wHeaderBuffer(byte[] hm3w, MapProject project)
    {
        if (project.MapInfo == null || hm3w.Length < 512)
            return;

        // Preserve original TRIGSTR_ name in HM3W if the original header used TRIGSTR_, otherwise use MapInfo.MapName
        int pos = 8;
        while (pos < 480 && hm3w[pos] != 0)
            pos++;

        if (pos + 9 < 512)
        {
            int flagsOffset = pos + 1;
            byte[] flagBytes = BitConverter.GetBytes((int)project.MapInfo.MapFlags);
            Buffer.BlockCopy(flagBytes, 0, hm3w, flagsOffset, 4);
        }
    }

    private static readonly HashSet<string> InternalMapMetadataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "(listfile)", "(attributes)", "(signature)",
        "war3map.j", @"scripts\war3map.j",
        "war3map.wts", "war3map.w3i", "war3map.wtg", "war3map.wct",
        "war3map.w3e", "war3map.shd", "war3map.wpm", "war3map.doo",
        "war3mapUnits.doo", "war3map.w3r", "war3map.w3c", "war3map.w3s",
        "war3map.w3u", "war3map.w3t", "war3map.w3a", "war3map.w3b",
        "war3map.w3d", "war3map.w3q", "war3map.w3h",
        "war3mapMap.blp", "war3mapMap.tga", "war3mapMap.b00",
        "war3mapPath.tga", "war3mapMisc.txt", "war3mapSkin.txt",
        "war3mapExtra.txt", "war3mapImp.txt", "war3map.imp"
    };

    private static void SyncBinaryImpFile(MapProject project)
    {
        var impPaths = project.Imports
            .Where(i => !i.IsDeleted && !string.IsNullOrWhiteSpace(i.MpqPath) && !InternalMapMetadataFiles.Contains(i.MpqPath))
            .Select(i => i.MpqPath.Replace('/', '\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (impPaths.Count == 0 && !project.RawMpqFiles.ContainsKey("war3map.imp"))
            return;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(1); // war3map.imp format version 1
        bw.Write(impPaths.Count);
        foreach (var path in impPaths)
        {
            if (path.StartsWith(@"war3mapImported\", StringComparison.OrdinalIgnoreCase))
            {
                bw.Write((byte)5);
                WriteNullTerminatedString(bw, path.Substring(@"war3mapImported\".Length));
            }
            else
            {
                bw.Write((byte)13);
                WriteNullTerminatedString(bw, path);
            }
        }
        bw.Flush();
        project.RawMpqFiles["war3map.imp"] = ms.ToArray();
    }
}
