using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using War3Net.Build.Common;
using War3Net.Build.Extensions;
using War3Net.Build.Info;
using War3Net.Build.Widget;
using W3Studio.Models;

namespace W3Studio.Services;

public static class MapScriptSyncService
{
    public static void SyncScenarioToW3iAndJass(MapProject project)
    {
        if (project.MapInfo == null)
            return;

        var info = project.MapInfo;
        project.MapName = project.ResolveTrigStr(info.MapName);
        project.MapAuthor = project.ResolveTrigStr(info.MapAuthor);
        project.MapDescription = project.ResolveTrigStr(info.MapDescription);
        project.SuggestedPlayers = project.ResolveTrigStr(info.RecommendedPlayers);

        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(info);
            bw.Flush();
            project.RawMpqFiles["war3map.w3i"] = ms.ToArray();
        }

        if (string.IsNullOrWhiteSpace(project.RawMapScript))
            return;

        var script = project.RawMapScript;
        script = ReplaceJassFunction(script, "InitCustomPlayerSlots", BuildInitCustomPlayerSlots(info));
        script = ReplaceJassFunction(script, "InitCustomTeams", BuildInitCustomTeams(info), insertBeforeFunction: "config");
        script = ReplaceJassFunction(script, "config", BuildConfigFunction(info, script));
        project.RawMapScript = script;

