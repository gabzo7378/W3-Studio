using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using W3Studio.Models;

namespace W3Studio.Services;

public enum IconStyleFilter
{
    NoDis = 0,
    Btn = 1,
    Pas = 2,
    Atc = 3,
    Dis = 4,
    Ui = 5,
    Imported = 6,
    All = 7
}

public enum IconRaceFilter
{
    All = 0,
    Human = 1,
    Orc = 2,
    Undead = 3,
    NightElf = 4,
    Neutral = 5,
    Other = 6
}

public enum IconUsageFilter
{
    All = 0,
    Heroes = 1,
    Units = 2,
    Buildings = 3,
    Abilities = 4,
    Items = 5,
    Upgrades = 6,
    Other = 7
}

public sealed class IconCatalogEntry
{
    public string MpqPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string BaseStem { get; init; } = string.Empty;
    public bool IsImported { get; init; }
    public bool IsDisabled { get; init; }
    public bool IsPassive { get; init; }
    public bool IsAutocast { get; init; }
    public bool IsActiveBtn { get; init; }
    public bool IsUiIcon { get; init; }
    public HashSet<IconRaceFilter> Races { get; init; } = new();
    public HashSet<IconUsageFilter> Usages { get; init; } = new();
    public string SearchableMetadata { get; init; } = string.Empty;

    public string StyleBadge => IsImported
        ? "Mapa"
        : IsDisabled
            ? "DIS"
            : IsPassive
                ? "PAS"
                : IsAutocast
                    ? "ATC"
                    : IsUiIcon
                        ? "UI"
                        : "BTN";
}

/// <summary>
/// Pure classifier and multi-axis filter for Warcraft III icons (Style/Prefix, Race, Object Category, and multi-token search).
/// Cross-references WarcraftBaseDataService objects so icons inherit the exact race, usage, and object names of WC3 entities.
/// </summary>
public static class IconCatalogFilterHelper
{
    private sealed class StemMetadata
    {
        public HashSet<IconRaceFilter> Races { get; } = new();
        public HashSet<IconUsageFilter> Usages { get; } = new();
        public HashSet<string> ObjectNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] HumanKeywords =
    {
        "human", "footman", "knight", "paladin", "archmage", "mountainking", "bloodmage",
        "priest", "sorceress", "rifleman", "mortar", "gryphon", "peasant", "militia",
        "spellbreaker", "dragonhawk", "steamtank", "siegeengine", "flyingmachine",
        "gyrocopter", "dwarf", "dwarven", "townhall", "keep", "castle", "arcanetower",
        "guardtower", "cannontower", "farm", "blacksmith", "lumbermill", "holy", "devotion",
        "divineshield", "blizzard", "waterlemental", "waterelemental", "stormbolt", "thunderclap",
        "avatar", "flamestrike", "banish", "siphonmana", "phoenix", "innerfire", "heal",
        "dispel", "polymorph", "invisibility", "slow", "defend", "militia", "masonry", "calltoarms"
    };

    private static readonly string[] OrcKeywords =
    {
        "orc", "grunt", "raider", "tauren", "troll", "headhunter", "berserker", "shaman",
        "witchdoctor", "wyvern", "windrider", "kodo", "kodobeast", "peon", "demolisher",
        "catapult", "batrider", "spiritwalker", "blademaster", "farseer", "shadowhunter",
        "chieftain", "thrall", "grom", "cairne", "voljin", "rexxar", "greathall", "stronghold",
        "fortress", "burrow", "watchtower", "warmill", "spiritlodge", "beastiary", "bestiary",
        "bloodlust", "bloodlust", "chainlightning", "farsee", "feralspirit", "earthquake",
        "windwalk", "mirrorimage", "criticalstrike", "bladestorm", "shockwave", "warstomp",
        "enduranceaura", "reincarnation", "healingwave", "hex", "serpentward", "voodoo",
        "pillage", "ensnare", "liquidfire", "spikedbarricades", "reinforcedhides", "berserk"
    };

