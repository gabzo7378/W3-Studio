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
}
