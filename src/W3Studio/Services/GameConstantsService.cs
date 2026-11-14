using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using W3Studio.Models;

namespace W3Studio.Services;

public sealed class IniKeyEntry
{
    public string Section { get; set; } = "Misc";
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string DefaultValue { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public bool IsModified => !string.Equals(CurrentValue, DefaultValue, StringComparison.Ordinal);

    public bool LooksLikeIcon =>
        CurrentValue.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ||
        DefaultValue.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ||
        Key.Contains("Icon", StringComparison.OrdinalIgnoreCase) ||
        Key.Contains("Command", StringComparison.OrdinalIgnoreCase);

    public bool LooksLikeModel =>
        CurrentValue.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
        CurrentValue.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
        DefaultValue.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
        DefaultValue.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
}

public static class GameConstantsService
{
    private static readonly Dictionary<string, (string Label, string Category)> KnownMiscMeta = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MaxHeroLevel"] = ("Héroes - Nivel Máximo (MaxHeroLevel)", "Héroes"),
        ["HeroFactorXP"] = ("Héroes - Tabla XP por Nivel (Factor)", "Héroes"),
        ["HeroExpRange"] = ("Héroes - Rango de Obtención de XP", "Héroes"),
        ["GrantHeroXP"] = ("Héroes - XP otorgada al morir", "Héroes"),
        ["GrantNormalXP"] = ("Héroes - XP base por unidad enemiga", "Héroes"),
        ["HeroMaxReviveCostGold"] = ("Héroes - Coste Máx. Revivir (Oro)", "Héroes"),
        ["HeroMaxReviveCostLumber"] = ("Héroes - Coste Máx. Revivir (Madera)", "Héroes"),
        ["HeroAbilityLevelSkip"] = ("Héroes - Salto de nivel para habilidades", "Héroes"),
        ["StrAttackBonus"] = ("Atributos - Bonus Daño por Fuerza (Héroe Primario)", "Héroes"),
        ["StrHitPointBonus"] = ("Atributos - Bonus Vida (HP) por punto de Fuerza", "Héroes"),
        ["StrRegenBonus"] = ("Atributos - Regeneración HP por Fuerza", "Héroes"),
        ["AgiDefenseBonus"] = ("Atributos - Armadura por punto de Agilidad", "Héroes"),
        ["AgiDefenseBase"] = ("Atributos - Armadura Base antes de Agilidad", "Héroes"),
        ["AgiMoveBonus"] = ("Atributos - Velocidad Mov. por Agilidad", "Héroes"),
        ["AgiAttackSpeedBonus"] = ("Atributos - Velocidad Ataque por Agilidad", "Héroes"),
        ["IntManaBonus"] = ("Atributos - Bonus Maná por punto de Inteligencia", "Héroes"),
        ["IntRegenBonus"] = ("Atributos - Regeneración Maná por Inteligencia", "Héroes"),
        ["DefenseArmor"] = ("Combate - Multiplicador Reducción por Armadura (def = 0.06)", "Combate"),
        ["DamageBonusNormal"] = ("Combate - Tabla Daño Normal vs Armaduras (7 valores)", "Combate"),
        ["DamageBonusPierce"] = ("Combate - Tabla Daño Perforación (Pierce) vs Armaduras", "Combate"),
        ["DamageBonusSiege"] = ("Combate - Tabla Daño Asedio (Siege) vs Armaduras", "Combate"),
        ["DamageBonusMagic"] = ("Combate - Tabla Daño Mágico vs Armaduras", "Combate"),
        ["DamageBonusHero"] = ("Combate - Tabla Daño Héroe vs Armaduras", "Combate"),
        ["DamageBonusSpells"] = ("Combate - Tabla Daño Hechizos vs Armaduras", "Combate"),
        ["DamageBonusChaos"] = ("Combate - Tabla Daño Caos vs Armaduras", "Combate"),
        ["EtherealDamageBonus"] = ("Combate - Multiplicador Daño Etéreo", "Combate"),
        ["UpkeepGoldTax"] = ("Economía - Impuesto Oro por Upkeep", "Economía y Creeps"),
        ["UpkeepLumberTax"] = ("Economía - Impuesto Madera por Upkeep", "Economía y Creeps"),
        ["UpkeepUsage"] = ("Economía - Límites de Comida Upkeep", "Economía y Creeps"),
        ["TradingIncSmall"] = ("Economía - Incremento Comercio Normal", "Economía y Creeps"),
        ["TradingIncLarge"] = ("Economía - Incremento Comercio Ctrl+Clic", "Economía y Creeps"),
        ["FollowRange"] = ("Unidades - Distancia de Seguimiento", "General"),
        ["GuardDistance"] = ("Creeps - Distancia Máxima de Guardia", "Economía y Creeps"),
        ["GuardReturnTime"] = ("Creeps - Tiempo antes de regresar al campamento", "Economía y Creeps"),
        ["MoveSpeedMin"] = ("Unidades - Velocidad Movimiento Mínima", "General"),
        ["MoveSpeedMax"] = ("Unidades - Velocidad Movimiento Máxima", "General"),
        ["AttackSpeedMin"] = ("Unidades - Velocidad Ataque Mínima", "General"),
        ["AttackSpeedMax"] = ("Unidades - Velocidad Ataque Máxima", "General"),
        ["MaxUnitSelection"] = ("Interfaz - Máximo de unidades seleccionables", "General"),
        ["ConstructionRefundRate"] = ("Edificios - % Reembolso al cancelar", "Economía y Creeps"),
        ["UpgradeRefundRate"] = ("Edificios - % Reembolso mejora cancelada", "Economía y Creeps")
    };

