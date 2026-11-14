using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using War3Net.IO.Mpq;
using W3Studio.Models;

namespace W3Studio.Services;

public sealed class MapOptimizerOptions
{
    // TWEAKS (JASS Script)
    public bool RemoveJassComments { get; set; } = true;
    public bool CompactJassWhitespace { get; set; } = true;
    public bool CompressJassNames { get; set; } = true;
    public bool InlineTrigStrings { get; set; } = true;
    public bool RemoveDebugLines { get; set; } = true;
    public bool ValidateWithPjass { get; set; } = true;

    // REMOVE (Editor-only & Bloat)
    public bool RemoveEditorTriggerFiles { get; set; } = true; // .wtg, .wct, .imp
    public bool RemoveEditorSoundsFile { get; set; } = false;   // .w3s
    public bool RemoveEditorCamerasFile { get; set; } = false;  // .w3c
    public bool RemoveMapPreviewImage { get; set; } = false;    // war3mapPreview.tga
    public bool CleanUnusedWtsStrings { get; set; } = true;     // war3map.wts
    public bool CleanObjectDataStrings { get; set; } = true;    // war3map.w3u/.w3a/etc cleanup

    // MPQ & PROTECTION
    public bool RemoveListfileAndAttributes { get; set; } = false; // Classic WE protection
    public bool RecompressAllMpqFiles { get; set; } = true;
    public ushort MpqBlockSize { get; set; } = 3; // 3 = 4KB sectors (classic), 4 = 8KB
}

public sealed class MapSizeBreakdown
{
    public long TotalSourceBytes { get; set; }
    public int FileCount { get; set; }
    public long JassBytes { get; set; }
    public long EditorOnlyBytes { get; set; }
    public long ObjectDataBytes { get; set; }
    public long TerrainAndDooBytes { get; set; }
    public long ImportsBytes { get; set; }
    public long OtherBytes { get; set; }
}

public sealed class MapOptimizationResult
{
    public bool Success { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public long OriginalFileBytes { get; set; }
    public long OptimizedFileBytes { get; set; }
    public long SavedBytes => Math.Max(0, OriginalFileBytes - OptimizedFileBytes);
    public double SavedPercent => OriginalFileBytes > 0 ? (SavedBytes * 100.0 / OriginalFileBytes) : 0;
    public int FilesBefore { get; set; }
    public int FilesAfter { get; set; }
    public int ObfuscatedIdentifiers { get; set; }
    public int InlinedStrings { get; set; }
    public bool PjassPassed { get; set; }
    public List<string> LogLines { get; } = new();
    public string ErrorMessage { get; set; } = string.Empty;
}

public static class MapOptimizerService
{
    private static readonly HashSet<string> JassKeywords = new(StringComparer.Ordinal)
    {
        "function", "endfunction", "takes", "returns", "nothing", "local", "set", "call",
        "if", "then", "else", "elseif", "endif", "loop", "endloop", "exitwhen", "return",
        "globals", "endglobals", "constant", "array", "native", "type", "extends",
        "true", "false", "null", "and", "or", "not", "debug",
        "code", "handle", "integer", "real", "boolean", "string"
    };

    private static readonly string[] EditorTriggerFiles =
    {
        "war3map.wtg",
        "war3map.wct",
        "war3map.imp"
    };

    private static readonly string[] ObjectDataFiles =
    {
        "war3map.w3u", "war3map.w3t", "war3map.w3b", "war3map.w3d",
        "war3map.w3a", "war3map.w3h", "war3map.w3q"
    };

    public static MapSizeBreakdown AnalyzeMapFile(string mapPath)
    {
        var breakdown = new MapSizeBreakdown();
        if (!File.Exists(mapPath))
            return breakdown;

        breakdown.TotalSourceBytes = new FileInfo(mapPath).Length;
        using var fs = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var mpqOffset = FindMpqOffset(fs);
        fs.Position = mpqOffset;

        using var archive = MpqArchive.Open(fs, false);
        var names = DiscoverArchiveFileNames(archive);
        archive.AddFileNames(names);

        int existingCount = 0;
        foreach (var name in names)
        {
            if (name.StartsWith("(", StringComparison.Ordinal) && name.EndsWith(")", StringComparison.Ordinal))
                continue;
            if (!archive.FileExists(name))
                continue;

            try
            {
                using var s = archive.OpenFile(name);
                long sz = s.Length;
                existingCount++;
                CategorizeSize(name, sz, breakdown);
            }
            catch { }
        }
        breakdown.FileCount = existingCount;

        return breakdown;
    }

