using System;
using System.Collections.Generic;
using System.IO;
using W3Studio.Models;
using W3Studio.Services;
using Xunit;

namespace W3Studio.Tests;

public class MapRoundtripAndScriptsTests : IDisposable
{
    private readonly string _tempDir;

    public MapRoundtripAndScriptsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "W3StudioMapTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Ignore cleanup locks
        }
    }

    [Fact]
    public void WriteRegionsW3rAndSyncJass_UpdatesBinaryAndJassCreateRegions()
    {
        var project = new MapProject
        {
            RawMapScript = """
                globals
                endglobals
                function CreateRegions takes nothing returns nothing
                    // old
                endfunction
                function main takes nothing returns nothing
                    call CreateRegions()
                endfunction
                """
        };
        project.Regions.Add(new W3MapRegion
        {
            CreationNumber = 3,
            Name = "Hero Spawn",
            Left = -256f,
            Bottom = -512f,
            Right = 256f,
            Top = 512f,
            WeatherId = WarcraftBaseDataService.RawcodeToInt("RAhr"),
            AmbientSound = "Sound\\Ambient\\Rain.wav",
            ColorR = 64,
            ColorG = 192,
            ColorB = 255
        });

        MapScriptSyncService.SyncRegionsToW3rAndJass(project);

        Assert.True(project.RawMpqFiles.TryGetValue("war3map.w3r", out var w3rBytes));
        Assert.NotNull(w3rBytes);
        Assert.Equal(5, BitConverter.ToInt32(w3rBytes, 0));
        Assert.Equal(1, BitConverter.ToInt32(w3rBytes, 4));
        Assert.Contains("gg_rct_Hero_Spawn", project.RawMapScript);
        Assert.Contains("Rect( -256.0, -512.0, 256.0, 512.0 )", project.RawMapScript);
        Assert.Contains("EnableWeatherEffect", project.RawMapScript);
    }

    [Fact]
    public void WriteCamerasW3cAndParseCameras_RoundtripsClassicCameras()
    {
        var src = new MapProject();
        src.Cameras.Add(new W3MapCamera
        {
            Name = "Intro Cam",
            TargetX = 512f,
            TargetY = -768f,
            ZOffset = 64f,
            Rotation = 90f,
            AngleOfAttack = 304f,
            Distance = 1850f,
            Roll = 0f,
            FieldOfView = 70f,
            FarClip = 5000f,
            NearClip = 100f
        });

        var bytes = MapMpqService.WriteCamerasW3c(src);

        var dst = new MapProject();
        using var ms = new MemoryStream(bytes);
        MapMpqService.ParseCameras(ms, dst);

        Assert.Single(dst.Cameras);
        var c = dst.Cameras[0];
        Assert.Equal("Intro Cam", c.Name);
        Assert.Equal(512f, c.TargetX);
        Assert.Equal(-768f, c.TargetY);
        Assert.Equal(64f, c.ZOffset);
        Assert.Equal(90f, c.Rotation);
        Assert.Equal(304f, c.AngleOfAttack);
        Assert.Equal(1850f, c.Distance);
        Assert.Equal(70f, c.FieldOfView);
    }

    [Fact]
    public void CreateBlankMap_SaveAndLoadRoundtrip_PreservesTerrainRegionsCamerasAndShadows()
    {
        var mapPath = Path.Combine(_tempDir, "TestBlankMap.w3m");
        var baseData = new WarcraftBaseDataService();
        var mpqService = new MapMpqService(baseData);

        MapShadowAndMinimapService.CreateBlankMap(new NewMapCreationOptions
        {
            FilePath = mapPath,
            MapName = "Automated Test Map",
            Author = "Test Suite",
            Description = "Roundtrip test map",
            CellsWidth = 32,
            CellsHeight = 32,
            TilesetChar = 'L',
            BaseCliffLayer = 2
        }, baseData);

        Assert.True(File.Exists(mapPath));

        var loaded = mpqService.LoadMap(mapPath);
        Assert.Equal(33, loaded.Terrain.GridWidth);
        Assert.Equal(33, loaded.Terrain.GridHeight);
        Assert.Equal('L', loaded.Terrain.Tileset);
        Assert.NotNull(loaded.Terrain.RawW3eBytes);
        Assert.NotEmpty(loaded.PlacedUnits); // Initial sloc start location(s)

        // Mutate region, camera & script, save back cleanly
        loaded.Regions.Add(new W3MapRegion
        {
            CreationNumber = 1,
            Name = "Arena Center",
            Left = -384f,
            Bottom = -384f,
            Right = 384f,
            Top = 384f,
            ColorR = 255,
            ColorG = 128,
            ColorB = 0
        });
        loaded.HasRegionEdits = true;

        loaded.Cameras.Add(new W3MapCamera
        {
            Name = "Boss Cam",
            TargetX = 128f,
            TargetY = 256f,
            Distance = 2100f,
            Rotation = 45f,
            AngleOfAttack = 310f,
            FieldOfView = 70f,
            FarClip = 5000f,
            NearClip = 100f
        });
        loaded.HasCameraEdits = true;
        loaded.HasTerrainEdits = true;

        var savedPath = Path.Combine(_tempDir, "TestSavedMap.w3m");
        mpqService.SaveMap(loaded, savedPath);

        var reloaded = mpqService.LoadMap(savedPath);
        Assert.Contains(reloaded.Regions, r => r.Name == "Arena Center");
        Assert.Contains(reloaded.Cameras, c => c.Name == "Boss Cam" && Math.Abs(c.Distance - 2100f) < 0.01f);
        Assert.True(reloaded.RawMpqFiles.ContainsKey("war3map.shd"));
        Assert.Equal(32 * 4 * 32 * 4, reloaded.RawMpqFiles["war3map.shd"].Length);
        Assert.True(reloaded.RawMpqFiles.ContainsKey("war3mapMap.blp"));
        Assert.True(reloaded.RawMpqFiles["war3mapMap.blp"].Length > 100);
    }

    [Fact]
    public void AtomicFileHelper_PreservesOriginalFileWhenWriteFails_AndCreatesBackupOnSuccess()
    {
        var target = Path.Combine(_tempDir, "atomic_test.w3x");
        var originalBytes = new byte[] { 0x48, 0x4D, 0x33, 0x57, 0x11, 0x22, 0x33 };
        File.WriteAllBytes(target, originalBytes);

        // 1. Mid-write failure must leave original intact and clean up temp file
        Assert.Throws<InvalidOperationException>(() =>
            AtomicFileHelper.WriteAtomically(target, fs =>
            {
                fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
                throw new InvalidOperationException("Simulated mid-write crash");
            }, createBackup: true));

        Assert.Equal(originalBytes, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".bak"));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));

        // 2. Successful write updates target, writes old contents to .bak, leaves no .tmp
        var updatedBytes = new byte[] { 0x48, 0x4D, 0x33, 0x57, 0x99, 0x88, 0x77, 0x66 };
        AtomicFileHelper.WriteAtomically(target, fs => fs.Write(updatedBytes), createBackup: true);

        Assert.Equal(updatedBytes, File.ReadAllBytes(target));
        Assert.Equal(originalBytes, File.ReadAllBytes(target + ".bak"));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public void LoadMap_RecordsLoadWarnings_WhenCorruptObjectSubfileIsPresent()
    {
        var mapPath = Path.Combine(_tempDir, "CorruptSubfileBase.w3m");
        var corruptPath = Path.Combine(_tempDir, "CorruptSubfileSaved.w3m");
        var baseData = new WarcraftBaseDataService();
        var mpqService = new MapMpqService(baseData);

        MapShadowAndMinimapService.CreateBlankMap(new NewMapCreationOptions
        {
            FilePath = mapPath,
            MapName = "Corrupt Subfile Warning Test",
            Author = "Tests",
            CellsWidth = 32,
            CellsHeight = 32,
            TilesetChar = 'L',
            BaseCliffLayer = 2
        }, baseData);

        var loaded = mpqService.LoadMap(mapPath);
        Assert.Empty(loaded.LoadWarnings);

        // Inject a malformed war3map.w3u payload into the map MPQ
        loaded.RawMpqFiles["war3map.w3u"] = new byte[] { 0xFF, 0xFF, 0x00, 0x01 };
        mpqService.SaveMap(loaded, corruptPath);

        var reloaded = mpqService.LoadMap(corruptPath);
        Assert.Contains(reloaded.LoadWarnings, w => w.Contains("war3map.w3u", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateShadowMapShd_DoesNotBakeBuildingsOrBlockers_AndCastsProportionalTreeShadows()
    {
        var mapPath = Path.Combine(_tempDir, "shadow_audit.w3m");
        var baseData = new WarcraftBaseDataService();
        MapShadowAndMinimapService.CreateBlankMap(new NewMapCreationOptions
        {
            FilePath = mapPath,
            CellsWidth = 32,
            CellsHeight = 32,
            TilesetChar = 'L',
            BaseCliffLayer = 2
        }, baseData);

        var mpqService = new MapMpqService(baseData);
        var project = mpqService.LoadMap(mapPath);
        project.PlacedUnits.Clear();
        project.PlacedDoodads.Clear();

        // 1. Gold Mine + Town Hall + Scout Tower: must NEVER bake static shadows into war3map.shd
        project.PlacedUnits.Add(new W3PlacedUnit { Rawcode = "ngol", CreationNumber = 1, PlayerId = 15, X = -512f, Y = -512f, IsBuilding = true, Scale = 1f });
        project.PlacedUnits.Add(new W3PlacedUnit { Rawcode = "htow", CreationNumber = 2, PlayerId = 0, X = 0f, Y = -512f, IsBuilding = true, Scale = 1f });
        project.PlacedUnits.Add(new W3PlacedUnit { Rawcode = "hwtw", CreationNumber = 3, PlayerId = 0, X = 512f, Y = -512f, IsBuilding = true, Scale = 1f });

        // 2. Invisible Pathing Blocker & Dead Tree: must NOT bake shadows into war3map.shd
        project.PlacedDoodads.Add(new W3PlacedDoodad { Rawcode = "YTpb", CreationNumber = 10, X = -512f, Y = 512f, IsDestructible = true, Life = 100, Scale = 1f });
        project.PlacedDoodads.Add(new W3PlacedDoodad { Rawcode = "LTlt", CreationNumber = 11, X = 0f, Y = 512f, IsDestructible = true, Life = 0, Scale = 1f });

        // 3. Living Tree: MUST cast a compact, proportional shadow (not an oversized blob)
        var livingTree = new W3PlacedDoodad { Rawcode = "LTlt", CreationNumber = 12, X = 512f, Y = 512f, IsDestructible = true, Life = 100, Scale = 1f };
        Assert.True(livingTree.IsTree);
        project.PlacedDoodads.Add(livingTree);

        var shd = MapShadowAndMinimapService.GenerateShadowMapShd(project);
        int shdW = (project.Terrain.GridWidth - 1) * 4;
        int shdH = (project.Terrain.GridHeight - 1) * 4;
        Assert.Equal(shdW * shdH, shd.Length);

        int CountShadowCells(float wx, float wy, int halfCells)
        {
            int cx = (int)MathF.Floor((wx - project.Terrain.OffsetX) / 32f);
            int cy = (int)MathF.Floor((wy - project.Terrain.OffsetY) / 32f);
            int count = 0;
            for (int dy = -halfCells; dy <= halfCells; dy++)
            {
                for (int dx = -halfCells; dx <= halfCells; dx++)
                {
                    int sx = cx + dx;
                    int sy = cy + dy;
                    if (sx >= 0 && sx < shdW && sy >= 0 && sy < shdH && shd[sy * shdW + sx] != 0)
                        count++;
                }
            }
            return count;
        }

        // Zero static shadow baked under Gold Mine, Town Hall, Tower, Pathing Blocker, and Dead Tree
        Assert.Equal(0, CountShadowCells(-512f, -512f, 8));
        Assert.Equal(0, CountShadowCells(0f, -512f, 8));
        Assert.Equal(0, CountShadowCells(512f, -512f, 6));
        Assert.Equal(0, CountShadowCells(-512f, 512f, 5));
        Assert.Equal(0, CountShadowCells(0f, 512f, 5));

        // Living tree casts a compact proportional shadow (~4-12 subcells, never an oversized 40+ subcell blob)
        int treeShadowCells = CountShadowCells(512f, 512f, 5);
        Assert.InRange(treeShadowCells, 3, 16);
    }
}
