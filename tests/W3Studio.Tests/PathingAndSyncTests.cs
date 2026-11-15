using System;
using System.IO;
using W3Studio.Models;
using W3Studio.Services;
using Xunit;

namespace W3Studio.Tests;

public class PathingAndSyncTests
{
    private static byte[] BuildTestTga(int width, int height, bool topDown, bool rle, (byte B, byte G, byte R)[] pixelsRowMajor)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((byte)0); // id length
        bw.Write((byte)0); // color map type
        bw.Write((byte)(rle ? 10 : 2)); // image type
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write((byte)0);
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((byte)24);
        bw.Write((byte)(topDown ? 0x20 : 0x00));

        if (!rle)
        {
            foreach (var p in pixelsRowMajor)
            {
                bw.Write(p.B);
                bw.Write(p.G);
                bw.Write(p.R);
            }
        }
        else
        {
            Assert.True(pixelsRowMajor.Length <= 128);
            bw.Write((byte)(pixelsRowMajor.Length - 1));
            foreach (var p in pixelsRowMajor)
            {
                bw.Write(p.B);
                bw.Write(p.G);
                bw.Write(p.R);
            }
        }

        return ms.ToArray();
    }

    private static W3TerrainData CreateFlatTerrain(int gridW, int gridH, float ox = 0f, float oy = 0f) => new()
    {
        GridWidth = gridW,
        GridHeight = gridH,
        OffsetX = ox,
        OffsetY = oy,
        GroundHeights = new float[gridW, gridH],
        WaterDepths = new float[gridW, gridH],
        CliffLevels = new byte[gridW, gridH],
        WaterFlags = new bool[gridW, gridH],
        RampFlags = new bool[gridW, gridH],
        BoundaryFlags = new bool[gridW, gridH]
    };

    [Fact]
    public void DecodePathTextureTga_UncompressedAndRle_DecodesRgbBitsAndOrientationCorrectly()
    {
        var pixels = new (byte B, byte G, byte R)[]
        {
            (0, 0, 255),     // top-left when topDown: R -> 0x02 (no-walk)
            (255, 0, 255),   // top-right when topDown: R+B -> 0x0A (no-walk + no-build)
            (0, 255, 0),     // bottom-left when topDown: G -> 0x04 (no-fly)
            (255, 0, 0)      // bottom-right when topDown: B -> 0x08 (no-build)
        };

        byte[] uncompressedTopDown = BuildTestTga(2, 2, topDown: true, rle: false, pixels);
        var gridA = PathingService.DecodePathTextureTga(uncompressedTopDown);
        Assert.NotNull(gridA);
        Assert.Equal(2, gridA!.Width);
        Assert.Equal(2, gridA.Height);
        Assert.Equal(0x04, gridA.Cells[0]);
        Assert.Equal(0x08, gridA.Cells[1]);
        Assert.Equal(0x02, gridA.Cells[2]);
        Assert.Equal(0x0A, gridA.Cells[3]);

        byte[] rleBottomUp = BuildTestTga(2, 2, topDown: false, rle: true, pixels);
        var gridB = PathingService.DecodePathTextureTga(rleBottomUp);
        Assert.NotNull(gridB);
        Assert.Equal(0x02, gridB!.Cells[0]);
        Assert.Equal(0x0A, gridB.Cells[1]);
        Assert.Equal(0x04, gridB.Cells[2]);
        Assert.Equal(0x08, gridB.Cells[3]);
    }

    [Fact]
    public void SnapPositionForFootprint_MatchesOfficialWc3GridRules()
    {
        float ox = -512f;
        float oy = -512f;

        var snap4 = PathingService.SnapPositionForFootprint(-370f, -340f, 4, 4, ox, oy);
        Assert.Equal(-384f, snap4.X);
        Assert.Equal(-320f, snap4.Y);

        var snap10 = PathingService.SnapPositionForFootprint(-345f, -210f, 10, 10, ox, oy);
        float relOrigX = (snap10.X - ox) - 160f;
        float relOrigY = (snap10.Y - oy) - 160f;
        Assert.Equal(0f, relOrigX % 64f);
        Assert.Equal(0f, relOrigY % 64f);
        Assert.Equal(-352f, snap10.X);
        Assert.Equal(-224f, snap10.Y);

        var snap3 = PathingService.SnapPositionForFootprint(-410f, -410f, 3, 3, ox, oy);
        float oddOrigX = (snap3.X - ox) - 48f;
        Assert.Equal(0f, oddOrigX % 32f);
    }

    [Fact]
    public void BuildUpdatedWpmBytes_ClearsMovedInitialFootprintPreservesCustomAndStampsNewPosition()
    {
        PathingService.ClearCache();
        const string customTgaPath = "war3mapImported\\Test2x2Solid.tga";
        var solid2x2Pixels = new[]
        {
            ((byte)255, (byte)0, (byte)255),
            ((byte)255, (byte)0, (byte)255),
            ((byte)255, (byte)0, (byte)255),
            ((byte)255, (byte)0, (byte)255)
        };
        byte[] tgaBytes = BuildTestTga(2, 2, topDown: false, rle: false, solid2x2Pixels);

        var project = new MapProject
        {
            Terrain = CreateFlatTerrain(4, 4, 0f, 0f),
            HasTerrainEdits = false
        };
        project.Imports.Add(new W3ImportedFile
        {
            MpqPath = customTgaPath,
            Data = tgaBytes,
            SizeBytes = tgaBytes.Length
        });

        var customDef = new W3ObjectEntry
        {
            Category = W3ObjectCategory.Units,
            Rawcode = "h000",
            BaseRawcode = "hbar",
            OldId = WarcraftBaseDataService.RawcodeToInt("hbar"),
            NewId = WarcraftBaseDataService.RawcodeToInt("h000")
        };
        customDef.Fields.Add(new W3ObjectField { FieldId = "upat", ResolvedValue = customTgaPath });
        customDef.Fields.Add(new W3ObjectField { FieldId = "ubdg", ResolvedValue = "1" });
        project.Objects.Add(customDef);

        byte[] initialWpm = new byte[16 + 144];
        initialWpm[0] = (byte)'M'; initialWpm[1] = (byte)'P'; initialWpm[2] = (byte)'3'; initialWpm[3] = (byte)'W';
        BitConverter.GetBytes(0).CopyTo(initialWpm, 4);
        BitConverter.GetBytes(12).CopyTo(initialWpm, 8);
        BitConverter.GetBytes(12).CopyTo(initialWpm, 12);
        initialWpm[16 + 0 * 12 + 0] = 0x0A;
        initialWpm[16 + 0 * 12 + 1] = 0x0A;
        initialWpm[16 + 1 * 12 + 0] = 0x0A;
        initialWpm[16 + 1 * 12 + 1] = 0x0A;
        initialWpm[16 + 10 * 12 + 10] = 0x08;

        project.Terrain.InitialWpmBytes = initialWpm;
        project.InitialPathingFootprints.Add(new InitialEntityPathingSnapshot
        {
            Rawcode = "h000",
            IsBuilding = true,
            X = 32f,
            Y = 32f,
            PathTexture = customTgaPath
        });

        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "h000",
            CreationNumber = 1,
            IsBuilding = true,
            X = 160f,
            Y = 160f,
            PlayerId = 0
        });

        var baseData = new WarcraftBaseDataService();
        byte[] updatedWpm = PathingService.BuildUpdatedWpmBytes(project, baseData, null);

        Assert.Equal(16 + 144, updatedWpm.Length);
        Assert.Equal(0x00, updatedWpm[16 + 0 * 12 + 0]);
        Assert.Equal(0x00, updatedWpm[16 + 1 * 12 + 1]);
        Assert.Equal(0x08, updatedWpm[16 + 10 * 12 + 10]);
        Assert.Equal(0x0A, updatedWpm[16 + 4 * 12 + 4]);
        Assert.Equal(0x0A, updatedWpm[16 + 5 * 12 + 5]);
    }

    [Fact]
    public void SyncUnitsAndDoodadsToDooAndJass_EmitsCreateUnitForNormalGoldMineAndBlightedOnlyForUgol()
    {
        var project = new MapProject
        {
            Terrain = CreateFlatTerrain(5, 5, -256f, -256f),
            RawMapScript = """
                globals
                endglobals
                function InitBlizzard takes nothing returns nothing
                endfunction
                function main takes nothing returns nothing
                    call InitBlizzard()
                    call CreateAllUnits()
                endfunction
                """
        };

        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "ngol",
            CreationNumber = 1,
            PlayerId = 15,
            IsBuilding = true,
            X = 9999f,
            Y = -9999f,
            GoldAmount = 12500
        });

        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "ugol",
            CreationNumber = 2,
            PlayerId = 0,
            IsBuilding = true,
            X = 0f,
            Y = 0f,
            GoldAmount = 15000
        });

        var baseData = new WarcraftBaseDataService();
        MapScriptSyncService.SyncUnitsAndDoodadsToDooAndJass(project, baseData);

        Assert.NotNull(project.RawMapScript);
        string jass = project.RawMapScript!;

        Assert.Contains("CreateUnit( Player(PLAYER_NEUTRAL_PASSIVE), 'ngol',", jass);
        Assert.Contains("SetResourceAmount( u, 12500 )", jass);
        Assert.DoesNotContain("CreateBlightedGoldmine( Player(PLAYER_NEUTRAL_PASSIVE)", jass);
        Assert.Contains("CreateBlightedGoldmine( p, 0.0, 0.0,", jass);

        Assert.InRange(project.PlacedUnits[0].X, -256f, 256f);
        Assert.InRange(project.PlacedUnits[0].Y, -256f, 256f);
    }
}
