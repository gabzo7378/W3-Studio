using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using War3Net.Build.Object;
using War3Net.IO.Mpq;
using W3Studio.Models;

namespace W3Studio.Services;

public class BaseObjectMetadata
{
    public string Rawcode { get; set; } = string.Empty;
    public W3ObjectCategory Category { get; set; } = W3ObjectCategory.Units;
    public string SubcategoryFolder { get; set; } = "General";
    public string Name { get; set; } = string.Empty;
    public string EditorSuffix { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
    public string Ubertip { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    public string Art { get; set; } = string.Empty;
    public string ModelFile { get; set; } = string.Empty;
    public int ButtonPosX { get; set; }
    public int ButtonPosY { get; set; }

    public Dictionary<string, string> DefaultFields { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public class FieldMetaDefinition
{
    public string FieldId { get; set; } = string.Empty;
    public string Category { get; set; } = "Data";
    public string DisplayName { get; set; } = string.Empty;
    public string SlkColumn { get; set; } = string.Empty;
    public ObjectDataType DataType { get; set; } = ObjectDataType.String;
    public List<FieldChoiceOption>? Choices { get; set; }
    public int? MinInt { get; set; }
    public int? MaxInt { get; set; }
    public double? MinReal { get; set; }
    public double? MaxReal { get; set; }
    public int DecimalPlaces { get; set; } = 2;
}

public class WarcraftBaseDataService
{
    private readonly Dictionary<string, string> _fieldDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FieldMetaDefinition> _fieldMeta = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BaseObjectMetadata> _baseObjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _westringLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _terrainTileTexturePaths = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBaseMpqLoaded { get; private set; }
    public int LoadedBaseObjectsCount => _baseObjects.Count;
    public IReadOnlyDictionary<string, BaseObjectMetadata> AllBaseObjects => _baseObjects;

    public WarcraftBaseDataService()
    {
        InitializeDefaultFieldCatalog();
    }

    private void InitializeDefaultFieldCatalog()
    {
        // Opciones desplegables estándar de Warcraft III (limpias y directas)
        var attackTypeChoices = new List<FieldChoiceOption>
        {
            new("normal", "Normal"),
            new("pierce", "Perforador / Pierce"),
            new("siege", "Asedio / Siege"),
            new("magic", "Mágico / Magic"),
            new("chaos", "Caos / Chaos"),
            new("hero", "Héroe / Hero"),
            new("spells", "Hechizos / Spells")
        };

        var defenseTypeChoices = new List<FieldChoiceOption>
        {
            new("none", "Sin Armadura / Unarmored"),
            new("small", "Ligera / Small"),
            new("medium", "Media / Medium"),
            new("heavy", "Pesada / Heavy"),
            new("fort", "Fortificada / Fortified"),
            new("hero", "Héroe / Hero"),
            new("divine", "Divina / Divine")
        };

        var primaryAttrChoices = new List<FieldChoiceOption>
        {
            new("STR", "Fuerza / Strength"),
            new("AGI", "Agilidad / Agility"),
            new("INT", "Inteligencia / Intelligence")
        };

        var weaponTypeChoices = new List<FieldChoiceOption>
        {
            new("normal", "Normal"),
            new("instant", "Instantáneo / Instant"),
            new("artillery", "Artillería / Artillery"),
            new("aline", "Artillería en línea / Artillery Line"),
            new("missile", "Proyectil / Missile"),
            new("msplash", "Proyectil Salpicadura / Missile Splash"),
            new("mbounce", "Proyectil Rebote / Missile Bounce"),
            new("mline", "Proyectil Línea / Missile Line")
        };

        var moveTypeChoices = new List<FieldChoiceOption>
        {
            new("foot", "A pie / Foot"),
            new("fly", "Vuelo / Fly"),
            new("hover", "Flotar / Hover"),
            new("float", "Agua / Float"),
            new("amph", "Anfibio / Amph")
        };

        var attacksEnabledChoices = new List<FieldChoiceOption>
        {
            new("0", "0 - Ninguno"),
            new("1", "1 - Ataque 1"),
            new("2", "2 - Ataque 2"),
            new("3", "3 - Ambos")
        };

        var itemClassChoices = new List<FieldChoiceOption>
        {
            new("Permanent", "Permanente / Permanent"),
            new("Charged", "Con Cargas / Charged"),
            new("PowerUp", "Poder Instantáneo / Power Up"),
            new("Artifact", "Artefacto / Artifact"),
            new("Purchasable", "Comprable / Purchasable"),
            new("Campaign", "Campaña / Campaign"),
            new("Miscellaneous", "Varios / Miscellaneous")
        };

        var boolYesNoChoices = new List<FieldChoiceOption>
        {
            new("0", "0 - No"),
            new("1", "1 - Sí")
        };

        // Unidades (.w3u) e Ítems (.w3t)
        AddField("unam", "Text", "Name (Nombre)", "Name", ObjectDataType.String);
        AddField("unsf", "Text", "Editor Suffix (Sufijo del Editor)", "EditorSuffix", ObjectDataType.String);
        AddField("utip", "Text", "Tooltip - Basic (Tooltip Básico)", "Tip", ObjectDataType.String);
        AddField("utub", "Text", "Tooltip - Extended (Tooltip Extendido / Ubertip)", "Ubertip", ObjectDataType.String);
        AddField("uhot", "Text", "Hotkey (Tecla Rápida)", "Hotkey", ObjectDataType.String);
        AddField("upro", "Text", "Proper Names (Nombres de Héroe)", "Propernames", ObjectDataType.String);
        AddField("usnd", "Sound", "Unit Sound Set (Conjunto de Voces)", "unitSound", ObjectDataType.String);
        AddField("uico", "Art", "Icon - Game Interface (Icono)", "Art", ObjectDataType.String);
        AddField("ussi", "Art", "Icon - Score Screen", "ScoreScreenIcon", ObjectDataType.String);
        AddField("umdl", "Art", "Model File (Archivo de Modelo .mdx)", "file", ObjectDataType.String);
        AddField("usca", "Art", "Scaling Value (Escala de Modelo)", "modelScale", ObjectDataType.Real, minReal: 0.05, maxReal: 20.0, decimalPlaces: 2);
        AddField("ussc", "Art", "Selection Scale (Círculo de Selección)", "scale", ObjectDataType.Real, minReal: 0.1, maxReal: 20.0, decimalPlaces: 2);
        AddField("uclr", "Art", "Tinting Color 1 (Red 0-255)", "red", ObjectDataType.Int, minInt: 0, maxInt: 255);
        AddField("uclg", "Art", "Tinting Color 2 (Green 0-255)", "green", ObjectDataType.Int, minInt: 0, maxInt: 255);
        AddField("uclb", "Art", "Tinting Color 3 (Blue 0-255)", "blue", ObjectDataType.Int, minInt: 0, maxInt: 255);
        AddField("ubpx", "Art", "Button Position (X)", "ButtonposX", ObjectDataType.Int, minInt: 0, maxInt: 3);
        AddField("ubpy", "Art", "Button Position (Y)", "ButtonposY", ObjectDataType.Int, minInt: 0, maxInt: 2);
        AddField("uhpm", "Stats", "Hit Points Maximum (Base HP)", "HP", ObjectDataType.Int, minInt: 1, maxInt: 10000000);
        AddField("uhpr", "Stats", "Hit Points Regeneration Rate", "regenHP", ObjectDataType.Real, minReal: -100.0, maxReal: 1000.0, decimalPlaces: 2);
        AddField("umpm", "Stats", "Mana Maximum", "manaN", ObjectDataType.Int, minInt: 0, maxInt: 10000000);
        AddField("umpr", "Stats", "Mana Regeneration", "regenMana", ObjectDataType.Real, minReal: -100.0, maxReal: 1000.0, decimalPlaces: 2);
        AddField("umpi", "Stats", "Mana Initial Amount", "mana0", ObjectDataType.Int, minInt: 0, maxInt: 10000000);
        AddField("udef", "Combat", "Defense Base (Armadura)", "def", ObjectDataType.Int, minInt: -100, maxInt: 1000);
        AddField("udty", "Combat", "Defense Type (Tipo de Armadura)", "defType", ObjectDataType.String, choices: defenseTypeChoices);
        AddField("ua1b", "Combat", "Attack 1 - Damage Base (Daño Base)", "dmgplus1", ObjectDataType.Int, minInt: 0, maxInt: 1000000);
        AddField("ua1d", "Combat", "Attack 1 - Damage Number of Dice", "dice1", ObjectDataType.Int, minInt: 0, maxInt: 1000);
        AddField("ua1s", "Combat", "Attack 1 - Damage Sides per Die", "sides1", ObjectDataType.Int, minInt: 0, maxInt: 1000);
        AddField("ua1c", "Combat", "Attack 1 - Cooldown Time (BAT)", "cool1", ObjectDataType.Real, minReal: 0.05, maxReal: 30.0, decimalPlaces: 2);
        AddField("ua1r", "Combat", "Attack 1 - Range (Rango de Ataque)", "rangeN1", ObjectDataType.Int, minInt: 0, maxInt: 20000);
        AddField("ua1t", "Combat", "Attack 1 - Attack Type", "atkType1", ObjectDataType.String, choices: attackTypeChoices);
        AddField("ua1w", "Combat", "Attack 1 - Weapon Type", "weapTp1", ObjectDataType.String, choices: weaponTypeChoices);
        AddField("ua1m", "Combat", "Attack 1 - Projectile Art", "Missileart", ObjectDataType.String);
        AddField("ua1z", "Combat", "Attack 1 - Projectile Speed", "Missilespeed", ObjectDataType.Int, minInt: 0, maxInt: 10000);
        AddField("uaen", "Combat", "Attacks Enabled", "weapsOn", ObjectDataType.Int, choices: attacksEnabledChoices, minInt: 0, maxInt: 3);
        AddField("umvs", "Movement", "Speed Base (Velocidad de Movimiento)", "spd", ObjectDataType.Int, minInt: 1, maxInt: 522);
        AddField("umvt", "Movement", "Type (Foot/Fly/Float/Hover)", "movetp", ObjectDataType.String, choices: moveTypeChoices);
        AddField("umvh", "Movement", "Height (Altura de Vuelo)", "moveHeight", ObjectDataType.Real, minReal: 0.0, maxReal: 5000.0, decimalPlaces: 1);
        AddField("ucol", "Pathing", "Collision Size (Tamaño de Colisión)", "collision", ObjectDataType.Real, minReal: 0.0, maxReal: 500.0, decimalPlaces: 1);
        AddField("ugol", "Stats", "Gold Cost (Coste de Oro)", "goldcost", ObjectDataType.Int, minInt: 0, maxInt: 1000000);
        AddField("ulum", "Stats", "Lumber Cost (Coste de Madera)", "lumbercost", ObjectDataType.Int, minInt: 0, maxInt: 1000000);
        AddField("ufma", "Stats", "Food Produced (Comida Producida)", "fmade", ObjectDataType.Int, minInt: 0, maxInt: 100);
        AddField("ufoo", "Stats", "Food Cost (Coste de Comida)", "fused", ObjectDataType.Int, minInt: 0, maxInt: 100);
        AddField("ubld", "Stats", "Build Time (Tiempo de Construcción)", "bldtm", ObjectDataType.Int, minInt: 1, maxInt: 10000);
        AddField("ulev", "Stats", "Level (Nivel)", "level", ObjectDataType.Int, minInt: 1, maxInt: 100);
        AddField("ustr", "Stats", "Hero - Starting Strength (Fuerza Inicial)", "STR", ObjectDataType.Int, minInt: 1, maxInt: 1000);
        AddField("uagi", "Stats", "Hero - Starting Agility (Agilidad Inicial)", "AGI", ObjectDataType.Int, minInt: 1, maxInt: 1000);
        AddField("uint", "Stats", "Hero - Starting Intelligence (Inteligencia Inicial)", "INT", ObjectDataType.Int, minInt: 1, maxInt: 1000);
        AddField("ustp", "Stats", "Hero - Strength per Level", "STRplus", ObjectDataType.Real, minReal: 0.0, maxReal: 100.0, decimalPlaces: 2);
        AddField("uagp", "Stats", "Hero - Agility per Level", "AGIplus", ObjectDataType.Real, minReal: 0.0, maxReal: 100.0, decimalPlaces: 2);
        AddField("uinp", "Stats", "Hero - Intelligence per Level", "INTplus", ObjectDataType.Real, minReal: 0.0, maxReal: 100.0, decimalPlaces: 2);
        AddField("upra", "Stats", "Hero - Primary Attribute (STR/AGI/INT)", "Primary", ObjectDataType.String, choices: primaryAttrChoices);
        AddField("uabi", "Abilities", "Normal (Habilidades Normales)", "abilList", ObjectDataType.String);
        AddField("uhab", "Abilities", "Hero (Habilidades de Héroe)", "heroAbilList", ObjectDataType.String);
        AddField("ures", "Techtree", "Researches Available", "Researches", ObjectDataType.String);
        AddField("utra", "Techtree", "Units Trained", "Trains", ObjectDataType.String);
        AddField("usei", "Techtree", "Items Sold", "Sellitems", ObjectDataType.String);
        AddField("useu", "Techtree", "Units Sold", "Sellunits", ObjectDataType.String);

        // Ítems (.w3t)
        AddField("iabi", "Abilities", "Abilities (Habilidades del Ítem)", "abilList", ObjectDataType.String);
        AddField("iico", "Art", "Interface Icon (Icono del Ítem)", "Art", ObjectDataType.String);
        AddField("ifil", "Art", "Model Used (Modelo en el Suelo)", "file", ObjectDataType.String);
        AddField("igol", "Stats", "Gold Cost (Coste de Oro)", "goldcost", ObjectDataType.Int, minInt: 0, maxInt: 1000000);
        AddField("ilum", "Stats", "Lumber Cost (Coste de Madera)", "lumbercost", ObjectDataType.Int, minInt: 0, maxInt: 1000000);
        AddField("ilev", "Stats", "Level (Nivel del Ítem)", "Level", ObjectDataType.Int, minInt: 0, maxInt: 100);
        AddField("icla", "Stats", "Classification (Clase de Ítem)", "class", ObjectDataType.String, choices: itemClassChoices);
        AddField("isto", "Stats", "Stock Maximum (Stock Máximo)", "stockMax", ObjectDataType.Int, minInt: 0, maxInt: 1000);
        AddField("istr", "Stats", "Stock Replenish Interval", "stockRegen", ObjectDataType.Int, minInt: 0, maxInt: 100000);
        AddField("iusa", "Stats", "Actively Used (Usable Activamente)", "usable", ObjectDataType.Int, choices: boolYesNoChoices, minInt: 0, maxInt: 1);
        AddField("iper", "Stats", "Perishable (Perecedero / Consumible)", "perishable", ObjectDataType.Int, choices: boolYesNoChoices, minInt: 0, maxInt: 1);
        AddField("iuse", "Stats", "Number of Charges (Número de Cargas)", "uses", ObjectDataType.Int, minInt: 0, maxInt: 1000);
        AddField("icid", "Stats", "Cooldown Group (Grupo de Cooldown)", "cooldownID", ObjectDataType.String);
        AddField("idrp", "Stats", "Dropped When Carrier Dies", "droppable", ObjectDataType.Int, choices: boolYesNoChoices, minInt: 0, maxInt: 1);
        AddField("ipaw", "Stats", "Can Be Sold To Merchants", "pawnable", ObjectDataType.Int, choices: boolYesNoChoices, minInt: 0, maxInt: 1);
        AddField("ipri", "Stats", "Priority", "prio", ObjectDataType.Int, minInt: 0, maxInt: 1000);

        // Habilidades (.w3a)
        AddField("anam", "Text", "Name (Nombre de Habilidad)", "Name", ObjectDataType.String);
        AddField("ansf", "Text", "Editor Suffix (Sufijo del Editor)", "EditorSuffix", ObjectDataType.String);
        AddField("ahky", "Text", "Hotkey - Normal (Tecla Rápida)", "Hotkey", ObjectDataType.String);
        AddField("auhk", "Text", "Hotkey - Turn Off", "Unhotkey", ObjectDataType.String);
        AddField("arhk", "Text", "Hotkey - Learn", "Researchhotkey", ObjectDataType.String);
        AddField("aart", "Art", "Icon - Normal (Icono)", "Art", ObjectDataType.String);
        AddField("auar", "Art", "Icon - Turn Off", "Unart", ObjectDataType.String);
        AddField("arar", "Art", "Icon - Research", "ResearchArt", ObjectDataType.String);
        AddField("abpx", "Art", "Button Position - Normal (X)", "ButtonposX", ObjectDataType.Int, minInt: 0, maxInt: 3);
        AddField("abpy", "Art", "Button Position - Normal (Y)", "ButtonposY", ObjectDataType.Int, minInt: 0, maxInt: 2);
        AddField("acat", "Art", "Caster (Efecto en Lanzador)", "CasterArt", ObjectDataType.String);
        AddField("atat", "Art", "Target (Efecto en Objetivo)", "TargetArt", ObjectDataType.String);
        AddField("asat", "Art", "Special (Efecto Especial)", "SpecialArt", ObjectDataType.String);
        AddField("aeat", "Art", "Effect (Efecto de Área)", "EffectArt", ObjectDataType.String);
        AddField("amat", "Art", "Missile Art (Arte de Proyectil)", "Missileart", ObjectDataType.String);
        AddField("amsp", "Art", "Missile Speed (Velocidad de Proyectil)", "Missilespeed", ObjectDataType.Int, minInt: 0, maxInt: 10000);
        AddField("atp1", "Text", "Tooltip - Normal", "Tip", ObjectDataType.String);
        AddField("aub1", "Text", "Tooltip - Normal - Extended (Ubertip)", "Ubertip", ObjectDataType.String);
        AddField("aret", "Text", "Tooltip - Learn", "Researchtip", ObjectDataType.String);
        AddField("arut", "Text", "Tooltip - Learn - Extended", "Researchubertip", ObjectDataType.String);
        AddField("amcs", "Stats", "Mana Cost (Coste de Maná)", "Cost1", ObjectDataType.Int, minInt: 0, maxInt: 100000);
        AddField("acdn", "Stats", "Cooldown (Tiempo de Recarga)", "Cool1", ObjectDataType.Real, minReal: 0.0, maxReal: 3600.0, decimalPlaces: 2);
        AddField("aran", "Stats", "Cast Range (Rango de Lanzamiento)", "Rng1", ObjectDataType.Real, minReal: 0.0, maxReal: 20000.0, decimalPlaces: 1);
        AddField("aare", "Stats", "Area of Effect (Área de Efecto)", "Area1", ObjectDataType.Real, minReal: 0.0, maxReal: 5000.0, decimalPlaces: 1);
        AddField("adur", "Stats", "Duration - Normal", "Dur1", ObjectDataType.Real, minReal: 0.0, maxReal: 3600.0, decimalPlaces: 2);
        AddField("ahdu", "Stats", "Duration - Hero", "HeroDur1", ObjectDataType.Real, minReal: 0.0, maxReal: 3600.0, decimalPlaces: 2);
        AddField("acas", "Stats", "Casting Time", "Cast1", ObjectDataType.Real, minReal: 0.0, maxReal: 60.0, decimalPlaces: 2);
        AddField("atar", "Stats", "Targets Allowed", "targs1", ObjectDataType.String);
        AddField("abuf", "Stats", "Buffs", "BuffID1", ObjectDataType.String);
        AddField("aeff", "Stats", "Effects", "EfctID1", ObjectDataType.String);
        AddField("alev", "Stats", "Levels (Niveles Totales)", "levels", ObjectDataType.Int, minInt: 1, maxInt: 100);
        AddField("aher", "Stats", "Hero Ability (¿Es Habilidad de Héroe?)", "hero", ObjectDataType.Int, choices: boolYesNoChoices, minInt: 0, maxInt: 1);
        AddField("aite", "Stats", "Item Ability (¿Es Habilidad de Ítem?)", "item", ObjectDataType.Int, choices: boolYesNoChoices, minInt: 0, maxInt: 1);
        AddField("aord", "Text", "Order String - Use/Turn On", "Order", ObjectDataType.String);

        // Buffs (.w3h)
        AddField("fnam", "Text", "Name (Editor Only)", "EditorName", ObjectDataType.String);
        AddField("ftip", "Text", "Tooltip (Nombre en Juego)", "Bufftip", ObjectDataType.String);
        AddField("fube", "Text", "Tooltip - Extended", "Buffubertip", ObjectDataType.String);
        AddField("fart", "Art", "Icon", "Buffart", ObjectDataType.String);
        AddField("ftat", "Art", "Target (Efecto Visual)", "TargetArt", ObjectDataType.String);
        AddField("fta0", "Art", "Target Attachment Point 1", "Targetattach", ObjectDataType.String);

        // Destructibles (.w3b)
        AddField("bnam", "Text", "Name (Nombre)", "Name", ObjectDataType.String);
        AddField("bfil", "Art", "Model File (Archivo .mdx)", "file", ObjectDataType.String);
        AddField("bhps", "Stats", "Hit Points (Vida)", "HP", ObjectDataType.Real, minReal: 1.0, maxReal: 1000000.0, decimalPlaces: 1);

        // Mejoras (.w3q)
        AddField("gnam", "Text", "Name (Nombre de Mejora)", "Name", ObjectDataType.String);
        AddField("gtp1", "Text", "Tooltip", "Tip", ObjectDataType.String);
        AddField("gub1", "Text", "Tooltip - Extended", "Ubertip", ObjectDataType.String);
        AddField("ghk1", "Text", "Hotkey", "Hotkey", ObjectDataType.String);
        AddField("gar1", "Art", "Icon", "Art", ObjectDataType.String);
        AddField("gglb", "Stats", "Gold Cost Base", "goldbase", ObjectDataType.Int, minInt: 0, maxInt: 1000000);
        AddField("gglm", "Stats", "Gold Cost Increment", "goldmod", ObjectDataType.Int, minInt: 0, maxInt: 1000000);
    }

    private void AddField(string id, string cat, string display, string slkCol, ObjectDataType type,
        List<FieldChoiceOption>? choices = null,
        int? minInt = null, int? maxInt = null,
        double? minReal = null, double? maxReal = null,
        int decimalPlaces = 2)
    {
        var fullDisplay = $"{cat} - {display}";
        _fieldDisplayNames[id] = fullDisplay;
        _fieldMeta[id] = new FieldMetaDefinition
        {
            FieldId = id,
            Category = cat,
            DisplayName = fullDisplay,
            SlkColumn = slkCol,
            DataType = type,
            Choices = choices,
            MinInt = minInt,
            MaxInt = maxInt,
            MinReal = minReal,
            MaxReal = maxReal,
            DecimalPlaces = decimalPlaces
        };
    }

    public void LoadFromWarcraftFolder(WorkspaceSettings settings)
    {
        var mpqPaths = new[]
        {
            settings.War3MpqPath,
            settings.War3xMpqPath,
            settings.War3PatchMpqPath
        };

        var stringFiles = new (string Path, W3ObjectCategory Cat, string DefaultFolder)[]
        {
            (@"Units\HumanUnitStrings.txt", W3ObjectCategory.Units, "Human"),
            (@"Units\OrcUnitStrings.txt", W3ObjectCategory.Units, "Orc"),
            (@"Units\UndeadUnitStrings.txt", W3ObjectCategory.Units, "Undead"),
            (@"Units\NightElfUnitStrings.txt", W3ObjectCategory.Units, "Night Elf"),
            (@"Units\NeutralUnitStrings.txt", W3ObjectCategory.Units, "Neutral"),
            (@"Units\CampaignUnitStrings.txt", W3ObjectCategory.Units, "Campaign"),
            (@"Units\ItemStrings.txt", W3ObjectCategory.Items, "Miscellaneous"),
            (@"Units\CommonAbilityStrings.txt", W3ObjectCategory.Abilities, "Special / Common"),
            (@"Units\HumanAbilityStrings.txt", W3ObjectCategory.Abilities, "Human"),
            (@"Units\OrcAbilityStrings.txt", W3ObjectCategory.Abilities, "Orc"),
            (@"Units\UndeadAbilityStrings.txt", W3ObjectCategory.Abilities, "Undead"),
            (@"Units\NightElfAbilityStrings.txt", W3ObjectCategory.Abilities, "Night Elf"),
            (@"Units\NeutralAbilityStrings.txt", W3ObjectCategory.Abilities, "Neutral"),
            (@"Units\CampaignAbilityStrings.txt", W3ObjectCategory.Abilities, "Campaign"),
            (@"Units\ItemAbilityStrings.txt", W3ObjectCategory.Abilities, "Items"),
            (@"Units\HumanUpgradeStrings.txt", W3ObjectCategory.Upgrades, "Human"),
            (@"Units\OrcUpgradeStrings.txt", W3ObjectCategory.Upgrades, "Orc"),
            (@"Units\UndeadUpgradeStrings.txt", W3ObjectCategory.Upgrades, "Undead"),
            (@"Units\NightElfUpgradeStrings.txt", W3ObjectCategory.Upgrades, "Night Elf"),
            (@"Units\NeutralUpgradeStrings.txt", W3ObjectCategory.Upgrades, "Neutral")
        };

        var funcFiles = new (string Path, W3ObjectCategory Cat, string DefaultFolder)[]
        {
            (@"Units\HumanUnitFunc.txt", W3ObjectCategory.Units, "Human"),
            (@"Units\OrcUnitFunc.txt", W3ObjectCategory.Units, "Orc"),
            (@"Units\UndeadUnitFunc.txt", W3ObjectCategory.Units, "Undead"),
            (@"Units\NightElfUnitFunc.txt", W3ObjectCategory.Units, "Night Elf"),
            (@"Units\NeutralUnitFunc.txt", W3ObjectCategory.Units, "Neutral"),
            (@"Units\CampaignUnitFunc.txt", W3ObjectCategory.Units, "Campaign"),
            (@"Units\ItemFunc.txt", W3ObjectCategory.Items, "Miscellaneous"),
            (@"Units\CommonAbilityFunc.txt", W3ObjectCategory.Abilities, "Special / Common"),
            (@"Units\HumanAbilityFunc.txt", W3ObjectCategory.Abilities, "Human"),
            (@"Units\OrcAbilityFunc.txt", W3ObjectCategory.Abilities, "Orc"),
            (@"Units\UndeadAbilityFunc.txt", W3ObjectCategory.Abilities, "Undead"),
            (@"Units\NightElfAbilityFunc.txt", W3ObjectCategory.Abilities, "Night Elf"),
            (@"Units\NeutralAbilityFunc.txt", W3ObjectCategory.Abilities, "Neutral"),
            (@"Units\CampaignAbilityFunc.txt", W3ObjectCategory.Abilities, "Campaign"),
            (@"Units\ItemAbilityFunc.txt", W3ObjectCategory.Abilities, "Items"),
            (@"Units\HumanUpgradeFunc.txt", W3ObjectCategory.Upgrades, "Human"),
            (@"Units\OrcUpgradeFunc.txt", W3ObjectCategory.Upgrades, "Orc"),
            (@"Units\UndeadUpgradeFunc.txt", W3ObjectCategory.Upgrades, "Undead"),
            (@"Units\NightElfUpgradeFunc.txt", W3ObjectCategory.Upgrades, "Night Elf"),
            (@"Units\NeutralUpgradeFunc.txt", W3ObjectCategory.Upgrades, "Neutral")
        };

        foreach (var mpqPath in mpqPaths)
        {
            if (!File.Exists(mpqPath))
                continue;

            try
            {
                using var fs = new FileStream(mpqPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var archive = MpqArchive.Open(fs, false);
                IsBaseMpqLoaded = true;

                // Load UI\WorldEditStrings.txt so WESTRING_* in SLK files resolves to real names
                if (archive.FileExists(@"UI\WorldEditStrings.txt"))
                {
                    using var wes = archive.OpenFile(@"UI\WorldEditStrings.txt");
                    using var sr = new StreamReader(wes, Encoding.UTF8);
                    ParseWorldEditStrings(sr.ReadToEnd());
                }

                // Load SLK tables first so categories, classes, races, and default stats are populated
                LoadTerrainSlkFromArchive(archive, @"TerrainArt\Terrain.slk");
                LoadSlkFromArchive(archive, @"Units\ItemData.slk", W3ObjectCategory.Items);
                LoadSlkFromArchive(archive, @"Units\UnitData.slk", W3ObjectCategory.Units);
                LoadSlkFromArchive(archive, @"Units\UnitBalance.slk", W3ObjectCategory.Units);
                LoadSlkFromArchive(archive, @"Units\UnitUI.slk", W3ObjectCategory.Units);
                LoadSlkFromArchive(archive, @"Units\UnitWeapons.slk", W3ObjectCategory.Units);
                LoadSlkFromArchive(archive, @"Units\AbilityData.slk", W3ObjectCategory.Abilities);
                LoadSlkFromArchive(archive, @"Units\AbilityBuffData.slk", W3ObjectCategory.Buffs);
                LoadSlkFromArchive(archive, @"Units\DestructableData.slk", W3ObjectCategory.Destructables);
                LoadSlkFromArchive(archive, @"Doodads\Doodads.slk", W3ObjectCategory.Doodads);
                LoadSlkFromArchive(archive, @"Units\UpgradeData.slk", W3ObjectCategory.Upgrades);

                foreach (var sf in stringFiles)
                {
                    if (archive.FileExists(sf.Path))
                    {
                        using var stream = archive.OpenFile(sf.Path);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        ParseIniStyleObjectFile(reader.ReadToEnd(), sf.Cat, sf.DefaultFolder);
                    }
                }

                foreach (var ff in funcFiles)
                {
                    if (archive.FileExists(ff.Path))
                    {
                        using var stream = archive.OpenFile(ff.Path);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        ParseIniStyleObjectFile(reader.ReadToEnd(), ff.Cat, ff.DefaultFolder);
                    }
                }
            }
            catch
            {
                // Continue if MPQ is locked
            }
        }
    }

    private void ParseWorldEditStrings(string text)
    {
        using var sr = new StringReader(text);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim().Trim('"');
                _westringLookup[k] = v;
            }
        }
    }

    private string ResolveWestring(string val)
    {
        if (string.IsNullOrEmpty(val)) return string.Empty;
        if (val.StartsWith("WESTRING_", StringComparison.OrdinalIgnoreCase) &&
            _westringLookup.TryGetValue(val, out var resolved))
        {
            return resolved;
        }
        return val;
    }

    private void LoadTerrainSlkFromArchive(MpqArchive archive, string slkPath)
    {
        if (!archive.FileExists(slkPath))
            return;

        try
        {
            using var s = archive.OpenFile(slkPath);
            using var sr = new StreamReader(s, Encoding.UTF8);
            var slkText = sr.ReadToEnd();

            var headers = new Dictionary<int, string>();
            var rowIds = new Dictionary<int, string>();
            var rowDir = new Dictionary<int, string>();
            var rowFile = new Dictionary<int, string>();
            var curX = 1;
            var curY = 1;

            using var reader = new StringReader(slkText);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("C;", StringComparison.Ordinal))
                    continue;

                var parts = line.Split(';');
                string? kVal = null;
                for (var i = 1; i < parts.Length; i++)
                {
                    var p = parts[i];
                    if (p.Length < 2) continue;
                    var code = p[0];
                    var arg = p.Substring(1);
                    if (code == 'X' && int.TryParse(arg, out var x))
                        curX = x;
                    else if (code == 'Y' && int.TryParse(arg, out var y))
                        curY = y;
                    else if (code == 'K')
                        kVal = arg.Trim('"');
                }

                if (kVal == null) continue;
                if (curY == 1)
                {
                    headers[curX] = kVal;
                    continue;
                }

                if (curX == 1 && kVal.Length == 4)
                {
                    rowIds[curY] = kVal;
                    continue;
                }

                if (!headers.TryGetValue(curX, out var colName))
                    continue;

                if (colName.Equals("dir", StringComparison.OrdinalIgnoreCase))
                    rowDir[curY] = kVal;
                else if (colName.Equals("file", StringComparison.OrdinalIgnoreCase))
                    rowFile[curY] = kVal;
            }

            foreach (var kvp in rowIds)
            {
                if (rowDir.TryGetValue(kvp.Key, out var dir) && rowFile.TryGetValue(kvp.Key, out var file) &&
                    !string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(file))
                {
                    _terrainTileTexturePaths[kvp.Value] = $"{dir.Replace('/', '\\')}\\{file}.blp";
                }
            }
        }
        catch { }
    }

    public string? GetTerrainTileTexturePath(string tileRawcode)
    {
        if (string.IsNullOrWhiteSpace(tileRawcode))
            return null;
        return _terrainTileTexturePaths.TryGetValue(tileRawcode.Trim(), out var path) ? path : null;
    }

    private void LoadSlkFromArchive(MpqArchive archive, string slkPath, W3ObjectCategory category)
    {
        if (!archive.FileExists(slkPath))
            return;

        try
        {
            using var s = archive.OpenFile(slkPath);
            using var sr = new StreamReader(s, Encoding.UTF8);
            var content = sr.ReadToEnd();
            ParseSlkTable(content, category);
        }
        catch { }
    }

    private void ParseSlkTable(string slkText, W3ObjectCategory category)
    {
        var headers = new Dictionary<int, string>();
        var rowIds = new Dictionary<int, string>();
        var curX = 1;
        var curY = 1;

        using var reader = new StringReader(slkText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.StartsWith("C;", StringComparison.Ordinal))
                continue;

            var parts = line.Split(';');
            string? kVal = null;
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.Length < 2) continue;
                var code = p[0];
                var arg = p.Substring(1);
                if (code == 'X' && int.TryParse(arg, out var x))
                    curX = x;
                else if (code == 'Y' && int.TryParse(arg, out var y))
                    curY = y;
                else if (code == 'K')
                    kVal = arg.Trim('"');
            }