        var root = project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
        if (root != null)
            root.Code = script;
    }

    public static void SyncUnitsAndDoodadsToDooAndJass(MapProject project, WarcraftBaseDataService baseData)
    {
        // 1. Rebuild MapUnits (war3mapUnits.doo) preserving untouched advanced fields by CreationNumber
        var oldUnitsByCreation = project.RawMapUnitsData?.Units
            .GroupBy(u => u.CreationNumber)
            .ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<int, UnitData>();

        var mapUnits = project.RawMapUnitsData ?? new MapUnits(MapWidgetsFormatVersion.v8, MapWidgetsSubVersion.v11, useNewFormat: false);
        mapUnits.Units.Clear();

        foreach (var pu in project.PlacedUnits)
        {
            UnitData ud;
            if (oldUnitsByCreation.TryGetValue(pu.CreationNumber, out var existing))
            {
                ud = existing;
            }
            else
            {
                ud = new UnitData
                {
                    CreationNumber = pu.CreationNumber,
                    CustomPlayerColorId = -1,
                    WaygateDestinationRegionId = -1,
                    MapItemTableId = -1,
                    SkinId = 0
                };
            }

            bool hasBounds = project.Terrain.GridWidth > 1 && project.Terrain.GridHeight > 1;
            float clampedX = float.IsFinite(pu.X)
                ? (hasBounds ? Math.Clamp(pu.X, project.Terrain.OffsetX, project.Terrain.MaxX) : pu.X)
                : 0f;
            float clampedY = float.IsFinite(pu.Y)
                ? (hasBounds ? Math.Clamp(pu.Y, project.Terrain.OffsetY, project.Terrain.MaxY) : pu.Y)
                : 0f;
            float clampedZ = float.IsFinite(pu.Z) ? pu.Z : 0f;
            pu.X = clampedX;
            pu.Y = clampedY;
            pu.Z = clampedZ;

            ud.TypeId = WarcraftBaseDataService.RawcodeToInt(pu.Rawcode);
            ud.Variation = pu.Variation;
            ud.Position = new Vector3(clampedX, clampedY, clampedZ);
            ud.Rotation = pu.AngleRadians;
            ud.Scale = new Vector3(pu.Scale, pu.ScaleY, pu.ScaleZ);
            ud.Flags = pu.Flags;
            ud.OwnerId = pu.PlayerId;
            ud.HP = pu.HP;
            ud.MP = pu.MP;
            ud.GoldAmount = pu.GoldAmount;
            ud.TargetAcquisition = pu.TargetAcquisition;
            ud.HeroLevel = Math.Max(1, pu.Level);
            ud.HeroStrength = pu.HeroStrength;
            ud.HeroAgility = pu.HeroAgility;
            ud.HeroIntelligence = pu.HeroIntelligence;
            mapUnits.Units.Add(ud);
        }

        project.RawMapUnitsData = mapUnits;
        using (var msU = new MemoryStream())
        using (var bwU = new BinaryWriter(msU, Encoding.UTF8, leaveOpen: true))
        {
            bwU.Write(mapUnits);
            bwU.Flush();
            project.RawMpqFiles["war3mapUnits.doo"] = msU.ToArray();
        }

        // 2. Rebuild MapDoodads (war3map.doo) preserving untouched special properties
        var oldDoodadsByCreation = project.RawMapDoodadsData?.Doodads
            .GroupBy(d => d.CreationNumber)
            .ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<int, DoodadData>();

        var mapDoodads = project.RawMapDoodadsData ?? new MapDoodads(MapWidgetsFormatVersion.v8, MapWidgetsSubVersion.v11, useNewFormat: false);
        mapDoodads.Doodads.Clear();

        foreach (var pd in project.PlacedDoodads)
        {
            DoodadData dd;
            if (oldDoodadsByCreation.TryGetValue(pd.CreationNumber, out var existing))
            {
                dd = existing;
            }
            else
            {
                dd = new DoodadData
                {
                    CreationNumber = pd.CreationNumber,
                    MapItemTableId = -1
                };
            }

            bool hasBoundsD = project.Terrain.GridWidth > 1 && project.Terrain.GridHeight > 1;
            float clampedDX = float.IsFinite(pd.X)
                ? (hasBoundsD ? Math.Clamp(pd.X, project.Terrain.OffsetX, project.Terrain.MaxX) : pd.X)
                : 0f;
            float clampedDY = float.IsFinite(pd.Y)
                ? (hasBoundsD ? Math.Clamp(pd.Y, project.Terrain.OffsetY, project.Terrain.MaxY) : pd.Y)
                : 0f;
            float clampedDZ = float.IsFinite(pd.Z) ? pd.Z : 0f;
            pd.X = clampedDX;
            pd.Y = clampedDY;
            pd.Z = clampedDZ;

            dd.TypeId = WarcraftBaseDataService.RawcodeToInt(pd.Rawcode);
            dd.Variation = pd.Variation;
            dd.Position = new Vector3(clampedDX, clampedDY, clampedDZ);
            dd.Rotation = pd.AngleRadians;
            dd.Scale = new Vector3(pd.Scale, pd.ScaleY, pd.ScaleZ);
            dd.State = (DoodadState)pd.Flags;
            dd.Life = pd.Life;
            mapDoodads.Doodads.Add(dd);
        }

        project.RawMapDoodadsData = mapDoodads;
        using (var msD = new MemoryStream())
        using (var bwD = new BinaryWriter(msD, Encoding.UTF8, leaveOpen: true))
        {
            bwD.Write(mapDoodads);
            bwD.Flush();
            project.RawMpqFiles["war3map.doo"] = msD.ToArray();
        }

        // 3. Sync CreateAllDestructables / CreateAllItems / CreateAllUnits in war3map.j
        if (!string.IsNullOrWhiteSpace(project.RawMapScript))
        {
            var script = SyncWidgetSpawnsInJass(project, baseData);
            project.RawMapScript = script;
            var root = project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
            if (root != null)
                root.Code = script;
        }
    }

    public static void SyncRegionsToW3rAndJass(MapProject project)
    {
        project.RawMpqFiles["war3map.w3r"] = WriteRegionsW3r(project.Regions);

        if (string.IsNullOrWhiteSpace(project.RawMapScript))
            return;

        var script = project.RawMapScript;
        var declared = ExtractDeclaredRegionGlobals(script);

        // Ensure globals block declares any new region's rect gg_rct_* = null
        var missingDecls = new List<string>();
        foreach (var reg in project.Regions)
        {
            string gg = reg.JassVariableName;
            if (!string.IsNullOrWhiteSpace(gg) && declared.Add(gg))
            {
                missingDecls.Add($"    rect                    {gg}               = null");
            }
        }

        if (missingDecls.Count > 0)
        {
            var mEnd = Regex.Match(script, @"(?m)^endglobals\b");
            if (mEnd.Success)
            {
                string block = string.Join("\r\n", missingDecls) + "\r\n";
                script = script.Insert(mEnd.Index, block);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("function CreateRegions takes nothing returns nothing");
        sb.AppendLine("    local weathereffect we");
        sb.AppendLine();
        foreach (var reg in project.Regions)
        {
            string gg = reg.JassVariableName;
            if (string.IsNullOrWhiteSpace(gg))
                continue;
            float l = Math.Min(reg.Left, reg.Right);
            float r = Math.Max(reg.Left, reg.Right);
            float b = Math.Min(reg.Bottom, reg.Top);
            float t = Math.Max(reg.Bottom, reg.Top);
            sb.AppendLine($"    set {gg} = Rect( {Fmt(l)}, {Fmt(b)}, {Fmt(r)}, {Fmt(t)} )");
            if (reg.WeatherId != 0)
            {
                string rawWeather = WarcraftBaseDataService.IntToRawcode(reg.WeatherId);
                sb.AppendLine($"    set we = AddWeatherEffect( {gg}, '{rawWeather}' )");
                sb.AppendLine("    call EnableWeatherEffect( we, true )");
            }
        }
        sb.AppendLine("endfunction");

        script = ReplaceJassFunction(script, "CreateRegions", sb.ToString(), insertBeforeFunction: "CreateAllDestructables");
        project.RawMapScript = script;
        var root = project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
        if (root != null)
            root.Code = script;
    }

    public static byte[] WriteRegionsW3r(IReadOnlyList<W3MapRegion> regions)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(5); // war3map.w3r version 5
        bw.Write(regions.Count);
        foreach (var r in regions)
        {
            bw.Write(Math.Min(r.Left, r.Right));
            bw.Write(Math.Max(r.Left, r.Right));
            bw.Write(Math.Min(r.Bottom, r.Top));
            bw.Write(Math.Max(r.Bottom, r.Top));
            WriteNullTerminated(bw, r.Name ?? $"Region {r.CreationNumber:D3}");
            bw.Write(r.CreationNumber);
            bw.Write(r.WeatherId);
            WriteNullTerminated(bw, r.AmbientSound ?? string.Empty);
            bw.Write(r.ColorB);
            bw.Write(r.ColorG);
            bw.Write(r.ColorR);
            bw.Write((byte)0xFF);
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteNullTerminated(BinaryWriter bw, string value)
    {
        bw.Write(Encoding.UTF8.GetBytes(value ?? string.Empty));
        bw.Write((byte)0);
    }

    private static HashSet<string> ExtractDeclaredRegionGlobals(string script)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var mGlobals = Regex.Match(script, @"(?ms)^globals\b([\s\S]*?)^endglobals\b");
        if (!mGlobals.Success)
            return set;

        foreach (Match m in Regex.Matches(mGlobals.Groups[1].Value, @"\b(gg_rct_[A-Za-z0-9_]+)\b"))
            set.Add(m.Groups[1].Value);

        return set;
    }

    private static string BuildInitCustomPlayerSlots(MapInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("function InitCustomPlayerSlots takes nothing returns nothing");

        foreach (var p in info.Players)
        {
            if (p.Controller == PlayerController.None)
                continue;

            sb.AppendLine();
            sb.AppendLine($"    // Player {p.Id}");
            sb.AppendLine($"    call SetPlayerStartLocation( Player({p.Id}), {p.Id} )");
            if (p.Flags.HasFlag(PlayerFlags.FixedStartPosition))
                sb.AppendLine($"    call ForcePlayerStartLocation( Player({p.Id}), {p.Id} )");
            sb.AppendLine($"    call SetPlayerColor( Player({p.Id}), ConvertPlayerColor({p.Id}) )");
            sb.AppendLine($"    call SetPlayerRacePreference( Player({p.Id}), {RaceToJassPref(p.Race)} )");
            sb.AppendLine($"    call SetPlayerRaceSelectable( Player({p.Id}), {(p.Race == PlayerRace.Selectable || p.Flags.HasFlag(PlayerFlags.RaceSelectable) ? "true" : "false")} )");
            sb.AppendLine($"    call SetPlayerController( Player({p.Id}), {ControllerToJassConst(p.Controller)} )");
        }

        sb.AppendLine();
        sb.AppendLine("endfunction");
        return sb.ToString();
    }

    private static string BuildInitCustomTeams(MapInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("function InitCustomTeams takes nothing returns nothing");

        var activeIds = info.Players.Where(p => p.Controller != PlayerController.None).Select(p => p.Id).ToHashSet();

        for (int teamIdx = 0; teamIdx < info.Forces.Count; teamIdx++)
        {
            var force = info.Forces[teamIdx];
            var members = new List<int>();
            for (int pid = 0; pid < 24; pid++)
            {
                if (activeIds.Contains(pid) && force.Players[pid])
                    members.Add(pid);
            }

            if (members.Count == 0)
                continue;

            sb.AppendLine($"    // Force: {force.Name}");
            foreach (var pid in members)
                sb.AppendLine($"    call SetPlayerTeam( Player({pid}), {teamIdx} )");

            if (force.Flags.HasFlag(ForceFlags.Allied))
            {
                foreach (var a in members)
                foreach (var b in members)
                {
                    if (a != b)
                        sb.AppendLine($"    call SetPlayerAllianceStateAllyBJ( Player({a}), Player({b}), true )");
                }
            }

            if (force.Flags.HasFlag(ForceFlags.ShareVision))
            {
                foreach (var a in members)
                foreach (var b in members)
                {
                    if (a != b)
                        sb.AppendLine($"    call SetPlayerAllianceStateVisionBJ( Player({a}), Player({b}), true )");
                }
            }

            if (force.Flags.HasFlag(ForceFlags.ShareUnitControl))
            {
                foreach (var a in members)
                foreach (var b in members)
                {
                    if (a != b)
                        sb.AppendLine($"    call SetPlayerAllianceStateControlBJ( Player({a}), Player({b}), true )");
                }
            }

            if (force.Flags.HasFlag(ForceFlags.ShareAdvancedUnitControl))
            {
                foreach (var a in members)
                foreach (var b in members)
                {
                    if (a != b)
                        sb.AppendLine($"    call SetPlayerAllianceStateFullControlBJ( Player({a}), Player({b}), true )");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("endfunction");
        return sb.ToString();
    }

    private static string BuildConfigFunction(MapInfo info, string existingScript)
    {
        var activePlayers = info.Players.Where(p => p.Controller != PlayerController.None).ToList();
        int teamCount = Math.Max(1, info.Forces.Count);
        bool useCustom = info.MapFlags.HasFlag(MapFlags.UseCustomForces) || info.MapFlags.HasFlag(MapFlags.FixedPlayerSettingsForCustomForces);

        var sb = new StringBuilder();
        sb.AppendLine("function config takes nothing returns nothing");
        sb.AppendLine($"    call SetMapName( \"{EscapeJassString(info.MapName ?? "")}\" )");
        sb.AppendLine($"    call SetMapDescription( \"{EscapeJassString(info.MapDescription ?? "")}\" )");
        sb.AppendLine($"    call SetPlayers( {Math.Max(1, activePlayers.Count)} )");
        sb.AppendLine($"    call SetTeams( {teamCount} )");
        sb.AppendLine($"    call SetGamePlacement( {(useCustom ? "MAP_PLACEMENT_USE_MAP_SETTINGS" : "MAP_PLACEMENT_TEAMS_TOGETHER")} )");
        sb.AppendLine();

        foreach (var p in activePlayers)
        {
            sb.AppendLine($"    call DefineStartLocation( {p.Id}, {Fmt(p.StartPosition.X)}, {Fmt(p.StartPosition.Y)} )");
        }

        sb.AppendLine();
        sb.AppendLine("    // Player setup");
        sb.AppendLine("    call InitCustomPlayerSlots(  )");
        if (useCustom && (teamCount > 0 || Regex.IsMatch(existingScript, @"\bInitCustomTeams\b")))
            sb.AppendLine("    call InitCustomTeams(  )");
        foreach (var p in activePlayers)
        {
            sb.AppendLine($"    call SetPlayerSlotAvailable( Player({p.Id}), {ControllerToJassConst(p.Controller)} )");
        }
        sb.AppendLine("    call InitGenericPlayerSlots(  )");
        sb.AppendLine("endfunction");
        return sb.ToString();
    }

    private static string SyncWidgetSpawnsInJass(MapProject project, WarcraftBaseDataService baseData)
    {
        var script = project.RawMapScript;
        var declaredGlobals = ExtractDeclaredGlobals(script);

        // Identify items vs units vs buildings vs neutral
        var itemRawcodes = new HashSet<string>(
            project.Objects.Where(o => o.Category == W3ObjectCategory.Items).Select(o => o.Rawcode),
            StringComparer.OrdinalIgnoreCase);
        var destructibleRawcodes = new HashSet<string>(
            project.Objects.Where(o => o.Category == W3ObjectCategory.Destructables).Select(o => o.Rawcode),
            StringComparer.OrdinalIgnoreCase);

        var items = new List<W3PlacedUnit>();
        var playerBuildings = new Dictionary<int, List<W3PlacedUnit>>();
        var playerUnits = new Dictionary<int, List<W3PlacedUnit>>();
        var neutralHostileBuildings = new List<W3PlacedUnit>();
        var neutralHostileUnits = new List<W3PlacedUnit>();
        var neutralPassiveBuildings = new List<W3PlacedUnit>();
        var neutralPassiveUnits = new List<W3PlacedUnit>();

        foreach (var u in project.PlacedUnits)
        {
            if (u.IsStartLocation || u.Rawcode == "sloc")
                continue;

            var baseObj = baseData.GetBaseObject(u.Rawcode);
            bool isItem = u.IsItem || itemRawcodes.Contains(u.Rawcode) || baseObj?.Category == W3ObjectCategory.Items;
            if (isItem)
            {
                items.Add(u);
                continue;
            }

            bool isBldg = u.IsBuilding || PathingService.IsUnitBuilding(u.Rawcode, project, baseData);
            if (u.PlayerId == 12)
            {
                if (isBldg) neutralHostileBuildings.Add(u);
                else neutralHostileUnits.Add(u);
            }
            else if (u.PlayerId >= 13)
            {
                if (isBldg) neutralPassiveBuildings.Add(u);
                else neutralPassiveUnits.Add(u);
            }
            else
            {
                if (isBldg)
                {
                    if (!playerBuildings.TryGetValue(u.PlayerId, out var list))
                        playerBuildings[u.PlayerId] = list = new List<W3PlacedUnit>();
                    list.Add(u);
                }
                else
                {
                    if (!playerUnits.TryGetValue(u.PlayerId, out var list))
                        playerUnits[u.PlayerId] = list = new List<W3PlacedUnit>();
                    list.Add(u);
                }
            }
        }

        // Destructables
        var destructables = project.PlacedDoodads
            .Where(d => d.IsDestructible || destructibleRawcodes.Contains(d.Rawcode) || baseData.GetBaseObject(d.Rawcode)?.Category == W3ObjectCategory.Destructables)
            .ToList();

        // Remove old per-player CreateUnitsForPlayer* / CreateBuildingsForPlayer* functions to rebuild cleanly
        script = Regex.Replace(script, @"function\s+Create(?:Units|Buildings)ForPlayer\d+\s+takes\s+nothing\s+returns\s+nothing[\s\S]*?\nendfunction[ \t]*\r?\n?", "", RegexOptions.Multiline);
        script = RemoveJassFunction(script, "CreateNeutralHostile");
        script = RemoveJassFunction(script, "CreateNeutralHostileBuildings");
        script = RemoveJassFunction(script, "CreateNeutralPassive");
        script = RemoveJassFunction(script, "CreateNeutralPassiveBuildings");
        script = RemoveJassFunction(script, "CreatePlayerBuildings");
        script = RemoveJassFunction(script, "CreatePlayerUnits");

        // Build unified CreateAllUnits helper block
        var unitBlockSb = new StringBuilder();
        foreach (var kvp in playerBuildings.OrderBy(k => k.Key))
            AppendUnitGroupFunction(unitBlockSb, $"CreateBuildingsForPlayer{kvp.Key}", $"Player({kvp.Key})", kvp.Value, declaredGlobals);
        foreach (var kvp in playerUnits.OrderBy(k => k.Key))
            AppendUnitGroupFunction(unitBlockSb, $"CreateUnitsForPlayer{kvp.Key}", $"Player({kvp.Key})", kvp.Value, declaredGlobals);

        if (neutralHostileBuildings.Count > 0)
            AppendUnitGroupFunction(unitBlockSb, "CreateNeutralHostileBuildings", "Player(PLAYER_NEUTRAL_AGGRESSIVE)", neutralHostileBuildings, declaredGlobals);
        if (neutralHostileUnits.Count > 0)
            AppendUnitGroupFunction(unitBlockSb, "CreateNeutralHostile", "Player(PLAYER_NEUTRAL_AGGRESSIVE)", neutralHostileUnits, declaredGlobals);
        if (neutralPassiveBuildings.Count > 0)
            AppendUnitGroupFunction(unitBlockSb, "CreateNeutralPassiveBuildings", "Player(PLAYER_NEUTRAL_PASSIVE)", neutralPassiveBuildings, declaredGlobals);
        if (neutralPassiveUnits.Count > 0)
            AppendUnitGroupFunction(unitBlockSb, "CreateNeutralPassive", "Player(PLAYER_NEUTRAL_PASSIVE)", neutralPassiveUnits, declaredGlobals);

        unitBlockSb.AppendLine("function CreatePlayerBuildings takes nothing returns nothing");
        foreach (var pid in playerBuildings.Keys.OrderBy(k => k))
            unitBlockSb.AppendLine($"    call CreateBuildingsForPlayer{pid}(  )");
        unitBlockSb.AppendLine("endfunction");
        unitBlockSb.AppendLine();

        unitBlockSb.AppendLine("function CreatePlayerUnits takes nothing returns nothing");
        foreach (var pid in playerUnits.Keys.OrderBy(k => k))
            unitBlockSb.AppendLine($"    call CreateUnitsForPlayer{pid}(  )");
        unitBlockSb.AppendLine("endfunction");
        unitBlockSb.AppendLine();

        unitBlockSb.AppendLine("function CreateAllUnits takes nothing returns nothing");
        if (neutralPassiveBuildings.Count > 0)
            unitBlockSb.AppendLine("    call CreateNeutralPassiveBuildings(  )");
        if (neutralHostileBuildings.Count > 0)
            unitBlockSb.AppendLine("    call CreateNeutralHostileBuildings(  )");
        unitBlockSb.AppendLine("    call CreatePlayerBuildings(  )");
        if (neutralHostileUnits.Count > 0)
            unitBlockSb.AppendLine("    call CreateNeutralHostile(  )");
        if (neutralPassiveUnits.Count > 0)
            unitBlockSb.AppendLine("    call CreateNeutralPassive(  )");
        unitBlockSb.AppendLine("    call CreatePlayerUnits(  )");
        unitBlockSb.AppendLine("endfunction");

        script = ReplaceJassFunction(script, "CreateAllUnits", unitBlockSb.ToString(), insertBeforeFunction: "InitBlizzard");

        // Build CreateAllItems
        var itemSb = new StringBuilder();
        itemSb.AppendLine("function CreateAllItems takes nothing returns nothing");
        itemSb.AppendLine("    local integer itemID");
        itemSb.AppendLine();
        foreach (var it in items)
        {
            itemSb.AppendLine($"    call CreateItem( '{it.Rawcode}', {Fmt(it.X)}, {Fmt(it.Y)} )");
        }
        itemSb.AppendLine("endfunction");
        script = ReplaceJassFunction(script, "CreateAllItems", itemSb.ToString(), insertBeforeFunction: "CreateAllUnits");

        // Build CreateAllDestructables
        var destSb = new StringBuilder();
        destSb.AppendLine("function CreateAllDestructables takes nothing returns nothing");
        destSb.AppendLine("    local destructable d");
        destSb.AppendLine("    local trigger t");
        destSb.AppendLine("    local real life");
        destSb.AppendLine();
        foreach (var d in destructables)
        {
            string ggName = $"gg_dest_{d.Rawcode}_{d.CreationNumber:D4}";
            string assign = declaredGlobals.Contains(ggName) ? $"set {ggName} = " : "set d = ";
            float deg = (float)(d.AngleRadians * 180.0 / Math.PI);
            destSb.AppendLine($"    {assign}CreateDestructable( '{d.Rawcode}', {Fmt(d.X)}, {Fmt(d.Y)}, {Fmt(deg)}, {Fmt(d.Scale)}, {d.Variation} )");
            if (d.Life == 0)
            {
                string varTarget = declaredGlobals.Contains(ggName) ? ggName : "d";
                destSb.AppendLine($"    call KillDestructable( {varTarget} )");
            }
            else if (d.Life < 100)
            {
                string varTarget = declaredGlobals.Contains(ggName) ? ggName : "d";
                destSb.AppendLine($"    set life = GetDestructableLife( {varTarget} )");
                destSb.AppendLine($"    call SetDestructableLife( {varTarget}, {Fmt(d.Life / 100f)} * life )");
            }
        }
        destSb.AppendLine("endfunction");
        script = ReplaceJassFunction(script, "CreateAllDestructables", destSb.ToString(), insertBeforeFunction: "CreateAllItems");

        return script;
    }

    private static void AppendUnitGroupFunction(
        StringBuilder sb,
        string funcName,
        string playerExpr,
        List<W3PlacedUnit> units,
        HashSet<string> declaredGlobals)
    {
        sb.AppendLine($"function {funcName} takes nothing returns nothing");
        sb.AppendLine($"    local player p = {playerExpr}");
        sb.AppendLine("    local unit u");
        sb.AppendLine("    local integer unitID");
        sb.AppendLine("    local trigger t");
        sb.AppendLine("    local real life");
        sb.AppendLine();

        foreach (var u in units)
        {
            string ggName = $"gg_unit_{u.Rawcode}_{u.CreationNumber:D4}";
            bool keepGlobal = declaredGlobals.Contains(ggName);
            float deg = (float)(u.AngleRadians * 180.0 / Math.PI);
            string ownerExpr = u.PlayerId switch
            {
                12 => "Player(PLAYER_NEUTRAL_AGGRESSIVE)",
                13 => "Player(bj_PLAYER_NEUTRAL_VICTIM)",
                14 => "Player(bj_PLAYER_NEUTRAL_EXTRA)",
                15 => "Player(PLAYER_NEUTRAL_PASSIVE)",
                _ => "p"
            };

            if (u.Rawcode.Equals("ugol", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"    set u = CreateBlightedGoldmine( {ownerExpr}, {Fmt(u.X)}, {Fmt(u.Y)}, {Fmt(deg)} )");
                if (keepGlobal)
                    sb.AppendLine($"    set {ggName} = u");
                sb.AppendLine($"    call SetResourceAmount( u, {Math.Max(0, u.GoldAmount)} )");
            }
            else if (u.Rawcode.Equals("ngol", StringComparison.OrdinalIgnoreCase) ||
                     u.Rawcode.Equals("egol", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"    set u = CreateUnit( {ownerExpr}, '{u.Rawcode}', {Fmt(u.X)}, {Fmt(u.Y)}, {Fmt(deg)} )");
                if (keepGlobal)
                    sb.AppendLine($"    set {ggName} = u");
                sb.AppendLine($"    call SetResourceAmount( u, {Math.Max(0, u.GoldAmount)} )");
            }
            else
            {
                sb.AppendLine($"    set u = CreateUnit( {ownerExpr}, '{u.Rawcode}', {Fmt(u.X)}, {Fmt(u.Y)}, {Fmt(deg)} )");
                if (keepGlobal)
                    sb.AppendLine($"    set {ggName} = u");
            }

            if (u.Level > 1 && char.IsUpper(u.Rawcode.FirstOrDefault()))
                sb.AppendLine($"    call SetHeroLevel( u, {u.Level}, false )");

            if (u.HP > 0 && u.HP != 100)
            {
                sb.AppendLine("    set life = GetUnitState( u, UNIT_STATE_LIFE )");
                sb.AppendLine($"    call SetUnitState( u, UNIT_STATE_LIFE, {Fmt(u.HP / 100f)} * life )");
            }

            if (u.MP >= 0)
                sb.AppendLine($"    call SetUnitState( u, UNIT_STATE_MANA, {u.MP} )");

            if (u.TargetAcquisition >= 0f)
                sb.AppendLine($"    call SetUnitAcquireRange( u, {Fmt(u.TargetAcquisition)} )");
        }

        sb.AppendLine("endfunction");
        sb.AppendLine();
    }

    private static bool IsBuildingRawcode(string rawcode, BaseObjectMetadata? baseObj)
    {
        if (baseObj != null)
        {
            if (baseObj.DefaultFields.TryGetValue("isbldg", out var isb) &&
                (isb == "1" || isb.Equals("true", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (baseObj.DefaultFields.TryGetValue("ubdg", out var ubdg) &&
                (ubdg == "1" || ubdg.Equals("true", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static HashSet<string> ExtractDeclaredGlobals(string script)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var mGlobals = Regex.Match(script, @"(?ms)^globals\b([\s\S]*?)^endglobals\b");
        if (!mGlobals.Success)
            return set;

        foreach (Match m in Regex.Matches(mGlobals.Groups[1].Value, @"\b(gg_(?:unit|dest)_[A-Za-z0-9_]+)\b"))
            set.Add(m.Groups[1].Value);

        return set;
    }

    private static string ReplaceJassFunction(string script, string funcName, string newBlock, string? insertBeforeFunction = null)
    {
        var pattern = $@"(?ms)^[ \t]*function[ \t]+{Regex.Escape(funcName)}[ \t]+takes\b[\s\S]*?^[ \t]*endfunction[ \t]*";
        var match = Regex.Match(script, pattern);
        if (match.Success)
            return script.Substring(0, match.Index) + newBlock.TrimEnd() + script.Substring(match.Index + match.Length);

        if (!string.IsNullOrEmpty(insertBeforeFunction))
        {
            var anchor = Regex.Match(script, $@"(?m)^[ \t]*function[ \t]+{Regex.Escape(insertBeforeFunction)}[ \t]+takes\b");
            if (anchor.Success)
                return script.Insert(anchor.Index, newBlock.TrimEnd() + "\r\n\r\n");
        }

        return script + "\r\n\r\n" + newBlock.TrimEnd() + "\r\n";
    }

    private static string RemoveJassFunction(string script, string funcName)
    {
        var pattern = $@"(?ms)^[ \t]*function[ \t]+{Regex.Escape(funcName)}[ \t]+takes\b[\s\S]*?^[ \t]*endfunction[ \t]*\r?\n?";
        return Regex.Replace(script, pattern, "");
    }

    private static string RaceToJassPref(PlayerRace race) => race switch
    {
        PlayerRace.Human => "RACE_PREF_HUMAN",
        PlayerRace.Orc => "RACE_PREF_ORC",
        PlayerRace.Undead => "RACE_PREF_UNDEAD",
        PlayerRace.NightElf => "RACE_PREF_NIGHTELF",
        _ => "RACE_PREF_RANDOM"
    };

    private static string ControllerToJassConst(PlayerController ctrl) => ctrl switch
    {
        PlayerController.User => "MAP_CONTROL_USER",
        PlayerController.Computer => "MAP_CONTROL_COMPUTER",
        PlayerController.Rescuable => "MAP_CONTROL_RESCUABLE",
        PlayerController.Neutral => "MAP_CONTROL_NEUTRAL",
        _ => "MAP_CONTROL_NONE"
    };

    private static string Fmt(float v) => v.ToString("0.0##", CultureInfo.InvariantCulture);

    private static string EscapeJassString(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
    }
}
