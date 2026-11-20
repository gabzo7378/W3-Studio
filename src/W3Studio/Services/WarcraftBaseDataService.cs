using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using War3Net.Build.Object;
using War3Net.IO.Mpq;
using W3Studio.Models;

namespace W3Studio.Services;

public class BaseObjectMetadata
{
    public string Rawcode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = "Other";
    public string SubcategoryFolder { get; set; } = "General";
    public string IconPath { get; set; } = string.Empty;
    public string TooltipBasic { get; set; } = string.Empty;
    public string TooltipExtended { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    public int ButtonPosX { get; set; }
    public int ButtonPosY { get; set; }
    public W3ObjectCategory Category { get; set; }
    public Dictionary<string, string> DefaultFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Art
    {
        get => IconPath;
        set => IconPath = value;
    }

    public string Tooltip
    {
        get => TooltipBasic;
        set => TooltipBasic = value;
    }

    public string Ubertip
    {
        get => TooltipExtended;
        set => TooltipExtended = value;
    }

    public string ModelFile => DefaultFields.TryGetValue("file", out var f) ? f : string.Empty;
}

public class FieldMetaDefinition
{
    public string FieldId { get; set; } = string.Empty;
    public string SectionKey { get; set; } = "Stats";
    public string TitleEs { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;

    public string DisplayName =>
        $"{LocalizationService.TranslateFieldSection(SectionKey)} - {LocalizationService.LocalizeFieldTitle(FieldId, TitleEs, TitleEn)}";

    public ObjectDataType DataType { get; set; } = ObjectDataType.String;
    public string SlkTypeName { get; set; } = string.Empty;
    public string SlkColumn { get; set; } = string.Empty;
    public int Index { get; set; } = -1;
    public int DataPointer { get; set; }
    public bool RepeatForLevels { get; set; }

    public W3ObjectCategory? TargetCategory { get; set; }
    public bool UseUnit { get; set; } = true;
    public bool UseHero { get; set; } = true;
    public bool UseBuilding { get; set; } = true;
    public bool UseItem { get; set; }
    public string UseSpecific { get; set; } = string.Empty;
    public string NotSpecific { get; set; } = string.Empty;

    // Restricciones y listas desplegables fieles a Warcraft III 1.27b
    public List<FieldChoiceOption>? Choices { get; set; }
    public int? MinInt { get; set; }
    public int? MaxInt { get; set; }
    public double? MinReal { get; set; }
    public double? MaxReal { get; set; }
    public int DecimalPlaces { get; set; } = 2;
}

public sealed record TerrainTileDefinition(string TileId, string DisplayName, string TexturePath, bool Buildable);
public sealed record CliffTileDefinition(string CliffId, string DisplayName, string GroundTile, string TexturePath);

/// <summary>
/// Extrae directamente de los archivos MPQ de Warcraft III 1.27b (War3.mpq, War3x.mpq, War3Patch.mpq)
/// las tablas SLK, archivos de texto *Strings.txt / *Func.txt y los catálogos *MetaData.slk oficiales.
/// </summary>
public class WarcraftBaseDataService
{
    private readonly Dictionary<string, BaseObjectMetadata> _allObjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FieldMetaDefinition> _fieldCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<W3ObjectCategory, List<string>> _categoryFieldOrder = new();
    private readonly Dictionary<string, string> _weStrings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _terrainTileTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TerrainTileDefinition> _terrainTileCatalog = new();
    private readonly List<CliffTileDefinition> _cliffTileCatalog = new();

    public IReadOnlyDictionary<string, BaseObjectMetadata> AllBaseObjects => _allObjects;
    public IReadOnlyDictionary<string, FieldMetaDefinition> FieldCatalog => _fieldCatalog;
    public IReadOnlyList<TerrainTileDefinition> TerrainTileCatalog => _terrainTileCatalog;
    public IReadOnlyList<CliffTileDefinition> CliffTileCatalog => _cliffTileCatalog;
    public bool IsLoaded { get; private set; }
    public bool IsBaseMpqLoaded => IsLoaded;
    public int LoadedBaseObjectsCount => _allObjects.Count;
    public string StatusSummary { get; private set; } = string.Empty;

    public void LoadFromWarcraftFolder(WorkspaceSettings settings)
        => LoadFromWarcraftFolder(settings?.Warcraft3Dir ?? string.Empty);

    public BaseObjectMetadata? GetBaseObject(string? rawcode)
    {
        if (string.IsNullOrWhiteSpace(rawcode))
            return null;
        return _allObjects.TryGetValue(rawcode.Trim(), out var meta) ? meta : null;
    }

    public BaseObjectMetadata? GetBaseObject(int id)
        => id == 0 ? null : GetBaseObject(IntToRawcode(id));

    public static int RawcodeToInt(string rawcode)
    {
        if (string.IsNullOrEmpty(rawcode) || rawcode.Length < 4)
            return 0;
        return (byte)rawcode[0] | ((byte)rawcode[1] << 8) | ((byte)rawcode[2] << 16) | ((byte)rawcode[3] << 24);
    }

    public static string IntToRawcode(int id)
    {
        if (id == 0) return string.Empty;
        var chars = new[]
        {
            (char)(id & 0xFF),
            (char)((id >> 8) & 0xFF),
            (char)((id >> 16) & 0xFF),
            (char)((id >> 24) & 0xFF)
        };
        return new string(chars);
    }

    public string? GetTerrainTileTexturePath(string tileCode)
    {
        if (string.IsNullOrWhiteSpace(tileCode))
            return null;
        return _terrainTileTextures.TryGetValue(tileCode.Trim(), out var path) ? path : null;
    }

    public WarcraftBaseDataService()
    {
        InitializeDefaultFieldCatalog();
    }

    private static FieldChoiceOption C(string val, string es, string en)
        => FieldChoiceOption.Localized(val, es, en);

    private static List<FieldChoiceOption> CreateBoolChoices() => new()
    {
        C("0", "Falso (No)", "False (No)"),
        C("1", "Verdadero (Sí)", "True (Yes)")
    };

    private static List<FieldChoiceOption> CreateRaceChoices() => new()
    {
        C("human", "Humano", "Human"),
        C("orc", "Orco", "Orc"),
        C("undead", "No-Muerto", "Undead"),
        C("nightelf", "Elfo Nocturno", "Night Elf"),
        C("naga", "Naga", "Naga"),
        C("creeps", "Creeps (Neutral Hostil)", "Creeps (Neutral Hostile)"),
        C("commoner", "Neutral Pasivo (Aldeano)", "Commoner (Neutral Passive)"),
        C("other", "Otro / Especial", "Other / Special"),
        C("demon", "Demonio", "Demon"),
        C("critters", "Criatura (Critter)", "Critter"),
        C("unknown", "Ninguna / Desconocida", "None / Unknown")
    };

    private static List<FieldChoiceOption> CreateDefenseTypeChoices() => new()
    {
        C("none", "Sin Armadura", "Unarmored"),
        C("small", "Ligera", "Light (Small)"),
        C("medium", "Media", "Medium"),
        C("large", "Pesada", "Heavy (Large)"),
        C("fort", "Fortificada", "Fortified"),
        C("normal", "Normal", "Normal"),
        C("hero", "Héroe", "Hero"),
        C("divine", "Divina", "Divine")
    };

    private static List<FieldChoiceOption> CreateArmorSoundChoices() => new()
    {
        C("Flesh", "Carne", "Flesh"),
        C("Metal", "Metal", "Metal"),
        C("Wood", "Madera", "Wood"),
        C("Stone", "Piedra", "Stone"),
        C("Ethereal", "Etéreo", "Ethereal")
    };

    private static List<FieldChoiceOption> CreateMoveTypeChoices() => new()
    {
        C("", "Ninguno (Estático / Edificio)", "None (Static / Building)"),
        C("foot", "A Pie", "Foot"),
        C("horse", "Montado", "Horse"),
        C("fly", "Volador", "Fly"),
        C("hover", "Flotante (Hover)", "Hover"),
        C("float", "Acuático (Barco)", "Float"),
        C("amph", "Anfibio", "Amphibious"),
        C("wind", "Viento", "Wind")
    };

    private static List<FieldChoiceOption> CreateAttackTypeChoices() => new()
    {
        C("unknown", "Ninguno", "None"),
        C("normal", "Normal (Cuerpo a cuerpo)", "Normal"),
        C("pierce", "Perforación", "Pierce"),
        C("siege", "Asedio", "Siege"),
        C("spells", "Hechizos", "Spells"),
        C("chaos", "Caos", "Chaos"),
        C("magic", "Mágico", "Magic"),
        C("hero", "Héroe", "Hero")
    };

    private static List<FieldChoiceOption> CreateWeaponTypeChoices() => new()
    {
        C("", "Ninguno", "None"),
        C("normal", "Normal (Cuerpo a cuerpo)", "Normal (Melee)"),
        C("instant", "Instantáneo", "Instant"),
        C("artillery", "Artillería (Área)", "Artillery"),
        C("aline", "Artillería en Línea", "Artillery (Line)"),
        C("missile", "Proyectil (Flecha / Magia)", "Missile"),
        C("msplash", "Proyectil con Área (Splash)", "Missile (Splash)"),
        C("mbounce", "Proyectil con Rebote (Bounce)", "Missile (Bounce)"),
        C("mline", "Proyectil en Línea", "Missile (Line)")
    };

    private static List<FieldChoiceOption> CreateWeaponSoundChoices() => new()
    {
        C("Nothing", "Ninguno (Silencioso / Distancia)", "Nothing"),
        C("AxeMediumChop", "Hacha Media", "AxeMediumChop"),
        C("MetalHeavyBash", "Metal Pesado (Maza)", "MetalHeavyBash"),
        C("MetalHeavyChop", "Metal Pesado (Corte)", "MetalHeavyChop"),
        C("MetalHeavySlice", "Metal Pesado (Tajo)", "MetalHeavySlice"),
        C("MetalLightChop", "Metal Ligero (Corte)", "MetalLightChop"),
        C("MetalLightSlice", "Metal Ligero (Tajo)", "MetalLightSlice"),
        C("MetalMediumBash", "Metal Medio (Golpe)", "MetalMediumBash"),
        C("MetalMediumChop", "Metal Medio (Corte)", "MetalMediumChop"),
        C("MetalMediumSlice", "Metal Medio (Tajo)", "MetalMediumSlice"),
        C("RockHeavyBash", "Roca Pesada", "RockHeavyBash"),
        C("WoodHeavyBash", "Madera Pesada", "WoodHeavyBash"),
        C("WoodLightBash", "Madera Ligera", "WoodLightBash"),
        C("WoodMediumBash", "Madera Media", "WoodMediumBash")
    };

    private static List<FieldChoiceOption> CreateAttacksEnabledChoices() => new()
    {
        C("0", "Ninguno (Sin ataque)", "None"),
        C("1", "Solo Ataque 1", "Attack 1 Only"),
        C("2", "Solo Ataque 2", "Attack 2 Only"),
        C("3", "Ambos (Ataque 1 y 2)", "Both (Attack 1 & 2)")
    };

    private static List<FieldChoiceOption> CreateHeroAttributeChoices() => new()
    {
        C("STR", "Fuerza (STR)", "Strength (STR)"),
        C("AGI", "Agilidad (AGI)", "Agility (AGI)"),
        C("INT", "Inteligencia (INT)", "Intelligence (INT)")
    };

    private static List<FieldChoiceOption> CreateRegenTypeChoices() => new()
    {
        C("none", "Ninguna", "None"),
        C("always", "Siempre", "Always"),
        C("blight", "Solo en Tierra Corrupta (Blight)", "Only while on Blight"),
        C("day", "Solo de Día", "Only during the Day"),
        C("night", "Solo de Noche", "Only during the Night")
    };

    private static List<FieldChoiceOption> CreateDeathTypeChoices() => new()
    {
        C("0", "No puede revivir, no se pudre", "Can't raise, Does not decay"),
        C("1", "Puede revivir, no se pudre", "Can raise, Does not decay"),
        C("2", "No puede revivir, sí se pudre", "Can't raise, Does decay"),
        C("3", "Puede revivir, sí se pudre", "Can raise, Does decay")
    };

    private static List<FieldChoiceOption> CreateItemClassChoices() => new()
    {
        C("Permanent", "Permanente", "Permanent"),
        C("Charged", "Con Cargas", "Charged"),
        C("PowerUp", "Potenciador (PowerUp al recoger)", "PowerUp"),
        C("Artifact", "Artefacto", "Artifact"),
        C("Purchasable", "Comprable", "Purchasable"),
        C("Campaign", "Campaña", "Campaign"),
        C("Miscellaneous", "Misceláneo", "Miscellaneous")
    };

    private static List<FieldChoiceOption> CreateTeamColorChoices() => new()
    {
        C("-1", "Color del Jugador Propietario", "Match Owning Player"),
        C("0", "Jugador 1 (Rojo)", "Player 1 (Red)"),
        C("1", "Jugador 2 (Azul)", "Player 2 (Blue)"),
        C("2", "Jugador 3 (Turquesa)", "Player 3 (Teal)"),
        C("3", "Jugador 4 (Púrpura)", "Player 4 (Purple)"),
        C("4", "Jugador 5 (Amarillo)", "Player 5 (Yellow)"),
        C("5", "Jugador 6 (Naranja)", "Player 6 (Orange)"),
        C("6", "Jugador 7 (Verde)", "Player 7 (Green)"),
        C("7", "Jugador 8 (Rosa)", "Player 8 (Pink)"),
        C("8", "Jugador 9 (Gris)", "Player 9 (Gray)"),
        C("9", "Jugador 10 (Azul Claro)", "Player 10 (Light Blue)"),
        C("10", "Jugador 11 (Verde Oscuro)", "Player 11 (Dark Green)"),
        C("11", "Jugador 12 (Marrón)", "Player 12 (Brown)"),
        C("12", "Neutral Hostil (Negro)", "Neutral Hostile (Black)")
    };

