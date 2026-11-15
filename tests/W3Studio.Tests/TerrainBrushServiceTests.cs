using System;
using System.Collections.Generic;
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
}
