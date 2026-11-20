using System;
using System.Collections.Generic;
using System.IO;
using W3Studio.Services;
using Xunit;

namespace W3Studio.Tests;

public class TerrainBrushServiceTests
{
    [Fact]
    public void GetW3eCornerDataOffset_MatchesStandardAndExtendedHeaders()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(4, 4);
        int offset = TerrainBrushService.GetW3eCornerDataOffset(project.Terrain.RawW3eBytes!);
        Assert.Equal(37 + 4 * 2 + 4 * 2, offset);
    }

    [Fact]
    public void ApplyDabDetailed_PaintTextureAndBlightUpdatesArraysAndRawBytes()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(8, 8);
        var stroke = new TerrainStrokeState();

        var dab = TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions
            {
                ApplyTexture = true,
                TextureIndex = 1,
                ApplyBlight = true,
                BrushSize = 2,
                Shape = TerrainBrushShape.Square
            },
            isStrokeStart: true,
            stroke);

        Assert.True(dab.Modified);
        Assert.False(dab.CliffsChanged);
        Assert.NotEmpty(dab.Patches);
        Assert.Equal(1, project.Terrain.TextureIndices![4, 4]);
    }

    [Fact]
    public void ApplyDabDetailed_CliffIncreaseAndWaterSetCliffsChangedFlag()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(8, 8);
        var stroke = new TerrainStrokeState();

        var cliffDab = TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions
            {
                ApplyCliff = true,
                CliffTool = TerrainCliffTool.IncreaseOne,
                BrushSize = 1
            },
            isStrokeStart: true,
            stroke);

        Assert.True(cliffDab.Modified);
        Assert.True(cliffDab.CliffsChanged);
        Assert.Equal((byte)3, project.Terrain.CliffLevels![4, 4]);

        var waterStroke = new TerrainStrokeState();
        var waterDab = TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions
            {
                ApplyCliff = true,
                CliffTool = TerrainCliffTool.Water,
                BrushSize = 1
            },
            isStrokeStart: true,
            waterStroke);

        Assert.True(waterDab.Modified);
        Assert.True(waterDab.CliffsChanged);
        Assert.True(project.Terrain.WaterFlags![4, 4]);
    }

    [Fact]
    public void ApplyCornerBytePatches_SynchronizesMemoryArraysAndBounds()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(4, 4);
        int headerLen = TerrainBrushService.GetW3eCornerDataOffset(project.Terrain.RawW3eBytes!);
        int cornerIndex = 2 * project.Terrain.GridWidth + 2; // (x=2, y=2)
        var patched7 = new byte[7];
        Buffer.BlockCopy(project.Terrain.RawW3eBytes!, headerLen + cornerIndex * 7, patched7, 0, 7);
        patched7[4] = (byte)((patched7[4] & 0xF0) | 0x01); // texture = 1
        patched7[6] = 0x04; // layer = 4

        var res = TerrainBrushService.ApplyCornerBytePatches(
            project,
            new List<(int, byte[])> { (cornerIndex, patched7) });

        Assert.True(res.Modified);
        Assert.True(res.CliffsChanged);
        Assert.Equal(2, res.MinX);
        Assert.Equal(2, res.MaxY);
        Assert.Equal((byte)1, project.Terrain.TextureIndices![2, 2]);
        Assert.Equal((byte)4, project.Terrain.CliffLevels![2, 2]);
    }

    [Fact]
    public void DiamondBrush_BoundaryAndEyedropper_WorkAccurately()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(8, 8);
        var stroke = new TerrainStrokeState();

        var dab = TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions
            {
                ApplyTexture = true,
                TextureIndex = 1,
                ApplyBlight = true,
                BoundaryTool = TerrainBoundaryTool.Add,
                BrushSize = 2,
                Shape = TerrainBrushShape.Diamond
            },
            isStrokeStart: true,
            stroke);

        Assert.True(dab.Modified);
        Assert.True(project.Terrain.BoundaryFlags![4, 4]);
        // Corner diagonal (+1, +1) has L1 dist 2.0 > 1.55 so diamond excludes it
        Assert.False(project.Terrain.BoundaryFlags![5, 5]);

        var inspected = TerrainBrushService.InspectCorner(project, 0f, 0f);
        Assert.NotNull(inspected);
        Assert.Equal(1, inspected!.TextureIndex);
        Assert.True(inspected.HasBlight);
        Assert.True(inspected.IsBoundary);

        var eraseDab = TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions
            {
                BoundaryTool = TerrainBoundaryTool.Remove,
                BrushSize = 2,
                Shape = TerrainBrushShape.Diamond
            },
            isStrokeStart: true,
            new TerrainStrokeState());
        Assert.True(eraseDab.Modified);
        Assert.False(project.Terrain.BoundaryFlags![4, 4]);
    }

    [Fact]
    public void PathingBrush_AppliesAndPersistsAcrossWpmSync()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(5, 5);
        var baseData = new WarcraftBaseDataService();
        var opts = new TerrainBrushOptions
        {
            PathingTool = TerrainPathingTool.BlockWalkAndBuild,
            BrushSize = 1,
            Shape = TerrainBrushShape.Square
        };

        var pdab = PathingService.ApplyPathingBrushDab(project, baseData, null, 0f, 0f, opts);
        Assert.True(pdab.Modified);
        Assert.NotEmpty(project.Terrain.ManualPathingOverrides);

        // Even if terrain edits trigger a full WPM rebuild, manual pathing overrides survive
        project.HasTerrainEdits = true;
        var rebuiltWpm = PathingService.BuildUpdatedWpmBytes(project, baseData, null);
        int pw = (project.Terrain.GridWidth - 1) * 4;
        int centerSy = pw / 2;
        int centerSx = pw / 2;
        byte cellFlag = rebuiltWpm[16 + centerSy * pw + centerSx];
        Assert.Equal(PathingService.FlagWalkBlocked | PathingService.FlagBuildBlocked,
            (byte)(cellFlag & (PathingService.FlagWalkBlocked | PathingService.FlagBuildBlocked)));
    }

    [Fact]
    public void HeightmapExportAndImport_RoundtripsHeightsAndUpdatesW3e()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(9, 9);
        var stroke = new TerrainStrokeState();
        // Sculpt a hill at the center before exporting
        TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions
            {
                ApplyHeight = true,
                HeightTool = TerrainHeightTool.Raise,
                HeightStrength = 2.0f,
                BrushSize = 2
            },
            isStrokeStart: true,
            stroke);

        string tempPng = Path.Combine(Path.GetTempPath(), $"w3s_hmap_{Guid.NewGuid():N}.png");
        try
        {
            var (minZ, maxZ) = TerrainOperationsService.ExportHeightmapPng(project, tempPng);
            Assert.True(File.Exists(tempPng));
            Assert.True(maxZ > minZ);

            // Reset to flat & import scaled back to [0..300]
            var target = TestTerrainFactory.CreateSyntheticTerrainProject(9, 9);
            int modified = TerrainOperationsService.ImportHeightmapFromImage(target, tempPng, 0f, 300f, smoothPass: false);
            Assert.True(modified > 0);
            Assert.True(target.Terrain.GroundHeights![4, 4] > target.Terrain.GroundHeights![0, 0] + 100f);
        }
        finally
        {
            try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
        }
    }

    [Fact]
    public void ApplyTilesetConfiguration_RebuildsHeaderAndRemapsGroundTiles()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(5, 5);
        TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions { ApplyTexture = true, TextureIndex = 1, BrushSize = 1 },
            isStrokeStart: true,
            new TerrainStrokeState());

        // Swap slot 0 & 1 and add a 3rd texture "Lrok"
        var remap = new Dictionary<int, int> { [0] = 1, [1] = 0 };
        TerrainOperationsService.ApplyTilesetConfiguration(
            project,
            'F',
            new[] { project.Terrain.GroundTiles[1], project.Terrain.GroundTiles[0], "Lrok" },
            new[] { "CFdi", "CFgr" },
            remap);

        Assert.Equal('F', project.Terrain.Tileset);
        Assert.Equal(3, project.Terrain.GroundTiles.Count);
        Assert.Equal("Lrok", project.Terrain.GroundTiles[2]);
        Assert.Equal(2, project.Terrain.CliffTiles.Count);
        // Former slot 1 at center (2,2) was remapped to new slot 0
        Assert.Equal((byte)0, project.Terrain.TextureIndices![2, 2]);
        int cornerOffset = TerrainBrushService.GetW3eCornerDataOffset(project.Terrain.RawW3eBytes!);
        Assert.Equal(37 + 4 * 3 + 4 * 2, cornerOffset);
    }
}