    private void InitializeDefaultFieldCatalog()
    {
        _fieldCatalog.Clear();
        _categoryFieldOrder.Clear();
        foreach (W3ObjectCategory cat in Enum.GetValues<W3ObjectCategory>())
        {
            _categoryFieldOrder[cat] = new List<string>();
        }

        var boolChoices = CreateBoolChoices();
        var raceChoices = CreateRaceChoices();
        var defTypeChoices = CreateDefenseTypeChoices();
        var armorSoundChoices = CreateArmorSoundChoices();
        var moveTypeChoices = CreateMoveTypeChoices();
        var atkTypeChoices = CreateAttackTypeChoices();
        var weapTypeChoices = CreateWeaponTypeChoices();
        var weapSoundChoices = CreateWeaponSoundChoices();
        var attacksEnabledChoices = CreateAttacksEnabledChoices();
        var attrChoices = CreateHeroAttributeChoices();
        var regenChoices = CreateRegenTypeChoices();
        var deathChoices = CreateDeathTypeChoices();
        var itemClassChoices = CreateItemClassChoices();
        var teamColorChoices = CreateTeamColorChoices();

        void Reg(
            W3ObjectCategory cat,
            string id,
            string section,
            string es,
            string en,
            ObjectDataType type,
            string col,
            List<FieldChoiceOption>? choices = null,
            int? minInt = null,
            int? maxInt = null,
            double? minReal = null,
            double? maxReal = null,
            int decimals = 2,
            int index = -1,
            bool useUnit = true,
            bool useHero = true,
            bool useBuilding = true,
            bool useItem = false,
            bool repeatLevels = false,
            int dataPtr = 0)
        {
            var def = new FieldMetaDefinition
            {
                FieldId = id,
                TargetCategory = cat,
                SectionKey = section,
                TitleEs = es,
                TitleEn = en,
                DataType = type,
                SlkColumn = col,
                Choices = choices,
                MinInt = minInt,
                MaxInt = maxInt,
                MinReal = minReal,
                MaxReal = maxReal,
                DecimalPlaces = decimals,
                Index = index,
                UseUnit = useUnit,
                UseHero = useHero,
                UseBuilding = useBuilding,
                UseItem = useItem,
                RepeatForLevels = repeatLevels,
                DataPointer = dataPtr
            };
            _fieldCatalog[id] = def;
            if (!_categoryFieldOrder[cat].Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                _categoryFieldOrder[cat].Add(id);
            }
        }

        // =========================================================================
        // 1. UNITS (UNIDADES, HÉROES Y EDIFICIOS) — CATÁLOGO COMPLETO 1.27b
        // =========================================================================
        // --- Habilidades (Abilities) ---
        Reg(W3ObjectCategory.Units, "uabi", "Abilities", "Habilidades Normales", "Normal", ObjectDataType.String, "abilList");
        Reg(W3ObjectCategory.Units, "udaa", "Abilities", "Habilidad Activa por Defecto", "Default Active Ability", ObjectDataType.String, "auto");
        Reg(W3ObjectCategory.Units, "uhab", "Abilities", "Habilidades de Héroe", "Hero", ObjectDataType.String, "heroAbilList", useUnit: false, useHero: true, useBuilding: false);

        // --- Arte (Art) ---
        Reg(W3ObjectCategory.Units, "umdl", "Art", "Archivo de Modelo 3D (.mdx)", "Model File", ObjectDataType.String, "file");
        Reg(W3ObjectCategory.Units, "uver", "Art", "Versiones Extra del Modelo (RoC/TFT)", "Model File - Extra Versions", ObjectDataType.Int, "fileVerFlags", minInt: 0, maxInt: 3);
        Reg(W3ObjectCategory.Units, "uico", "Art", "Icono de Interfaz", "Icon - Game Interface", ObjectDataType.String, "Art");
        Reg(W3ObjectCategory.Units, "ussi", "Art", "Icono de Pantalla de Puntuación", "Icon - Score Screen", ObjectDataType.String, "ScoreScreenIcon");
        Reg(W3ObjectCategory.Units, "usca", "Art", "Escala del Modelo", "Scaling Value", ObjectDataType.Real, "modelScale", minReal: 0.01, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "ussc", "Art", "Escala del Círculo de Selección", "Selection Scale", ObjectDataType.Real, "scale", minReal: 0.1, maxReal: 20.0);
        Reg(W3ObjectCategory.Units, "uslz", "Art", "Altura del Círculo de Selección", "Selection Circle - Height", ObjectDataType.Real, "selZ", minReal: -1000.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Units, "usew", "Art", "Círculo de Selección sobre Agua", "Selection Circle On Water", ObjectDataType.Int, "selCircOnWater", boolChoices);
        Reg(W3ObjectCategory.Units, "uclr", "Art", "Color de Tinte 1 (Rojo 0-255)", "Tinting Color 1 (Red)", ObjectDataType.Int, "red", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Units, "uclg", "Art", "Color de Tinte 2 (Verde 0-255)", "Tinting Color 2 (Green)", ObjectDataType.Int, "green", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Units, "uclb", "Art", "Color de Tinte 3 (Azul 0-255)", "Tinting Color 3 (Blue)", ObjectDataType.Int, "blue", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Units, "utcc", "Art", "Permitir Color de Equipo Personalizado", "Allow Custom Team Color", ObjectDataType.Int, "customTeamColor", boolChoices);
        Reg(W3ObjectCategory.Units, "utco", "Art", "Color de Equipo", "Team Color", ObjectDataType.Int, "teamColor", teamColorChoices);
        Reg(W3ObjectCategory.Units, "uble", "Art", "Tiempo de Mezcla de Animación (seg)", "Animation - Blend Time (seconds)", ObjectDataType.Real, "blend", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "ucbs", "Art", "Animación de Retroceso de Hechizo (Cast Backswing)", "Animation - Cast Backswing", ObjectDataType.Real, "castbsw", minReal: 0.0, maxReal: 10.0, decimals: 3);
        Reg(W3ObjectCategory.Units, "ucpt", "Art", "Punto de Lanzamiento de Hechizo (Cast Point)", "Animation - Cast Point", ObjectDataType.Real, "castpt", minReal: 0.0, maxReal: 10.0, decimals: 3);
        Reg(W3ObjectCategory.Units, "urun", "Art", "Velocidad de Animación al Correr", "Animation - Run Speed", ObjectDataType.Real, "run", minReal: 1.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Units, "uwal", "Art", "Velocidad de Animación al Caminar", "Animation - Walk Speed", ObjectDataType.Real, "walk", minReal: 1.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Units, "ubpx", "Art", "Posición de Botón (X)", "Button Position (X)", ObjectDataType.Int, "Buttonpos", minInt: 0, maxInt: 3, index: 0);
        Reg(W3ObjectCategory.Units, "ubpy", "Art", "Posición de Botón (Y)", "Button Position (Y)", ObjectDataType.Int, "Buttonpos", minInt: 0, maxInt: 2, index: 1);
        Reg(W3ObjectCategory.Units, "udtm", "Art", "Tiempo de Muerte (seg)", "Death Time (seconds)", ObjectDataType.Real, "death", minReal: 0.1, maxReal: 60.0);
        Reg(W3ObjectCategory.Units, "uept", "Art", "Puntos de Muestreo de Elevación", "Elevation - Sample Points", ObjectDataType.Int, "elevPts", minInt: 0, maxInt: 4);
        Reg(W3ObjectCategory.Units, "uerd", "Art", "Radio de Muestreo de Elevación", "Elevation - Sample Radius", ObjectDataType.Real, "elevRad", minReal: 0.0, maxReal: 1000.0);
        Reg(W3ObjectCategory.Units, "ufrd", "Art", "Radio de Muestreo de Niebla de Guerra", "Fog of War - Sample Radius", ObjectDataType.Real, "fogRad", minReal: 0.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "uhws", "Art", "Tiene Sombra en el Agua", "Has Water Shadow", ObjectDataType.Int, "waterShadow", boolChoices);
        Reg(W3ObjectCategory.Units, "umxp", "Art", "Ángulo Máximo de Inclinación Pitch (°)", "Maximum Pitch Angle (degrees)", ObjectDataType.Real, "maxPitch", minReal: 0.0, maxReal: 180.0);
        Reg(W3ObjectCategory.Units, "umxr", "Art", "Ángulo Máximo de Balanceo Roll (°)", "Maximum Roll Angle (degrees)", ObjectDataType.Real, "maxRoll", minReal: 0.0, maxReal: 180.0);
        Reg(W3ObjectCategory.Units, "uocc", "Art", "Altura de Oclusión de Visión", "Occluder Height", ObjectDataType.Real, "occH", minReal: 0.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "uori", "Art", "Interpolación de Orientación", "Orientation Interpolation", ObjectDataType.Int, "orientInterp", minInt: 0, maxInt: 8);
        Reg(W3ObjectCategory.Units, "uimz", "Art", "Impacto de Proyectil - Altura Z", "Projectile Impact - Z", ObjectDataType.Real, "impactZ", minReal: -1000.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "uisz", "Art", "Impacto de Proyectil - Altura Z (Nadando)", "Projectile Impact - Z (Swimming)", ObjectDataType.Real, "impactSwimZ", minReal: -1000.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "ulpx", "Art", "Lanzamiento de Proyectil - Desplazamiento X", "Projectile Launch - X", ObjectDataType.Real, "launchX", minReal: -1000.0, maxReal: 1000.0);
        Reg(W3ObjectCategory.Units, "ulpy", "Art", "Lanzamiento de Proyectil - Desplazamiento Y", "Projectile Launch - Y", ObjectDataType.Real, "launchY", minReal: -1000.0, maxReal: 1000.0);
        Reg(W3ObjectCategory.Units, "ulpz", "Art", "Lanzamiento de Proyectil - Altura Z", "Projectile Launch - Z", ObjectDataType.Real, "launchZ", minReal: -1000.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "ulsz", "Art", "Lanzamiento de Proyectil - Altura Z (Nadando)", "Projectile Launch - Z (Swimming)", ObjectDataType.Real, "launchSwimZ", minReal: -1000.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "uprw", "Art", "Ventana de Propulsión (°)", "Propulsion Window (degrees)", ObjectDataType.Real, "propWin", minReal: 0.0, maxReal: 180.0);
        Reg(W3ObjectCategory.Units, "uani", "Art", "Nombres de Animación Requeridos", "Required Animation Names", ObjectDataType.String, "animProps");
        Reg(W3ObjectCategory.Units, "uaap", "Art", "Nombres de Animación para Adjuntos", "Required Animation Names - Attachments", ObjectDataType.String, "Attachmentanimprops");
        Reg(W3ObjectCategory.Units, "ualp", "Art", "Puntos de Anclaje Requeridos", "Required Attachment Link Names", ObjectDataType.String, "Attachmentlinkprops");
        Reg(W3ObjectCategory.Units, "ubpr", "Art", "Nombres de Huesos Requeridos", "Required Bone Names", ObjectDataType.String, "Boneprops");
        Reg(W3ObjectCategory.Units, "uscb", "Art", "Escalar Proyectiles con la Unidad", "Scale Projectiles", ObjectDataType.Int, "scaleBull", boolChoices);
        Reg(W3ObjectCategory.Units, "ushu", "Art", "Imagen de Sombra de Unidad", "Shadow Image (Unit)", ObjectDataType.String, "unitShadow");
        Reg(W3ObjectCategory.Units, "ushx", "Art", "Sombra - Centro X", "Shadow Image - Center X", ObjectDataType.Real, "shadowX", minReal: -1000.0, maxReal: 1000.0);
        Reg(W3ObjectCategory.Units, "ushy", "Art", "Sombra - Centro Y", "Shadow Image - Center Y", ObjectDataType.Real, "shadowY", minReal: -1000.0, maxReal: 1000.0);
        Reg(W3ObjectCategory.Units, "ushh", "Art", "Sombra - Alto", "Shadow Image - Height", ObjectDataType.Real, "shadowH", minReal: 0.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "ushw", "Art", "Sombra - Ancho", "Shadow Image - Width", ObjectDataType.Real, "shadowW", minReal: 0.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Units, "ushb", "Art", "Textura de Sombra (Edificio)", "Shadow Texture (Building)", ObjectDataType.String, "buildingShadow", useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "uspa", "Art", "Arte Especial / Efecto al Morir", "Special", ObjectDataType.String, "Specialart");
        Reg(W3ObjectCategory.Units, "utaa", "Art", "Efecto sobre Objetivo", "Target", ObjectDataType.String, "Targetart");
        Reg(W3ObjectCategory.Units, "ulos", "Art", "Usar Línea de Visión Extendida", "Use Extended Line of Sight", ObjectDataType.Int, "fatLOS", boolChoices);

        // --- Combate General & Ataque 1 (Combat) ---
        Reg(W3ObjectCategory.Units, "uaen", "Combat", "Ataques Habilitados", "Attacks Enabled", ObjectDataType.Int, "weapsOn", attacksEnabledChoices);
        Reg(W3ObjectCategory.Units, "uacq", "Combat", "Rango de Adquisición", "Acquisition Range", ObjectDataType.Real, "acquire", minReal: 0.0, maxReal: 20000.0);
        Reg(W3ObjectCategory.Units, "uarm", "Combat", "Tipo de Sonido de Armadura", "Armor Type", ObjectDataType.String, "armor", armorSoundChoices);
        Reg(W3ObjectCategory.Units, "udef", "Combat", "Armadura Base", "Defense Base", ObjectDataType.Int, "def", minInt: -1000, maxInt: 100000);
        Reg(W3ObjectCategory.Units, "udty", "Combat", "Tipo de Defensa", "Defense Type", ObjectDataType.String, "defType", defTypeChoices);
        Reg(W3ObjectCategory.Units, "udup", "Combat", "Bonificación de Armadura por Mejora", "Defense Upgrade Bonus", ObjectDataType.Int, "defUp", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "udea", "Combat", "Tipo de Muerte", "Death Type", ObjectDataType.Int, "deathType", deathChoices);
        Reg(W3ObjectCategory.Units, "umin", "Combat", "Rango Mínimo de Ataque", "Minimum Attack Range", ObjectDataType.Int, "minRange", minInt: 0, maxInt: 20000);
        Reg(W3ObjectCategory.Units, "utar", "Combat", "Clasificado como Objetivo (Targeted as)", "Targeted as", ObjectDataType.String, "targType");

        // --- Combate: Ataque 1 Completo ---
        Reg(W3ObjectCategory.Units, "ua1t", "Combat", "Ataque 1 - Tipo de Ataque", "Attack 1 - Attack Type", ObjectDataType.String, "atkType1", atkTypeChoices);
        Reg(W3ObjectCategory.Units, "ua1w", "Combat", "Ataque 1 - Tipo de Arma", "Attack 1 - Weapon Type", ObjectDataType.String, "weapTp1", weapTypeChoices);
        Reg(W3ObjectCategory.Units, "ucs1", "Combat", "Ataque 1 - Sonido de Arma", "Attack 1 - Weapon Sound", ObjectDataType.String, "weapType1", weapSoundChoices);
        Reg(W3ObjectCategory.Units, "ua1b", "Combat", "Ataque 1 - Daño Base", "Attack 1 - Damage Base", ObjectDataType.Int, "dmgplus1", minInt: 0, maxInt: 999999);
        Reg(W3ObjectCategory.Units, "ua1d", "Combat", "Ataque 1 - Número de Dados de Daño", "Attack 1 - Damage Number of Dice", ObjectDataType.Int, "dice1", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Units, "ua1s", "Combat", "Ataque 1 - Caras por Dado de Daño", "Attack 1 - Damage Sides per Die", ObjectDataType.Int, "sides1", minInt: 0, maxInt: 1000);
        Reg(W3ObjectCategory.Units, "udu1", "Combat", "Ataque 1 - Bonificación de Daño por Mejora", "Attack 1 - Damage Upgrade Amount", ObjectDataType.Int, "dmgUp1", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "ua1c", "Combat", "Ataque 1 - Tiempo de Enfriamiento (Cooldown)", "Attack 1 - Cooldown Time", ObjectDataType.Real, "cool1", minReal: 0.0, maxReal: 3600.0);
        Reg(W3ObjectCategory.Units, "ua1r", "Combat", "Ataque 1 - Rango", "Attack 1 - Range", ObjectDataType.Int, "rangeN1", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Units, "urb1", "Combat", "Ataque 1 - Margen de Movimiento en Rango", "Attack 1 - Range Motion Buffer", ObjectDataType.Real, "rngBuff1", minReal: 0.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Units, "ua1g", "Combat", "Ataque 1 - Objetivos Permitidos", "Attack 1 - Targets Allowed", ObjectDataType.String, "targs1");
        Reg(W3ObjectCategory.Units, "ua1p", "Combat", "Ataque 1 - Objetivos de Área de Efecto", "Attack 1 - Area of Effect Targets", ObjectDataType.String, "splashTargs1");
        Reg(W3ObjectCategory.Units, "ua1f", "Combat", "Ataque 1 - Área de Efecto (Daño Total)", "Attack 1 - Area of Effect (Full Damage)", ObjectDataType.Int, "Farea1", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "ua1h", "Combat", "Ataque 1 - Área de Efecto (Daño Medio)", "Attack 1 - Area of Effect (Medium Damage)", ObjectDataType.Int, "Harea1", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "ua1q", "Combat", "Ataque 1 - Área de Efecto (Daño Pequeño)", "Attack 1 - Area of Effect (Small Damage)", ObjectDataType.Int, "Qarea1", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "uhd1", "Combat", "Ataque 1 - Factor de Daño Medio", "Attack 1 - Damage Factor - Medium", ObjectDataType.Real, "Hfact1", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "uqd1", "Combat", "Ataque 1 - Factor de Daño Pequeño", "Attack 1 - Damage Factor - Small", ObjectDataType.Real, "Qfact1", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "udl1", "Combat", "Ataque 1 - Factor de Pérdida de Daño", "Attack 1 - Damage Loss Factor", ObjectDataType.Real, "dmgLoss1", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "usd1", "Combat", "Ataque 1 - Distancia de Dispersión de Daño", "Attack 1 - Damage Spill Distance", ObjectDataType.Real, "spillDist1", minReal: 0.0, maxReal: 10000.0);
        Reg(W3ObjectCategory.Units, "usr1", "Combat", "Ataque 1 - Radio de Dispersión de Daño", "Attack 1 - Damage Spill Radius", ObjectDataType.Real, "spillRadius1", minReal: 0.0, maxReal: 10000.0);
        Reg(W3ObjectCategory.Units, "utc1", "Combat", "Ataque 1 - Número Máximo de Objetivos", "Attack 1 - Maximum Number of Targets", ObjectDataType.Int, "targCount1", minInt: 1, maxInt: 100);
        Reg(W3ObjectCategory.Units, "ubs1", "Combat", "Ataque 1 - Punto de Retroceso de Animación", "Attack 1 - Animation Backswing Point", ObjectDataType.Real, "backSw1", minReal: 0.0, maxReal: 10.0, decimals: 3);
        Reg(W3ObjectCategory.Units, "udp1", "Combat", "Ataque 1 - Punto de Daño de Animación", "Attack 1 - Animation Damage Point", ObjectDataType.Real, "dmgpt1", minReal: 0.0, maxReal: 10.0, decimals: 3);
        Reg(W3ObjectCategory.Units, "ua1m", "Combat", "Ataque 1 - Modelo del Proyectil", "Attack 1 - Projectile Art", ObjectDataType.String, "Missileart", index: 0);
        Reg(W3ObjectCategory.Units, "ua1z", "Combat", "Ataque 1 - Velocidad del Proyectil", "Attack 1 - Projectile Speed", ObjectDataType.Int, "Missilespeed", minInt: 0, maxInt: 20000, index: 0);
        Reg(W3ObjectCategory.Units, "uma1", "Combat", "Ataque 1 - Arco del Proyectil", "Attack 1 - Projectile Arc", ObjectDataType.Real, "Missilearc", minReal: 0.0, maxReal: 1.0, index: 0);
        Reg(W3ObjectCategory.Units, "umh1", "Combat", "Ataque 1 - Proyectil Teledirigido", "Attack 1 - Projectile Homing Enabled", ObjectDataType.Int, "MissileHoming", boolChoices, index: 0);
        Reg(W3ObjectCategory.Units, "uwu1", "Combat", "Ataque 1 - Mostrar en Interfaz", "Attack 1 - Show UI", ObjectDataType.Int, "showUI1", boolChoices);

        // --- Combate: Ataque 2 Completo ---
        Reg(W3ObjectCategory.Units, "ua2t", "Combat", "Ataque 2 - Tipo de Ataque", "Attack 2 - Attack Type", ObjectDataType.String, "atkType2", atkTypeChoices);
        Reg(W3ObjectCategory.Units, "ua2w", "Combat", "Ataque 2 - Tipo de Arma", "Attack 2 - Weapon Type", ObjectDataType.String, "weapTp2", weapTypeChoices);
        Reg(W3ObjectCategory.Units, "ucs2", "Combat", "Ataque 2 - Sonido de Arma", "Attack 2 - Weapon Sound", ObjectDataType.String, "weapType2", weapSoundChoices);
        Reg(W3ObjectCategory.Units, "ua2b", "Combat", "Ataque 2 - Daño Base", "Attack 2 - Damage Base", ObjectDataType.Int, "dmgplus2", minInt: 0, maxInt: 999999);
        Reg(W3ObjectCategory.Units, "ua2d", "Combat", "Ataque 2 - Número de Dados de Daño", "Attack 2 - Damage Number of Dice", ObjectDataType.Int, "dice2", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Units, "ua2s", "Combat", "Ataque 2 - Caras por Dado de Daño", "Attack 2 - Damage Sides per Die", ObjectDataType.Int, "sides2", minInt: 0, maxInt: 1000);
        Reg(W3ObjectCategory.Units, "udu2", "Combat", "Ataque 2 - Bonificación de Daño por Mejora", "Attack 2 - Damage Upgrade Amount", ObjectDataType.Int, "dmgUp2", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "ua2c", "Combat", "Ataque 2 - Tiempo de Enfriamiento (Cooldown)", "Attack 2 - Cooldown Time", ObjectDataType.Real, "cool2", minReal: 0.0, maxReal: 3600.0);
        Reg(W3ObjectCategory.Units, "ua2r", "Combat", "Ataque 2 - Rango", "Attack 2 - Range", ObjectDataType.Int, "rangeN2", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Units, "urb2", "Combat", "Ataque 2 - Margen de Movimiento en Rango", "Attack 2 - Range Motion Buffer", ObjectDataType.Real, "rngBuff2", minReal: 0.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Units, "ua2g", "Combat", "Ataque 2 - Objetivos Permitidos", "Attack 2 - Targets Allowed", ObjectDataType.String, "targs2");
        Reg(W3ObjectCategory.Units, "ua2p", "Combat", "Ataque 2 - Objetivos de Área de Efecto", "Attack 2 - Area of Effect Targets", ObjectDataType.String, "splashTargs2");
        Reg(W3ObjectCategory.Units, "ua2f", "Combat", "Ataque 2 - Área de Efecto (Daño Total)", "Attack 2 - Area of Effect (Full Damage)", ObjectDataType.Int, "Farea2", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "ua2h", "Combat", "Ataque 2 - Área de Efecto (Daño Medio)", "Attack 2 - Area of Effect (Medium Damage)", ObjectDataType.Int, "Harea2", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "ua2q", "Combat", "Ataque 2 - Área de Efecto (Daño Pequeño)", "Attack 2 - Area of Effect (Small Damage)", ObjectDataType.Int, "Qarea2", minInt: 0, maxInt: 10000);
        Reg(W3ObjectCategory.Units, "uhd2", "Combat", "Ataque 2 - Factor de Daño Medio", "Attack 2 - Damage Factor - Medium", ObjectDataType.Real, "Hfact2", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "uqd2", "Combat", "Ataque 2 - Factor de Daño Pequeño", "Attack 2 - Damage Factor - Small", ObjectDataType.Real, "Qfact2", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "udl2", "Combat", "Ataque 2 - Factor de Pérdida de Daño", "Attack 2 - Damage Loss Factor", ObjectDataType.Real, "dmgLoss2", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Units, "usd2", "Combat", "Ataque 2 - Distancia de Dispersión de Daño", "Attack 2 - Damage Spill Distance", ObjectDataType.Real, "spillDist2", minReal: 0.0, maxReal: 10000.0);
        Reg(W3ObjectCategory.Units, "usr2", "Combat", "Ataque 2 - Radio de Dispersión de Daño", "Attack 2 - Damage Spill Radius", ObjectDataType.Real, "spillRadius2", minReal: 0.0, maxReal: 10000.0);
        Reg(W3ObjectCategory.Units, "utc2", "Combat", "Ataque 2 - Número Máximo de Objetivos", "Attack 2 - Maximum Number of Targets", ObjectDataType.Int, "targCount2", minInt: 1, maxInt: 100);
        Reg(W3ObjectCategory.Units, "ubs2", "Combat", "Ataque 2 - Punto de Retroceso de Animación", "Attack 2 - Animation Backswing Point", ObjectDataType.Real, "backSw2", minReal: 0.0, maxReal: 10.0, decimals: 3);
        Reg(W3ObjectCategory.Units, "udp2", "Combat", "Ataque 2 - Punto de Daño de Animación", "Attack 2 - Animation Damage Point", ObjectDataType.Real, "dmgpt2", minReal: 0.0, maxReal: 10.0, decimals: 3);
        Reg(W3ObjectCategory.Units, "ua2m", "Combat", "Ataque 2 - Modelo del Proyectil", "Attack 2 - Projectile Art", ObjectDataType.String, "Missileart", index: 1);
        Reg(W3ObjectCategory.Units, "ua2z", "Combat", "Ataque 2 - Velocidad del Proyectil", "Attack 2 - Projectile Speed", ObjectDataType.Int, "Missilespeed", minInt: 0, maxInt: 20000, index: 1);
        Reg(W3ObjectCategory.Units, "uma2", "Combat", "Ataque 2 - Arco del Proyectil", "Attack 2 - Projectile Arc", ObjectDataType.Real, "Missilearc", minReal: 0.0, maxReal: 1.0, index: 1);
        Reg(W3ObjectCategory.Units, "umh2", "Combat", "Ataque 2 - Proyectil Teledirigido", "Attack 2 - Projectile Homing Enabled", ObjectDataType.Int, "MissileHoming", boolChoices, index: 1);
        Reg(W3ObjectCategory.Units, "uwu2", "Combat", "Ataque 2 - Mostrar en Interfaz", "Attack 2 - Show UI", ObjectDataType.Int, "showUI2", boolChoices);

        // --- Movimiento (Movement) ---
        Reg(W3ObjectCategory.Units, "umvt", "Movement", "Tipo de Movimiento", "Type", ObjectDataType.String, "movetp", moveTypeChoices);
        Reg(W3ObjectCategory.Units, "umvs", "Movement", "Velocidad Base", "Speed Base", ObjectDataType.Int, "spd", minInt: 0, maxInt: 522);
        Reg(W3ObjectCategory.Units, "umig", "Movement", "Velocidad Mínima", "Speed Minimum", ObjectDataType.Int, "minSpd", minInt: 0, maxInt: 522);
        Reg(W3ObjectCategory.Units, "umas", "Movement", "Velocidad Máxima", "Speed Maximum", ObjectDataType.Int, "maxSpd", minInt: 0, maxInt: 522);
        Reg(W3ObjectCategory.Units, "umvr", "Movement", "Velocidad de Giro (Turn Rate)", "Turn Rate", ObjectDataType.Real, "turnRate", minReal: 0.1, maxReal: 3.0);
        Reg(W3ObjectCategory.Units, "umvh", "Movement", "Altura de Vuelo", "Height", ObjectDataType.Real, "moveHeight", minReal: -1000.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Units, "umvf", "Movement", "Altura Mínima de Vuelo", "Height Minimum", ObjectDataType.Real, "moveFloor", minReal: -1000.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Units, "uori", "Movement", "Interpolación de Orientación", "Orientation Interpolation", ObjectDataType.Int, "orientInterp", minInt: 0, maxInt: 8);
        Reg(W3ObjectCategory.Units, "urpg", "Movement", "Prioridad de Grupo de Repulsión", "Group Separation - Group Number", ObjectDataType.Int, "repulseGroup", minInt: 0, maxInt: 10);
        Reg(W3ObjectCategory.Units, "urpp", "Movement", "Parámetro de Repulsión", "Group Separation - Parameter", ObjectDataType.Int, "repulseParam", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Units, "urpo", "Movement", "Separación de Grupo Habilitada", "Group Separation - Enabled", ObjectDataType.Int, "repulse", boolChoices);
        Reg(W3ObjectCategory.Units, "urpr", "Movement", "Prioridad de Separación de Grupo", "Group Separation - Priority", ObjectDataType.Int, "repulsePrio", minInt: 0, maxInt: 100);

        // --- Ruta y Colisión (Pathing) ---
        Reg(W3ObjectCategory.Units, "ucol", "Pathing", "Tamaño de Colisión", "Collision Size", ObjectDataType.Real, "collision", minReal: 0.0, maxReal: 1024.0);
        Reg(W3ObjectCategory.Units, "upat", "Pathing", "Mapa de Colisión (Pathing Map)", "Pathing Map", ObjectDataType.String, "pathTex", useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "upap", "Pathing", "Requisitos de Colocación", "Placement Requires", ObjectDataType.String, "preventPlace", useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "upar", "Pathing", "Radio de Agua de Colocación", "Placement Requires Water Radius", ObjectDataType.Real, "requireWaterRadius", minReal: 0.0, maxReal: 2000.0, useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "uabr", "Pathing", "Radio de IA de Construcción", "AI Placement Radius", ObjectDataType.Real, "buffRadius", minReal: 0.0, maxReal: 5000.0, useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "uabt", "Pathing", "Tipo de IA de Construcción", "AI Placement Type", ObjectDataType.String, "buffType", useUnit: false, useHero: false, useBuilding: true);

        // --- Sonido (Sound) ---
        Reg(W3ObjectCategory.Units, "usnd", "Sound", "Conjunto de Sonidos de la Unidad (Sound Set)", "Unit Sound Set", ObjectDataType.String, "unitSound");
        Reg(W3ObjectCategory.Units, "ubsl", "Sound", "Sonido de Construcción", "Construction", ObjectDataType.String, "BuildingSoundLabel", useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "ulfo", "Sound", "Fundido de Entrada de Bucle (Looping Fade In)", "Looping Fade In Rate", ObjectDataType.Int, "loopingSoundFadeIn", minInt: 0, maxInt: 12700);
        Reg(W3ObjectCategory.Units, "ulfi", "Sound", "Fundido de Salida de Bucle (Looping Fade Out)", "Looping Fade Out Rate", ObjectDataType.Int, "loopingSoundFadeOut", minInt: 0, maxInt: 12700);
        Reg(W3ObjectCategory.Units, "umsl", "Sound", "Sonido de Movimiento", "Movement", ObjectDataType.String, "MovementSoundLabel");
        Reg(W3ObjectCategory.Units, "ursl", "Sound", "Sonido Aleatorio", "Random", ObjectDataType.String, "RandomSoundLabel");

        // --- Estadísticas (Stats) ---
        Reg(W3ObjectCategory.Units, "uhpm", "Stats", "Vida Máxima (Base)", "Hit Points Maximum (Base)", ObjectDataType.Int, "HP", minInt: 1, maxInt: 9999999);
        Reg(W3ObjectCategory.Units, "uhpr", "Stats", "Regeneración de Vida por Segundo", "Hit Points Regeneration Rate", ObjectDataType.Real, "regenHP", minReal: -10000.0, maxReal: 100000.0);
        Reg(W3ObjectCategory.Units, "uhrt", "Stats", "Tipo de Regeneración de Vida", "Hit Points Regeneration Type", ObjectDataType.String, "regenType", regenChoices);
        Reg(W3ObjectCategory.Units, "umpm", "Stats", "Maná Máximo", "Mana Maximum", ObjectDataType.Int, "manaN", minInt: 0, maxInt: 9999999);
        Reg(W3ObjectCategory.Units, "umpi", "Stats", "Maná Inicial", "Mana Initial Amount", ObjectDataType.Int, "mana0", minInt: 0, maxInt: 9999999);
        Reg(W3ObjectCategory.Units, "umpr", "Stats", "Regeneración de Maná por Segundo", "Mana Regeneration", ObjectDataType.Real, "regenMana", minReal: -10000.0, maxReal: 100000.0);
        Reg(W3ObjectCategory.Units, "ugol", "Stats", "Costo de Oro", "Gold Cost", ObjectDataType.Int, "goldcost", minInt: 0, maxInt: 1000000);
        Reg(W3ObjectCategory.Units, "ulum", "Stats", "Costo de Madera", "Lumber Cost", ObjectDataType.Int, "lumbercost", minInt: 0, maxInt: 1000000);
        Reg(W3ObjectCategory.Units, "ugor", "Stats", "Costo de Oro de Reparación", "Repair Gold Cost", ObjectDataType.Int, "goldRep", minInt: 0, maxInt: 1000000);
        Reg(W3ObjectCategory.Units, "ulur", "Stats", "Costo de Madera de Reparación", "Repair Lumber Cost", ObjectDataType.Int, "lumberRep", minInt: 0, maxInt: 1000000);
        Reg(W3ObjectCategory.Units, "urtm", "Stats", "Tiempo de Reparación", "Repair Time", ObjectDataType.Int, "repTime", minInt: 0, maxInt: 3600);
        Reg(W3ObjectCategory.Units, "ufoo", "Stats", "Consumo de Comida", "Food Cost", ObjectDataType.Int, "fused", minInt: 0, maxInt: 300);
        Reg(W3ObjectCategory.Units, "ufma", "Stats", "Comida Producida", "Food Produced", ObjectDataType.Int, "fmade", minInt: 0, maxInt: 300);
        Reg(W3ObjectCategory.Units, "ubld", "Stats", "Tiempo de Construcción / Entrenamiento", "Build Time", ObjectDataType.Int, "bldtm", minInt: 1, maxInt: 3600);
        Reg(W3ObjectCategory.Units, "ubba", "Stats", "Recompensa de Oro - Base", "Gold Bounty Awarded - Base", ObjectDataType.Int, "bountyplus", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Units, "ubdi", "Stats", "Recompensa de Oro - Número de Dados", "Gold Bounty Awarded - Number of Dice", ObjectDataType.Int, "bountydice", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Units, "ubsi", "Stats", "Recompensa de Oro - Caras por Dado", "Gold Bounty Awarded - Sides per Die", ObjectDataType.Int, "bountysides", minInt: 0, maxInt: 1000);
        Reg(W3ObjectCategory.Units, "ulba", "Stats", "Recompensa de Madera - Base", "Lumber Bounty Awarded - Base", ObjectDataType.Int, "lumberbountyplus", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Units, "ulbd", "Stats", "Recompensa de Madera - Número de Dados", "Lumber Bounty Awarded - Number of Dice", ObjectDataType.Int, "lumberbountydice", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Units, "ulbs", "Stats", "Recompensa de Madera - Caras por Dado", "Lumber Bounty Awarded - Sides per Die", ObjectDataType.Int, "lumberbountysides", minInt: 0, maxInt: 1000);
        Reg(W3ObjectCategory.Units, "ulag", "Stats", "Nivel de la Unidad", "Level", ObjectDataType.Int, "level", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Units, "usid", "Stats", "Radio de Visión (Día)", "Sight Radius (Day)", ObjectDataType.Int, "sight", minInt: 0, maxInt: 1800);
        Reg(W3ObjectCategory.Units, "usin", "Stats", "Radio de Visión (Noche)", "Sight Radius (Night)", ObjectDataType.Int, "nsight", minInt: 0, maxInt: 1800);
        Reg(W3ObjectCategory.Units, "usle", "Stats", "Duerme de Noche", "Sleeps", ObjectDataType.Int, "sleep", boolChoices);
        Reg(W3ObjectCategory.Units, "usma", "Stats", "Stock Máximo en Tienda", "Stock Maximum", ObjectDataType.Int, "stockMax", minInt: 0, maxInt: 32);
        Reg(W3ObjectCategory.Units, "usrg", "Stats", "Intervalo de Reposición de Stock", "Stock Replenish Interval", ObjectDataType.Int, "stockRegen", minInt: 0, maxInt: 3600);
        Reg(W3ObjectCategory.Units, "usst", "Stats", "Retraso Inicial de Stock", "Stock Start Delay", ObjectDataType.Int, "stockStart", minInt: 0, maxInt: 3600);
        Reg(W3ObjectCategory.Units, "ucar", "Stats", "Tamaño de Transporte", "Transported Size", ObjectDataType.Int, "cargoSize", minInt: 0, maxInt: 20);
        Reg(W3ObjectCategory.Units, "ufor", "Stats", "Rango de Formación", "Formation Rank", ObjectDataType.Int, "formation", minInt: 0, maxInt: 10);
        Reg(W3ObjectCategory.Units, "upoi", "Stats", "Valor en Puntos", "Point Value", ObjectDataType.Int, "points", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Units, "upri", "Stats", "Prioridad de Objetivo", "Priority", ObjectDataType.Int, "prio", minInt: 0, maxInt: 20);
        Reg(W3ObjectCategory.Units, "ubdg", "Stats", "Es un Edificio (Is a Building)", "Is a Building", ObjectDataType.Int, "isbldg", boolChoices);
        Reg(W3ObjectCategory.Units, "unbm", "Stats", "Mostrar Icono de Edificio Neutral en Minimapa", "Neutral Building - Shows Minimap Icon", ObjectDataType.Int, "nbmmIcon", boolChoices, useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "uhom", "Stats", "Ocultar Icono en Minimapa", "Hide Minimap Display", ObjectDataType.Int, "hideOnMinimap", boolChoices);
        Reg(W3ObjectCategory.Units, "ucam", "Stats", "Puede Huir (Can Flee)", "Can Flee", ObjectDataType.Int, "canFlee", boolChoices);
        Reg(W3ObjectCategory.Units, "urac", "Stats", "Raza", "Race", ObjectDataType.String, "race", raceChoices);

        // --- Estadísticas de Héroe (Hero Stats) ---
        Reg(W3ObjectCategory.Units, "upra", "Stats", "Héroe - Atributo Primario", "Primary Attribute", ObjectDataType.String, "Primary", attrChoices, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "ustr", "Stats", "Héroe - Fuerza Inicial", "Starting Strength", ObjectDataType.Int, "STR", minInt: 0, maxInt: 10000, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "uagi", "Stats", "Héroe - Agilidad Inicial", "Starting Agility", ObjectDataType.Int, "AGI", minInt: 0, maxInt: 10000, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "uint", "Stats", "Héroe - Inteligencia Inicial", "Starting Intelligence", ObjectDataType.Int, "INT", minInt: 0, maxInt: 10000, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "ustp", "Stats", "Héroe - Fuerza por Nivel", "Strength per Level", ObjectDataType.Real, "STRplus", minReal: 0.0, maxReal: 1000.0, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "uagp", "Stats", "Héroe - Agilidad por Nivel", "Agility per Level", ObjectDataType.Real, "AGIplus", minReal: 0.0, maxReal: 1000.0, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "uinp", "Stats", "Héroe - Inteligencia por Nivel", "Intelligence per Level", ObjectDataType.Real, "INTplus", minReal: 0.0, maxReal: 1000.0, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "uhhd", "Stats", "Héroe - Ocultar Mensajes de Muerte", "Hero - Hide Hero Death Message", ObjectDataType.Int, "hideHeroDeathMsg", boolChoices, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "uhhb", "Stats", "Héroe - Ocultar Icono de Interfaz", "Hero - Hide Hero Interface Icon", ObjectDataType.Int, "hideHeroBar", boolChoices, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "uhhm", "Stats", "Héroe - Ocultar en Minimapa", "Hero - Hide Hero Minimap Display", ObjectDataType.Int, "hideHeroMinimap", boolChoices, useUnit: false, useHero: true, useBuilding: false);

        // --- Árbol Tecnológico (Techtree) ---
        Reg(W3ObjectCategory.Units, "ureq", "Techtree", "Requisitos Tecnológicos", "Requirements", ObjectDataType.String, "Requires");
        Reg(W3ObjectCategory.Units, "urqa", "Techtree", "Niveles de Requisitos", "Requirements - Levels", ObjectDataType.String, "Requiresamount");
        Reg(W3ObjectCategory.Units, "urq1", "Techtree", "Requisitos - Nivel 2", "Requirements - Tier 2", ObjectDataType.String, "Requires1", useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "urq2", "Techtree", "Requisitos - Nivel 3", "Requirements - Tier 3", ObjectDataType.String, "Requires2", useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "ubui", "Techtree", "Estructuras Construidas", "Structures Built", ObjectDataType.String, "Builds");
        Reg(W3ObjectCategory.Units, "utra", "Techtree", "Unidades Entrenadas", "Units Trained", ObjectDataType.String, "Trains", useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "ures", "Techtree", "Investigaciones Disponibles", "Researches Available", ObjectDataType.String, "Researches", useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "uupt", "Techtree", "Mejora a Estructura", "Upgrades To", ObjectDataType.String, "Upgrade", useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "upgr", "Techtree", "Mejoras Aplicables (Upgrades Used)", "Upgrades Used", ObjectDataType.String, "upgrades");
        Reg(W3ObjectCategory.Units, "useu", "Techtree", "Unidades Vendidas", "Units Sold", ObjectDataType.String, "Sellunits");
        Reg(W3ObjectCategory.Units, "usei", "Techtree", "Objetos Vendidos", "Items Sold", ObjectDataType.String, "Sellitems");
        Reg(W3ObjectCategory.Units, "umki", "Techtree", "Objetos Fabricados", "Items Made", ObjectDataType.String, "Makeitems");
        Reg(W3ObjectCategory.Units, "urev", "Techtree", "Revive Héroes Caídos", "Revives Dead Heroes", ObjectDataType.Int, "Revive", boolChoices, useUnit: false, useHero: false, useBuilding: true);
        Reg(W3ObjectCategory.Units, "udea", "Techtree", "Equivalentes de Dependencia", "Dependency Equivalents", ObjectDataType.String, "DependencyOr");

        // --- Texto (Text) ---
        Reg(W3ObjectCategory.Units, "unam", "Text", "Nombre", "Name", ObjectDataType.String, "Name");
        Reg(W3ObjectCategory.Units, "unsf", "Text", "Sufijo del Editor", "Name - Editor Suffix", ObjectDataType.String, "EditorSuffix");
        Reg(W3ObjectCategory.Units, "uhot", "Text", "Tecla Rápida (Hotkey)", "Hotkey", ObjectDataType.String, "Hotkey");
        Reg(W3ObjectCategory.Units, "utip", "Text", "Tooltip Básico", "Tooltip - Basic", ObjectDataType.String, "Tip");
        Reg(W3ObjectCategory.Units, "utub", "Text", "Tooltip Extendido (Ubertip)", "Tooltip - Extended", ObjectDataType.String, "Ubertip");
        Reg(W3ObjectCategory.Units, "uawt", "Text", "Tooltip - Despertar Héroe", "Tooltip - Awaken", ObjectDataType.String, "Awakentip", useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "utpr", "Text", "Tooltip - Revivir Héroe", "Tooltip - Revive", ObjectDataType.String, "Revivetip", useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "upro", "Text", "Nombres Propios del Héroe", "Proper Names", ObjectDataType.String, "Propernames", useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "upru", "Text", "Cantidad de Nombres Propios Usados", "Proper Names Used", ObjectDataType.Int, "nameCount", minInt: 0, maxInt: 50, useUnit: false, useHero: true, useBuilding: false);
        Reg(W3ObjectCategory.Units, "ides", "Text", "Descripción", "Description", ObjectDataType.String, "Description");

        // --- Editor ---
        Reg(W3ObjectCategory.Units, "utyp", "Editor", "Clasificación de la Unidad", "Unit Classification", ObjectDataType.String, "type");
        Reg(W3ObjectCategory.Units, "ucun", "Editor", "Categorizar - Campaña", "Categorize - Campaign", ObjectDataType.Int, "campaign", boolChoices);
        Reg(W3ObjectCategory.Units, "uspe", "Editor", "Categorizar - Especial", "Categorize - Special", ObjectDataType.Int, "special", boolChoices);
        Reg(W3ObjectCategory.Units, "utss", "Editor", "Tiene Datos Específicos de Tileset", "Has Tileset Specific Data", ObjectDataType.Int, "tilesetSpecific", boolChoices);
        Reg(W3ObjectCategory.Units, "uibo", "Editor", "Colocable en el Editor", "Placeable In Editor", ObjectDataType.Int, "inEditor", boolChoices);
        Reg(W3ObjectCategory.Units, "util", "Editor", "Conjuntos de Terreno (Tilesets)", "Tilesets", ObjectDataType.String, "tilesets");
        Reg(W3ObjectCategory.Units, "uuch", "Editor", "Usar Sonido de Clic al Seleccionar", "Use Click Helper", ObjectDataType.Int, "useClickHelper", boolChoices);

        // =========================================================================
        // 2. ITEMS (OBJETOS / ÍTEMS) — CATÁLOGO COMPLETO 1.27b
        // =========================================================================
        Reg(W3ObjectCategory.Items, "iabi", "Abilities", "Habilidades del Objeto", "Abilities", ObjectDataType.String, "abilList", useItem: true);
        Reg(W3ObjectCategory.Items, "iico", "Art", "Icono de Interfaz", "Interface Icon", ObjectDataType.String, "Art", useItem: true);
        Reg(W3ObjectCategory.Items, "ifil", "Art", "Archivo de Modelo 3D (.mdx)", "Model Used", ObjectDataType.String, "file", useItem: true);
        Reg(W3ObjectCategory.Items, "isca", "Art", "Escala del Modelo", "Scaling Value", ObjectDataType.Real, "scale", minReal: 0.1, maxReal: 10.0, useItem: true);
        Reg(W3ObjectCategory.Items, "issc", "Art", "Escala de Selección", "Selection Size - Editor", ObjectDataType.Real, "selSize", minReal: 0.1, maxReal: 10.0, useItem: true);
        Reg(W3ObjectCategory.Items, "iclr", "Art", "Color de Tinte 1 (Rojo 0-255)", "Tinting Color 1 (Red)", ObjectDataType.Int, "colorR", minInt: 0, maxInt: 255, useItem: true);
        Reg(W3ObjectCategory.Items, "iclg", "Art", "Color de Tinte 2 (Verde 0-255)", "Tinting Color 2 (Green)", ObjectDataType.Int, "colorG", minInt: 0, maxInt: 255, useItem: true);
        Reg(W3ObjectCategory.Items, "iclb", "Art", "Color de Tinte 3 (Azul 0-255)", "Tinting Color 3 (Blue)", ObjectDataType.Int, "colorB", minInt: 0, maxInt: 255, useItem: true);
        Reg(W3ObjectCategory.Items, "iarm", "Combat", "Tipo de Armadura", "Armor Type", ObjectDataType.String, "armor", armorSoundChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "icid", "Stats", "Grupo de Enfriamiento (Cooldown Group)", "Cooldown Group", ObjectDataType.String, "cooldownID", useItem: true);
        Reg(W3ObjectCategory.Items, "icla", "Stats", "Clase de Objeto", "Classification", ObjectDataType.String, "class", itemClassChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "ilev", "Stats", "Nivel del Objeto", "Level", ObjectDataType.Int, "Level", minInt: 0, maxInt: 20, useItem: true);
        Reg(W3ObjectCategory.Items, "ilvo", "Stats", "Nivel (Clasificación Antigua)", "Level (Unclassified)", ObjectDataType.Int, "oldLevel", minInt: 0, maxInt: 20, useItem: true);
        Reg(W3ObjectCategory.Items, "igol", "Stats", "Costo de Oro", "Gold Cost", ObjectDataType.Int, "goldcost", minInt: 0, maxInt: 1000000, useItem: true);
        Reg(W3ObjectCategory.Items, "ilum", "Stats", "Costo de Madera", "Lumber Cost", ObjectDataType.Int, "lumbercost", minInt: 0, maxInt: 1000000, useItem: true);
        Reg(W3ObjectCategory.Items, "ihtp", "Stats", "Puntos de Vida del Objeto", "Hit Points", ObjectDataType.Int, "HP", minInt: 1, maxInt: 999999, useItem: true);
        Reg(W3ObjectCategory.Items, "iuse", "Stats", "Número de Cargas", "Number of Charges", ObjectDataType.Int, "uses", minInt: 0, maxInt: 999, useItem: true);
        Reg(W3ObjectCategory.Items, "isto", "Stats", "Stock Máximo en Tienda", "Stock Maximum", ObjectDataType.Int, "stockMax", minInt: 0, maxInt: 32, useItem: true);
        Reg(W3ObjectCategory.Items, "istr", "Stats", "Intervalo de Reposición de Stock", "Stock Replenish Interval", ObjectDataType.Int, "stockRegen", minInt: 0, maxInt: 3600, useItem: true);
        Reg(W3ObjectCategory.Items, "isst", "Stats", "Retraso Inicial de Stock", "Stock Start Delay", ObjectDataType.Int, "stockStart", minInt: 0, maxInt: 3600, useItem: true);
        Reg(W3ObjectCategory.Items, "iusa", "Stats", "Usable Activamente", "Actively Used", ObjectDataType.Int, "usable", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "iper", "Stats", "Perecedero (Se consume al agotar cargas)", "Perishable", ObjectDataType.Int, "perishable", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "idro", "Stats", "Soltable por el Portador", "Can Be Dropped", ObjectDataType.Int, "droppable", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "idrp", "Stats", "Cae al Morir el Portador", "Dropped When Carrier Dies", ObjectDataType.Int, "drop", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "ipaw", "Stats", "Vendible en Tiendas", "Can Be Sold By Merchants", ObjectDataType.Int, "sellable", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "iprn", "Stats", "Empeñable en Tiendas", "Can Be Sold To Merchants", ObjectDataType.Int, "pawnable", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "ipow", "Stats", "Usar Automáticamente al Recoger (PowerUp)", "Use Automatically When Acquired", ObjectDataType.Int, "powerup", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "ipri", "Stats", "Prioridad", "Priority", ObjectDataType.Int, "prio", minInt: 0, maxInt: 1000, useItem: true);
        Reg(W3ObjectCategory.Items, "iicd", "Stats", "Ignorar Tiempo de Enfriamiento", "Ignore Cooldown", ObjectDataType.Int, "ignoreCD", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "imor", "Stats", "Transformación Válida", "Valid Target For Transformation", ObjectDataType.Int, "morph", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "ipoo", "Stats", "Incluir en Selección Aleatoria", "Include As Random Choice", ObjectDataType.Int, "pickRandom", boolChoices, useItem: true);
        Reg(W3ObjectCategory.Items, "ireq", "Techtree", "Requisitos Tecnológicos", "Requirements", ObjectDataType.String, "Requires", useItem: true);
        Reg(W3ObjectCategory.Items, "urqa", "Techtree", "Niveles de Requisitos", "Requirements - Levels", ObjectDataType.String, "Requiresamount", useItem: true);
        Reg(W3ObjectCategory.Items, "unam", "Text", "Nombre", "Name", ObjectDataType.String, "Name", useItem: true);
        Reg(W3ObjectCategory.Items, "uhot", "Text", "Tecla Rápida (Hotkey)", "Hotkey", ObjectDataType.String, "Hotkey", useItem: true);
        Reg(W3ObjectCategory.Items, "utip", "Text", "Tooltip Básico", "Tooltip - Basic", ObjectDataType.String, "Tip", useItem: true);
        Reg(W3ObjectCategory.Items, "utub", "Text", "Tooltip Extendido (Ubertip)", "Tooltip - Extended", ObjectDataType.String, "Ubertip", useItem: true);
        Reg(W3ObjectCategory.Items, "ides", "Text", "Descripción", "Description", ObjectDataType.String, "Description", useItem: true);

        // =========================================================================
        // 3. ABILITIES (HABILIDADES) — CATÁLOGO COMPLETO 1.27b
        // =========================================================================
        Reg(W3ObjectCategory.Abilities, "aani", "Art", "Nombres de Animación", "Animation Names", ObjectDataType.String, "Animnames");
        Reg(W3ObjectCategory.Abilities, "aart", "Art", "Icono - Normal", "Icon - Normal", ObjectDataType.String, "Art");
        Reg(W3ObjectCategory.Abilities, "auar", "Art", "Icono - Desactivar (Turn Off)", "Icon - Turn Off", ObjectDataType.String, "Unart");
        Reg(W3ObjectCategory.Abilities, "arar", "Art", "Icono - Aprender (Research)", "Icon - Research", ObjectDataType.String, "ResearchArt");
        Reg(W3ObjectCategory.Abilities, "abpx", "Art", "Posición de Botón - Normal (X)", "Button Position - Normal (X)", ObjectDataType.Int, "Buttonpos", minInt: 0, maxInt: 3, index: 0);
        Reg(W3ObjectCategory.Abilities, "abpy", "Art", "Posición de Botón - Normal (Y)", "Button Position - Normal (Y)", ObjectDataType.Int, "Buttonpos", minInt: 0, maxInt: 2, index: 1);
        Reg(W3ObjectCategory.Abilities, "aubx", "Art", "Posición de Botón - Desactivar (X)", "Button Position - Turn Off (X)", ObjectDataType.Int, "Unbuttonpos", minInt: 0, maxInt: 3, index: 0);
        Reg(W3ObjectCategory.Abilities, "auby", "Art", "Posición de Botón - Desactivar (Y)", "Button Position - Turn Off (Y)", ObjectDataType.Int, "Unbuttonpos", minInt: 0, maxInt: 2, index: 1);
        Reg(W3ObjectCategory.Abilities, "arpx", "Art", "Posición de Botón - Aprender (X)", "Button Position - Research (X)", ObjectDataType.Int, "Researchbuttonpos", minInt: 0, maxInt: 3, index: 0);
        Reg(W3ObjectCategory.Abilities, "arpy", "Art", "Posición de Botón - Aprender (Y)", "Button Position - Research (Y)", ObjectDataType.Int, "Researchbuttonpos", minInt: 0, maxInt: 2, index: 1);
        Reg(W3ObjectCategory.Abilities, "acat", "Art", "Efecto en el Lanzador (Caster Art)", "Caster", ObjectDataType.String, "Casterart");
        Reg(W3ObjectCategory.Abilities, "acap", "Art", "Punto de Anclaje del Lanzador 1", "Caster Attachment Point 1", ObjectDataType.String, "Casterattach");
        Reg(W3ObjectCategory.Abilities, "aca1", "Art", "Punto de Anclaje del Lanzador 2", "Caster Attachment Point 2", ObjectDataType.String, "Casterattach1");
        Reg(W3ObjectCategory.Abilities, "acac", "Art", "Cantidad de Anclajes del Lanzador", "Caster Attachments", ObjectDataType.Int, "Casterattachcount", minInt: 0, maxInt: 6);
        Reg(W3ObjectCategory.Abilities, "atat", "Art", "Efecto en el Objetivo (Target Art)", "Target", ObjectDataType.String, "Targetart");
        Reg(W3ObjectCategory.Abilities, "ata0", "Art", "Punto de Anclaje del Objetivo 1", "Target Attachment Point 1", ObjectDataType.String, "Targetattach");
        Reg(W3ObjectCategory.Abilities, "ata1", "Art", "Punto de Anclaje del Objetivo 2", "Target Attachment Point 2", ObjectDataType.String, "Targetattach1");
        Reg(W3ObjectCategory.Abilities, "ata2", "Art", "Punto de Anclaje del Objetivo 3", "Target Attachment Point 3", ObjectDataType.String, "Targetattach2");
        Reg(W3ObjectCategory.Abilities, "ata3", "Art", "Punto de Anclaje del Objetivo 4", "Target Attachment Point 4", ObjectDataType.String, "Targetattach3");
        Reg(W3ObjectCategory.Abilities, "ata4", "Art", "Punto de Anclaje del Objetivo 5", "Target Attachment Point 5", ObjectDataType.String, "Targetattach4");
        Reg(W3ObjectCategory.Abilities, "ata5", "Art", "Punto de Anclaje del Objetivo 6", "Target Attachment Point 6", ObjectDataType.String, "Targetattach5");
        Reg(W3ObjectCategory.Abilities, "atac", "Art", "Cantidad de Anclajes del Objetivo", "Target Attachments", ObjectDataType.Int, "Targetattachcount", minInt: 0, maxInt: 6);
        Reg(W3ObjectCategory.Abilities, "asat", "Art", "Efecto Especial (Special Art)", "Special", ObjectDataType.String, "Specialart");
        Reg(W3ObjectCategory.Abilities, "aspt", "Art", "Punto de Anclaje Especial", "Special Attachment Point", ObjectDataType.String, "Specialattach");
        Reg(W3ObjectCategory.Abilities, "aeat", "Art", "Efecto de Impacto (Effect Art)", "Effect", ObjectDataType.String, "Effectart");
        Reg(W3ObjectCategory.Abilities, "aaea", "Art", "Efecto de Área (Area Effect Art)", "Area Effect", ObjectDataType.String, "Areaeffectart");
        Reg(W3ObjectCategory.Abilities, "amat", "Art", "Modelo del Proyectil (Missile Art)", "Missile Art", ObjectDataType.String, "Missileart");
        Reg(W3ObjectCategory.Abilities, "amsp", "Art", "Velocidad del Proyectil", "Missile Speed", ObjectDataType.Int, "Missilespeed", minInt: 0, maxInt: 20000);
        Reg(W3ObjectCategory.Abilities, "amac", "Art", "Arco del Proyectil", "Missile Arc", ObjectDataType.Real, "Missilearc", minReal: 0.0, maxReal: 1.0);
        Reg(W3ObjectCategory.Abilities, "amho", "Art", "Proyectil Teledirigido", "Missile Homing Enabled", ObjectDataType.Int, "MissileHoming", boolChoices);
        Reg(W3ObjectCategory.Abilities, "alig", "Art", "Efectos de Rayo (Lightning Effects)", "Lightning Effects", ObjectDataType.String, "LightningEffect");
        Reg(W3ObjectCategory.Abilities, "efsound", "Sound", "Sonido de Efecto", "Effect Sound", ObjectDataType.String, "Effectsound");
        Reg(W3ObjectCategory.Abilities, "efsoundloop", "Sound", "Sonido de Efecto (En Bucle)", "Effect Sound (Looping)", ObjectDataType.String, "Effectsoundlooped");

        // --- Stats de Habilidad ---
        Reg(W3ObjectCategory.Abilities, "alev", "Stats", "Cantidad de Niveles", "Levels", ObjectDataType.Int, "levels", minInt: 1, maxInt: 100);
        Reg(W3ObjectCategory.Abilities, "aher", "Stats", "Es Habilidad de Héroe", "Hero Ability", ObjectDataType.Int, "hero", boolChoices);
        Reg(W3ObjectCategory.Abilities, "aite", "Stats", "Es Habilidad de Objeto", "Item Ability", ObjectDataType.Int, "item", boolChoices);
        Reg(W3ObjectCategory.Abilities, "arac", "Stats", "Raza", "Race", ObjectDataType.String, "race", raceChoices);
        Reg(W3ObjectCategory.Abilities, "arlv", "Stats", "Nivel Requerido del Héroe", "Required Level", ObjectDataType.Int, "reqLevel", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Abilities, "alsk", "Stats", "Salto de Nivel para Mejorar", "Level Skip Requirement", ObjectDataType.Int, "levelSkip", minInt: 0, maxInt: 20);
        Reg(W3ObjectCategory.Abilities, "apri", "Stats", "Prioridad para Robar Hechizo", "Priority for Spell Steal", ObjectDataType.Int, "priority", minInt: 0, maxInt: 1000);
        Reg(W3ObjectCategory.Abilities, "acdn", "Stats", "Tiempo de Enfriamiento (Cooldown)", "Cooldown", ObjectDataType.Real, "Cool1", minReal: 0.0, maxReal: 3600.0, repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "amcs", "Stats", "Costo de Maná", "Mana Cost", ObjectDataType.Int, "Cost1", minInt: 0, maxInt: 100000, repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "aran", "Stats", "Rango de Lanzamiento", "Cast Range", ObjectDataType.Real, "Rng1", minReal: 0.0, maxReal: 99999.0, repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "aare", "Stats", "Área de Efecto (AoE)", "Area of Effect", ObjectDataType.Real, "Area1", minReal: 0.0, maxReal: 99999.0, repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "adur", "Stats", "Duración - Unidad Normal", "Duration - Normal", ObjectDataType.Real, "Dur1", minReal: 0.0, maxReal: 3600.0, repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "ahdu", "Stats", "Duración - Héroe", "Duration - Hero", ObjectDataType.Real, "HeroDur1", minReal: 0.0, maxReal: 3600.0, repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "acas", "Stats", "Tiempo de Canalización (Casting Time)", "Casting Time", ObjectDataType.Real, "Cast1", minReal: 0.0, maxReal: 3600.0, repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "atar", "Stats", "Objetivos Permitidos", "Targets Allowed", ObjectDataType.String, "targs1", repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "abuf", "Stats", "Buffs Aplicados", "Buffs", ObjectDataType.String, "BuffID1", repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "aeff", "Stats", "Efectos (Effects)", "Effects", ObjectDataType.String, "EfctID1", repeatLevels: true);

        // --- Datos Genéricos de Habilidad (DataA..DataI) ---
        Reg(W3ObjectCategory.Abilities, "DataA", "Data", "Dato A (Valor Principal / Daño / Cura)", "Data A", ObjectDataType.Real, "DataA1", repeatLevels: true, dataPtr: 1);
        Reg(W3ObjectCategory.Abilities, "DataB", "Data", "Dato B (Valor Secundario)", "Data B", ObjectDataType.Real, "DataB1", repeatLevels: true, dataPtr: 2);
        Reg(W3ObjectCategory.Abilities, "DataC", "Data", "Dato C (Valor Terciario)", "Data C", ObjectDataType.Real, "DataC1", repeatLevels: true, dataPtr: 3);
        Reg(W3ObjectCategory.Abilities, "DataD", "Data", "Dato D (Parámetro 4)", "Data D", ObjectDataType.Real, "DataD1", repeatLevels: true, dataPtr: 4);
        Reg(W3ObjectCategory.Abilities, "DataE", "Data", "Dato E (Parámetro 5)", "Data E", ObjectDataType.Real, "DataE1", repeatLevels: true, dataPtr: 5);
        Reg(W3ObjectCategory.Abilities, "DataF", "Data", "Dato F (Parámetro 6)", "Data F", ObjectDataType.Real, "DataF1", repeatLevels: true, dataPtr: 6);
        Reg(W3ObjectCategory.Abilities, "DataG", "Data", "Dato G (Parámetro 7)", "Data G", ObjectDataType.Real, "DataG1", repeatLevels: true, dataPtr: 7);
        Reg(W3ObjectCategory.Abilities, "DataH", "Data", "Dato H (Parámetro 8)", "Data H", ObjectDataType.Real, "DataH1", repeatLevels: true, dataPtr: 8);
        Reg(W3ObjectCategory.Abilities, "DataI", "Data", "Dato I (Parámetro 9)", "Data I", ObjectDataType.Real, "DataI1", repeatLevels: true, dataPtr: 9);

        // --- Techtree & Texto de Habilidad ---
        Reg(W3ObjectCategory.Abilities, "areq", "Techtree", "Requisitos Tecnológicos", "Requirements", ObjectDataType.String, "Requires");
        Reg(W3ObjectCategory.Abilities, "arqa", "Techtree", "Niveles de Requisitos", "Requirements - Levels", ObjectDataType.String, "Requiresamount");
        Reg(W3ObjectCategory.Abilities, "achd", "Techtree", "Verificar Dependencias", "Check Dependencies", ObjectDataType.Int, "checkDep", boolChoices);
        Reg(W3ObjectCategory.Abilities, "anam", "Text", "Nombre", "Name", ObjectDataType.String, "Name");
        Reg(W3ObjectCategory.Abilities, "ansf", "Text", "Sufijo del Editor", "Editor Suffix", ObjectDataType.String, "EditorSuffix");
        Reg(W3ObjectCategory.Abilities, "ahky", "Text", "Tecla Rápida - Normal", "Hotkey - Normal", ObjectDataType.String, "Hotkey");
        Reg(W3ObjectCategory.Abilities, "auhk", "Text", "Tecla Rápida - Desactivar", "Hotkey - Turn Off", ObjectDataType.String, "Unhotkey");
        Reg(W3ObjectCategory.Abilities, "arhk", "Text", "Tecla Rápida - Aprender", "Hotkey - Learn", ObjectDataType.String, "Researchhotkey");
        Reg(W3ObjectCategory.Abilities, "atp1", "Text", "Tooltip - Normal", "Tooltip - Normal", ObjectDataType.String, "Tip", repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "aub1", "Text", "Tooltip - Normal Extendido", "Tooltip - Normal - Extended", ObjectDataType.String, "Ubertip", repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "aut1", "Text", "Tooltip - Desactivar", "Tooltip - Turn Off", ObjectDataType.String, "Untip", repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "auu1", "Text", "Tooltip - Desactivar Extendido", "Tooltip - Turn Off - Extended", ObjectDataType.String, "Unubertip", repeatLevels: true);
        Reg(W3ObjectCategory.Abilities, "aret", "Text", "Tooltip - Aprender", "Tooltip - Learn", ObjectDataType.String, "Researchtip");
        Reg(W3ObjectCategory.Abilities, "arut", "Text", "Tooltip - Aprender Extendido", "Tooltip - Learn - Extended", ObjectDataType.String, "Researchubertip");
        Reg(W3ObjectCategory.Abilities, "aord", "Text", "Cadena de Orden - Usar/Activar", "Order String - Use/Turn On", ObjectDataType.String, "Order");
        Reg(W3ObjectCategory.Abilities, "aoru", "Text", "Cadena de Orden - Desactivar", "Order String - Turn Off", ObjectDataType.String, "Unorder");
        Reg(W3ObjectCategory.Abilities, "aoro", "Text", "Cadena de Orden - Activar Auto", "Order String - Activate", ObjectDataType.String, "Orderon");
        Reg(W3ObjectCategory.Abilities, "aorf", "Text", "Cadena de Orden - Desactivar Auto", "Order String - Deactivate", ObjectDataType.String, "Orderoff");

        // =========================================================================
        // 4. BUFFS / EFFECTS — CATÁLOGO COMPLETO 1.27b
        // =========================================================================
        Reg(W3ObjectCategory.Buffs, "fart", "Art", "Icono", "Icon", ObjectDataType.String, "Buffart");
        Reg(W3ObjectCategory.Buffs, "ftat", "Art", "Efecto en el Objetivo (Target Art)", "Target", ObjectDataType.String, "Targetart");
        Reg(W3ObjectCategory.Buffs, "fta0", "Art", "Punto de Anclaje del Objetivo 1", "Target Attachment Point 1", ObjectDataType.String, "Targetattach");
        Reg(W3ObjectCategory.Buffs, "fta1", "Art", "Punto de Anclaje del Objetivo 2", "Target Attachment Point 2", ObjectDataType.String, "Targetattach1");
        Reg(W3ObjectCategory.Buffs, "fta2", "Art", "Punto de Anclaje del Objetivo 3", "Target Attachment Point 3", ObjectDataType.String, "Targetattach2");
        Reg(W3ObjectCategory.Buffs, "fta3", "Art", "Punto de Anclaje del Objetivo 4", "Target Attachment Point 4", ObjectDataType.String, "Targetattach3");
        Reg(W3ObjectCategory.Buffs, "fta4", "Art", "Punto de Anclaje del Objetivo 5", "Target Attachment Point 5", ObjectDataType.String, "Targetattach4");
        Reg(W3ObjectCategory.Buffs, "fta5", "Art", "Punto de Anclaje del Objetivo 6", "Target Attachment Point 6", ObjectDataType.String, "Targetattach5");
        Reg(W3ObjectCategory.Buffs, "ftac", "Art", "Cantidad de Anclajes del Objetivo", "Target Attachments", ObjectDataType.Int, "Targetattachcount", minInt: 0, maxInt: 6);
        Reg(W3ObjectCategory.Buffs, "fsat", "Art", "Efecto Especial (Special Art)", "Special", ObjectDataType.String, "Specialart");
        Reg(W3ObjectCategory.Buffs, "fspt", "Art", "Punto de Anclaje Especial", "Special Attachment Point", ObjectDataType.String, "Specialattach");
        Reg(W3ObjectCategory.Buffs, "feat", "Art", "Efecto (Effect Art)", "Effect", ObjectDataType.String, "Effectart");
        Reg(W3ObjectCategory.Buffs, "feft", "Art", "Punto de Anclaje de Efecto", "Effect Attachment Point", ObjectDataType.String, "Effectattach");
        Reg(W3ObjectCategory.Buffs, "fmat", "Art", "Modelo de Proyectil", "Missile Art", ObjectDataType.String, "Missileart");
        Reg(W3ObjectCategory.Buffs, "fmsp", "Art", "Velocidad de Proyectil", "Missile Speed", ObjectDataType.Int, "Missilespeed", minInt: 0, maxInt: 20000);
        Reg(W3ObjectCategory.Buffs, "fmac", "Art", "Arco de Proyectil", "Missile Arc", ObjectDataType.Real, "Missilearc", minReal: 0.0, maxReal: 1.0);
        Reg(W3ObjectCategory.Buffs, "fmho", "Art", "Proyectil Teledirigido", "Missile Homing Enabled", ObjectDataType.Int, "MissileHoming", boolChoices);
        Reg(W3ObjectCategory.Buffs, "flig", "Art", "Efectos de Rayo (Lightning)", "Lightning", ObjectDataType.String, "LightningEffect");
        Reg(W3ObjectCategory.Buffs, "fefs", "Sound", "Sonido de Efecto", "Effect Sound", ObjectDataType.String, "Effectsound");
        Reg(W3ObjectCategory.Buffs, "fefl", "Sound", "Sonido de Efecto (En Bucle)", "Effect Sound (Looping)", ObjectDataType.String, "Effectsoundlooped");
        Reg(W3ObjectCategory.Buffs, "frac", "Stats", "Raza", "Race", ObjectDataType.String, "race", raceChoices);
        Reg(W3ObjectCategory.Buffs, "feff", "Stats", "Es un Efecto", "Is an Effect", ObjectDataType.Int, "isEffect", boolChoices);
        Reg(W3ObjectCategory.Buffs, "fnam", "Text", "Nombre (Solo Editor)", "Name (Editor Only)", ObjectDataType.String, "EditorName");
        Reg(W3ObjectCategory.Buffs, "fnsf", "Text", "Sufijo del Editor", "Editor Suffix", ObjectDataType.String, "EditorSuffix");
        Reg(W3ObjectCategory.Buffs, "ftip", "Text", "Tooltip", "Tooltip", ObjectDataType.String, "Bufftip");
        Reg(W3ObjectCategory.Buffs, "fube", "Text", "Tooltip Extendido", "Tooltip - Extended", ObjectDataType.String, "Buffubertip");

        // =========================================================================
        // 5. DESTRUCTABLES (ÁRBOLES, PUERTAS, PUENTES) — CATÁLOGO COMPLETO 1.27b
        // =========================================================================
        Reg(W3ObjectCategory.Destructables, "bfil", "Art", "Archivo de Modelo 3D (.mdx)", "Model File", ObjectDataType.String, "file");
        Reg(W3ObjectCategory.Destructables, "bvar", "Art", "Variaciones del Modelo", "Model File - Variations", ObjectDataType.Int, "numVar", minInt: 1, maxInt: 10);
        Reg(W3ObjectCategory.Destructables, "btxi", "Art", "ID de Textura Reemplazable", "Replaceable Texture ID", ObjectDataType.Int, "texID", minInt: 0, maxInt: 100);
        Reg(W3ObjectCategory.Destructables, "btxf", "Art", "Archivo de Textura Reemplazable", "Replaceable Texture File", ObjectDataType.String, "texFile");
        Reg(W3ObjectCategory.Destructables, "bflo", "Art", "Altura de Vuelo", "Fly-Over Height", ObjectDataType.Real, "flyH", minReal: 0.0, maxReal: 5000.0);
        Reg(W3ObjectCategory.Destructables, "bfxr", "Art", "Rotación Fija", "Fixed Rotation", ObjectDataType.Real, "fixedRot", minReal: -1.0, maxReal: 360.0);
        Reg(W3ObjectCategory.Destructables, "bsel", "Art", "Tamaño del Círculo de Selección (Juego)", "Selection Size - Game", ObjectDataType.Real, "selcircsize", minReal: 0.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Destructables, "bgsc", "Art", "Tamaño del Círculo de Selección (Editor)", "Selection Size - Editor", ObjectDataType.Real, "selSize", minReal: 0.0, maxReal: 2000.0);
        Reg(W3ObjectCategory.Destructables, "bclh", "Art", "Altura de Oclusión", "Occluder Height", ObjectDataType.Real, "cliffHeight", minReal: 0.0, maxReal: 10.0);
        Reg(W3ObjectCategory.Destructables, "bclr", "Art", "Color de Tinte 1 (Rojo 0-255)", "Tinting Color 1 (Red)", ObjectDataType.Int, "colorR", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Destructables, "bclg", "Art", "Color de Tinte 2 (Verde 0-255)", "Tinting Color 2 (Green)", ObjectDataType.Int, "colorG", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Destructables, "bclb", "Art", "Color de Tinte 3 (Azul 0-255)", "Tinting Color 3 (Blue)", ObjectDataType.Int, "colorB", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Destructables, "bshd", "Art", "Sombra", "Shadow", ObjectDataType.String, "shadow");
        Reg(W3ObjectCategory.Destructables, "bsmm", "Art", "Mostrar en Minimapa", "Show In Minimap", ObjectDataType.Int, "showInMM", boolChoices);
        Reg(W3ObjectCategory.Destructables, "bmmr", "Art", "Color en Minimapa (Rojo)", "Minimap Color 1 (Red)", ObjectDataType.Int, "MMRed", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Destructables, "bmmg", "Art", "Color en Minimapa (Verde)", "Minimap Color 2 (Green)", ObjectDataType.Int, "MMGreen", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Destructables, "bmmb", "Art", "Color en Minimapa (Azul)", "Minimap Color 3 (Blue)", ObjectDataType.Int, "MMBlue", minInt: 0, maxInt: 255);
        Reg(W3ObjectCategory.Destructables, "barm", "Combat", "Tipo de Armadura", "Armor Type", ObjectDataType.String, "armor", armorSoundChoices);
        Reg(W3ObjectCategory.Destructables, "btar", "Combat", "Clasificado como Objetivo (Targeted as)", "Targeted As", ObjectDataType.String, "targType");
        Reg(W3ObjectCategory.Destructables, "bcat", "Editor", "Categoría", "Category", ObjectDataType.String, "category");
        Reg(W3ObjectCategory.Destructables, "btil", "Editor", "Conjuntos de Terreno (Tilesets)", "Tilesets", ObjectDataType.String, "tilesets");
        Reg(W3ObjectCategory.Destructables, "bmis", "Editor", "Escala Mínima", "Minimum Scale", ObjectDataType.Real, "minScale", minReal: 0.01, maxReal: 10.0);
        Reg(W3ObjectCategory.Destructables, "bmas", "Editor", "Escala Máxima", "Maximum Scale", ObjectDataType.Real, "maxScale", minReal: 0.01, maxReal: 10.0);
        Reg(W3ObjectCategory.Destructables, "bcpr", "Editor", "Puede Colocar Escala Aleatoria", "Can Place Random Scale", ObjectDataType.Int, "canPlaceRandScale", boolChoices);
        Reg(W3ObjectCategory.Destructables, "bcpd", "Editor", "Puede Colocar Muerto", "Can Place Dead", ObjectDataType.Int, "canPlaceDead", boolChoices);
        Reg(W3ObjectCategory.Destructables, "bptx", "Pathing", "Textura de Colisión (Vivo)", "Pathing Texture", ObjectDataType.String, "pathTex");
        Reg(W3ObjectCategory.Destructables, "bptd", "Pathing", "Textura de Colisión (Muerto)", "Pathing Texture (Dead)", ObjectDataType.String, "pathTexDeath");
        Reg(W3ObjectCategory.Destructables, "bwal", "Pathing", "Caminable (Puente / Rampa)", "Is Walkable", ObjectDataType.Int, "walkable", boolChoices);
        Reg(W3ObjectCategory.Destructables, "bdsn", "Sound", "Sonido al Morir", "Death", ObjectDataType.String, "deathSnd");
        Reg(W3ObjectCategory.Destructables, "bhps", "Stats", "Puntos de Vida", "Hit Points", ObjectDataType.Real, "HP", minReal: 1.0, maxReal: 999999.0);
        Reg(W3ObjectCategory.Destructables, "breg", "Stats", "Tiempo de Construcción", "Build Time", ObjectDataType.Int, "buildTime", minInt: 0, maxInt: 3600);
        Reg(W3ObjectCategory.Destructables, "brep", "Stats", "Tiempo de Reparación", "Repair Time", ObjectDataType.Int, "repairTime", minInt: 0, maxInt: 3600);
        Reg(W3ObjectCategory.Destructables, "bgol", "Stats", "Costo de Oro de Reparación", "Gold Repair Cost", ObjectDataType.Int, "goldRep", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Destructables, "blum", "Stats", "Costo de Madera de Reparación", "Lumber Repair Cost", ObjectDataType.Int, "lumberRep", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Destructables, "bnam", "Text", "Nombre", "Name", ObjectDataType.String, "Name");
        Reg(W3ObjectCategory.Destructables, "bsuf", "Text", "Sufijo del Editor", "Editor Suffix", ObjectDataType.String, "EditorSuffix");

        // =========================================================================
        // 6. DOODADS (DECORACIONES DE ENTORNO) — CATÁLOGO COMPLETO 1.27b
        // =========================================================================
        Reg(W3ObjectCategory.Doodads, "dfil", "Art", "Archivo de Modelo 3D (.mdx)", "Model File", ObjectDataType.String, "file");
        Reg(W3ObjectCategory.Doodads, "dvar", "Art", "Variaciones del Modelo", "Variations", ObjectDataType.Int, "numVar", minInt: 1, maxInt: 10);
        Reg(W3ObjectCategory.Doodads, "dflo", "Art", "Flota en el Agua", "Floats", ObjectDataType.Int, "floats", boolChoices);
        Reg(W3ObjectCategory.Doodads, "dfxr", "Art", "Rotación Fija", "Fixed Rotation", ObjectDataType.Real, "fixedRot", minReal: -1.0, maxReal: 360.0);
        Reg(W3ObjectCategory.Doodads, "dshd", "Art", "Tiene Sombra", "Has a Shadow", ObjectDataType.Int, "shadow", boolChoices);
        Reg(W3ObjectCategory.Doodads, "dsmm", "Art", "Mostrar en Minimapa", "Show In Minimap", ObjectDataType.Int, "showInMM", boolChoices);
        Reg(W3ObjectCategory.Doodads, "dfor", "Art", "Mostrar en Niebla de Guerra", "Show In Fog", ObjectDataType.Int, "showInFog", boolChoices);
        Reg(W3ObjectCategory.Doodads, "danf", "Art", "Animar en Niebla de Guerra", "Animate In Fog", ObjectDataType.Int, "animInFog", boolChoices);
        Reg(W3ObjectCategory.Doodads, "dmis", "Editor", "Escala Mínima", "Minimum Scale", ObjectDataType.Real, "minScale", minReal: 0.01, maxReal: 10.0);
        Reg(W3ObjectCategory.Doodads, "dmas", "Editor", "Escala Máxima", "Maximum Scale", ObjectDataType.Real, "maxScale", minReal: 0.01, maxReal: 10.0);
        Reg(W3ObjectCategory.Doodads, "ddes", "Editor", "Escala por Defecto", "Default Scale", ObjectDataType.Real, "defScale", minReal: 0.01, maxReal: 10.0);
        Reg(W3ObjectCategory.Doodads, "dcpr", "Editor", "Puede Colocar Escala Aleatoria", "Can Place Random Scale", ObjectDataType.Int, "canPlaceRandScale", boolChoices);
        Reg(W3ObjectCategory.Doodads, "dcat", "Editor", "Categoría", "Category", ObjectDataType.String, "category");
        Reg(W3ObjectCategory.Doodads, "dtil", "Editor", "Conjuntos de Terreno (Tilesets)", "Tilesets", ObjectDataType.String, "tilesets");
        Reg(W3ObjectCategory.Doodads, "duch", "Editor", "Usar Ayudante de Clic", "Use Click Helper", ObjectDataType.Int, "useClickHelper", boolChoices);
        Reg(W3ObjectCategory.Doodads, "digl", "Editor", "Ignorar Clics de Modelo", "Ignore Model Clicks", ObjectDataType.Int, "ignoreModelClick", boolChoices);
        Reg(W3ObjectCategory.Doodads, "dptx", "Pathing", "Textura de Colisión (Pathing Texture)", "Pathing Texture", ObjectDataType.String, "pathTex");
        Reg(W3ObjectCategory.Doodads, "dwlk", "Pathing", "Caminable", "Walkable", ObjectDataType.Int, "walkable", boolChoices);
        Reg(W3ObjectCategory.Doodads, "dsnd", "Sound", "Sonido en Bucle", "Looping Sound", ObjectDataType.String, "soundLoop");
        Reg(W3ObjectCategory.Doodads, "dnam", "Text", "Nombre", "Name", ObjectDataType.String, "Name");

        // =========================================================================
        // 7. UPGRADES (MEJORAS TECNOLÓGICAS) — CATÁLOGO COMPLETO 1.27b
        // =========================================================================
        Reg(W3ObjectCategory.Upgrades, "gar1", "Art", "Icono", "Icon", ObjectDataType.String, "Art", repeatLevels: true);
        Reg(W3ObjectCategory.Upgrades, "gbpx", "Art", "Posición de Botón (X)", "Button Position (X)", ObjectDataType.Int, "Buttonpos", minInt: 0, maxInt: 3, index: 0);
        Reg(W3ObjectCategory.Upgrades, "gbpy", "Art", "Posición de Botón (Y)", "Button Position (Y)", ObjectDataType.Int, "Buttonpos", minInt: 0, maxInt: 2, index: 1);
        Reg(W3ObjectCategory.Upgrades, "gef1", "Data", "Efecto 1 - Tipo", "Effect 1", ObjectDataType.String, "effect1");
        Reg(W3ObjectCategory.Upgrades, "gba1", "Data", "Efecto 1 - Base", "Effect 1 - Base", ObjectDataType.Real, "base1");
        Reg(W3ObjectCategory.Upgrades, "gmo1", "Data", "Efecto 1 - Incremento por Nivel", "Effect 1 - Increment", ObjectDataType.Real, "mod1");
        Reg(W3ObjectCategory.Upgrades, "gco1", "Data", "Efecto 1 - Código / Unidad", "Effect 1 - Code", ObjectDataType.String, "code1");
        Reg(W3ObjectCategory.Upgrades, "gef2", "Data", "Efecto 2 - Tipo", "Effect 2", ObjectDataType.String, "effect2");
        Reg(W3ObjectCategory.Upgrades, "gba2", "Data", "Efecto 2 - Base", "Effect 2 - Base", ObjectDataType.Real, "base2");
        Reg(W3ObjectCategory.Upgrades, "gmo2", "Data", "Efecto 2 - Incremento por Nivel", "Effect 2 - Increment", ObjectDataType.Real, "mod2");
        Reg(W3ObjectCategory.Upgrades, "gco2", "Data", "Efecto 2 - Código / Unidad", "Effect 2 - Code", ObjectDataType.String, "code2");
        Reg(W3ObjectCategory.Upgrades, "gef3", "Data", "Efecto 3 - Tipo", "Effect 3", ObjectDataType.String, "effect3");
        Reg(W3ObjectCategory.Upgrades, "gba3", "Data", "Efecto 3 - Base", "Effect 3 - Base", ObjectDataType.Real, "base3");
        Reg(W3ObjectCategory.Upgrades, "gmo3", "Data", "Efecto 3 - Incremento por Nivel", "Effect 3 - Increment", ObjectDataType.Real, "mod3");
        Reg(W3ObjectCategory.Upgrades, "gco3", "Data", "Efecto 3 - Código / Unidad", "Effect 3 - Code", ObjectDataType.String, "code3");
        Reg(W3ObjectCategory.Upgrades, "gef4", "Data", "Efecto 4 - Tipo", "Effect 4", ObjectDataType.String, "effect4");
        Reg(W3ObjectCategory.Upgrades, "gba4", "Data", "Efecto 4 - Base", "Effect 4 - Base", ObjectDataType.Real, "base4");
        Reg(W3ObjectCategory.Upgrades, "gmo4", "Data", "Efecto 4 - Incremento por Nivel", "Effect 4 - Increment", ObjectDataType.Real, "mod4");
        Reg(W3ObjectCategory.Upgrades, "gco4", "Data", "Efecto 4 - Código / Unidad", "Effect 4 - Code", ObjectDataType.String, "code4");
        Reg(W3ObjectCategory.Upgrades, "glvl", "Stats", "Cantidad de Niveles", "Levels", ObjectDataType.Int, "maxlevel", minInt: 1, maxInt: 100);
        Reg(W3ObjectCategory.Upgrades, "gglb", "Stats", "Costo de Oro Base", "Gold Cost Base", ObjectDataType.Int, "goldbase", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Upgrades, "gglm", "Stats", "Incremento de Oro por Nivel", "Gold Cost Increment", ObjectDataType.Int, "goldmod", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Upgrades, "glmb", "Stats", "Costo de Madera Base", "Lumber Cost Base", ObjectDataType.Int, "lumberbase", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Upgrades, "glmm", "Stats", "Incremento de Madera por Nivel", "Lumber Cost Increment", ObjectDataType.Int, "lumbermod", minInt: 0, maxInt: 100000);
        Reg(W3ObjectCategory.Upgrades, "gtib", "Stats", "Tiempo Base de Investigación", "Time Base", ObjectDataType.Int, "timebase", minInt: 1, maxInt: 3600);
        Reg(W3ObjectCategory.Upgrades, "gtim", "Stats", "Incremento de Tiempo por Nivel", "Time Increment", ObjectDataType.Int, "timemod", minInt: 0, maxInt: 3600);
        Reg(W3ObjectCategory.Upgrades, "grac", "Stats", "Raza", "Race", ObjectDataType.String, "race", raceChoices);
        Reg(W3ObjectCategory.Upgrades, "gcls", "Stats", "Clase de Mejora", "Class", ObjectDataType.String, "class");
        Reg(W3ObjectCategory.Upgrades, "ginh", "Stats", "Se Transfiere al Cambiar de Dueño", "Applies to all units", ObjectDataType.Int, "inherit", boolChoices);
        Reg(W3ObjectCategory.Upgrades, "greq", "Techtree", "Requisitos Tecnológicos", "Requirements", ObjectDataType.String, "Requires", repeatLevels: true);
        Reg(W3ObjectCategory.Upgrades, "grqc", "Techtree", "Niveles de Requisitos", "Requirements - Levels", ObjectDataType.String, "Requiresamount", repeatLevels: true);
        Reg(W3ObjectCategory.Upgrades, "gnam", "Text", "Nombre", "Name", ObjectDataType.String, "Name", repeatLevels: true);
        Reg(W3ObjectCategory.Upgrades, "gnsf", "Text", "Sufijo del Editor", "Editor Suffix", ObjectDataType.String, "EditorSuffix", repeatLevels: true);
        Reg(W3ObjectCategory.Upgrades, "ghk1", "Text", "Tecla Rápida (Hotkey)", "Hotkey", ObjectDataType.String, "Hotkey", repeatLevels: true);
        Reg(W3ObjectCategory.Upgrades, "gtp1", "Text", "Tooltip Básico", "Tooltip", ObjectDataType.String, "Tip", repeatLevels: true);
        Reg(W3ObjectCategory.Upgrades, "gub1", "Text", "Tooltip Extendido", "Tooltip - Extended", ObjectDataType.String, "Ubertip", repeatLevels: true);
    }

