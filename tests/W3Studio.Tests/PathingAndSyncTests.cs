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
    public void BuildUpdatedWpmBytes_DoesNotBakeBuildingsOrDestructibles_StampsStaticDoodadsAndHealsStaleWpm()
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
            Terrain = CreateFlatTerrain(6, 6, 0f, 0f),
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

        var staticDoodadDef = new W3ObjectEntry
        {
            Category = W3ObjectCategory.Doodads,
            Rawcode = "D000",
            BaseRawcode = "YOss",
            OldId = WarcraftBaseDataService.RawcodeToInt("YOss"),
            NewId = WarcraftBaseDataService.RawcodeToInt("D000")
        };
        staticDoodadDef.Fields.Add(new W3ObjectField { FieldId = "dptx", ResolvedValue = customTgaPath });
        project.Objects.Add(staticDoodadDef);

        var treeDestDef = new W3ObjectEntry
        {
            Category = W3ObjectCategory.Destructables,
            Rawcode = "B000",
            BaseRawcode = "LTlt",
            OldId = WarcraftBaseDataService.RawcodeToInt("LTlt"),
            NewId = WarcraftBaseDataService.RawcodeToInt("B000")
        };
        treeDestDef.Fields.Add(new W3ObjectField { FieldId = "bptx", ResolvedValue = customTgaPath });
        project.Objects.Add(treeDestDef);

        int pw = 20;
        byte[] initialWpm = new byte[16 + pw * pw];
        initialWpm[0] = (byte)'M'; initialWpm[1] = (byte)'P'; initialWpm[2] = (byte)'3'; initialWpm[3] = (byte)'W';
        BitConverter.GetBytes(0).CopyTo(initialWpm, 4);
        BitConverter.GetBytes(pw).CopyTo(initialWpm, 8);
        BitConverter.GetBytes(pw).CopyTo(initialWpm, 12);
        // Simulate a stale building footprint (with 0x40 Blizzard ground bit + 0x0A) at (32, 32)
        initialWpm[16 + 0 * pw + 0] = 0x4A;
        initialWpm[16 + 0 * pw + 1] = 0x4A;
        initialWpm[16 + 1 * pw + 0] = 0x4A;
        initialWpm[16 + 1 * pw + 1] = 0x4A;
        // Custom unrelated cell preserved
        initialWpm[16 + 18 * pw + 18] = 0x08;

        project.Terrain.InitialWpmBytes = initialWpm;
        project.InitialPathingFootprints.Add(new InitialEntityPathingSnapshot
        {
            Rawcode = "h000",
            IsBuilding = true,
            X = 32f,
            Y = 32f,
            PathTexture = customTgaPath
        });

        // Placed building at (160, 160) -> subcells [4..5, 4..5] MUST remain clean in war3map.wpm
        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "h000",
            CreationNumber = 1,
            IsBuilding = true,
            X = 160f,
            Y = 160f,
            PlayerId = 0
        });

        // Placed destructible (tree) at (288, 288) -> subcells [8..9, 8..9] MUST remain clean in war3map.wpm
        project.PlacedDoodads.Add(new W3PlacedDoodad
        {
            Rawcode = "B000",
            CreationNumber = 2,
            IsDestructible = true,
            X = 288f,
            Y = 288f
        });

        // Placed static non-destructible doodad at (416, 416) -> subcells [12..13, 12..13] MUST be stamped 0x0A
        project.PlacedDoodads.Add(new W3PlacedDoodad
        {
            Rawcode = "D000",
            CreationNumber = 3,
            IsDestructible = false,
            X = 416f,
            Y = 416f
        });

        var baseData = new WarcraftBaseDataService();
        byte[] updatedWpm = PathingService.BuildUpdatedWpmBytes(project, baseData, null);

        Assert.Equal(16 + pw * pw, updatedWpm.Length);
        // Stale building footprint healed while preserving 0x40 bit
        Assert.Equal(0x40, updatedWpm[16 + 0 * pw + 0]);
        Assert.Equal(0x40, updatedWpm[16 + 1 * pw + 1]);
        // Unrelated custom cell preserved
        Assert.Equal(0x08, updatedWpm[16 + 18 * pw + 18]);
        // Building NOT baked into war3map.wpm
        Assert.Equal(0x00, updatedWpm[16 + 4 * pw + 4]);
        Assert.Equal(0x00, updatedWpm[16 + 5 * pw + 5]);
        // Destructible NOT baked into war3map.wpm
        Assert.Equal(0x00, updatedWpm[16 + 8 * pw + 8]);
        Assert.Equal(0x00, updatedWpm[16 + 9 * pw + 9]);
        // Static non-destructible doodad IS stamped into war3map.wpm
        Assert.Equal(0x0A, updatedWpm[16 + 12 * pw + 12]);
        Assert.Equal(0x0A, updatedWpm[16 + 13 * pw + 13]);
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

    [Fact]
    public void EntityArchetypeHelper_ClassifiesWc3ArchetypesCleanly()
    {
        var project = new MapProject();

        var customGoldMine = new W3ObjectEntry
        {
            Rawcode = "ngol",
            Category = W3ObjectCategory.Units,
            Name = "Gold Mine"
        };
        customGoldMine.Fields.Add(new W3ObjectField { FieldId = "uabi", RawValue = "Agld", ResolvedValue = "Agld" });
        customGoldMine.Fields.Add(new W3ObjectField { FieldId = "ubdg", RawValue = "1", ResolvedValue = "1" });
        project.Objects.Add(customGoldMine);

        var customFountain = new W3ObjectEntry
        {
            Rawcode = "nfoh",
            Category = W3ObjectCategory.Units,
            Name = "Fountain of Health"
        };
        customFountain.Fields.Add(new W3ObjectField { FieldId = "uabi", RawValue = "Avul,Arll", ResolvedValue = "Avul,Arll" });
        customFountain.Fields.Add(new W3ObjectField { FieldId = "ubdg", RawValue = "1", ResolvedValue = "1" });
        project.Objects.Add(customFountain);

        var customTower = new W3ObjectEntry
        {
            Rawcode = "hctw",
            Category = W3ObjectCategory.Units,
            Name = "Cannon Tower"
        };
        customTower.Fields.Add(new W3ObjectField { FieldId = "ubdg", RawValue = "1", ResolvedValue = "1" });
        customTower.Fields.Add(new W3ObjectField { FieldId = "uhpm", RawValue = "600", ResolvedValue = "600" });
        project.Objects.Add(customTower);

        var customHero = new W3ObjectEntry
        {
            Rawcode = "Hpal",
            Category = W3ObjectCategory.Units,
            Name = "Paladin"
        };
        customHero.Fields.Add(new W3ObjectField { FieldId = "ustr", RawValue = "22", ResolvedValue = "22" });
        customHero.Fields.Add(new W3ObjectField { FieldId = "umpm", RawValue = "255", ResolvedValue = "255" });
        project.Objects.Add(customHero);

        var customItem = new W3ObjectEntry
        {
            Rawcode = "ches",
            Category = W3ObjectCategory.Items,
            Name = "Cheese",
            TooltipExtended = "|cffffcc00Cheese|r|nRestores HP."
        };
        project.Objects.Add(customItem);

        var mineArch = EntityArchetypeHelper.Analyze(new W3PlacedUnit { Rawcode = "ngol", IsBuilding = true }, project, null);
        Assert.True(mineArch.IsGoldMine);
        Assert.True(mineArch.IsBuilding);
        Assert.False(mineArch.IsHero);

        var fountainArch = EntityArchetypeHelper.Analyze(new W3PlacedUnit { Rawcode = "nfoh", IsBuilding = true }, project, null);
        Assert.True(fountainArch.IsBuilding);
        Assert.True(fountainArch.IsInvulnerable);
        Assert.Contains("Avul", fountainArch.AbilityRawcodes);
        Assert.Contains("Arll", fountainArch.AbilityRawcodes);

        var towerArch = EntityArchetypeHelper.Analyze(new W3PlacedUnit { Rawcode = "hctw", IsBuilding = true }, project, null);
        Assert.True(towerArch.IsBuilding);
        Assert.False(towerArch.IsInvulnerable);
        Assert.False(towerArch.IsGoldMine);
        Assert.Equal(600, towerArch.BaseHp);

        var heroArch = EntityArchetypeHelper.Analyze(new W3PlacedUnit { Rawcode = "Hpal", Level = 3 }, project, null);
        Assert.True(heroArch.IsHero);
        Assert.False(heroArch.IsBuilding);
        Assert.Equal(22, heroArch.BaseStr);
        Assert.Equal(255, heroArch.BaseMp);
        Assert.Equal(3, heroArch.HeaderLevel);

        var creepArch = EntityArchetypeHelper.Analyze(new W3PlacedUnit { Rawcode = "nftb" }, project, null);
        Assert.False(creepArch.IsHero);
        Assert.False(creepArch.IsBuilding);
        Assert.False(creepArch.IsItem);

        var itemArch = EntityArchetypeHelper.Analyze(new W3PlacedUnit { Rawcode = "ches", IsItem = true }, project, null);
        Assert.True(itemArch.IsItem);
        Assert.Equal("Cheese" + Environment.NewLine + "Restores HP.", itemArch.DescriptionText);
    }

    [Fact]
    public void SyncWorldEditsToScript_AndSubsequentSaveMap_PreservesPlacedUnitsInWar3MapJ()
    {
        var baseData = new WarcraftBaseDataService();
        var mpqService = new MapMpqService(baseData);
        var rootNode = new TriggerNode { Name = "MapScript", Kind = TriggerNodeKind.RootScript };
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
                """,
            HasUnitDoodadEdits = true
        };
        project.AllTriggerNodes.Add(rootNode);

        project.PlacedUnits.Add(new W3PlacedUnit
        {
            Rawcode = "nbt2",
            CreationNumber = 10,
            PlayerId = 0,
            IsBuilding = true,
            X = 0f,
            Y = 0f
        });

        string tempMapPath = Path.Combine(Path.GetTempPath(), $"w3s_save_twice_{Guid.NewGuid():N}.w3x");
        try
        {
            // Simulate SaveAndCompileMapAsync order: SyncWorldEditsToScript before SaveMap
            mpqService.SyncWorldEditsToScript(project);
            var scriptBeforeSave = project.RawMapScript;
            Assert.Contains("'nbt2'", scriptBeforeSave);

            mpqService.SaveMap(project, tempMapPath);
            Assert.False(project.HasUnitDoodadEdits);

            var jassSave1 = System.Text.Encoding.UTF8.GetString(project.RawMpqFiles["war3map.j"]);
            Assert.Contains("'nbt2'", jassSave1);
            Assert.Contains("'nbt2'", rootNode.Code);

            // Second consecutive save without touching units must STILL retain placed buildings in war3map.j
            mpqService.SyncWorldEditsToScript(project);
            mpqService.SaveMap(project, tempMapPath);

            var jassSave2 = System.Text.Encoding.UTF8.GetString(project.RawMpqFiles["war3map.j"]);
            Assert.Contains("'nbt2'", jassSave2);
        }
        finally
        {
            try { if (File.Exists(tempMapPath)) File.Delete(tempMapPath); } catch { }
            try { if (File.Exists(tempMapPath + ".bak")) File.Delete(tempMapPath + ".bak"); } catch { }
        }
    }

    [Fact]
    public void BuildClientOverrides_IncludesAnimPropsForUnitVariantModels()
    {
        var project = new MapProject();
        var customTower = new W3ObjectEntry
        {
            Rawcode = "u001",
            BaseRawcode = "nbt2",
            Category = W3ObjectCategory.Units,
            Name = "Custom Boulder Tower"
        };
        customTower.Fields.Add(new W3ObjectField { FieldId = "uani", RawValue = "upgrade,second", ResolvedValue = "upgrade,second" });
        customTower.Fields.Add(new W3ObjectField { FieldId = "ubdg", RawValue = "1", ResolvedValue = "1" });
        project.Objects.Add(customTower);

        var overrides = PathingService.BuildClientOverrides(project, new WarcraftBaseDataService());
        var ov = Assert.Single(overrides);
        Assert.Equal("u001", ov.Rawcode);
        Assert.True(ov.IsBuilding);
        Assert.Equal("upgrade,second", ov.AnimProps);
    }

    [Fact]
    public void UnitPaletteFilterHelper_FiltersByRaceHeroBuildingItemAndCustom()
    {
        Assert.True(UnitPaletteFilterHelper.IsHeroUnit(W3ObjectCategory.Units, "Hpal", null));
        Assert.True(UnitPaletteFilterHelper.IsHeroUnit(W3ObjectCategory.Units, "h000", "Hamg"));
        Assert.False(UnitPaletteFilterHelper.IsHeroUnit(W3ObjectCategory.Units, "hfoo", null));
        Assert.False(UnitPaletteFilterHelper.IsHeroUnit(W3ObjectCategory.Items, "Hmod", null));

        // Race filtering
        Assert.True(UnitPaletteFilterHelper.MatchesRaceFilter("human", "human", isCustom: false, isItem: false));
        Assert.False(UnitPaletteFilterHelper.MatchesRaceFilter("orc", "human", isCustom: false, isItem: false));
        Assert.True(UnitPaletteFilterHelper.MatchesRaceFilter("creeps", "creeps", isCustom: false, isItem: false));
        Assert.True(UnitPaletteFilterHelper.MatchesRaceFilter("creeps", "demon", isCustom: false, isItem: false));
        Assert.True(UnitPaletteFilterHelper.MatchesRaceFilter("neutral_passive", "naga", isCustom: false, isItem: false));
        Assert.True(UnitPaletteFilterHelper.MatchesRaceFilter("neutral_passive", "other", isCustom: false, isItem: false));
        Assert.True(UnitPaletteFilterHelper.MatchesRaceFilter("custom", "human", isCustom: true, isItem: false));
        Assert.False(UnitPaletteFilterHelper.MatchesRaceFilter("custom", "human", isCustom: false, isItem: false));

        // Type filtering
        Assert.True(UnitPaletteFilterHelper.MatchesTypeFilter("heroes", isHero: true, isBuilding: false, isItem: false));
        Assert.False(UnitPaletteFilterHelper.MatchesTypeFilter("units", isHero: true, isBuilding: false, isItem: false));
        Assert.True(UnitPaletteFilterHelper.MatchesTypeFilter("buildings", isHero: false, isBuilding: true, isItem: false));
        Assert.True(UnitPaletteFilterHelper.MatchesTypeFilter("items", isHero: false, isBuilding: false, isItem: true));
        Assert.False(UnitPaletteFilterHelper.MatchesTypeFilter("items", isHero: false, isBuilding: false, isItem: false));
        Assert.False(UnitPaletteFilterHelper.MatchesTypeFilter("units", isHero: false, isBuilding: false, isItem: false, rawcode: "sloc"));
    }
}