    private static readonly string[] UndeadKeywords =
    {
        "undead", "ghoul", "abomination", "necromancer", "banshee", "lich", "cryptfiend",
        "cryptlord", "crypt", "acolyte", "gargoyle", "obsidian", "destroyer", "frostwyrm",
        "meatwagon", "shade", "skeleton", "zombie", "deathknight", "dreadlord", "arthas",
        "kelthuzad", "anubarak", "malganis", "tichondrius", "sylvanas", "ziggurat",
        "necropolis", "hallsofthedead", "blackcitadel", "slaughterhouse", "boneyard",
        "sacrificialpit", "tomb", "haunted", "blight", "deathcoil", "deathpact", "unholyaura",
        "animatedead", "carrionswarm", "sleep", "vampiricaura", "inferno", "frostnova",
        "frostarmor", "darkritual", "deathanddecay", "impale", "spikedcarapace", "carrionbeetles",
        "locustswarm", "cannibalize", "web", "burrow", "raisedead", "unholyfrenzy", "cripple",
        "curse", "antimagicshell", "possession", "plague", "diseasecloud", "exhumecorpse"
    };

    private static readonly string[] NightElfKeywords =
    {
        "nightelf", "elf", "archer", "huntress", "dryad", "druid", "wisp", "chimaera",
        "hippogryph", "mountaingiant", "faeriedragon", "glaivethrower", "ballista", "treant",
        "ancient", "treeoflife", "treeofages", "treeofeternity", "moonwell", "huntershall",
        "demonhunter", "keeper", "grove", "priestess", "warden", "illidan", "malfurion",
        "furion", "tyrande", "maiev", "cenarius", "manaburn", "immolation", "evasion",
        "metamorphosis", "entanglingroots", "forceofnature", "thornsaura", "tranquility",
        "scout", "searingarrows", "trueshot", "starfall", "fanofknives", "blink",
        "shadowstrike", "vengeance", "shadowmeld", "sentinel", "ultravision", "wellspring",
        "natureblessing", "moonarmor", "moonglaive", "vorpal", "roar", "rejuvenation",
        "bearform", "crowform", "cyclone", "faeriefire", "abolishmagic", "corrosivebreath"
    };

    private static readonly string[] NeutralKeywords =
    {
        "naga", "demon", "doomguard", "felhound", "infernal", "pitlord", "succubus",
        "eredar", "dragon", "drake", "whelp", "golem", "ogre", "gnoll", "murloc", "mur'gul",
        "murgul", "kobold", "bandit", "brigand", "assassin", "mercenary", "tavern", "goblin",
        "zeppelin", "sapper", "shredder", "tinker", "alchemist", "pandaren", "brewmaster",
        "beastmaster", "darkranger", "firelord", "seawitch", "neutral", "creep", "centaur",
        "harpy", "quillboar", "razormane", "satyr", "furbolg", "wendigo", "magnataur",
        "tuskar", "tuskarr", "nerubian", "faceless", "hydralisk", "hydra", "turtle", "makrura",
        "spider", "wolf", "bear", "owl", "boar", "crab", "arachnathid", "sludge", "sasquatch",
        "draenei", "broken", "skink", "frog", "sheep", "pig", "chicken", "stag", "vulture",
        "goldmine", "fountain", "waygate", "marketplace", "shipyard"
    };

    private static readonly string[] ItemKeywords =
    {
        "potion", "scroll", "ring", "claws", "gauntlet", "slippers", "boots", "mantle",
        "tome", "manual", "amulet", "necklace", "pendant", "orb", "wand", "staff", "rod",
        "cheese", "gem", "crystal", "crown", "helm", "hood", "mask", "sobi", "robe", "cloak",
        "periapt", "rune", "ankh", "circlet", "bracer", "belt", "medallion", "talisman",
        "gloves", "scepter", "blade", "sword", "axe", "hammer", "shield", "armor", "dust",
        "ward", "flare", "horn", "drum", "pipe", "khagad", "khadgar", "alleria", "medivh",
        "guldan", "frostmourne", "chest", "box", "crate", "key", "coin", "gold", "lumber",
        "bundle", "stone", "heart", "soul", "vial", "flask", "elixir", "bread", "meat"
    };