    public string GetFieldDisplayName(string fieldId, bool includeRawId = true)
    {
        if (_fieldCatalog.TryGetValue(fieldId, out var def))
        {
            return includeRawId ? $"{def.DisplayName} ({def.FieldId})" : def.DisplayName;
        }
        return fieldId;
    }

    public ObjectDataType GetFieldDataType(string fieldId)
    {
        return _fieldCatalog.TryGetValue(fieldId, out var def) ? def.DataType : ObjectDataType.String;
    }

    public void RefreshLocalization(MapProject? project)
    {
        if (project == null) return;
        foreach (var obj in project.Objects)
        {
            foreach (var field in obj.Fields)
            {
                field.FieldName = GetFieldDisplayName(field.FieldId, false);
                field.NotifyLocalizationChanged();
            }
        }
    }

    public void LoadFromWarcraftFolder(string warcraftDir)
    {
        _allObjects.Clear();
        _weStrings.Clear();
        InitializeDefaultFieldCatalog();

        if (string.IsNullOrWhiteSpace(warcraftDir) || !Directory.Exists(warcraftDir))
        {
            StatusSummary = LocalizationService.L(
                "Carpeta de Warcraft III no configurada.",
                "Warcraft III folder is not configured.");
            IsLoaded = false;
            return;
        }

        var mpqNames = new[] { "War3.mpq", "War3x.mpq", "War3xlocal.mpq", "War3Patch.mpq" };
        var archives = new List<MpqArchive>();

        try
        {
            foreach (var name in mpqNames)
            {
                var fullPath = Path.Combine(warcraftDir, name);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        archives.Add(MpqArchive.Open(fullPath, true));
                    }
                    catch
                    {
                        // Si un MPQ está bloqueado por otro proceso, creamos una copia sombra temporal de lectura
                        var shadowPath = Path.Combine(Path.GetTempPath(), $"w3studio_shadow_{name}");
                        File.Copy(fullPath, shadowPath, true);
                        archives.Add(MpqArchive.Open(shadowPath, true));
                    }
                }
            }