    private static readonly string[] MiscBasePaths =
    {
        @"Units\MiscData.txt",
        @"Units\MiscGame.txt",
        @"Units\MiscUI.txt"
    };

    private static readonly string[] SkinBasePaths =
    {
        @"UI\war3skins.txt",
        @"Units\CommandStrings.txt",
        @"UI\MiscData.txt"
    };

    public static List<IniKeyEntry> LoadGameplayConstants(MapProject project, BlpIconService blpService)
    {
        var defaults = new Dictionary<(string Section, string Key), string>(SectionKeyComparer.Instance);
        var order = new List<(string Section, string Key)>();

        foreach (var path in MiscBasePaths)
        {
            var bytes = blpService.FindRawFileBytes(path, project);
            if (bytes != null && bytes.Length > 0)
            {
                MergeIniInto(Encoding.UTF8.GetString(bytes), defaults, order, overwrite: true);
            }
        }

        // Fallback essentials if user hasn't configured WC3 MPQ path yet
        EnsureFallbackMiscDefaults(defaults, order);

        var overrides = new Dictionary<(string Section, string Key), string>(SectionKeyComparer.Instance);
        if (project.RawMpqFiles.TryGetValue("war3mapMisc.txt", out var mapMiscBytes) && mapMiscBytes.Length > 0)
        {
            MergeIniInto(Encoding.UTF8.GetString(mapMiscBytes), overrides, order, overwrite: true);
        }

        var result = new List<IniKeyEntry>(order.Count);
        foreach (var (sec, key) in order)
        {
            defaults.TryGetValue((sec, key), out var defVal);
            var curVal = overrides.TryGetValue((sec, key), out var ov) ? ov : (defVal ?? string.Empty);

            string display = key;
            string category = sec.Equals("Misc", StringComparison.OrdinalIgnoreCase) ? CategorizeMiscKey(key) : sec;
            if (KnownMiscMeta.TryGetValue(key, out var meta))
            {
                display = meta.Label;
                category = meta.Category;
            }

            result.Add(new IniKeyEntry
            {
                Section = sec,
                Key = key,
                DisplayName = display,
                Category = category,
                DefaultValue = defVal ?? string.Empty,
                CurrentValue = curVal
            });
        }

        return result;
    }

