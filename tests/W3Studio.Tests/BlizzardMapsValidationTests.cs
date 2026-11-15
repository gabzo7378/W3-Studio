using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using War3Net.Build.Audio;
using War3Net.Build.Environment;
using War3Net.Build.Info;
using War3Net.Build.Script;
using War3Net.Build.Widget;
using War3Net.IO.Mpq;
using W3Studio.Models;
using W3Studio.Services;
using Xunit;

namespace W3Studio.Tests;

public sealed class BlizzardMapsValidationTests : IDisposable
{
    private const string War3Directory = @"F:\WARCRAFT\Warcraft III 1.27";
    private const string OfficialMapsFolder = @"F:\WARCRAFT\Warcraft III 1.27\Maps\Test W3-Studio";

    private readonly string _tempDir;

    public BlizzardMapsValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "W3Studio_BlizzValidation_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static (MapMpqService Service, WarcraftBaseDataService BaseData, BlpIconService Blp) CreateWar3Services()
    {
        var settings = new WorkspaceSettings { Warcraft3Dir = War3Directory };
        var blp = new BlpIconService();
        blp.ConfigureBaseMpqs(settings);
        var baseData = new WarcraftBaseDataService();
        baseData.LoadFromWarcraftFolder(settings);
        return (new MapMpqService(baseData, blp), baseData, blp);
    }