            if (archives.Count == 0)
            {
                StatusSummary = LocalizationService.L(
                    "No se encontraron archivos MPQ en la carpeta de Warcraft III.",
                    "No MPQ archives found in Warcraft III folder.");
                IsLoaded = false;
                return;
            }

            // 1. Cargar WorldEditStrings.txt y WorldEditGameStrings.txt (para resolver WESTRING_*)
            var weStringsContent = ReadTextFromArchives(archives, @"UI\WorldEditStrings.txt");
            if (!string.IsNullOrEmpty(weStringsContent))
                ParseWorldEditStrings(weStringsContent);

            var weGameStringsContent = ReadTextFromArchives(archives, @"UI\WorldEditGameStrings.txt");
            if (!string.IsNullOrEmpty(weGameStringsContent))
                ParseWorldEditStrings(weGameStringsContent);

            // 2. Cargar los *MetaData.slk oficiales de Warcraft III 1.27b para tener el 100% de los campos
            ParseObjectMetaSlk(archives, @"Units\UnitMetaData.slk", W3ObjectCategory.Units);
            ParseObjectMetaSlk(archives, @"Units\AbilityMetaData.slk", W3ObjectCategory.Abilities);
            ParseObjectMetaSlk(archives, @"Units\AbilityBuffMetaData.slk", W3ObjectCategory.Buffs);
            ParseObjectMetaSlk(archives, @"Units\DestructableMetaData.slk", W3ObjectCategory.Destructables);
            ParseObjectMetaSlk(archives, @"Doodads\DoodadMetaData.slk", W3ObjectCategory.Doodads);
            ParseObjectMetaSlk(archives, @"Units\UpgradeMetaData.slk", W3ObjectCategory.Upgrades);