    public static MapSizeBreakdown AnalyzeProjectInMemory(MapProject project)
    {
        var breakdown = new MapSizeBreakdown();
        breakdown.FileCount = project.RawMpqFiles.Count;
        if (!string.IsNullOrEmpty(project.FilePath) && File.Exists(project.FilePath))
            breakdown.TotalSourceBytes = new FileInfo(project.FilePath).Length;
        else
            breakdown.TotalSourceBytes = project.RawMpqFiles.Values.Sum(b => (long)b.Length);

        foreach (var kvp in project.RawMpqFiles)
        {
            CategorizeSize(kvp.Key, kvp.Value.Length, breakdown);
        }
        return breakdown;
    }

    private static void CategorizeSize(string name, long size, MapSizeBreakdown b)
    {
        var lower = name.Replace('/', '\\').ToLowerInvariant();
        if (lower is "war3map.j" or @"scripts\war3map.j")
            b.JassBytes += size;
        else if (lower is "war3map.wtg" or "war3map.wct" or "war3map.imp" or "war3map.w3s" or "war3map.w3c" or "war3mappreview.tga")
            b.EditorOnlyBytes += size;
        else if (ObjectDataFiles.Contains(lower))
            b.ObjectDataBytes += size;
        else if (lower is "war3map.w3e" or "war3map.wpm" or "war3map.shd" or "war3map.mmp" or "war3map.doo" or "war3mapunits.doo" or "war3map.w3r")
            b.TerrainAndDooBytes += size;
        else if (lower.EndsWith(".blp") || lower.EndsWith(".tga") || lower.EndsWith(".mdx") || lower.EndsWith(".mdl") || lower.EndsWith(".wav") || lower.EndsWith(".mp3"))
            b.ImportsBytes += size;
        else
            b.OtherBytes += size;
    }