            if (kVal == null)
                continue;

            if (curY == 1)
            {
                headers[curX] = kVal;
                continue;
            }

            if (curX == 1)
            {
                if (kVal.Length == 4)
                {
                    rowIds[curY] = kVal;
                    if (!_baseObjects.TryGetValue(kVal, out var obj))
                    {
                        obj = new BaseObjectMetadata
                        {
                            Rawcode = kVal,
                            Category = category,
                            Name = kVal
                        };
                        _baseObjects[kVal] = obj;
                    }
                }
                continue;
            }

            if (!rowIds.TryGetValue(curY, out var rawcode) || !headers.TryGetValue(curX, out var colName))
                continue;

            if (!_baseObjects.TryGetValue(rawcode, out var meta))
                continue;

            meta.DefaultFields[colName] = kVal;

            // Map SLK columns to folder classifications matching classic World Editor
            if (category == W3ObjectCategory.Items && colName.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                meta.SubcategoryFolder = NormalizeItemClassFolder(kVal);
            }
            else if (colName.Equals("race", StringComparison.OrdinalIgnoreCase))
            {
                if (meta.SubcategoryFolder == "General")
                    meta.SubcategoryFolder = NormalizeRaceFolder(kVal);
            }
            else if (colName.Equals("Name", StringComparison.OrdinalIgnoreCase) && meta.Name == rawcode)
            {
                meta.Name = ResolveWestring(kVal);
            }
            else if (colName.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                meta.ModelFile = kVal;
            }
        }
    }

    private static string NormalizeItemClassFolder(string rawClass)
    {
        return rawClass.ToLowerInvariant() switch
        {
            "permanent" => "Permanent",
            "charged" => "Charged",
            "powerup" => "Power Up",
            "artifact" => "Artifact",
            "purchasable" => "Purchasable",
            "campaign" => "Campaign",
            _ => "Miscellaneous"
        };
    }

    private static string NormalizeRaceFolder(string rawRace)
    {
        return rawRace.ToLowerInvariant() switch
        {
            "human" => "Human",
            "orc" => "Orc",
            "undead" => "Undead",
            "nightelf" => "Night Elf",
            "creeps" => "Neutral Hostile",
            "commoner" or "other" or "passive" => "Neutral Passive",
            "naga" => "Naga",
            _ => "Neutral / Common"
        };
    }

    private void ParseIniStyleObjectFile(string content, W3ObjectCategory defaultCategory, string defaultFolder)
    {
        BaseObjectMetadata? current = null;
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]") && line.Length == 6)
            {
                var rawcode = line.Substring(1, 4);
                if (!_baseObjects.TryGetValue(rawcode, out current))
                {
                    current = new BaseObjectMetadata
                    {
                        Rawcode = rawcode,
                        Category = defaultCategory,
                        SubcategoryFolder = defaultFolder
                    };
                    _baseObjects[rawcode] = current;
                }
                else if (current.SubcategoryFolder == "General")
                {
                    current.SubcategoryFolder = defaultFolder;
                }
                continue;
            }

            if (current == null)
                continue;

            var eqIdx = line.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            var key = line.Substring(0, eqIdx).Trim();
            var val = line.Substring(eqIdx + 1).Trim().Trim('"');
            current.DefaultFields[key] = val;

            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Bufftip", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("EditorName", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(val))
                    current.Name = ResolveWestring(val);
            }
            else if (key.Equals("EditorSuffix", StringComparison.OrdinalIgnoreCase))
            {
                current.EditorSuffix = ResolveWestring(val);
            }
            else if (key.Equals("Tip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(current.Tooltip))
            {
                current.Tooltip = val;
            }
            else if ((key.Equals("Ubertip", StringComparison.OrdinalIgnoreCase) || key.Equals("Buffubertip", StringComparison.OrdinalIgnoreCase)) && string.IsNullOrEmpty(current.Ubertip))
            {
                current.Ubertip = val;
            }
            else if (key.Equals("Hotkey", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(current.Hotkey))
            {
                current.Hotkey = val;
            }
            else if ((key.Equals("Art", StringComparison.OrdinalIgnoreCase) || key.Equals("Buffart", StringComparison.OrdinalIgnoreCase)) && string.IsNullOrEmpty(current.Art))
            {
                current.Art = val;
            }
            else if (key.Equals("Buttonpos", StringComparison.OrdinalIgnoreCase))
            {
                var parts = val.Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var bx) && int.TryParse(parts[1], out var by))
                {
                    current.ButtonPosX = bx;
                    current.ButtonPosY = by;
                    current.DefaultFields["ButtonposX"] = bx.ToString();
                    current.DefaultFields["ButtonposY"] = by.ToString();
                }
            }
        }
    }

    public string GetFieldDisplayName(string fieldCode, bool showRawCode = false)
    {
        if (_fieldDisplayNames.TryGetValue(fieldCode, out var name))
            return showRawCode ? $"{fieldCode} ({name})" : name;
        return showRawCode ? fieldCode : $"Data - Field [{fieldCode}]";
    }

    public BaseObjectMetadata? GetBaseObject(string rawcode)
    {
        if (string.IsNullOrWhiteSpace(rawcode))
            return null;
        return _baseObjects.TryGetValue(rawcode.Trim(), out var meta) ? meta : null;
    }

    public List<W3ObjectField> BuildCompleteFieldListForEntry(W3ObjectEntry entry)
    {
        var existingByCode = entry.Fields
            .GroupBy(f => $"{f.FieldId}:{f.Level}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var baseMeta = GetBaseObject(string.IsNullOrEmpty(entry.BaseRawcode) ? entry.Rawcode : entry.BaseRawcode);
        var result = new List<W3ObjectField>();

        // 1. Add all modified fields first so they are preserved
        foreach (var modField in entry.Fields)
        {
            modField.IsModified = true;
            if (_fieldMeta.TryGetValue(modField.FieldId, out var metaDef))
            {
                modField.Choices ??= metaDef.Choices;
                modField.MinInt ??= metaDef.MinInt;
                modField.MaxInt ??= metaDef.MaxInt;
                modField.MinReal ??= metaDef.MinReal;
                modField.MaxReal ??= metaDef.MaxReal;
                modField.DecimalPlaces = metaDef.DecimalPlaces;
            }
            result.Add(modField);
        }

        // 2. Add standard fields for this category from BaseObjectMetadata SLK/Strings if not already present
        var relevantFieldIds = GetRelevantFieldIdsForCategory(entry.Category);
        foreach (var fid in relevantFieldIds)
        {
            var key0 = $"{fid}:0";
            var key1 = $"{fid}:1";
            if (existingByCode.ContainsKey(key0) || existingByCode.ContainsKey(key1))
                continue;

            if (!_fieldMeta.TryGetValue(fid, out var metaDef))
                continue;

            var defVal = string.Empty;
            if (baseMeta != null && !string.IsNullOrEmpty(metaDef.SlkColumn))
            {
                baseMeta.DefaultFields.TryGetValue(metaDef.SlkColumn, out defVal);
            }

            var defaultField = new W3ObjectField
            {
                FieldId = fid,
                FieldIntId = RawcodeToInt(fid),
                FieldName = metaDef.DisplayName,
                Level = 0,
                DataType = metaDef.DataType,
                RawValue = defVal ?? string.Empty,
                ResolvedValue = defVal ?? string.Empty,
                IsModified = false,
                Choices = metaDef.Choices,
                MinInt = metaDef.MinInt,
                MaxInt = metaDef.MaxInt,
                MinReal = metaDef.MinReal,
                MaxReal = metaDef.MaxReal,
                DecimalPlaces = metaDef.DecimalPlaces,
                ValueUpdatedCallback = f =>
                {
                    if (!entry.Fields.Contains(f))
                        entry.Fields.Add(f);
                    entry.IsModified = true;
                    entry.RefreshSummaryFromFields();
                }
            };

            result.Add(defaultField);
        }

        return result
            .OrderByDescending(f => f.IsModified)
            .ThenBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Level)
            .ToList();
    }

    private static string[] GetRelevantFieldIdsForCategory(W3ObjectCategory cat)
    {
        return cat switch
        {
            W3ObjectCategory.Units => new[]
            {
                "unam", "unsf", "utip", "utub", "uhot", "upro",
                "uico", "ussi", "umdl", "usca", "ussc", "ubpx", "ubpy", "uclr", "uclg", "uclb",
                "uhpm", "uhpr", "umpm", "umpr", "umpi", "ugol", "ulum", "ufma", "ufoo", "ubld", "ulev",
                "ustr", "uagi", "uint", "ustp", "uagp", "uinp", "upra",
                "udef", "udty", "ua1b", "ua1d", "ua1s", "ua1c", "ua1r", "ua1t", "ua1m", "ua1z", "uaen",
                "umvs", "umvt", "umvh", "ucol", "usnd",
                "uabi", "uhab", "ures", "utra", "usei", "useu"
            },
            W3ObjectCategory.Items => new[]
            {
                "unam", "utip", "utub", "uhot", "iico", "ifil", "usca", "ubpx", "ubpy",
                "iabi", "igol", "ilum", "ilev", "icla", "uhpm",
                "iusa", "iper", "iuse", "icid", "isto", "istr", "idrp", "ipaw", "ipri"
            },
            W3ObjectCategory.Abilities => new[]
            {
                "anam", "ansf", "atp1", "aub1", "aret", "arut", "ahky", "auhk", "arhk", "aord",
                "aart", "auar", "arar", "abpx", "abpy", "acat", "atat", "asat", "aeat", "amat", "amsp",
                "alev", "aher", "aite", "amcs", "acdn", "aran", "aare", "adur", "ahdu", "acas", "atar", "abuf", "aeff"
            },
            W3ObjectCategory.Buffs => new[]
            {
                "fnam", "ftip", "fube", "fart", "ftat", "fta0"
            },
            W3ObjectCategory.Destructables => new[]
            {
                "bnam", "bfil", "bhps"
            },
            W3ObjectCategory.Upgrades => new[]
            {
                "gnam", "gtp1", "gub1", "ghk1", "gar1", "gglb", "gglm"
            },
            _ => new[] { "unam", "utip", "utub" }
        };
    }

    public static int RawcodeToInt(string rawcode)
    {
        if (string.IsNullOrEmpty(rawcode) || rawcode.Length < 4)
            return 0;
        var bytes = Encoding.ASCII.GetBytes(rawcode.Substring(0, 4));
        return BitConverter.ToInt32(bytes, 0);
    }
}
