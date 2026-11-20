using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using W3Studio.Models;
using W3Studio.Services;
using War3Net.Build.Object;
using Xunit;

namespace W3Studio.Tests;

public class TriggerAndObjectEditorTests : IDisposable
{
    private readonly string _tempDir;

    public TriggerAndObjectEditorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "W3StudioTrgObjTests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore temp cleanup locks
        }
    }

    [Fact]
    public void TriggerLifecycle_EnsureRenameAndRemove_KeepsScriptInSync()
    {
        var project = new MapProject
        {
            RawMapScript = """
                globals
                    integer udg_Score = 0
                endglobals

                function InitCustomTriggers takes nothing returns nothing
                endfunction

                function main takes nothing returns nothing
                    call InitCustomTriggers()
                endfunction
                """
        };

        var trg = new TriggerNode
        {
            Name = "Hero Respawn",
            Kind = TriggerNodeKind.GuiTrigger,
            IsEnabled = true,
            IsInitiallyOn = true
        };
        trg.Events.Add(new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Event,
            Category = "Map",
            FunctionName = "MapInitialization",
            DisplayText = "Map initialization"
        });
        trg.Code = trg.GenerateJassFromGui();
        project.AllTriggerNodes.Add(trg);

        // 1. Add & ensure global + InitCustomTriggers call
        JassKnowledgeService.SyncNodeCodeToProject(project, trg, string.Empty, trg.Code);
        JassKnowledgeService.EnsureTriggerCallAndGlobal(
            project,
            JassKnowledgeService.CleanTriggerIdentifier(trg.Name),
            ensureInitCall: true);

        Assert.Contains("trigger gg_trg_Hero_Respawn = null", project.RawMapScript);
        Assert.Contains("function InitTrig_Hero_Respawn takes nothing returns nothing", project.RawMapScript);
        Assert.Contains("call InitTrig_Hero_Respawn()", project.RawMapScript);

        // 2. Rename trigger -> old symbols renamed without duplicates
        var oldName = trg.Name;
        trg.Name = "Boss Spawn";
        JassKnowledgeService.RenameTriggerInProjectScript(project, trg, oldName, trg.Name);

        Assert.DoesNotContain("Hero_Respawn", project.RawMapScript);
        Assert.Contains("trigger gg_trg_Boss_Spawn = null", project.RawMapScript);
        Assert.Contains("function InitTrig_Boss_Spawn takes nothing returns nothing", project.RawMapScript);
        Assert.Contains("call InitTrig_Boss_Spawn()", project.RawMapScript);

        // 3. Remove trigger -> strips functions, global, and InitCustomTriggers call
        JassKnowledgeService.RemoveTriggerFromProjectScript(project, trg);

        Assert.DoesNotContain("gg_trg_Boss_Spawn", project.RawMapScript);
        Assert.DoesNotContain("InitTrig_Boss_Spawn", project.RawMapScript);
        Assert.DoesNotContain("Trig_Boss_Spawn_Actions", project.RawMapScript);
    }

    [Fact]
    public void MapScriptLineToTrigger_MapsErrorLineToOwningTriggerAndLocalLine()
    {
        var trigCode = "function Trig_Alpha_Actions takes nothing returns nothing\n    call DisplayTextToForce(GetPlayersAll(), \"Hi\")\n    call InvalidNative()\nendfunction";
        var fullScript =
            "globals\n" +
            "    trigger gg_trg_Alpha = null\n" +
            "endglobals\n" +
            "\n" +
            trigCode + "\n" +
            "function main takes nothing returns nothing\n" +
            "endfunction\n";

        var node = new TriggerNode
        {
            Name = "Alpha",
            Kind = TriggerNodeKind.CustomTextTrigger,
            Code = trigCode
        };

        var project = new MapProject { RawMapScript = fullScript };
        project.AllTriggerNodes.Add(node);

        // Line 7 in fullScript corresponds to line 3 ("    call InvalidNative()") inside trigCode
        var (mapped, localLine) = JassKnowledgeService.MapScriptLineToTrigger(project, 7);

        Assert.Same(node, mapped);
        Assert.Equal(3, localLine);
    }

    [Fact]
    public async Task JassCompiler_127bAnalyzer_IgnoresCommentsInStringsAndCatchesStructuralErrors()
    {
        var compiler = new JassCompilerService();
        var settings = new WorkspaceSettings();

        // 1. Valid script with "//" inside a string literal should NOT fail
        var validScript = """
            globals
                string udg_Url = "https://example.com//path"
            endglobals
            function Foo takes nothing returns nothing
                local integer x = 1
                if x == 1 then
                    call BJDebugMsg("http://warcraft3.info//test")
                endif
            endfunction
            function main takes nothing returns nothing
                call Foo()
            endfunction
            """;
        var okRes = await compiler.ValidateAndCompileAsync(validScript, settings, "valid.j");
        Assert.True(okRes.Success);
        Assert.Equal(0, okRes.ErrorCount);

        // 2. Script with unclosed if, unclosed loop, unclosed globals, and 1.29+ Blz native
        var brokenScript = """
            globals
                integer udg_A = 1
            function Bad takes nothing returns nothing
                if true then
                    loop
                        call BlzSetUnitName(null, "ReforgedOnly")
            endfunction
            """;
        var badRes = await compiler.ValidateAndCompileAsync(brokenScript, settings, "broken.j");
        Assert.False(badRes.Success);
        Assert.True(badRes.ErrorCount >= 3);
        Assert.Contains(badRes.Diagnostics, d => d.Message.Contains("BlzSetUnitName", StringComparison.Ordinal));
        Assert.Contains(badRes.Diagnostics, d => d.Message.Contains("if", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(badRes.Diagnostics, d => d.Message.Contains("loop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(badRes.Diagnostics, d => d.Message.Contains("globals", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WtgAndWctRoundtrip_PreservesCategoriesVariablesAndTriggerCode()
    {
        var mapPath = Path.Combine(_tempDir, "WtgRoundtripBase.w3m");
        var savedPath = Path.Combine(_tempDir, "WtgRoundtripSaved.w3m");
        var baseData = new WarcraftBaseDataService();
        var mpq = new MapMpqService(baseData);

        MapShadowAndMinimapService.CreateBlankMap(new NewMapCreationOptions
        {
            FilePath = mapPath,
            MapName = "WTG Roundtrip",
            Author = "Tests",
            CellsWidth = 32,
            CellsHeight = 32,
            TilesetChar = 'L',
            BaseCliffLayer = 2
        }, baseData);

        var project = mpq.LoadMap(mapPath);

        // Add category, global variable, and custom trigger
        var cat = new TriggerNode { Name = "Gameplay", Kind = TriggerNodeKind.Category };
        var trig = new TriggerNode
        {
            Name = "WelcomeMsg",
            Kind = TriggerNodeKind.CustomTextTrigger,
            CategoryName = "Gameplay",
            IsEnabled = true,
            IsInitiallyOn = true,
            IsCustomText = true,
            IsModified = true,
            Code = "function Trig_WelcomeMsg_Actions takes nothing returns nothing\n    call BJDebugMsg(\"Hola\")\nendfunction\nfunction InitTrig_WelcomeMsg takes nothing returns nothing\n    set gg_trg_WelcomeMsg = CreateTrigger()\n    call TriggerAddAction(gg_trg_WelcomeMsg, function Trig_WelcomeMsg_Actions)\nendfunction"
        };
        cat.Children.Add(trig);
        project.TriggerTree.Add(cat);
        project.AllTriggerNodes.Add(trig);

        project.GlobalVariables.Add(new W3GlobalVariable
        {
            Name = "KillCount",
            Type = "integer",
            IsArray = false,
            ArraySize = 1,
            InitialValue = "5"
        });
        project.HasTriggerStructureEdits = true;

        mpq.SaveMap(project, savedPath);

        // Reload and verify war3map.wtg and war3map.wct were persisted and parsed back
        var reloaded = mpq.LoadMap(savedPath);
        Assert.True(reloaded.RawMpqFiles.ContainsKey("war3map.wtg"));
        Assert.True(reloaded.RawMpqFiles.ContainsKey("war3map.wct"));
        Assert.Contains(reloaded.GlobalVariables, v => v.Name == "KillCount" && v.Type == "integer" && v.InitialValue == "5");
        var loadedTrig = reloaded.AllTriggerNodes.FirstOrDefault(n => n.Name == "WelcomeMsg");
        Assert.NotNull(loadedTrig);
        Assert.Contains("BJDebugMsg(\"Hola\")", loadedTrig!.Code);
    }

    [Fact]
    public void ObjectEditorHelper_HeroRawcodesAndCategoryMetadata_FollowWc3Rules()
    {
        // Heroes must keep uppercase first char; normal units lowercase; doodads 'D'; destructables 'B'
        Assert.Equal('H', ObjectEditorHelper.ResolveCustomRawcodePrefix(W3ObjectCategory.Units, "Hpal"));
        Assert.Equal('O', ObjectEditorHelper.ResolveCustomRawcodePrefix(W3ObjectCategory.Units, "Obla"));
        Assert.Equal('h', ObjectEditorHelper.ResolveCustomRawcodePrefix(W3ObjectCategory.Units, "hfoo"));
        Assert.Equal('D', ObjectEditorHelper.ResolveCustomRawcodePrefix(W3ObjectCategory.Doodads, "LTlt"));
        Assert.Equal('B', ObjectEditorHelper.ResolveCustomRawcodePrefix(W3ObjectCategory.Destructables, "LTlt"));

        Assert.Equal("dnam", ObjectEditorHelper.GetNameFieldId(W3ObjectCategory.Doodads));
        Assert.Equal("bnam", ObjectEditorHelper.GetNameFieldId(W3ObjectCategory.Destructables));
        Assert.Equal("anam", ObjectEditorHelper.GetNameFieldId(W3ObjectCategory.Abilities));
        Assert.Equal("fnam", ObjectEditorHelper.GetNameFieldId(W3ObjectCategory.Buffs));
        Assert.Equal("gnam", ObjectEditorHelper.GetNameFieldId(W3ObjectCategory.Upgrades));
        Assert.Equal("unam", ObjectEditorHelper.GetNameFieldId(W3ObjectCategory.Units));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "H000" };
        Assert.Equal("H001", ObjectEditorHelper.GenerateNextFreeRawcode('H', used));

        // Hero validation rejects lowercase, accepts uppercase
        Assert.False(ObjectEditorHelper.ValidateCustomRawcode("h001", W3ObjectCategory.Units, "Hpal", used).IsValid);
        Assert.True(ObjectEditorHelper.ValidateCustomRawcode("H001", W3ObjectCategory.Units, "Hpal", used).IsValid);
        // Normal unit rejects uppercase, accepts lowercase
        Assert.False(ObjectEditorHelper.ValidateCustomRawcode("H001", W3ObjectCategory.Units, "hfoo", used).IsValid);
        Assert.True(ObjectEditorHelper.ValidateCustomRawcode("h001", W3ObjectCategory.Units, "hfoo", used).IsValid);
        // Duplicate rejected
        Assert.False(ObjectEditorHelper.ValidateCustomRawcode("H000", W3ObjectCategory.Units, "Hpal", used).IsValid);
    }

    [Fact]
    public void ObjectEditorHelper_AutoFillLevels_ComputesLinearGeometricFormulaAndConstant()
    {
        var linear = ObjectEditorHelper.ComputeAutoFillLevelValues(100, 50, 1, 4, isInteger: true, AutoFillProgressionMode.Linear);
        Assert.Equal(new[] { "100", "150", "200", "250" }, linear);

        var geom = ObjectEditorHelper.ComputeAutoFillLevelValues(10, 2, 1, 4, isInteger: true, AutoFillProgressionMode.Geometric);
        Assert.Equal(new[] { "10", "20", "40", "80" }, geom);

        var wc3 = ObjectEditorHelper.ComputeAutoFillLevelValues(100, 25, 1.5, 3, isInteger: false, AutoFillProgressionMode.Wc3Formula);
        Assert.Equal(new[] { "100", "175", "287.5" }, wc3);

        var constant = ObjectEditorHelper.ComputeAutoFillLevelValues(12.5, 99, 2, 3, isInteger: false, AutoFillProgressionMode.Constant);
        Assert.Equal(new[] { "12.5", "12.5", "12.5" }, constant);
    }

    [Fact]
    public void ObjectEditorHelper_AttackArmorAndHeroAttributeIcons_UseAuthenticWc3InfoCardPaths()
    {
        var expectedAttacks = new Dictionary<string, string>
        {
            ["normal"] = @"UI\Widgets\Console\Human\infocard-neutral-attack-melee.blp",
            ["pierce"] = @"UI\Widgets\Console\Human\infocard-neutral-attack-piercing.blp",
            ["siege"] = @"UI\Widgets\Console\Human\infocard-neutral-attack-siege.blp",
            ["magic"] = @"UI\Widgets\Console\Human\infocard-neutral-attack-magic.blp",
            ["chaos"] = @"UI\Widgets\Console\Human\infocard-neutral-attack-chaos.blp",
            ["spells"] = @"ReplaceableTextures\CommandButtons\BTNFireBolt.blp",
            ["hero"] = @"UI\Widgets\Console\Human\infocard-neutral-attack-hero.blp"
        };
        foreach (var (key, expectedPath) in expectedAttacks)
        {
            var (path, label) = ObjectEditorHelper.ResolveAttackTypeHudInfo(key);
            Assert.Equal(expectedPath, path);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }

        var expectedArmors = new Dictionary<string, string>
        {
            ["none"] = @"UI\Widgets\Console\Human\infocard-neutral-armor-unarmored.blp",
            ["small"] = @"UI\Widgets\Console\Human\infocard-neutral-armor-small.blp",
            ["medium"] = @"UI\Widgets\Console\Human\infocard-neutral-armor-medium.blp",
            ["large"] = @"UI\Widgets\Console\Human\infocard-neutral-armor-large.blp",
            ["heavy"] = @"UI\Widgets\Console\Human\infocard-neutral-armor-large.blp",
            ["fort"] = @"UI\Widgets\Console\Human\infocard-neutral-armor-fortified.blp",
            ["hero"] = @"UI\Widgets\Console\Human\infocard-armor-hero.blp",
            ["divine"] = @"UI\Widgets\Console\Human\infocard-armor-divine.blp"
        };
        foreach (var (key, expectedPath) in expectedArmors)
        {
            var (path, label) = ObjectEditorHelper.ResolveArmorTypeHudInfo(key);
            Assert.Equal(expectedPath, path);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }

        Assert.Equal(@"UI\Widgets\Console\Human\infocard-heroattributes-str.blp", ObjectEditorHelper.ResolveHeroPrimaryAttributeIconPath("STR"));
        Assert.Equal(@"UI\Widgets\Console\Human\infocard-heroattributes-agi.blp", ObjectEditorHelper.ResolveHeroPrimaryAttributeIconPath("AGI"));
        Assert.Equal(@"UI\Widgets\Console\Human\infocard-heroattributes-int.blp", ObjectEditorHelper.ResolveHeroPrimaryAttributeIconPath("INT"));

        // When Warcraft III 1.27 MPQs are available locally, verify every single BLP exists and decodes cleanly (non-fallback)
        const string wc3Dir = @"F:\WARCRAFT\Warcraft III 1.27";
        if (Directory.Exists(wc3Dir))
        {
            var blp = new BlpIconService();
            blp.ConfigureBaseMpqs(new WorkspaceSettings { Warcraft3Dir = wc3Dir });
            var allPaths = expectedAttacks.Values
                .Concat(expectedArmors.Values)
                .Append(ObjectEditorHelper.HeroStrengthIconPath)
                .Append(ObjectEditorHelper.HeroAgilityIconPath)
                .Append(ObjectEditorHelper.HeroIntelligenceIconPath)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var iconPath in allPaths)
            {
                var bytes = blp.FindRawFileBytes(iconPath);
                Assert.True(bytes != null && bytes.Length > 20, $"Falta el icono oficial en MPQ: {iconPath}");
            }
        }
    }

    [Fact]
    public void IconCatalogFilterHelper_FiltersByStyleRaceUsageAndSearch_WithoutTruncation()
    {
        var samplePaths = new List<string>
        {
            @"ReplaceableTextures\CommandButtons\BTNHeroPaladin.blp",
            @"ReplaceableTextures\CommandButtonsDisabled\DISBTNHeroPaladin.blp",
            @"ReplaceableTextures\PassiveButtons\PASBTNDevotion.blp",
            @"ReplaceableTextures\CommandButtonsDisabled\DISPASBTNDevotion.blp",
            @"ReplaceableTextures\CommandButtons\BTNGrunt.blp",
            @"ReplaceableTextures\CommandButtons\BTNGhoul.blp",
            @"ReplaceableTextures\CommandButtons\BTNArcher.blp",
            @"ReplaceableTextures\CommandButtons\BTNPotionGreenSmall.blp",
            @"ReplaceableTextures\CommandButtons\BTNTownHall.blp",
            @"UI\Widgets\Console\Human\infocard-neutral-attack-hero.blp",
            @"war3mapImported\BTNCustomHero.blp"
        };
        // Add 1,400 extra synthetic icons to confirm there is NO artificial 1200-item truncation
        for (int i = 0; i < 1400; i++)
            samplePaths.Add($@"ReplaceableTextures\CommandButtons\BTNSynthOrcUnit{i:D4}.blp");

        var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"war3mapImported\BTNCustomHero.blp" };
        var catalog = IconCatalogFilterHelper.BuildCatalog(samplePaths, imported, baseData: null);

        // Default NoDis hides all DIS* icons and returns ALL >1400 non-DIS icons (no 1200 cap!)
        var noDis = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.NoDis, IconRaceFilter.All, IconUsageFilter.All, "");
        Assert.DoesNotContain(noDis, e => e.IsDisabled);
        Assert.True(noDis.Count > 1400);

        // Style filters
        var onlyDis = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.Dis, IconRaceFilter.All, IconUsageFilter.All, "");
        Assert.Equal(2, onlyDis.Count);
        Assert.All(onlyDis, e => Assert.True(e.IsDisabled));

        var onlyPas = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.Pas, IconRaceFilter.All, IconUsageFilter.All, "");
        Assert.Single(onlyPas);
        Assert.Equal("PASBTNDevotion.blp", onlyPas[0].FileName);

        var onlyUi = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.Ui, IconRaceFilter.All, IconUsageFilter.All, "");
        Assert.Single(onlyUi);
        Assert.Equal("infocard-neutral-attack-hero.blp", onlyUi[0].FileName);

        var onlyImported = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.Imported, IconRaceFilter.All, IconUsageFilter.All, "");
        Assert.Single(onlyImported);
        Assert.Equal("BTNCustomHero.blp", onlyImported[0].FileName);

        // Race & category filters
        var humanHeroes = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.NoDis, IconRaceFilter.Human, IconUsageFilter.Heroes, "");
        Assert.Contains(humanHeroes, e => e.FileName == "BTNHeroPaladin.blp");
        Assert.DoesNotContain(humanHeroes, e => e.FileName == "BTNGrunt.blp");

        var undead = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.NoDis, IconRaceFilter.Undead, IconUsageFilter.All, "");
        Assert.Contains(undead, e => e.FileName == "BTNGhoul.blp");

        var nightElf = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.NoDis, IconRaceFilter.NightElf, IconUsageFilter.All, "");
        Assert.Contains(nightElf, e => e.FileName == "BTNArcher.blp");

        var items = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.NoDis, IconRaceFilter.All, IconUsageFilter.Items, "");
        Assert.Contains(items, e => e.FileName == "BTNPotionGreenSmall.blp");

        var buildings = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.NoDis, IconRaceFilter.Human, IconUsageFilter.Buildings, "");
        Assert.Contains(buildings, e => e.FileName == "BTNTownHall.blp");

        // Multi-token search
        var multi = IconCatalogFilterHelper.Filter(catalog, IconStyleFilter.NoDis, IconRaceFilter.All, IconUsageFilter.All, "orc 1399");
        Assert.Single(multi);
        Assert.Equal("BTNSynthOrcUnit1399.blp", multi[0].FileName);
    }

    [Fact]
    public void ObjectEditorPersistence_DeleteCustomAndResetField_RoundtripsCleanlyToW3u()
    {
        var mapPath = Path.Combine(_tempDir, "ObjPersistBase.w3m");
        var savedStep1 = Path.Combine(_tempDir, "ObjPersistStep1.w3m");
        var savedStep2 = Path.Combine(_tempDir, "ObjPersistStep2.w3m");
        var baseData = new WarcraftBaseDataService();
        var mpq = new MapMpqService(baseData);

        MapShadowAndMinimapService.CreateBlankMap(new NewMapCreationOptions
        {
            FilePath = mapPath,
            MapName = "Object Persist Test",
            Author = "Tests",
            CellsWidth = 32,
            CellsHeight = 32,
            TilesetChar = 'L',
            BaseCliffLayer = 2
        }, baseData);

        var project = mpq.LoadMap(mapPath);

        // Step 1: Add custom hero H000 + modify standard Footman hfoo (unam + uhpm)
        var customHero = new W3ObjectEntry
        {
            Category = W3ObjectCategory.Units,
            OldId = WarcraftBaseDataService.RawcodeToInt("Hpal"),
            NewId = WarcraftBaseDataService.RawcodeToInt("H000"),
            BaseRawcode = "Hpal",
            Rawcode = "H000",
            Name = "Paladin Custom",
            IsModified = true
        };
        customHero.Fields.Add(new W3ObjectField
        {
            FieldId = "unam",
            FieldIntId = WarcraftBaseDataService.RawcodeToInt("unam"),
            DataType = ObjectDataType.String,
            RawValue = "Paladin Custom",
            ResolvedValue = "Paladin Custom",
            IsModified = true
        });
        project.Objects.Add(customHero);

        var stdFootman = new W3ObjectEntry
        {
            Category = W3ObjectCategory.Units,
            OldId = WarcraftBaseDataService.RawcodeToInt("hfoo"),
            NewId = 0,
            BaseRawcode = "hfoo",
            Rawcode = "hfoo",
            Name = "Elite Footman",
            IsModified = true
        };
        stdFootman.Fields.Add(new W3ObjectField
        {
            FieldId = "unam",
            FieldIntId = WarcraftBaseDataService.RawcodeToInt("unam"),
            DataType = ObjectDataType.String,
            RawValue = "Elite Footman",
            ResolvedValue = "Elite Footman",
            IsModified = true
        });
        stdFootman.Fields.Add(new W3ObjectField
        {
            FieldId = "uhpm",
            FieldIntId = WarcraftBaseDataService.RawcodeToInt("uhpm"),
            DataType = ObjectDataType.Int,
            RawValue = "950",
            ResolvedValue = "950",
            IsModified = true
        });
        project.Objects.Add(stdFootman);
        project.HasObjectEdits = true;

        mpq.SaveMap(project, savedStep1);

        var reloaded1 = mpq.LoadMap(savedStep1);
        Assert.Contains(reloaded1.Objects, o => o.Rawcode == "H000" && o.IsCustom);
        var loadedFoo1 = reloaded1.Objects.FirstOrDefault(o => o.Rawcode == "hfoo" && !o.IsCustom);
        Assert.NotNull(loadedFoo1);
        Assert.Equal(2, loadedFoo1!.Fields.Count);

        // Step 2: Delete custom hero H000, and reset 'uhpm' on Footman (keeping only 'unam')
        foreach (var custom in reloaded1.Objects.Where(o => o.Rawcode == "H000").ToList())
            reloaded1.Objects.Remove(custom);
        foreach (var hpField in loadedFoo1.Fields.Where(f => f.FieldId == "uhpm").ToList())
            loadedFoo1.Fields.Remove(hpField);
        loadedFoo1.IsModified = true;
        reloaded1.HasObjectEdits = true;

        mpq.SaveMap(reloaded1, savedStep2);

        var reloaded2 = mpq.LoadMap(savedStep2);
        Assert.DoesNotContain(reloaded2.Objects, o => o.Rawcode == "H000");
        var loadedFoo2 = reloaded2.Objects.FirstOrDefault(o => o.Rawcode == "hfoo" && !o.IsCustom);
        Assert.NotNull(loadedFoo2);
        Assert.Single(loadedFoo2!.Fields);
        Assert.Equal("unam", loadedFoo2.Fields[0].FieldId);
        Assert.Equal("Elite Footman", loadedFoo2.Fields[0].ResolvedValue);
    }

    [Fact]
    public async Task BlizzardMeleeTrigger_RenameAndEditGui_CompilesCleanlyWithoutStaleInitCalls()
    {
        var mapPath = Path.Combine(_tempDir, "MeleeRenameBase.w3m");
        var withMeleePath = Path.Combine(_tempDir, "MeleeRenamePrepared.w3m");
        var baseData = new WarcraftBaseDataService();
        var mpq = new MapMpqService(baseData);

        MapShadowAndMinimapService.CreateBlankMap(new NewMapCreationOptions
        {
            FilePath = mapPath,
            MapName = "Melee Rename Test",
            Author = "Tests",
            CellsWidth = 32,
            CellsHeight = 32,
            TilesetChar = 'L',
            BaseCliffLayer = 2
        }, baseData);

        // Inject a standard Blizzard Melee Initialization trigger into war3map.j (exactly like DivideAndConquer.w3m)
        var baseProject = mpq.LoadMap(mapPath);
        baseProject.RawMapScript = """
            globals
                trigger gg_trg_Melee_Initialization = null
            endglobals

            //***************************************************************************
            //*
            //*  Triggers
            //*
            //***************************************************************************

            //===========================================================================
            // Trigger: Melee Initialization
            //
            // Default melee game initialization for all players
            //===========================================================================
            function Trig_Melee_Initialization_Actions takes nothing returns nothing
                call MeleeStartingVisibility(  )
                call MeleeStartingHeroLimit(  )
                call MeleeGrantHeroItems(  )
                call MeleeStartingResources(  )
                call MeleeClearExcessUnits(  )
                call MeleeStartingUnits(  )
                call MeleeStartingAI(  )
                call MeleeInitVictoryDefeat(  )
            endfunction

            //===========================================================================
            function InitTrig_Melee_Initialization takes nothing returns nothing
                set gg_trg_Melee_Initialization = CreateTrigger(  )
                call TriggerAddAction( gg_trg_Melee_Initialization, function Trig_Melee_Initialization_Actions )
            endfunction

            //===========================================================================
            function InitCustomTriggers takes nothing returns nothing
                call InitTrig_Melee_Initialization(  )
            endfunction

            //===========================================================================
            function RunInitializationTriggers takes nothing returns nothing
                call ConditionalTriggerExecute( gg_trg_Melee_Initialization )
            endfunction

            function main takes nothing returns nothing
                call InitCustomTriggers(  )
                call RunInitializationTriggers(  )
            endfunction
            """;
        baseProject.HasMiscEdits = true;
        mpq.SaveMap(baseProject, withMeleePath);

        var project = mpq.LoadMap(withMeleePath);
        var melee = project.AllTriggerNodes.FirstOrDefault(n =>
            n.Kind == TriggerNodeKind.GuiTrigger &&
            n.Name.Equals("Melee Initialization", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(melee);
        // Verify Melee actions are parsed as real MeleeGame actions (not generic CustomScript)
        Assert.Contains(melee!.Actions, a => a.Category == "MeleeGame" && a.FunctionName == "MeleeStartingHeroLimit");

        // Simulate user renaming "Melee Initialization" -> "Melee" and syncing GUI
        var oldName = melee.Name;
        melee.Name = "Melee";
        JassKnowledgeService.RenameTriggerInProjectScript(project, melee, oldName, melee.Name);

        var oldCode = melee.Code;
        var generated = melee.GenerateJassFromGui();
        melee.Code = generated;
        JassKnowledgeService.SyncNodeCodeToProject(project, melee, oldCode, generated);
        JassKnowledgeService.EnsureTriggerCallAndGlobal(project, JassKnowledgeService.CleanTriggerIdentifier(melee.Name), ensureInitCall: true);

        // Must NOT leave stale InitTrig_Melee_Initialization or duplicate InitTrig_Melee
        Assert.DoesNotContain("InitTrig_Melee_Initialization", project.RawMapScript);
        Assert.DoesNotContain("gg_trg_Melee_Initialization", project.RawMapScript);
        Assert.Contains("function InitTrig_Melee takes nothing returns nothing", project.RawMapScript);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(project.RawMapScript, @"function\s+InitTrig_Melee\b"));

        var compiler = new JassCompilerService();
        var res = await compiler.ValidateAndCompileAsync(project.RawMapScript, new WorkspaceSettings(), "MeleeRenameTest.w3m");
        Assert.True(res.Success, string.Join("\n", res.Diagnostics.Select(d => $"{d.DisplayLine}: {d.Message}")));
    }
}