    public static MapOptimizationResult OptimizeMapFile(
        string sourceMapPath,
        string outputMapPath,
        MapOptimizerOptions options,
        WorkspaceSettings settings,
        MapProject? liveProject = null)
    {
        var result = new MapOptimizationResult { OutputPath = outputMapPath };

        try
        {
            if (!File.Exists(sourceMapPath) && liveProject == null)
            {
                result.ErrorMessage = "No se encontró el archivo del mapa origen.";
                return result;
            }

            byte[]? hm3wHeader = null;
            var fileMap = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            if (liveProject != null &&
                !string.IsNullOrEmpty(liveProject.FilePath) &&
                liveProject.FilePath.Equals(sourceMapPath, StringComparison.OrdinalIgnoreCase))
            {
                result.OriginalFileBytes = File.Exists(sourceMapPath) ? new FileInfo(sourceMapPath).Length : liveProject.RawMpqFiles.Values.Sum(v => (long)v.Length);
                foreach (var kvp in liveProject.RawMpqFiles)
                    fileMap[kvp.Key] = (byte[])kvp.Value.Clone();

                if (File.Exists(sourceMapPath))
                    hm3wHeader = ReadHm3wHeader(sourceMapPath);
            }
            else
            {
                result.OriginalFileBytes = new FileInfo(sourceMapPath).Length;
                hm3wHeader = ReadHm3wHeader(sourceMapPath);

                using var fs = new FileStream(sourceMapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var mpqOff = FindMpqOffset(fs);
                fs.Position = mpqOff;
                using var archive = MpqArchive.Open(fs, false);
                var names = DiscoverArchiveFileNames(archive);
                archive.AddFileNames(names);

                foreach (var name in names)
                {
                    if (name.StartsWith("(", StringComparison.Ordinal) && name.EndsWith(")", StringComparison.Ordinal))
                        continue;
                    if (!archive.FileExists(name))
                        continue;
                    try
                    {
                        using var s = archive.OpenFile(name);
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        fileMap[name] = ms.ToArray();
                    }
                    catch { }
                }
            }

            result.FilesBefore = fileMap.Count;
            result.LogLines.Add($"Mapa cargado: {fileMap.Count} archivo(s), tamaño original {FormatBytes(result.OriginalFileBytes)}.");

            // 1. Parsear war3map.wts para inline strings
            Dictionary<int, string> wtsDict = new();
            if (fileMap.TryGetValue("war3map.wts", out var wtsBytes))
            {
                wtsDict = ParseWts(Encoding.UTF8.GetString(wtsBytes));
            }

            // 2. Optimización de Script JASS (war3map.j / scripts\war3map.j)
            string? jassKey = fileMap.Keys.FirstOrDefault(k =>
                k.Equals("war3map.j", StringComparison.OrdinalIgnoreCase) ||
                k.Equals(@"scripts\war3map.j", StringComparison.OrdinalIgnoreCase));

            var remainingTrigIds = new HashSet<int>();

            if (jassKey != null)
            {
                var origJass = Encoding.UTF8.GetString(fileMap[jassKey]);
                int origJassLen = fileMap[jassKey].Length;
                var workingJass = origJass;

                if (options.InlineTrigStrings && wtsDict.Count > 0)
                {
                    workingJass = InlineTrigStringsInJass(workingJass, wtsDict, out int inlinedCount);
                    result.InlinedStrings = inlinedCount;
                    if (inlinedCount > 0)
                        result.LogLines.Add($"✓ TRIGSTR inline: {inlinedCount} string(s) insertados directamente en JASS.");
                }

                if (options.RemoveJassComments || options.CompactJassWhitespace || options.RemoveDebugLines)
                {
                    workingJass = StripCommentsAndCompactJass(
                        workingJass,
                        options.RemoveJassComments,
                        options.CompactJassWhitespace,
                        options.RemoveDebugLines);
                    result.LogLines.Add($"✓ Limpieza de JASS aplicada (comentarios/espacios).");
                }

                if (options.CompressJassNames)
                {
                    var protectedNames = LoadBlizzardReservedIdentifiers(settings);
                    workingJass = CompressJassIdentifiers(workingJass, protectedNames, out int renamedCount);
                    result.ObfuscatedIdentifiers = renamedCount;
                    result.LogLines.Add($"✓ Ofuscación de identificadores: {renamedCount} funciones/variables comprimidas.");
                }

                // Verificar con PJASS si está disponible y habilitado
                if (options.ValidateWithPjass && File.Exists(settings.PjassPath) &&
                    File.Exists(settings.CommonJPath) && File.Exists(settings.BlizzardJPath))
                {
                    var (pjOk, pjMsg) = VerifyJassWithPjass(workingJass, settings);
                    result.PjassPassed = pjOk;
                    if (!pjOk)
                    {
                        result.LogLines.Add($"⚠ PJASS detectó un problema tras optimizar: {pjMsg}. Se conserva versión sin ofuscar.");
                        workingJass = origJass;
                        if (options.RemoveJassComments || options.CompactJassWhitespace)
                            workingJass = StripCommentsAndCompactJass(origJass, options.RemoveJassComments, options.CompactJassWhitespace, options.RemoveDebugLines);
                    }
                    else
                    {
                        result.LogLines.Add("✓ Validación PJASS superada (0 errores de sintaxis tras optimizar).");
                    }
                }
                else
                {
                    result.PjassPassed = true;
                }

                var newJassBytes = Encoding.UTF8.GetBytes(workingJass);
                fileMap[jassKey] = newJassBytes;
                result.LogLines.Add($"  Script {jassKey}: {FormatBytes(origJassLen)} → {FormatBytes(newJassBytes.Length)}");

                foreach (Match m in Regex.Matches(workingJass, @"TRIGSTR_(\d+)"))
                {
                    if (int.TryParse(m.Groups[1].Value, out int tid))
                        remainingTrigIds.Add(tid);
                }
            }

            // 3. Limpiar war3map.wts (conservar solo TRIGSTR usados en JASS o en otros archivos war3map.*)
            if (options.CleanUnusedWtsStrings && fileMap.ContainsKey("war3map.wts") && wtsDict.Count > 0)
            {
                foreach (var kvp in fileMap)
                {
                    if (kvp.Key.Equals("war3map.wts", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals(jassKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (kvp.Key.EndsWith(".w3i", StringComparison.OrdinalIgnoreCase) ||
                        ObjectDataFiles.Contains(kvp.Key.ToLowerInvariant()) ||
                        kvp.Key.EndsWith(".w3r", StringComparison.OrdinalIgnoreCase))
                    {
                        var txt = Encoding.Latin1.GetString(kvp.Value);
                        foreach (Match m in Regex.Matches(txt, @"TRIGSTR_(\d+)"))
                        {
                            if (int.TryParse(m.Groups[1].Value, out int tid))
                                remainingTrigIds.Add(tid);
                        }
                    }
                }

                int beforeWts = wtsDict.Count;
                var kept = wtsDict.Where(kv => remainingTrigIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                if (kept.Count == 0)
                {
                    fileMap.Remove("war3map.wts");
                    result.LogLines.Add($"✓ war3map.wts eliminado ({beforeWts} strings ya integrados).");
                }
                else if (kept.Count < beforeWts)
                {
                    fileMap["war3map.wts"] = Encoding.UTF8.GetBytes(SerializeWts(kept));
                    result.LogLines.Add($"✓ war3map.wts reducido: {beforeWts} → {kept.Count} strings.");
                }
            }

            // 4. Eliminar archivos exclusivos del Editor
            int removedCount = 0;
            if (options.RemoveEditorTriggerFiles)
            {
                foreach (var ef in EditorTriggerFiles)
                {
                    if (fileMap.Remove(ef))
                    {
                        removedCount++;
                        result.LogLines.Add($"✓ Eliminado archivo de editor: {ef}");
                    }
                }
            }

            if (options.RemoveEditorSoundsFile && fileMap.Remove("war3map.w3s"))
            {
                removedCount++;
                result.LogLines.Add("✓ Eliminado war3map.w3s");
            }

            if (options.RemoveEditorCamerasFile && fileMap.Remove("war3map.w3c"))
            {
                removedCount++;
                result.LogLines.Add("✓ Eliminado war3map.w3c");
            }

            if (options.RemoveMapPreviewImage && fileMap.Remove("war3mapPreview.tga"))
            {
                removedCount++;
                result.LogLines.Add("✓ Eliminado war3mapPreview.tga");
            }

            // 5. Empaquetar MPQ final optimizado
            var outDir = Path.GetDirectoryName(outputMapPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            var mpqFiles = new List<MpqFile>(fileMap.Count);
            foreach (var kvp in fileMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var mem = new MemoryStream(kvp.Value);
                var mpqFile = MpqFile.New(mem, kvp.Key);
                if (options.RecompressAllMpqFiles)
                {
                    mpqFile.CompressionType = MpqCompressionType.ZLib;
                    mpqFile.TargetFlags = MpqFileFlags.Exists | MpqFileFlags.CompressedMulti;
                }
                mpqFiles.Add(mpqFile);
            }

            using (var outFs = new FileStream(outputMapPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (hm3wHeader != null)
                    outFs.Write(hm3wHeader, 0, hm3wHeader.Length);

                var createOpts = new MpqArchiveCreateOptions
                {
                    ListFileCreateMode = options.RemoveListfileAndAttributes ? MpqFileCreateMode.None : MpqFileCreateMode.Overwrite,
                    AttributesCreateMode = MpqFileCreateMode.None,
                    BlockSize = options.MpqBlockSize
                };

                ushort hashSize = 256;
                while (hashSize < mpqFiles.Count * 2 && hashSize < 32768)
                    hashSize <<= 1;
                createOpts.HashTableSize = hashSize;

                using var created = MpqArchive.Create(outFs, mpqFiles, createOpts, leaveOpen: true);
            }

            if (options.RemoveListfileAndAttributes)
                result.LogLines.Add("✓ Protección WE aplicada: (listfile) y (attributes) omitidos del MPQ.");

            result.FilesAfter = fileMap.Count;
            result.OptimizedFileBytes = new FileInfo(outputMapPath).Length;
            result.Success = true;
            result.LogLines.Add($"✅ Optimización completa: {FormatBytes(result.OriginalFileBytes)} → {FormatBytes(result.OptimizedFileBytes)} (Ahorro: {FormatBytes(result.SavedBytes)} / {result.SavedPercent:F1}%).");
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.LogLines.Add($"❌ Error durante optimización: {ex.Message}");
            return result;
        }
    }

    private static string InlineTrigStringsInJass(string jass, Dictionary<int, string> wts, out int count)
    {
        int replaced = 0;
        var res = Regex.Replace(jass, @"""TRIGSTR_(\d+)""", match =>
        {
            if (int.TryParse(match.Groups[1].Value, out int id) && wts.TryGetValue(id, out var text))
            {
                if (text.Length <= 220 && !text.Contains('\0'))
                {
                    var escaped = text
                        .Replace("\\", "\\\\")
                        .Replace("\"", "\\\"")
                        .Replace("\r\n", "\\n")
                        .Replace("\r", "\\n")
                        .Replace("\n", "\\n");
                    replaced++;
                    return $"\"{escaped}\"";
                }
            }
            return match.Value;
        });
        count = replaced;
        return res;
    }

    private static string StripCommentsAndCompactJass(string jass, bool stripComments, bool compactWhitespace, bool stripDebug)
    {
        var sb = new StringBuilder(jass.Length);
        var lines = jass.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (var rawLine in lines)
        {
            string line = stripComments ? StripCommentOutsideStrings(rawLine) : rawLine;
            string trimmed = compactWhitespace ? line.Trim() : line;

            if (compactWhitespace && string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (stripDebug && Regex.IsMatch(trimmed, @"^debug\b"))
                continue;

            if (compactWhitespace)
                trimmed = CollapseHorizontalSpacesOutsideStrings(trimmed);

            sb.Append(trimmed).Append('\n');
        }

        return sb.ToString();
    }

    private static string StripCommentOutsideStrings(string line)
    {
        bool inStr = false;
        bool inChar = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < line.Length) { i++; continue; }
                if (c == '"') inStr = false;
            }
            else if (inChar)
            {
                if (c == '\'') inChar = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '\'') inChar = true;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    return line.Substring(0, i);
            }
        }
        return line;
    }

    private static string CollapseHorizontalSpacesOutsideStrings(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool inStr = false;
        bool inChar = false;
        bool prevSpace = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
                    sb.Append(line[i]);
                    continue;
                }
                if (c == '"') inStr = false;
                prevSpace = false;
            }
            else if (inChar)
            {
                sb.Append(c);
                if (c == '\'') inChar = false;
                prevSpace = false;
            }
            else
            {
                if (c == '"') { inStr = true; sb.Append(c); prevSpace = false; }
                else if (c == '\'') { inChar = true; sb.Append(c); prevSpace = false; }
                else if (c == ' ' || c == '\t')
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
        }

        return sb.ToString();
    }

    private static HashSet<string> LoadBlizzardReservedIdentifiers(WorkspaceSettings settings)
    {
        var set = new HashSet<string>(JassKeywords, StringComparer.Ordinal)
        {
            "main",
            "config"
        };

        foreach (var file in new[] { settings.CommonJPath, settings.BlizzardJPath })
        {
            if (!File.Exists(file)) continue;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                        continue;

                    foreach (Match m in Regex.Matches(trimmed, @"\b[A-Za-z][A-Za-z0-9_]*\b"))
                        set.Add(m.Value);
                }
            }
            catch { }
        }

        return set;
    }