    public static void SaveGameplayConstants(MapProject project, IEnumerable<IniKeyEntry> entries)
    {
        var modified = entries.Where(e => e.IsModified).ToList();
        if (modified.Count == 0)
        {
            project.RawMpqFiles.Remove("war3mapMisc.txt");
            project.HasMiscEdits = true;
            return;
        }

        var text = SerializeIni(modified);
        project.RawMpqFiles["war3mapMisc.txt"] = Encoding.UTF8.GetBytes(text);
        project.HasMiscEdits = true;
    }

    public static List<IniKeyEntry> LoadGameInterface(MapProject project, BlpIconService blpService)
    {
        var defaults = new Dictionary<(string Section, string Key), string>(SectionKeyComparer.Instance);
        var order = new List<(string Section, string Key)>();

        foreach (var path in SkinBasePaths)
        {
            var bytes = blpService.FindRawFileBytes(path, project);
            if (bytes != null && bytes.Length > 0)
            {
                MergeIniInto(Encoding.UTF8.GetString(bytes), defaults, order, overwrite: true);
            }
        }

        EnsureFallbackSkinDefaults(defaults, order);

        var overrides = new Dictionary<(string Section, string Key), string>(SectionKeyComparer.Instance);
        if (project.RawMpqFiles.TryGetValue("war3mapSkin.txt", out var mapSkinBytes) && mapSkinBytes.Length > 0)
        {
            MergeIniInto(Encoding.UTF8.GetString(mapSkinBytes), overrides, order, overwrite: true);
        }

        var result = new List<IniKeyEntry>(order.Count);
        foreach (var (sec, key) in order)
        {
            defaults.TryGetValue((sec, key), out var defVal);
            var curVal = overrides.TryGetValue((sec, key), out var ov) ? ov : (defVal ?? string.Empty);

            result.Add(new IniKeyEntry
            {
                Section = sec,
                Key = key,
                DisplayName = $"{key} [{sec}]",
                Category = CategorizeSkinEntry(sec, key, curVal, defVal ?? string.Empty),
                DefaultValue = defVal ?? string.Empty,
                CurrentValue = curVal
            });
        }

        return result;
    }

    public static void SaveGameInterface(MapProject project, IEnumerable<IniKeyEntry> entries)
    {
        var modified = entries.Where(e => e.IsModified).ToList();
        if (modified.Count == 0)
        {
            project.RawMpqFiles.Remove("war3mapSkin.txt");
            project.HasSkinEdits = true;
            return;
        }

        var text = SerializeIni(modified);
        project.RawMpqFiles["war3mapSkin.txt"] = Encoding.UTF8.GetBytes(text);
        project.HasSkinEdits = true;
    }