    private static readonly string[] UpgradeKeywords =
    {
        "upgrade", "steelmelee", "steelranged", "steelarmor", "thorium", "arcanite",
        "leatherupgrade", "ironforged", "moonarmor", "strengthofthemoon", "strengthofthewild",
        "reinforcedhides", "unholystrength", "unholyarmor", "creatureattack", "creaturecarapace",
        "masonry", "spikedbarricades", "improvedbows", "ultravision", "wellspring", "adept",
        "master", "training", "backpack"
    };

    private static readonly string[] BuildingKeywords =
    {
        "townhall", "keep", "castle", "barracks", "farm", "altar", "blacksmith", "lumbermill",
        "workshop", "sanctum", "aviary", "tower", "greathall", "stronghold", "fortress",
        "burrow", "warmill", "lodge", "beastiary", "bestiary", "totem", "necropolis",
        "hallsofthedead", "blackcitadel", "ziggurat", "crypt", "graveyard", "slaughterhouse",
        "temple", "boneyard", "sacrificialpit", "tomb", "treeoflife", "treeofages",
        "treeofeternity", "moonwell", "ancient", "huntershall", "chimaeraroost", "tavern",
        "marketplace", "shipyard", "goldmine", "fountain", "waygate", "vault", "shop"
    };

    public static string NormalizeStem(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return string.Empty;

        var file = Path.GetFileNameWithoutExtension(iconPath.Trim().Replace('/', '\\'));
        if (string.IsNullOrEmpty(file))
            return string.Empty;

        // Strip stacked WC3 icon prefixes: DISPASBTN, DISBTN, DISATC, PASBTN, BTN, PAS, ATC, DIS
        string[] prefixes = { "DISPASBTN", "DISATCBTN", "DISBTN", "DISPAS", "DISATC", "PASBTN", "ATCBTN", "BTN", "PAS", "ATC", "DIS" };
        foreach (var p in prefixes)
        {
            if (file.Length > p.Length && file.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                file = file[p.Length..];
                break;
            }
        }
        return file.Trim();
    }