    [Fact]
    public void AllOfficialBlizzardMaps_OpenSaveReloadRoundtrip_PreservesEntitiesPathingShadowAndScripts()
    {
        if (!Directory.Exists(OfficialMapsFolder) || !Directory.Exists(War3Directory))
            return; // Omitido graciosamente en máquinas sin Warcraft III 1.27 instalado

        var mapFiles = Directory.GetFiles(OfficialMapsFolder, "*.w3x")
            .Concat(Directory.GetFiles(OfficialMapsFolder, "*.w3m"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(mapFiles);

        var (mpqService, _, _) = CreateWar3Services();
        var failures = new List<string>();

        foreach (var mapPath in mapFiles)
        {
            var fileName = Path.GetFileName(mapPath);
            try
            {
                var orig = mpqService.LoadMap(mapPath);
                Assert.True(orig.Terrain.GridWidth > 1 && orig.Terrain.GridHeight > 1, $"{fileName}: terreno inválido");
                Assert.True(orig.PlacedUnits.Count > 0 || orig.PlacedDoodads.Count > 0 || !string.IsNullOrWhiteSpace(orig.RawMapScript), $"{fileName}: mapa sin unidades, doodads ni script");

                int expectedWpmW = (orig.Terrain.GridWidth - 1) * 4;
                int expectedWpmH = (orig.Terrain.GridHeight - 1) * 4;
                Assert.NotNull(orig.Terrain.InitialWpmBytes);
                Assert.True(orig.Terrain.InitialWpmBytes!.Length >= 16 + expectedWpmW * expectedWpmH, $"{fileName}: WPM tamaño inesperado");
                Assert.Equal((byte)'M', orig.Terrain.InitialWpmBytes[0]);
                Assert.Equal((byte)'P', orig.Terrain.InitialWpmBytes[1]);
                Assert.Equal((byte)'3', orig.Terrain.InitialWpmBytes[2]);
                Assert.Equal((byte)'W', orig.Terrain.InitialWpmBytes[3]);

                // Fuerza regeneración completa de entidades/pathing/shadow/JASS igual que SaveMap real
                // (en mapas optimizados sin war3mapUnits.doo no debemos destruir el script precompilado cuando PlacedUnits==0)
                if (orig.PlacedUnits.Count > 0 || orig.PlacedDoodads.Count > 0)
                    orig.HasUnitDoodadEdits = true;
                var savedPath = Path.Combine(_tempDir, fileName);
                mpqService.SaveMap(orig, savedPath);
                Assert.True(File.Exists(savedPath), $"{fileName}: no se escribió el archivo guardado");

                var reloaded = mpqService.LoadMap(savedPath);
                Assert.Equal(orig.Terrain.GridWidth, reloaded.Terrain.GridWidth);
                Assert.Equal(orig.Terrain.GridHeight, reloaded.Terrain.GridHeight);
                Assert.Equal(orig.PlacedUnits.Count, reloaded.PlacedUnits.Count);
                Assert.Equal(orig.PlacedDoodads.Count, reloaded.PlacedDoodads.Count);
                Assert.Equal(orig.Regions.Count, reloaded.Regions.Count);

                // Verifica que ningún rawcode de unidad/doodad se invierta de endianness tras Guardar -> Recargar
                if (orig.PlacedUnits.Count > 0)
                {
                    Assert.Equal(orig.PlacedUnits[0].Rawcode, reloaded.PlacedUnits[0].Rawcode);
                    Assert.Equal(orig.PlacedUnits[^1].Rawcode, reloaded.PlacedUnits[^1].Rawcode);
                }
                if (orig.PlacedDoodads.Count > 0)
                {
                    Assert.Equal(orig.PlacedDoodads[0].Rawcode, reloaded.PlacedDoodads[0].Rawcode);
                    Assert.Equal(orig.PlacedDoodads[^1].Rawcode, reloaded.PlacedDoodads[^1].Rawcode);
                }

                Assert.NotNull(reloaded.Terrain.InitialWpmBytes);
                Assert.Equal(orig.Terrain.InitialWpmBytes.Length, reloaded.Terrain.InitialWpmBytes!.Length);
                Assert.Equal((byte)'M', reloaded.Terrain.InitialWpmBytes[0]);
                Assert.Equal((byte)'W', reloaded.Terrain.InitialWpmBytes[3]);

                // Verifica shadowmap y war3map.j dentro del MPQ guardado
                using var fs = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var archive = MpqArchive.Open(fs, true);
                Assert.True(archive.FileExists("war3map.j") || archive.FileExists(@"scripts\war3map.j"), $"{fileName}: falta war3map.j");
                Assert.True(archive.FileExists("war3map.wpm"), $"{fileName}: falta war3map.wpm");
                Assert.True(archive.FileExists("war3map.shd"), $"{fileName}: falta war3map.shd");

                using (var shdStream = archive.OpenFile("war3map.shd"))
                {
                    Assert.Equal((long)expectedWpmW * expectedWpmH, shdStream.Length);
                }

                // Verifica Gold Mines en JASS: 'ngol' usa CreateUnit(..., 'ngol', ...), nunca CreateBlightedGoldmine
                int goldMines = orig.PlacedUnits.Count(u => u.Rawcode == "ngol");
                if (goldMines > 0 && reloaded.RawMapScript is { Length: > 0 })
                {
                    Assert.DoesNotContain("CreateBlightedGoldmine(", reloaded.RawMapScript, StringComparison.Ordinal);
                    Assert.Contains("'ngol'", reloaded.RawMapScript, StringComparison.Ordinal);
                    Assert.Contains("SetResourceAmount(", reloaded.RawMapScript, StringComparison.Ordinal);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{fileName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, "Fallaron mapas oficiales:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void OfficialMap_MoveGoldMine_WaygateAndItemDrops_RoundtripsCleanlyAndUpdatesPathing()
    {
        if (!Directory.Exists(OfficialMapsFolder) || !Directory.Exists(War3Directory))
            return;

        var preferred = Path.Combine(OfficialMapsFolder, "(2)EchoIsles.w3x");
        var sourceMap = File.Exists(preferred)
            ? preferred
            : Directory.GetFiles(OfficialMapsFolder, "*.w3x").OrderBy(p => p).FirstOrDefault();
        if (string.IsNullOrEmpty(sourceMap) || !File.Exists(sourceMap))
            return;

        var (mpqService, baseData, blp) = CreateWar3Services();
        var project = mpqService.LoadMap(sourceMap);

        var goldMine = project.PlacedUnits.FirstOrDefault(u => u.Rawcode == "ngol");
        Assert.NotNull(goldMine);

        float oldMineX = goldMine!.X;
        float oldMineY = goldMine.Y;
        var pathTex = PathingService.ResolveObjectField("ngol", false, project, baseData, "upat", "pathTex");
        var grid = PathingService.GetPathTexture(pathTex, project, blp);
        Assert.NotNull(grid);

        // Desplazamos la mina 256 unidades en dirección libre hacia el centro del mapa y aplicamos snap oficial
        float dirX = oldMineX >= 0 ? -256f : 256f;
        float dirY = oldMineY >= 0 ? -256f : 256f;
        var (snappedX, snappedY) = PathingService.SnapPositionForFootprint(
            oldMineX + dirX,
            oldMineY + dirY,
            grid!.Width,
            grid.Height,
            project.Terrain.OffsetX,
            project.Terrain.OffsetY);
        goldMine.X = snappedX;
        goldMine.Y = snappedY;
        goldMine.GoldAmount = 19500;

        // Añadimos una región destino + Waygate + Héroe con stats + Item Drop Table
        var targetRegion = new W3MapRegion
        {
            Name = "Waygate_Dest_Test",
            CreationNumber = project.Regions.Count > 0 ? project.Regions.Max(r => r.CreationNumber) + 1 : 0,
            Left = 128f,
            Bottom = 128f,
            Right = 384f,
            Top = 384f
        };
        project.Regions.Add(targetRegion);
        project.HasRegionEdits = true;

        int nextUnitCn = project.PlacedUnits.Max(u => u.CreationNumber) + 1;
        var waygate = new W3PlacedUnit
        {
            Rawcode = "nwgt",
            Name = "Way Gate",
            CreationNumber = nextUnitCn++,
            PlayerId = 15,
            X = 0f,
            Y = 0f,
            AngleRadians = 4.712389f,
            WaygateDestinationRegionId = targetRegion.CreationNumber,
            IsBuilding = true
        };
        project.PlacedUnits.Add(waygate);

        var creepWithDrops = new W3PlacedUnit
        {
            Rawcode = "Hpal",
            Name = "Paladin",
            CreationNumber = nextUnitCn,
            PlayerId = 0,
            X = -512f,
            Y = -512f,
            AngleRadians = 1.570796f,
            Level = 6,
            HeroStrength = 35,
            HeroAgility = 22,
            HeroIntelligence = 18
        };
        creepWithDrops.ItemDropSets.Add(new List<W3UnitItemDrop>
        {
            new() { ItemRawcode = "rat9", Chance = 100 },
            new() { ItemRawcode = "ckng", Chance = 50 }
        });
        project.PlacedUnits.Add(creepWithDrops);
        project.HasUnitDoodadEdits = true;

        var savedPath = Path.Combine(_tempDir, "echo_edited.w3x");
        mpqService.SaveMap(project, savedPath);

        var reloaded = mpqService.LoadMap(savedPath);
        var reloadedMine = reloaded.PlacedUnits.FirstOrDefault(u => u.CreationNumber == goldMine.CreationNumber);
        Assert.NotNull(reloadedMine);
        Assert.Equal("ngol", reloadedMine!.Rawcode);
        Assert.InRange(reloadedMine.X, snappedX - 0.1f, snappedX + 0.1f);
        Assert.InRange(reloadedMine.Y, snappedY - 0.1f, snappedY + 0.1f);
        Assert.Equal(19500, reloadedMine.GoldAmount);

        // Verifica que war3map.wpm NO tenga 0x0A/0x08 bloqueando la posición de la mina (para que WC3 no la desplace al iniciar)
        Assert.True(reloaded.RawMpqFiles.TryGetValue("war3map.wpm", out var wpmBytes) && wpmBytes.Length >= 16);
        int wpmW = BitConverter.ToInt32(wpmBytes!, 8);
        int mineSx = (int)MathF.Floor((snappedX - reloaded.Terrain.OffsetX) / PathingService.SubcellWorldSize);
        int mineSy = (int)MathF.Floor((snappedY - reloaded.Terrain.OffsetY) / PathingService.SubcellWorldSize);
        byte mineCenterFlag = wpmBytes[16 + mineSy * wpmW + mineSx];
        Assert.Equal(0, mineCenterFlag & (PathingService.FlagWalkBlocked | PathingService.FlagBuildBlocked));

        var reloadedWaygate = reloaded.PlacedUnits.FirstOrDefault(u => u.CreationNumber == waygate.CreationNumber);
        Assert.NotNull(reloadedWaygate);
        Assert.Equal("nwgt", reloadedWaygate!.Rawcode);
        Assert.Equal(targetRegion.CreationNumber, reloadedWaygate.WaygateDestinationRegionId);

        var reloadedHero = reloaded.PlacedUnits.FirstOrDefault(u => u.CreationNumber == creepWithDrops.CreationNumber);
        Assert.NotNull(reloadedHero);
        Assert.Equal("Hpal", reloadedHero!.Rawcode);
        Assert.Equal(6, reloadedHero!.Level);
        Assert.Equal(35, reloadedHero.HeroStrength);
        Assert.Equal(22, reloadedHero.HeroAgility);
        Assert.Equal(18, reloadedHero.HeroIntelligence);
        Assert.Single(reloadedHero.ItemDropSets);
        Assert.Equal(2, reloadedHero.ItemDropSets[0].Count);
        Assert.Equal("rat9", reloadedHero.ItemDropSets[0][0].ItemRawcode);
        Assert.Equal(50, reloadedHero.ItemDropSets[0][1].Chance);

        // Verifica JASS generado para Waygate + HeroStats + GoldAmount
        Assert.Contains("WaygateActivate( u, true )", reloaded.RawMapScript, StringComparison.Ordinal);
        Assert.Contains("WaygateSetDestination( u, 256.0, 256.0 )", reloaded.RawMapScript, StringComparison.Ordinal);
        Assert.Contains("SetHeroStr( u, 35, true )", reloaded.RawMapScript, StringComparison.Ordinal);
        Assert.Contains("SetHeroAgi( u, 22, true )", reloaded.RawMapScript, StringComparison.Ordinal);
        Assert.Contains("SetHeroInt( u, 18, true )", reloaded.RawMapScript, StringComparison.Ordinal);
        Assert.Contains("SetResourceAmount( u, 19500 )", reloaded.RawMapScript, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncUnitsAndDoodadsToDooAndJass_WritesWaygateCustomColorHeroStatsAndItemTables()
    {
        var project = new MapProject
        {
            RawMapScript = """
            function CreateNeutralPassiveBuildings takes nothing returns nothing
            endfunction

            function CreateUnitsForPlayer0 takes nothing returns nothing
            endfunction

            function CreateAllUnits takes nothing returns nothing
                call CreateNeutralPassiveBuildings(  )
                call CreateUnitsForPlayer0(  )
            endfunction
            """,
            Terrain = new W3TerrainData { GridWidth = 65, GridHeight = 65, OffsetX = -4096f, OffsetY = -4096f }
        };

        project.Regions.Add(new W3MapRegion
        {
            Name = "PortalSalida",
            CreationNumber = 42,
            Left = 900f,
            Bottom = 400f,
            Right = 1100f,
            Top = 600f
        });

        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "nwgt",
            CreationNumber = 1,
            PlayerId = 15,
            X = 100f,
            Y = 200f,
            WaygateDestinationRegionId = 42,
            IsBuilding = true
        });

        var hero = new W3PlacedUnit
        {
            Rawcode = "Hamg",
            CreationNumber = 2,
            PlayerId = 0,
            CustomPlayerColorId = 4,
            X = 300f,
            Y = 400f,
            Level = 4,
            HeroStrength = 12,
            HeroAgility = 15,
            HeroIntelligence = 30
        };
        hero.ItemDropSets.Add(new List<W3UnitItemDrop>
        {
            new() { ItemRawcode = "pghe", Chance = 75 }
        });
        project.PlacedUnits.Add(hero);

        MapScriptSyncService.SyncUnitsAndDoodadsToDooAndJass(project, new WarcraftBaseDataService());

        Assert.True(project.RawMpqFiles.TryGetValue("war3mapUnits.doo", out var unitsDooBytes));
        Assert.NotNull(unitsDooBytes);
        using var ms = new MemoryStream(unitsDooBytes!);
        using var br = new BinaryReader(ms);
        var mapUnits = War3Net.Build.Extensions.BinaryReaderExtensions.ReadMapUnits(br);

        Assert.Equal(2, mapUnits.Units.Count);
        var serializedWaygate = mapUnits.Units.First(u => u.CreationNumber == 1);
        var serializedHero = mapUnits.Units.First(u => u.CreationNumber == 2);

        Assert.Equal(42, serializedWaygate.WaygateDestinationRegionId);
        Assert.Equal(4, serializedHero.CustomPlayerColorId);
        Assert.Equal(12, serializedHero.HeroStrength);
        Assert.Equal(15, serializedHero.HeroAgility);
        Assert.Equal(30, serializedHero.HeroIntelligence);
        Assert.Single(serializedHero.ItemTableSets);
        Assert.Single(serializedHero.ItemTableSets[0].Items);
        Assert.Equal(75, serializedHero.ItemTableSets[0].Items[0].Chance);

        var updatedJass = project.RawMapScript;
        Assert.Contains("WaygateActivate( u, true )", updatedJass, StringComparison.Ordinal);
        Assert.Contains("WaygateSetDestination( u, 1000.0, 500.0 )", updatedJass, StringComparison.Ordinal);
        Assert.Contains("SetHeroStr( u, 12, true )", updatedJass, StringComparison.Ordinal);
        Assert.Contains("SetHeroAgi( u, 15, true )", updatedJass, StringComparison.Ordinal);
        Assert.Contains("SetHeroInt( u, 30, true )", updatedJass, StringComparison.Ordinal);
    }
}