    private static string CategorizeMiscKey(string key)
    {
        if (key.Contains("Hero", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Str", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Agi", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Int", StringComparison.OrdinalIgnoreCase))
            return "Héroes";
        if (key.Contains("Damage", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Armor", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Defense", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Ethereal", StringComparison.OrdinalIgnoreCase))
            return "Combate";
        if (key.Contains("Upkeep", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Creep", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Guard", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Gold", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Lumber", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Refund", StringComparison.OrdinalIgnoreCase))
            return "Economía y Creeps";
        return "General";
    }

    private static string CategorizeSkinEntry(string section, string key, string val, string def)
    {
        var sample = !string.IsNullOrEmpty(val) ? val : def;
        if (sample.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) || key.Contains("Icon", StringComparison.OrdinalIgnoreCase))
            return "Iconos";
        if (sample.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || sample.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return "Modelos";
        if (sample.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || key.Contains("Font", StringComparison.OrdinalIgnoreCase))
            return "Fuentes";
        if (section.Equals("Errors", StringComparison.OrdinalIgnoreCase))
            return "Mensajes de Error";
        if (section.Equals("FrameDef", StringComparison.OrdinalIgnoreCase))
            return "Textos UI (FrameDef)";
        return section;
    }

    private static void MergeIniInto(
        string text,
        Dictionary<(string Section, string Key), string> target,
        List<(string Section, string Key)> order,
        bool overwrite)
    {
        string currentSection = "Misc";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith(";") || line.StartsWith("#"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]") && line.Length >= 3)
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();
            var sk = (currentSection, key);
            if (!target.ContainsKey(sk))
            {
                order.Add(sk);
                target[sk] = val;
            }
            else if (overwrite)
            {
                target[sk] = val;
            }
        }
    }

    private static string SerializeIni(IEnumerable<IniKeyEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var group in entries.GroupBy(e => e.Section, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (var item in group)
            {
                sb.AppendLine($"{item.Key}={item.CurrentValue}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void EnsureFallbackMiscDefaults(
        Dictionary<(string Section, string Key), string> target,
        List<(string Section, string Key)> order)
    {
        var fallback = new (string K, string V)[]
        {
            ("MaxHeroLevel", "10"),
            ("StrHitPointBonus", "25"),
            ("StrRegenBonus", "0.05"),
            ("AgiDefenseBonus", "0.30"),
            ("AgiMoveBonus", "0"),
            ("AgiAttackSpeedBonus", "0.02"),
            ("IntManaBonus", "15"),
            ("IntRegenBonus", "0.05"),
            ("DefenseArmor", "0.06"),
            ("DamageBonusNormal", "1.00,1.50,1.00,0.70,1.00,1.00,0.05,1.00"),
            ("DamageBonusPierce", "1.50,0.75,1.00,0.35,1.00,0.50,0.05,1.50"),
            ("DamageBonusSiege", "1.50,0.50,1.00,1.50,1.00,0.50,0.05,1.50"),
            ("DamageBonusMagic", "1.00,0.75,2.00,0.35,1.00,0.50,0.05,1.00"),
            ("DamageBonusHero", "1.00,1.00,1.00,0.50,1.00,1.00,0.05,1.00"),
            ("DamageBonusChaos", "1.00,1.00,1.00,1.00,1.00,1.00,1.00,1.00"),
            ("MoveSpeedMin", "150"),
            ("MoveSpeedMax", "522")
        };

        foreach (var (k, v) in fallback)
        {
            var sk = ("Misc", k);
            if (!target.ContainsKey(sk))
            {
                order.Add(sk);
                target[sk] = v;
            }
        }
    }

    private static void EnsureFallbackSkinDefaults(
        Dictionary<(string Section, string Key), string> target,
        List<(string Section, string Key)> order)
    {
        var fallback = new (string S, string K, string V)[]
        {
            ("FrameDef", "GOLD", "Gold"),
            ("FrameDef", "LUMBER", "Lumber"),
            ("FrameDef", "UPKEEP_NONE", "No Upkeep"),
            ("FrameDef", "UPKEEP_LOW", "Low Upkeep"),
            ("FrameDef", "UPKEEP_HIGH", "High Upkeep"),
            ("FrameDef", "QUESTS", "Quests"),
            ("Default", "GoldIcon", @"UI\Feedback\Resources\ResourceGold.blp"),
            ("Default", "LumberIcon", @"UI\Feedback\Resources\ResourceLumber.blp"),
            ("Default", "SupplyIcon", @"UI\Feedback\Resources\ResourceSupply.blp"),
            ("Default", "CommandAttack", @"ReplaceableTextures\CommandButtons\BTNAttack.blp"),
            ("Default", "CommandMove", @"ReplaceableTextures\CommandButtons\BTNMove.blp"),
            ("Default", "CommandStop", @"ReplaceableTextures\CommandButtons\BTNStop.blp"),
            ("Default", "CommandHoldPosition", @"ReplaceableTextures\CommandButtons\BTNHoldPosition.blp"),
            ("Default", "CommandPatrol", @"ReplaceableTextures\CommandButtons\BTNPatrol.blp")
        };

        foreach (var (s, k, v) in fallback)
        {
            var sk = (s, k);
            if (!target.ContainsKey(sk))
            {
                order.Add(sk);
                target[sk] = v;
            }
        }
    }

    private sealed class SectionKeyComparer : IEqualityComparer<(string Section, string Key)>
    {
        public static readonly SectionKeyComparer Instance = new();
        public bool Equals((string Section, string Key) x, (string Section, string Key) y) =>
            string.Equals(x.Section, y.Section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Section, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Section),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }
}