    private static string CompressJassIdentifiers(string jass, HashSet<string> protectedSet, out int renamedCount)
    {
        // 1. Proteger cualquier identificador mencionado dentro de literales string (ExecuteFunc, udg_ events, etc.)
        var allProtected = new HashSet<string>(protectedSet, StringComparer.Ordinal);
        bool inStr = false;
        var strBuf = new StringBuilder();

        for (int i = 0; i < jass.Length; i++)
        {
            char c = jass[i];
            if (inStr)
            {
                if (c == '\\' && i + 1 < jass.Length) { i++; continue; }
                if (c == '"')
                {
                    inStr = false;
                    foreach (Match m in Regex.Matches(strBuf.ToString(), @"\b[A-Za-z][A-Za-z0-9_]*\b"))
                        allProtected.Add(m.Value);
                    strBuf.Clear();
                }
                else
                {
                    strBuf.Append(c);
                }
            }
            else if (c == '"')
            {
                inStr = true;
                strBuf.Clear();
            }
        }

        // 2. Descubrir identificadores declarados por el mapa (funciones, globals, locales, parámetros)
        var userDeclared = new HashSet<string>(StringComparer.Ordinal);
        bool inGlobals = false;
        foreach (var rawLine in jass.Split('\n'))
        {
            var line = StripCommentOutsideStrings(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line == "globals") { inGlobals = true; continue; }
            if (line == "endglobals") { inGlobals = false; continue; }

            if (inGlobals)
            {
                // [constant] <type> [array] <name> [= ...]
                var gm = Regex.Match(line, @"^(?:constant\s+)?[A-Za-z][A-Za-z0-9_]*\s+(?:array\s+)?([A-Za-z][A-Za-z0-9_]*)\b");
                if (gm.Success)
                    TryAddCandidate(gm.Groups[1].Value, allProtected, userDeclared);
                continue;
            }

            // function X takes <type1> <p1>, ... returns <ret>
            var fm = Regex.Match(line, @"^(?:constant\s+)?function\s+([A-Za-z][A-Za-z0-9_]*)\s+takes\s+(.+?)\s+returns\b");
            if (fm.Success)
            {
                TryAddCandidate(fm.Groups[1].Value, allProtected, userDeclared);
                var takesPart = fm.Groups[2].Value.Trim();
                if (!takesPart.Equals("nothing", StringComparison.Ordinal))
                {
                    foreach (var p in takesPart.Split(','))
                    {
                        var pm = Regex.Match(p.Trim(), @"^[A-Za-z][A-Za-z0-9_]*\s+([A-Za-z][A-Za-z0-9_]*)$");
                        if (pm.Success)
                            TryAddCandidate(pm.Groups[1].Value, allProtected, userDeclared);
                    }
                }
                continue;
            }

            // local <type> [array] <name> [= ...]
            var lm = Regex.Match(line, @"^local\s+[A-Za-z][A-Za-z0-9_]*\s+(?:array\s+)?([A-Za-z][A-Za-z0-9_]*)\b");
            if (lm.Success)
                TryAddCandidate(lm.Groups[1].Value, allProtected, userDeclared);
        }

        // 3. Asignar nombres cortos únicos sin colisiones
        var renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        int nameGenCounter = 0;
        var usedOutputs = new HashSet<string>(allProtected, StringComparer.Ordinal);

        foreach (var orig in userDeclared.OrderByDescending(s => s.Length))
        {
            if (orig.Length <= 2) continue; // Ya es suficientemente corto
            string nextName;
            do
            {
                nextName = GenerateShortIdent(nameGenCounter++);
            }
            while (usedOutputs.Contains(nextName) || userDeclared.Contains(nextName));

            usedOutputs.Add(nextName);
            renameMap[orig] = nextName;
        }

        renamedCount = renameMap.Count;
        if (renamedCount == 0)
            return jass;

        // 4. Reemplazar solo tokens de identificador fuera de comillas/rawcodes
        var output = new StringBuilder(jass.Length);
        int idx = 0;
        while (idx < jass.Length)
        {
            char c = jass[idx];
            if (c == '"')
            {
                output.Append(c);
                idx++;
                while (idx < jass.Length)
                {
                    char sc = jass[idx++];
                    output.Append(sc);
                    if (sc == '\\' && idx < jass.Length)
                        output.Append(jass[idx++]);
                    else if (sc == '"')
                        break;
                }
            }
            else if (c == '\'')
            {
                output.Append(c);
                idx++;
                while (idx < jass.Length)
                {
                    char cc = jass[idx++];
                    output.Append(cc);
                    if (cc == '\'') break;
                }
            }
            else if (char.IsLetter(c))
            {
                int start = idx;
                while (idx < jass.Length && (char.IsLetterOrDigit(jass[idx]) || jass[idx] == '_'))
                    idx++;
                string token = jass.Substring(start, idx - start);
                output.Append(renameMap.TryGetValue(token, out var shortName) ? shortName : token);
            }
            else
            {
                output.Append(c);
                idx++;
            }
        }

        return output.ToString();
    }