            // 3. Cargar Tablas SLK Base (Units, Items, Abilities, Buffs, Destructables, Doodads, Upgrades)
            ParseSlkTable(archives, @"Units\UnitData.slk", W3ObjectCategory.Units);
            ParseSlkTable(archives, @"Units\UnitBalance.slk", W3ObjectCategory.Units);
            ParseSlkTable(archives, @"Units\UnitUI.slk", W3ObjectCategory.Units);
            ParseSlkTable(archives, @"Units\UnitWeapons.slk", W3ObjectCategory.Units);
            ParseSlkTable(archives, @"Units\UnitAbilities.slk", W3ObjectCategory.Units);

            ParseSlkTable(archives, @"Units\ItemData.slk", W3ObjectCategory.Items);
            ParseSlkTable(archives, @"Units\AbilityData.slk", W3ObjectCategory.Abilities);
            ParseSlkTable(archives, @"Units\AbilityBuffData.slk", W3ObjectCategory.Buffs);
            ParseSlkTable(archives, @"Units\DestructableData.slk", W3ObjectCategory.Destructables);
            ParseSlkTable(archives, @"Doodads\Doodads.slk", W3ObjectCategory.Doodads);
            ParseSlkTable(archives, @"Units\UpgradeData.slk", W3ObjectCategory.Upgrades);
            ParseTerrainSlk(archives, @"TerrainArt\Terrain.slk");