    public static List<IconCatalogEntry> BuildCatalog(
        IEnumerable<string> paths,
        ISet<string>? importedPaths = null,
        WarcraftBaseDataService? baseData = null)
    {
        var metaByStem = BuildStemMetadataIndex(baseData);
        var entries = new List<IconCatalogEntry>();

        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var normalizedPath = rawPath.Trim().Replace('/', '\\');
            var fileName = Path.GetFileName(normalizedPath);
            var stem = NormalizeStem(normalizedPath);
            bool isImported = importedPaths != null && importedPaths.Contains(rawPath);

            bool isDis = fileName.StartsWith("DIS", StringComparison.OrdinalIgnoreCase) ||
                         normalizedPath.Contains(@"\CommandButtonsDisabled\", StringComparison.OrdinalIgnoreCase);
            bool isPas = !isDis && (fileName.StartsWith("PAS", StringComparison.OrdinalIgnoreCase) ||
                                    normalizedPath.Contains(@"\PassiveButtons\", StringComparison.OrdinalIgnoreCase));
            bool isAtc = !isDis && (fileName.StartsWith("ATC", StringComparison.OrdinalIgnoreCase) ||
                                    fileName.Contains("AutoCast", StringComparison.OrdinalIgnoreCase) ||
                                    fileName.EndsWith("On.blp", StringComparison.OrdinalIgnoreCase) ||
                                    fileName.EndsWith("Off.blp", StringComparison.OrdinalIgnoreCase));
            bool isUi = normalizedPath.StartsWith(@"UI\", StringComparison.OrdinalIgnoreCase) ||
                        normalizedPath.Contains(@"\WorldEditArt\", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("infocard-", StringComparison.OrdinalIgnoreCase);
            bool isBtn = !isDis && !isPas && !isUi &&
                         (fileName.StartsWith("BTN", StringComparison.OrdinalIgnoreCase) ||
                          fileName.StartsWith("ATC", StringComparison.OrdinalIgnoreCase));

            var races = new HashSet<IconRaceFilter>();
            var usages = new HashSet<IconUsageFilter>();
            string metaNames = string.Empty;

            if (!string.IsNullOrEmpty(stem) && metaByStem.TryGetValue(stem, out var stemMeta))
            {
                foreach (var r in stemMeta.Races) races.Add(r);
                foreach (var u in stemMeta.Usages) usages.Add(u);
                if (stemMeta.ObjectNames.Count > 0)
                    metaNames = string.Join(" ", stemMeta.ObjectNames);
            }

            if (races.Count == 0)
                InferRaceFromKeywords(stem, normalizedPath, races);

            if (usages.Count == 0)
                InferUsageFromKeywords(stem, normalizedPath, isUi, usages);

            if (races.Count == 0)
                races.Add(IconRaceFilter.Other);
            if (usages.Count == 0)
                usages.Add(IconUsageFilter.Other);

            entries.Add(new IconCatalogEntry
            {
                MpqPath = rawPath,
                FileName = fileName,
                BaseStem = stem,
                IsImported = isImported,
                IsDisabled = isDis,
                IsPassive = isPas,
                IsAutocast = isAtc,
                IsActiveBtn = isBtn,
                IsUiIcon = isUi,
                Races = races,
                Usages = usages,
                SearchableMetadata = metaNames
            });
        }

        return entries;
    }

    public static List<IconCatalogEntry> Filter(
        IEnumerable<IconCatalogEntry> entries,
        IconStyleFilter style,
        IconRaceFilter race,
        IconUsageFilter usage,
        string? searchText)
    {
        IEnumerable<IconCatalogEntry> query = entries;

        query = style switch
        {
            IconStyleFilter.NoDis => query.Where(e => !e.IsDisabled),
            IconStyleFilter.Btn => query.Where(e => e.IsActiveBtn),
            IconStyleFilter.Pas => query.Where(e => e.IsPassive),
            IconStyleFilter.Atc => query.Where(e => e.IsAutocast),
            IconStyleFilter.Dis => query.Where(e => e.IsDisabled),
            IconStyleFilter.Ui => query.Where(e => e.IsUiIcon),
            IconStyleFilter.Imported => query.Where(e => e.IsImported),
            _ => query
        };

        if (race != IconRaceFilter.All)
            query = query.Where(e => e.Races.Contains(race));

        if (usage != IconUsageFilter.All)
            query = query.Where(e => e.Usages.Contains(usage));

        var trimmed = (searchText ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(e => tokens.All(tok =>
                e.FileName.Contains(tok, StringComparison.OrdinalIgnoreCase) ||
                e.MpqPath.Contains(tok, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(e.SearchableMetadata) &&
                 e.SearchableMetadata.Contains(tok, StringComparison.OrdinalIgnoreCase))));
        }

        return query.ToList();
    }

    private static Dictionary<string, StemMetadata> BuildStemMetadataIndex(WarcraftBaseDataService? baseData)
    {
        var index = new Dictionary<string, StemMetadata>(StringComparer.OrdinalIgnoreCase);
        if (baseData == null || baseData.AllBaseObjects.Count == 0)
            return index;

        foreach (var meta in baseData.AllBaseObjects.Values)
        {
            var iconPath = meta.IconPath;
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                if (meta.DefaultFields.TryGetValue("Art", out var art) && !string.IsNullOrWhiteSpace(art))
                    iconPath = art;
                else if (meta.DefaultFields.TryGetValue("Researchart", out var rArt) && !string.IsNullOrWhiteSpace(rArt))
                    iconPath = rArt;
            }
            if (string.IsNullOrWhiteSpace(iconPath))
                continue;

            // Some multi-level upgrades list comma-separated Art paths
            var iconParts = iconPath.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var race = MapRace(meta.Race);
            var usage = MapUsage(meta);

            foreach (var part in iconParts)
            {
                var stem = NormalizeStem(part);
                if (string.IsNullOrEmpty(stem))
                    continue;

                if (!index.TryGetValue(stem, out var entry))
                {
                    entry = new StemMetadata();
                    index[stem] = entry;
                }

                if (race != IconRaceFilter.Other)
                    entry.Races.Add(race);
                entry.Usages.Add(usage);

                if (!string.IsNullOrWhiteSpace(meta.Name))
                    entry.ObjectNames.Add(meta.Name);
            }
        }

        return index;
    }

    private static IconRaceFilter MapRace(string? rawRace)
    {
        var r = (rawRace ?? string.Empty).Trim().ToLowerInvariant();
        if (r.Contains("human")) return IconRaceFilter.Human;
        if (r.Contains("orc")) return IconRaceFilter.Orc;
        if (r.Contains("undead")) return IconRaceFilter.Undead;
        if (r.Contains("nightelf") || r.Contains("night elf")) return IconRaceFilter.NightElf;
        if (r.Contains("creep") || r.Contains("neutral") || r.Contains("naga") || r.Contains("demon") || r.Contains("critter"))
            return IconRaceFilter.Neutral;
        return IconRaceFilter.Other;
    }

    private static IconUsageFilter MapUsage(BaseObjectMetadata meta)
    {
        return meta.Category switch
        {
            W3ObjectCategory.Items => IconUsageFilter.Items,
            W3ObjectCategory.Abilities or W3ObjectCategory.Buffs => IconUsageFilter.Abilities,
            W3ObjectCategory.Upgrades => IconUsageFilter.Upgrades,
            W3ObjectCategory.Units => ClassifyUnitUsage(meta),
            _ => IconUsageFilter.Other
        };
    }

    private static IconUsageFilter ClassifyUnitUsage(BaseObjectMetadata meta)
    {
        if (UnitPaletteFilterHelper.IsHeroUnit(meta.Category, meta.Rawcode, meta.Rawcode))
            return IconUsageFilter.Heroes;

        if (meta.DefaultFields.TryGetValue("isbldg", out var isBldg) && isBldg == "1")
            return IconUsageFilter.Buildings;

        if (meta.SubcategoryFolder.Contains("Edificio", StringComparison.OrdinalIgnoreCase) ||
            meta.SubcategoryFolder.Contains("Building", StringComparison.OrdinalIgnoreCase))
            return IconUsageFilter.Buildings;

        return IconUsageFilter.Units;
    }

    private static void InferRaceFromKeywords(string stem, string fullPath, HashSet<IconRaceFilter> races)
    {
        if (fullPath.Contains(@"\Human\", StringComparison.OrdinalIgnoreCase) || MatchesAny(stem, HumanKeywords))
            races.Add(IconRaceFilter.Human);
        if (fullPath.Contains(@"\Orc\", StringComparison.OrdinalIgnoreCase) || MatchesAny(stem, OrcKeywords))
            races.Add(IconRaceFilter.Orc);
        if (fullPath.Contains(@"\Undead\", StringComparison.OrdinalIgnoreCase) || MatchesAny(stem, UndeadKeywords))
            races.Add(IconRaceFilter.Undead);
        if (fullPath.Contains(@"\NightElf\", StringComparison.OrdinalIgnoreCase) || MatchesAny(stem, NightElfKeywords))
            races.Add(IconRaceFilter.NightElf);
        if (MatchesAny(stem, NeutralKeywords))
            races.Add(IconRaceFilter.Neutral);
    }

    private static void InferUsageFromKeywords(string stem, string fullPath, bool isUi, HashSet<IconUsageFilter> usages)
    {
        if (isUi)
        {
            usages.Add(IconUsageFilter.Other);
            return;
        }
        if (stem.Contains("Hero", StringComparison.OrdinalIgnoreCase))
            usages.Add(IconUsageFilter.Heroes);
        if (MatchesAny(stem, BuildingKeywords))
            usages.Add(IconUsageFilter.Buildings);
        if (MatchesAny(stem, UpgradeKeywords))
            usages.Add(IconUsageFilter.Upgrades);
        if (MatchesAny(stem, ItemKeywords))
            usages.Add(IconUsageFilter.Items);
    }

    private static bool MatchesAny(string text, string[] keywords)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