    private static void TryAddCandidate(string ident, HashSet<string> allProtected, HashSet<string> userDeclared)
    {
        if (!string.IsNullOrEmpty(ident) && !allProtected.Contains(ident))
            userDeclared.Add(ident);
    }

    private static string GenerateShortIdent(int index)
    {
        const string firstChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string nextChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        if (index < firstChars.Length)
            return firstChars[index].ToString();

        var sb = new StringBuilder();
        int n = index - firstChars.Length;
        sb.Append(firstChars[n % firstChars.Length]);
        n /= firstChars.Length;
        do
        {
            sb.Append(nextChars[n % nextChars.Length]);
            n /= nextChars.Length;
        } while (n > 0);
        return sb.ToString();
    }

    private static (bool ok, string msg) VerifyJassWithPjass(string jassCode, WorkspaceSettings settings)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"w3opt_{Guid.NewGuid():N}.j");
        try
        {
            File.WriteAllText(tempFile, jassCode, new UTF8Encoding(false));
            var psi = new ProcessStartInfo
            {
                FileName = settings.PjassPath,
                Arguments = $"\"{settings.CommonJPath}\" \"{settings.BlizzardJPath}\" \"{tempFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return (true, "pjass no iniciado");
            var stdOut = proc.StandardOutput.ReadToEnd();
            var stdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            if (proc.ExitCode == 0)
                return (true, "OK");
            return (false, (stdErr + " " + stdOut).Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static byte[]? ReadHm3wHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length >= 512)
            {
                var buf = new byte[512];
                int n = fs.Read(buf, 0, 512);
                if (n == 512 && buf[0] == 'H' && buf[1] == 'M' && buf[2] == '3' && buf[3] == 'W')
                    return buf;
            }
        }
        catch { }
        return null;
    }