            // 4. Cargar Archivos INI/TXT de Nombres, Iconos, Tooltips y Posiciones de Botón
            var profileFiles = new[]
            {
                @"Units\CommandFunc.txt",
                @"Units\CommandStrings.txt",
                @"Units\HumanUnitFunc.txt",
                @"Units\HumanUnitStrings.txt",
                @"Units\OrcUnitFunc.txt",
                @"Units\OrcUnitStrings.txt",
                @"Units\UndeadUnitFunc.txt",
                @"Units\UndeadUnitStrings.txt",
                @"Units\NightElfUnitFunc.txt",
                @"Units\NightElfUnitStrings.txt",
                @"Units\NeutralUnitFunc.txt",
                @"Units\NeutralUnitStrings.txt",
                @"Units\CampaignUnitFunc.txt",
                @"Units\CampaignUnitStrings.txt",
                @"Units\ItemFunc.txt",
                @"Units\ItemStrings.txt",
                @"Units\HumanAbilityFunc.txt",
                @"Units\HumanAbilityStrings.txt",
                @"Units\OrcAbilityFunc.txt",
                @"Units\OrcAbilityStrings.txt",
                @"Units\UndeadAbilityFunc.txt",
                @"Units\UndeadAbilityStrings.txt",
                @"Units\NightElfAbilityFunc.txt",
                @"Units\NightElfAbilityStrings.txt",
                @"Units\NeutralAbilityFunc.txt",
                @"Units\NeutralAbilityStrings.txt",
                @"Units\ItemAbilityFunc.txt",
                @"Units\ItemAbilityStrings.txt",
                @"Units\CommonAbilityFunc.txt",
                @"Units\CommonAbilityStrings.txt",
                @"Units\CampaignAbilityFunc.txt",
                @"Units\CampaignAbilityStrings.txt",
                @"Units\HumanUpgradeFunc.txt",
                @"Units\HumanUpgradeStrings.txt",
                @"Units\OrcUpgradeFunc.txt",
                @"Units\OrcUpgradeStrings.txt",
                @"Units\UndeadUpgradeFunc.txt",
                @"Units\UndeadUpgradeStrings.txt",
                @"Units\NightElfUpgradeFunc.txt",
                @"Units\NightElfUpgradeStrings.txt",
                @"Units\NeutralUpgradeFunc.txt",
                @"Units\NeutralUpgradeStrings.txt",
                @"Units\CampaignUpgradeFunc.txt",
                @"Units\CampaignUpgradeStrings.txt"
            };

