using System;
using System.Text;
using W3Studio.Models;
using W3Studio.Services;
using Xunit;

namespace W3Studio.Tests;

internal static class TestTerrainFactory
{
    public static MapProject CreateSyntheticTerrainProject(int cols = 8, int rows = 8)
    {
        int gw = cols + 1;
        int gh = rows + 1;
        int groundCount = 2;
        int cliffCount = 2;
        int headerSize = 37 + groundCount * 4 + cliffCount * 4;
        var raw = new byte[headerSize + gw * gh * 7];

        Buffer.BlockCopy(Encoding.ASCII.GetBytes("W3E!"), 0, raw, 0, 4);
        BitConverter.TryWriteBytes(raw.AsSpan(4, 4), 11);
        raw[8] = (byte)'L';
        BitConverter.TryWriteBytes(raw.AsSpan(9, 4), 0);
        BitConverter.TryWriteBytes(raw.AsSpan(13, 4), groundCount);
        Buffer.BlockCopy(Encoding.ASCII.GetBytes("LdrtLgrs"), 0, raw, 17, 8);
        int cliffOff = 17 + groundCount * 4;
        BitConverter.TryWriteBytes(raw.AsSpan(cliffOff, 4), cliffCount);
        Buffer.BlockCopy(Encoding.ASCII.GetBytes("CLdiCLgr"), 0, raw, cliffOff + 4, 8);
        int dimsOff = cliffOff + 4 + cliffCount * 4;
        BitConverter.TryWriteBytes(raw.AsSpan(dimsOff, 4), gw);
        BitConverter.TryWriteBytes(raw.AsSpan(dimsOff + 4, 4), gh);
        float ox = -cols * 64f;
        float oy = -rows * 64f;
        BitConverter.TryWriteBytes(raw.AsSpan(dimsOff + 8, 4), ox);
        BitConverter.TryWriteBytes(raw.AsSpan(dimsOff + 12, 4), oy);

        var t = new W3TerrainData
        {
            GridWidth = gw,
            GridHeight = gh,
            OffsetX = ox,
            OffsetY = oy,
            Tileset = 'L',
            GroundTiles = { "Ldrt", "Lgrs" },
            CliffTiles = { "CLdi", "CLgr" },
            RawW3eBytes = raw,
            GroundHeights = new float[gw, gh],
            WaterDepths = new float[gw, gh],
            TextureIndices = new byte[gw, gh],
            CliffLevels = new byte[gw, gh],
            WaterFlags = new bool[gw, gh]
        };

        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                int cornerIdx = y * gw + x;
                int off = headerSize + cornerIdx * 7;
                ushort groundRaw = 8192;
                ushort waterRaw = 8192;
                BitConverter.TryWriteBytes(raw.AsSpan(off, 2), groundRaw);
                BitConverter.TryWriteBytes(raw.AsSpan(off + 2, 2), waterRaw);
                raw[off + 4] = 0x00; // ground texture index 0
                raw[off + 5] = 0x00;
                raw[off + 6] = 0x02; // cliff=0, layer=2
                t.GroundHeights[x, y] = 0f;
                t.WaterDepths[x, y] = 0f;
                t.CliffLevels[x, y] = 2;
                t.TextureIndices[x, y] = 0;
            }
        }

        return new MapProject { Terrain = t };
    }
}

public class EditorUndoServiceTests
{
    [Fact]
    public void DelegateUndo_UndoRedoAndClearBehaviorWorks()
    {
        var svc = new EditorUndoService();
        int value = 10;
        int notifications = 0;
        svc.HistoryChanged += () => notifications++;

        svc.Record(new DelegateUndoEdit("Set to 20", () => value = 10, () => value = 20));
        value = 20;

        Assert.True(svc.CanUndo);
        Assert.False(svc.CanRedo);
        Assert.Equal("Set to 20", svc.NextUndoDescription);

        svc.Undo();
        Assert.Equal(10, value);
        Assert.False(svc.CanUndo);
        Assert.True(svc.CanRedo);

        svc.Redo();
        Assert.Equal(20, value);
        Assert.True(svc.CanUndo);
        Assert.False(svc.CanRedo);

        // Recording after undo clears redo stack
        svc.Undo();
        svc.Record(new DelegateUndoEdit("Set to 99", () => value = 10, () => value = 99));
        value = 99;
        Assert.False(svc.CanRedo);

        svc.Clear();
        Assert.False(svc.CanUndo);
        Assert.False(svc.CanRedo);
        Assert.True(notifications >= 4);
    }

    [Fact]
    public void HistoryDepth_CapsAtOneHundredSteps()
    {
        var svc = new EditorUndoService();
        for (int i = 0; i < 120; i++)
        {
            int captured = i;
            svc.Record(new DelegateUndoEdit($"Step {captured}", () => { }, () => { }));
        }

        int undoCount = 0;
        while (svc.CanUndo)
        {
            svc.Undo();
            undoCount++;
        }

        Assert.Equal(100, undoCount);
        Assert.Equal("Step 20", svc.NextRedoDescription);
    }

    [Fact]
    public void TerrainStrokeUndoEdit_RestoresRawW3eAndHeightsOnUndoRedo()
    {
        var project = TestTerrainFactory.CreateSyntheticTerrainProject(6, 6);
        var beforeSnapshot = (byte[])project.Terrain.RawW3eBytes!.Clone();
        var stroke = new TerrainStrokeState();

        var dab = TerrainBrushService.ApplyDabDetailed(
            project,
            0f,
            0f,
            new TerrainBrushOptions
            {
                ApplyHeight = true,
                HeightTool = TerrainHeightTool.Raise,
                ApplyTexture = true,
                TextureIndex = 1,
                BrushSize = 2
            },
            isStrokeStart: true,
            stroke);

        Assert.True(dab.Modified);
        float raisedHeight = project.Terrain.GroundHeights![3, 3];
        Assert.True(raisedHeight > 0f);
        Assert.Equal(1, project.Terrain.TextureIndices![3, 3]);

        TerrainDabResult? lastPatched = null;
        var edit = TerrainStrokeUndoEdit.TryCreateFromSnapshots(
            "Terrain Raise",
            project,
            beforeSnapshot,
            res => lastPatched = res);

        Assert.NotNull(edit);
        var svc = new EditorUndoService();
        svc.Record(edit!);

        svc.Undo();
        Assert.Equal(0f, project.Terrain.GroundHeights[3, 3]);
        Assert.Equal(0, project.Terrain.TextureIndices[3, 3]);
        Assert.Equal(beforeSnapshot, project.Terrain.RawW3eBytes);
        Assert.NotNull(lastPatched);

        svc.Redo();
        Assert.Equal(raisedHeight, project.Terrain.GroundHeights[3, 3]);
        Assert.Equal(1, project.Terrain.TextureIndices[3, 3]);
    }
}