    private static long FindMpqOffset(FileStream fs)
    {
        fs.Position = 0;
        var buf = new byte[Math.Min(4096, (int)fs.Length)];
        int read = fs.Read(buf, 0, buf.Length);
        for (int off = 0; off + 4 <= read; off += 512)
        {
            if (buf[off] == 'M' && buf[off + 1] == 'P' && buf[off + 2] == 'Q' && buf[off + 3] == 0x1A)
                return off;
        }
        return 0;
    }

    private static List<string> DiscoverArchiveFileNames(MpqArchive archive)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "war3map.j", @"scripts\war3map.j", "war3map.w3i", "war3map.w3e",
            "war3map.wpm", "war3map.shd", "war3map.mmp", "war3map.doo",
            "war3mapUnits.doo", "war3map.w3r", "war3map.w3c", "war3map.w3s",
            "war3map.wtg", "war3map.wct", "war3map.wts", "war3map.imp",
            "war3map.w3u", "war3map.w3t", "war3map.w3b", "war3map.w3d",
            "war3map.w3a", "war3map.w3h", "war3map.w3q", "war3mapMap.blp",
            "war3mapPreview.tga", "war3mapMisc.txt", "war3mapSkin.txt", "war3mapExtra.txt"
        };

        if (archive.FileExists("(listfile)"))
        {
            try
            {
                using var s = archive.OpenFile("(listfile)");
                using var sr = new StreamReader(s);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        names.Add(trimmed);
                }
            }
            catch { }
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<int, string> ParseWts(string text)
    {
        var dict = new Dictionary<int, string>();
        var matches = Regex.Matches(text, @"STRING\s+(\d+)\s*\{([\s\S]*?)\n\}", RegexOptions.Multiline);
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out int id))
                dict[id] = m.Groups[2].Value.TrimStart('\r', '\n');
        }
        return dict;
    }

    private static string SerializeWts(Dictionary<int, string> dict)
    {
        var sb = new StringBuilder();
        foreach (var kvp in dict.OrderBy(k => k.Key))
        {
            sb.AppendLine($"STRING {kvp.Key}");
            sb.AppendLine("{");
            sb.AppendLine(kvp.Value);
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