            foreach (var txtPath in profileFiles)
            {
                var content = ReadAllTextLayersFromArchives(archives, txtPath);
                if (!string.IsNullOrEmpty(content))
                {
                    ParseProfileIni(content);
                }
            }

            // Resolver nombres WESTRING_* en Destructables y Doodads
            foreach (var meta in _allObjects.Values)
            {
                if (string.IsNullOrWhiteSpace(meta.Name) && meta.DefaultFields.TryGetValue("Name", out var slkName))
                {
                    meta.Name = ResolveWeString(slkName);
                }
                else if (meta.Name.StartsWith("WESTRING_", StringComparison.OrdinalIgnoreCase))
                {
                    meta.Name = ResolveWeString(meta.Name);
                }

                if (string.IsNullOrWhiteSpace(meta.Name) && meta.DefaultFields.TryGetValue("EditorName", out var edName))
                {
                    meta.Name = ResolveWeString(edName);
                }
                if (string.IsNullOrWhiteSpace(meta.Name) && meta.DefaultFields.TryGetValue("Bufftip", out var btip))
                {
                    meta.Name = ResolveWeString(btip);
                }
                if (string.IsNullOrWhiteSpace(meta.Name) && meta.DefaultFields.TryGetValue("code", out var codeName))
                {
                    meta.Name = codeName;
                }
                if (string.IsNullOrWhiteSpace(meta.Name))
                {
                    meta.Name = meta.Rawcode;
                }
                meta.SubcategoryFolder = !string.IsNullOrWhiteSpace(meta.Race) && meta.Race != "Other"
                    ? meta.Race
                    : "General";
            }

            IsLoaded = true;
            StatusSummary = LocalizationService.L(
                $"MPQ Base Cargado: {_allObjects.Count:N0} objetos y {_fieldCatalog.Count:N0} campos de Warcraft III 1.27b.",
                $"Base MPQ Loaded: {_allObjects.Count:N0} objects and {_fieldCatalog.Count:N0} fields from Warcraft III 1.27b.");
        }
        catch (Exception ex)
        {
            StatusSummary = $"Error MPQ: {ex.Message}";
            IsLoaded = false;
        }
        finally
        {
            foreach (var arch in archives)
            {
                try { arch.Dispose(); }
                catch (Exception ex) { AppLogger.Warn("WarcraftBaseDataService.LoadFromWarcraft3", "Error disposing base MPQ archive.", ex); }
            }
        }
    }

    private void ParseObjectMetaSlk(List<MpqArchive> archives, string virtualPath, W3ObjectCategory defaultCategory)
    {
        var slkText = ReadTextFromArchives(archives, virtualPath);
        if (string.IsNullOrEmpty(slkText))
            return;

        var rows = ParseRawSlkGrid(slkText);
        if (rows.Count <= 1)
            return;

        var headers = new Dictionary<int, string>();
        foreach (var kvp in rows[1])
        {
            headers[kvp.Key] = kvp.Value.Trim('"');
        }

        string GetCell(Dictionary<int, string> row, string colName)
        {
            foreach (var h in headers)
            {
                if (h.Value.Equals(colName, StringComparison.OrdinalIgnoreCase) && row.TryGetValue(h.Key, out var val))
                    return val.Trim('"');
            }
            return string.Empty;
        }

        foreach (var rowKvp in rows.Skip(1))
        {
            var row = rowKvp.Value;
            if (!row.TryGetValue(1, out var rawId))
                continue;

            var fieldId = rawId.Trim('"').Trim();
            if (fieldId.Length != 4)
                continue;

            var slkCol = GetCell(row, "field");
            var section = GetCell(row, "category");
            var displayKey = GetCell(row, "displayName");
            var typeStr = GetCell(row, "type");
            var indexStr = GetCell(row, "index");
            var repeatStr = GetCell(row, "repeat");
            var dataPtrStr = GetCell(row, "data");
            var minValStr = GetCell(row, "minVal");
            var maxValStr = GetCell(row, "maxVal");
            var useUnitStr = GetCell(row, "useUnit");
            var useHeroStr = GetCell(row, "useHero");
            var useBuildingStr = GetCell(row, "useBuilding");
            var useItemStr = GetCell(row, "useItem");
            var useSpecStr = GetCell(row, "useSpecific");
            var notSpecStr = GetCell(row, "notSpecific");

            var resolvedEnTitle = ResolveWeString(displayKey);
            if (string.IsNullOrWhiteSpace(resolvedEnTitle) || resolvedEnTitle.StartsWith("WESTRING_", StringComparison.OrdinalIgnoreCase))
            {
                resolvedEnTitle = !string.IsNullOrWhiteSpace(slkCol) ? slkCol : fieldId;
            }

            var targetCat = defaultCategory;
            if (defaultCategory == W3ObjectCategory.Units && useItemStr == "1" && useUnitStr != "1" && useHeroStr != "1" && useBuildingStr != "1")
            {
                targetCat = W3ObjectCategory.Items;
            }

            int.TryParse(indexStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedIndex);
            if (string.IsNullOrWhiteSpace(indexStr)) parsedIndex = -1;

            int.TryParse(repeatStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRepeat);
            int.TryParse(dataPtrStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDataPtr);

            if (defaultCategory == W3ObjectCategory.Abilities && parsedDataPtr is >= 1 and <= 9)
            {
                var letter = (char)('A' + parsedDataPtr - 1);
                slkCol = $"Data{letter}1";
            }

            var mappedType = MapSlkTypeToObjectDataType(typeStr);
            var mappedChoices = MapSlkTypeToChoices(typeStr);

            if (_fieldCatalog.TryGetValue(fieldId, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(typeStr))
                    existing.SlkTypeName = typeStr;
                if (!string.IsNullOrWhiteSpace(slkCol) && string.IsNullOrWhiteSpace(existing.SlkColumn))
                    existing.SlkColumn = slkCol;
                if (!string.IsNullOrWhiteSpace(useSpecStr))
                    existing.UseSpecific = useSpecStr;
                if (!string.IsNullOrWhiteSpace(notSpecStr))
                    existing.NotSpecific = notSpecStr;
                if (parsedDataPtr > 0)
                    existing.DataPointer = parsedDataPtr;
                if (parsedRepeat > 0)
                    existing.RepeatForLevels = true;
                if (defaultCategory == W3ObjectCategory.Units)
                {
                    existing.UseUnit = useUnitStr == "1";
                    existing.UseHero = useHeroStr == "1";
                    existing.UseBuilding = useBuildingStr == "1";
                    existing.UseItem = useItemStr == "1";
                }
            }
            else
            {
                var def = new FieldMetaDefinition
                {
                    FieldId = fieldId,
                    TargetCategory = targetCat,
                    SectionKey = NormalizeSectionKey(section),
                    TitleEn = resolvedEnTitle,
                    TitleEs = LocalizationService.TranslateEnglishFieldNameToSpanish(resolvedEnTitle),
                    DataType = mappedType,
                    SlkTypeName = typeStr,
                    SlkColumn = slkCol,
                    Index = parsedIndex,
                    DataPointer = parsedDataPtr,
                    RepeatForLevels = parsedRepeat > 0,
                    Choices = mappedChoices,
                    UseUnit = defaultCategory != W3ObjectCategory.Units || useUnitStr == "1",
                    UseHero = defaultCategory != W3ObjectCategory.Units || useHeroStr == "1",
                    UseBuilding = defaultCategory != W3ObjectCategory.Units || useBuildingStr == "1",
                    UseItem = useItemStr == "1",
                    UseSpecific = useSpecStr,
                    NotSpecific = notSpecStr
                };

                if (mappedType == ObjectDataType.Int)
                {
                    if (int.TryParse(minValStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var minI)) def.MinInt = minI;
                    if (int.TryParse(maxValStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var maxI) && maxI > minI) def.MaxInt = maxI;
                }
                else if (mappedType is ObjectDataType.Real or ObjectDataType.Unreal)
                {
                    if (double.TryParse(minValStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var minR)) def.MinReal = minR;
                    if (double.TryParse(maxValStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var maxR) && maxR > minR) def.MaxReal = maxR;
                }

                _fieldCatalog[fieldId] = def;
                if (_categoryFieldOrder.TryGetValue(targetCat, out var orderList) && !orderList.Contains(fieldId, StringComparer.OrdinalIgnoreCase))
                {
                    orderList.Add(fieldId);
                }
            }
        }
    }

    private static string NormalizeSectionKey(string rawCat)
    {
        return (rawCat ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "art" => "Art",
            "combat" => "Combat",
            "stats" => "Stats",
            "movement" => "Movement",
            "path" or "pathing" => "Pathing",
            "sound" => "Sound",
            "tech" or "techtree" => "Techtree",
            "text" => "Text",
            "abilities" or "ability" => "Abilities",
            "editor" => "Editor",
            "data" => "Data",
            _ => "Stats"
        };
    }

    private static ObjectDataType MapSlkTypeToObjectDataType(string slkType)
    {
        return (slkType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "int" or "bool" or "deathType" or "deathtype" or "teamcolor" or "attackbits" or "versionflags" or "silenceflags" or "spelldetail" => ObjectDataType.Int,
            "real" => ObjectDataType.Real,
            "unreal" => ObjectDataType.Unreal,
            _ => ObjectDataType.String
        };
    }

    private static List<FieldChoiceOption>? MapSlkTypeToChoices(string slkType)
    {
        return (slkType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "bool" => CreateBoolChoices(),
            "attacktype" => CreateAttackTypeChoices(),
            "defensetype" => CreateDefenseTypeChoices(),
            "armortype" => CreateArmorSoundChoices(),
            "movetype" => CreateMoveTypeChoices(),
            "weapontype" => CreateWeaponTypeChoices(),
            "attributetype" => CreateHeroAttributeChoices(),
            "regentype" => CreateRegenTypeChoices(),
            "deathtype" => CreateDeathTypeChoices(),
            "unitrace" => CreateRaceChoices(),
            "itemclass" => CreateItemClassChoices(),
            "teamcolor" => CreateTeamColorChoices(),
            "attackbits" => CreateAttacksEnabledChoices(),
            _ => null
        };
    }

    private void ParseWorldEditStrings(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("[")) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"');
            _weStrings[key] = val;
        }
    }

    public string ResolveWeString(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.StartsWith("WESTRING_", StringComparison.OrdinalIgnoreCase) && _weStrings.TryGetValue(value, out var resolved))
        {
            if (resolved.StartsWith("WESTRING_", StringComparison.OrdinalIgnoreCase) && _weStrings.TryGetValue(resolved, out var resolved2))
                return resolved2;
            return resolved;
        }
        return value;
    }

    private static Dictionary<int, Dictionary<int, string>> ParseRawSlkGrid(string slkText)
    {
        var rows = new Dictionary<int, Dictionary<int, string>>();
        int curX = 1, curY = 1;

        using var reader = new StringReader(slkText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.StartsWith("C;", StringComparison.Ordinal))
                continue;

            var parts = line.Split(';');
            string? val = null;
            for (int i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.Length == 0) continue;
                if (p[0] == 'X' && int.TryParse(p.AsSpan(1), out var x)) curX = x;
                else if (p[0] == 'Y' && int.TryParse(p.AsSpan(1), out var y)) curY = y;
                else if (p[0] == 'K')
                {
                    val = string.Join(";", parts.Skip(i))[1..];
                    break;
                }
            }

            if (val != null)
            {
                if (!rows.TryGetValue(curY, out var rowDict))
                {
                    rowDict = new Dictionary<int, string>();
                    rows[curY] = rowDict;
                }
                rowDict[curX] = val;
            }
        }
        return rows;
    }

    private void ParseSlkTable(List<MpqArchive> archives, string virtualPath, W3ObjectCategory category)
    {
        var slkText = ReadTextFromArchives(archives, virtualPath);
        if (string.IsNullOrEmpty(slkText)) return;

        var rows = ParseRawSlkGrid(slkText);
        if (rows.Count <= 1) return;

        var headers = new Dictionary<int, string>();
        foreach (var kvp in rows[1])
        {
            headers[kvp.Key] = kvp.Value.Trim('"');
        }

        foreach (var rowKvp in rows.Skip(1))
        {
            var row = rowKvp.Value;
            if (!row.TryGetValue(1, out var rawKey)) continue;
            var rawcode = rawKey.Trim('"');
            if (rawcode.Length != 4) continue;

            if (!_allObjects.TryGetValue(rawcode, out var meta))
            {
                meta = new BaseObjectMetadata
                {
                    Rawcode = rawcode,
                    Category = category
                };
                _allObjects[rawcode] = meta;
            }

            foreach (var cell in row)
            {
                if (headers.TryGetValue(cell.Key, out var colName))
                {
                    var cleanVal = cell.Value.Trim('"');
                    meta.DefaultFields[colName] = cleanVal;

                    if (colName.Equals("race", StringComparison.OrdinalIgnoreCase))
                        meta.Race = cleanVal;
                    else if (colName.Equals("Art", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(meta.IconPath))
                        meta.IconPath = cleanVal;
                }
            }
        }
    }

    private void ParseTerrainSlk(List<MpqArchive> archives, string virtualPath)
    {
        _terrainTileTextures.Clear();
        _terrainTileCatalog.Clear();
        _cliffTileCatalog.Clear();

        var slkText = ReadTextFromArchives(archives, virtualPath);
        if (!string.IsNullOrEmpty(slkText))
        {
            var rows = ParseRawSlkGrid(slkText);
            if (rows.Count > 1)
            {
                var headers = new Dictionary<int, string>();
                foreach (var kvp in rows[1])
                    headers[kvp.Key] = kvp.Value.Trim('"');

                foreach (var rowKvp in rows.Skip(1))
                {
                    var row = rowKvp.Value;
                    if (!row.TryGetValue(1, out var rawKey)) continue;
                    var tileId = rawKey.Trim('"');
                    if (tileId.Length != 4) continue;

                    string dir = string.Empty;
                    string file = string.Empty;
                    string comment = string.Empty;
                    string weNameKey = string.Empty;
                    bool buildable = true;
                    foreach (var cell in row)
                    {
                        if (!headers.TryGetValue(cell.Key, out var colName)) continue;
                        var val = cell.Value.Trim('"');
                        if (colName.Equals("dir", StringComparison.OrdinalIgnoreCase)) dir = val;
                        else if (colName.Equals("file", StringComparison.OrdinalIgnoreCase)) file = val;
                        else if (colName.Equals("comment", StringComparison.OrdinalIgnoreCase)) comment = val;
                        else if (colName.Equals("name", StringComparison.OrdinalIgnoreCase)) weNameKey = val;
                        else if (colName.Equals("buildable", StringComparison.OrdinalIgnoreCase)) buildable = val != "0";
                    }

                    if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(file))
                    {
                        string texPath = $"{dir}\\{file}.blp";
                        _terrainTileTextures[tileId] = texPath;
                        string display = ResolveWeString(weNameKey);
                        if (string.IsNullOrWhiteSpace(display) || display == weNameKey)
                            display = !string.IsNullOrWhiteSpace(comment) ? comment : TerrainBrushService.GetTilesetDisplayName(tileId);
                        _terrainTileCatalog.Add(new TerrainTileDefinition(tileId, display, texPath, buildable));
                    }
                }
            }
        }

        var cliffSlk = ReadTextFromArchives(archives, @"TerrainArt\CliffTypes.slk");
        if (!string.IsNullOrEmpty(cliffSlk))
        {
            var rows = ParseRawSlkGrid(cliffSlk);
            if (rows.Count > 1)
            {
                var headers = new Dictionary<int, string>();
                foreach (var kvp in rows[1])
                    headers[kvp.Key] = kvp.Value.Trim('"');

                foreach (var rowKvp in rows.Skip(1))
                {
                    var row = rowKvp.Value;
                    if (!row.TryGetValue(1, out var rawKey)) continue;
                    var cliffId = rawKey.Trim('"');
                    if (cliffId.Length != 4) continue;

                    string texDir = string.Empty;
                    string texFile = string.Empty;
                    string groundTile = string.Empty;
                    string weNameKey = string.Empty;
                    foreach (var cell in row)
                    {
                        if (!headers.TryGetValue(cell.Key, out var colName)) continue;
                        var val = cell.Value.Trim('"');
                        if (colName.Equals("texDir", StringComparison.OrdinalIgnoreCase)) texDir = val;
                        else if (colName.Equals("texFile", StringComparison.OrdinalIgnoreCase)) texFile = val;
                        else if (colName.Equals("groundTile", StringComparison.OrdinalIgnoreCase)) groundTile = val;
                        else if (colName.Equals("name", StringComparison.OrdinalIgnoreCase)) weNameKey = val;
                    }

                    string display = ResolveWeString(weNameKey);
                    if (string.IsNullOrWhiteSpace(display) || display == weNameKey)
                        display = string.IsNullOrWhiteSpace(groundTile) ? cliffId : $"{cliffId} ({groundTile})";
                    string texPath = !string.IsNullOrEmpty(texDir) && !string.IsNullOrEmpty(texFile)
                        ? $"{texDir}\\{texFile}.blp"
                        : string.Empty;
                    _cliffTileCatalog.Add(new CliffTileDefinition(cliffId, display, groundTile, texPath));
                }
            }
        }
    }

    private void ParseProfileIni(string iniText)
    {
        string? currentSection = null;
        BaseObjectMetadata? currentMeta = null;

        using var reader = new StringReader(iniText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith(";"))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (currentSection.Length == 4)
                {
                    if (!_allObjects.TryGetValue(currentSection, out currentMeta))
                    {
                        currentMeta = new BaseObjectMetadata
                        {
                            Rawcode = currentSection,
                            Category = GuessCategoryFromRawcode(currentSection)
                        };
                        _allObjects[currentSection] = currentMeta;
                    }
                }
                else
                {
                    currentMeta = null;
                }
                continue;
            }

            if (currentMeta == null) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var rawVal = line[(eq + 1)..].Trim();
            var cleanVal = StripOuterQuotes(rawVal);

            currentMeta.DefaultFields[key] = cleanVal;

            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                currentMeta.Name = ResolveWeString(cleanVal);
            }
            else if (key.Equals("Bufftip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(currentMeta.Name))
            {
                currentMeta.Name = ResolveWeString(cleanVal);
            }
            else if (key.Equals("EditorName", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(currentMeta.Name))
            {
                currentMeta.Name = ResolveWeString(cleanVal);
            }
            else if (key.Equals("Art", StringComparison.OrdinalIgnoreCase) || key.Equals("Buffart", StringComparison.OrdinalIgnoreCase))
            {
                currentMeta.IconPath = FirstCsvItem(cleanVal);
            }
            else if (key.Equals("Tip", StringComparison.OrdinalIgnoreCase) || key.Equals("Bufftip", StringComparison.OrdinalIgnoreCase))
            {
                currentMeta.TooltipBasic = cleanVal;
            }
            else if (key.Equals("Ubertip", StringComparison.OrdinalIgnoreCase) || key.Equals("Buffubertip", StringComparison.OrdinalIgnoreCase))
            {
                currentMeta.TooltipExtended = cleanVal;
            }
            else if (key.Equals("Hotkey", StringComparison.OrdinalIgnoreCase))
            {
                currentMeta.Hotkey = FirstCsvItem(cleanVal);
            }
            else if (key.Equals("Buttonpos", StringComparison.OrdinalIgnoreCase))
            {
                var parts = cleanVal.Split(',');
                if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out var bx)) currentMeta.ButtonPosX = bx;
                if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var by)) currentMeta.ButtonPosY = by;
            }
        }
    }

    private static W3ObjectCategory GuessCategoryFromRawcode(string rawcode)
    {
        if (string.IsNullOrEmpty(rawcode)) return W3ObjectCategory.Units;
        char c0 = rawcode[0];
        if (c0 == 'A') return W3ObjectCategory.Abilities;
        if (c0 == 'B' || c0 == 'X') return W3ObjectCategory.Buffs;
        if (c0 == 'R') return W3ObjectCategory.Upgrades;
        if (c0 == 'I') return W3ObjectCategory.Items;
        return W3ObjectCategory.Units;
    }

    private static string StripOuterQuotes(string s)
    {
        if (s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"'))
            return s[1..^1];
        return s;
    }

    private static string FirstCsvItem(string s)
    {
        var idx = s.IndexOf(',');
        return idx > 0 ? s[..idx].Trim('"') : s.Trim('"');
    }

    private static string ExtractIndexedValue(string rawValue, int index)
    {
        if (index < 0 || string.IsNullOrEmpty(rawValue))
            return rawValue;

        var parts = rawValue.Split(',');
        if (index < parts.Length)
            return parts[index].Trim().Trim('"');
        return index == 0 ? rawValue.Trim('"') : string.Empty;
    }

    private static string? ReadTextFromArchives(List<MpqArchive> archives, string virtualPath)
    {
        for (int i = archives.Count - 1; i >= 0; i--)
        {
            var arch = archives[i];
            if (arch.FileExists(virtualPath))
            {
                try
                {
                    using var stream = arch.OpenFile(virtualPath);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    return reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("WarcraftBaseDataService.ReadTextFromArchives", $"Could not read '{virtualPath}'.", ex);
                }
            }
        }
        return null;
    }

    private static string ReadAllTextLayersFromArchives(List<MpqArchive> archives, string virtualPath)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < archives.Count; i++)
        {
            var arch = archives[i];
            if (arch.FileExists(virtualPath))
            {
                try
                {
                    using var stream = arch.OpenFile(virtualPath);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    sb.AppendLine(reader.ReadToEnd());
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("WarcraftBaseDataService.ReadAllTextLayersFromArchives", $"Could not read layer '{virtualPath}'.", ex);
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Construye la lista completa de todos los campos oficiales de Warcraft III 1.27b
    /// para un objeto dado, combinando los valores base del SLK/TXT con las modificaciones del mapa.
    /// </summary>
    public List<W3ObjectField> BuildCompleteFieldListForEntry(W3ObjectEntry entry)
    {
        var result = new List<W3ObjectField>();
        var baseKey = !string.IsNullOrEmpty(entry.BaseRawcode) ? entry.BaseRawcode : entry.Rawcode;
        _allObjects.TryGetValue(baseKey, out var baseMeta);

        var relevantFields = GetRelevantFieldsForEntry(entry, baseMeta);
        var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int maxLevels = 1;
        if (entry.Category == W3ObjectCategory.Abilities)
        {
            var levField = entry.Fields.FirstOrDefault(f => f.FieldId.Equals("alev", StringComparison.OrdinalIgnoreCase));
            if (levField != null && int.TryParse(levField.ResolvedValue, out var customLv) && customLv > 0)
                maxLevels = Math.Clamp(customLv, 1, 20);
            else if (baseMeta != null && baseMeta.DefaultFields.TryGetValue("levels", out var baseLvStr) && int.TryParse(baseLvStr, out var baseLv) && baseLv > 0)
                maxLevels = Math.Clamp(baseLv, 1, 20);
        }
        else if (entry.Category == W3ObjectCategory.Upgrades)
        {
            var levField = entry.Fields.FirstOrDefault(f => f.FieldId.Equals("glvl", StringComparison.OrdinalIgnoreCase));
            if (levField != null && int.TryParse(levField.ResolvedValue, out var customLv) && customLv > 0)
                maxLevels = Math.Clamp(customLv, 1, 20);
            else if (baseMeta != null && baseMeta.DefaultFields.TryGetValue("maxlevel", out var baseLvStr) && int.TryParse(baseLvStr, out var baseLv) && baseLv > 0)
                maxLevels = Math.Clamp(baseLv, 1, 20);
        }

        foreach (var def in relevantFields)
        {
            var levelsToEmit = (def.RepeatForLevels && maxLevels > 1) ? maxLevels : 1;
            for (int lv = 1; lv <= levelsToEmit; lv++)
            {
                int fieldLevel = def.RepeatForLevels ? lv : 0;
                var existingMod = entry.Fields.FirstOrDefault(f =>
                    f.FieldId.Equals(def.FieldId, StringComparison.OrdinalIgnoreCase) &&
                    (fieldLevel == 0 || f.Level == fieldLevel || (fieldLevel == 1 && f.Level == 0)));

                if (existingMod != null)
                {
                    existingMod.FieldName = def.DisplayName;
                    existingMod.DataType = def.DataType;
                    existingMod.SlkTypeName = def.SlkTypeName;
                    existingMod.Choices = def.Choices;
                    existingMod.MinInt = def.MinInt;
                    existingMod.MaxInt = def.MaxInt;
                    existingMod.MinReal = def.MinReal;
                    existingMod.MaxReal = def.MaxReal;
                    existingMod.DecimalPlaces = def.DecimalPlaces;
                    result.Add(existingMod);
                    addedKeys.Add($"{existingMod.FieldId}:{existingMod.Level}");
                    addedKeys.Add($"{existingMod.FieldId}:0");
                }
                else
                {
                    var defVal = ResolveDefaultSlkValue(baseMeta, def, fieldLevel);
                    result.Add(new W3ObjectField
                    {
                        FieldId = def.FieldId,
                        FieldName = def.DisplayName,
                        DataType = def.DataType,
                        SlkTypeName = def.SlkTypeName,
                        Level = fieldLevel,
                        DataPointer = def.DataPointer,
                        RawValue = defVal,
                        ResolvedValue = defVal,
                        IsModified = false,
                        Choices = def.Choices,
                        MinInt = def.MinInt,
                        MaxInt = def.MaxInt,
                        MinReal = def.MinReal,
                        MaxReal = def.MaxReal,
                        DecimalPlaces = def.DecimalPlaces
                    });
                    addedKeys.Add($"{def.FieldId}:{fieldLevel}");
                }
            }
        }

        // Agregar cualquier campo personalizado adicional presente en el archivo binario del mapa
        foreach (var customField in entry.Fields)
        {
            var key = $"{customField.FieldId}:{customField.Level}";
            if (!addedKeys.Contains(key))
            {
                if (_fieldCatalog.TryGetValue(customField.FieldId, out var def))
                {
                    customField.FieldName = def.DisplayName;
                    customField.Choices = def.Choices;
                    customField.MinInt = def.MinInt;
                    customField.MaxInt = def.MaxInt;
                    customField.MinReal = def.MinReal;
                    customField.MaxReal = def.MaxReal;
                    customField.DecimalPlaces = def.DecimalPlaces;
                }
                else if (string.IsNullOrEmpty(customField.FieldName))
                {
                    customField.FieldName = GetFieldDisplayName(customField.FieldId, false);
                }
                result.Add(customField);
                addedKeys.Add(key);
            }
        }

        return result;
    }

    private string ResolveDefaultSlkValue(BaseObjectMetadata? baseMeta, FieldMetaDefinition def, int level)
    {
        if (baseMeta == null || string.IsNullOrEmpty(def.SlkColumn))
            return string.Empty;

        // Para campos por nivel en Abilities/Upgrades (ej. Cool1, Cool2, DataA1, DataA2, Tip, Ubertip)
        if (level > 0)
        {
            var levelCol = Regex.Replace(def.SlkColumn, @"\d+$", level.ToString(CultureInfo.InvariantCulture));
            if (baseMeta.DefaultFields.TryGetValue(levelCol, out var lvVal))
            {
                return ResolveWeString(ExtractIndexedValue(lvVal, def.Index));
            }
            if (baseMeta.DefaultFields.TryGetValue(def.SlkColumn, out var csvVal))
            {
                // En *AbilityStrings.txt, Tip="Nivel 1","Nivel 2","Nivel 3"
                var parts = SplitProfileMultiLevelValue(csvVal);
                if (level - 1 < parts.Count)
                    return ResolveWeString(parts[level - 1]);
            }
        }

        if (baseMeta.DefaultFields.TryGetValue(def.SlkColumn, out var rawVal))
        {
            var val = ExtractIndexedValue(rawVal, def.Index);
            if (val is "_" or "-")
                return string.Empty;
            return ResolveWeString(val);
        }

        return string.Empty;
    }

    private static List<string> SplitProfileMultiLevelValue(string raw)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(raw)) return list;

        var matches = Regex.Matches(raw, "\"([^\"]*)\"");
        if (matches.Count > 1)
        {
            foreach (Match m in matches)
                list.Add(m.Groups[1].Value);
            return list;
        }

        foreach (var part in raw.Split(','))
            list.Add(part.Trim().Trim('"'));
        return list;
    }

    private List<FieldMetaDefinition> GetRelevantFieldsForEntry(W3ObjectEntry entry, BaseObjectMetadata? baseMeta)
    {
        var cat = entry.Category;
        if (!_categoryFieldOrder.TryGetValue(cat, out var fieldIds))
            return new List<FieldMetaDefinition>();

        var baseCode = !string.IsNullOrEmpty(entry.BaseRawcode) ? entry.BaseRawcode : entry.Rawcode;
        if (baseMeta != null && baseMeta.DefaultFields.TryGetValue("code", out var aliasCode) && aliasCode.Length == 4)
        {
            // Algunas habilidades clonadas en AbilityData.slk tienen un code base en "code"
            baseCode = $"{baseCode},{aliasCode}";
        }

        bool isBuilding = false;
        bool isHero = false;

        if (cat == W3ObjectCategory.Units)
        {
            var bldgVal = entry.Fields.FirstOrDefault(f => f.FieldId.Equals("ubdg", StringComparison.OrdinalIgnoreCase))?.ResolvedValue
                          ?? (baseMeta != null && baseMeta.DefaultFields.TryGetValue("isbldg", out var b) ? b : "0");
            isBuilding = bldgVal == "1";

            var primVal = entry.Fields.FirstOrDefault(f => f.FieldId.Equals("upra", StringComparison.OrdinalIgnoreCase))?.ResolvedValue
                          ?? (baseMeta != null && baseMeta.DefaultFields.TryGetValue("Primary", out var p) ? p : string.Empty);
            isHero = (!string.IsNullOrWhiteSpace(primVal) && primVal != "_" && primVal != "-")
                     || (baseCode.Length > 0 && char.IsUpper(baseCode[0]));
        }

        var list = new List<FieldMetaDefinition>();
        foreach (var id in fieldIds)
        {
            if (!_fieldCatalog.TryGetValue(id, out var def))
                continue;

            if (cat == W3ObjectCategory.Units)
            {
                if (isHero && !def.UseHero) continue;
                if (isBuilding && !def.UseBuilding) continue;
                if (!isHero && !isBuilding && !def.UseUnit) continue;
            }
            else if (cat == W3ObjectCategory.Abilities)
            {
                if (!string.IsNullOrWhiteSpace(def.UseSpecific))
                {
                    if (!MatchesRawcodeList(def.UseSpecific, entry.Rawcode, entry.BaseRawcode, baseMeta))
                        continue;
                }
                if (!string.IsNullOrWhiteSpace(def.NotSpecific))
                {
                    if (MatchesRawcodeList(def.NotSpecific, entry.Rawcode, entry.BaseRawcode, baseMeta))
                        continue;
                }
            }

            list.Add(def);
        }

        // Si una habilidad tiene campos específicos (UseSpecific) para DataA/DataB/..., ocultamos los DataA/DataB genéricos duplicados
        if (cat == W3ObjectCategory.Abilities)
        {
            var specificDataPointers = list
                .Where(d => !string.IsNullOrWhiteSpace(d.UseSpecific) && d.DataPointer > 0)
                .Select(d => d.DataPointer)
                .ToHashSet();

            if (specificDataPointers.Count > 0)
            {
                list.RemoveAll(d => string.IsNullOrWhiteSpace(d.UseSpecific) && d.DataPointer > 0 && specificDataPointers.Contains(d.DataPointer));
            }
        }

        return list;
    }

    private static bool MatchesRawcodeList(string csvList, string rawcode, string baseRawcode, BaseObjectMetadata? baseMeta)
    {
        var codes = csvList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var c in codes)
        {
            if (c.Equals(rawcode, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(baseRawcode) && c.Equals(baseRawcode, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (baseMeta != null && baseMeta.DefaultFields.TryGetValue("code", out var slkCode) &&
                c.Equals(slkCode, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
