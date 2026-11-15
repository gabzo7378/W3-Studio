using System;
using System.Linq;
using W3Studio.Models;

namespace W3Studio.Services;

/// <summary>
/// Race and object-type classification helpers for the World Editor-style unit placement palette.
/// </summary>
public static class UnitPaletteFilterHelper
{
    public static string ResolveUnitRace(W3ObjectEntry? customObj, BaseObjectMetadata? baseMeta, bool isItem)
    {
        if (isItem)
            return "other";

        string? rawRace = null;
        if (customObj != null)
        {
            var raceField = customObj.Fields.FirstOrDefault(f => f.FieldId.Equals("urac", StringComparison.OrdinalIgnoreCase));
            if (raceField != null)
            {
                var candidate = !string.IsNullOrWhiteSpace(raceField.RawValue) ? raceField.RawValue : raceField.ResolvedValue;
                if (!string.IsNullOrWhiteSpace(candidate) && candidate != "_" && candidate != "-")
                    rawRace = candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(rawRace))
            rawRace = baseMeta?.Race;

        return string.IsNullOrWhiteSpace(rawRace) ? "other" : rawRace.Trim().ToLowerInvariant();
    }

    public static bool IsHeroUnit(W3ObjectCategory category, string? rawcode, string? baseRawcode)
    {
        if (category != W3ObjectCategory.Units)
            return false;

        string? effective = !string.IsNullOrWhiteSpace(baseRawcode) ? baseRawcode.Trim() : rawcode?.Trim();
        return !string.IsNullOrEmpty(effective) && char.IsUpper(effective[0]);
    }

    public static bool MatchesRaceFilter(string? raceFilter, string? unitRace, bool isCustom, bool isItem, string? typeFilter = null)
    {
        string filter = (raceFilter ?? "all").Trim().ToLowerInvariant();
        if (filter is "" or "all")
            return true;

        if (filter == "custom")
            return isCustom;

        if (isItem)
        {
            // When user explicitly switches Type = Items, only restrict if Race = Custom; otherwise treat items as Neutral.
            if (string.Equals(typeFilter?.Trim(), "items", StringComparison.OrdinalIgnoreCase))
                return filter == "neutral_passive";
            return filter == "neutral_passive";
        }

        string race = (unitRace ?? "other").Trim().ToLowerInvariant();
        return filter switch
        {
            "human" => race == "human",
            "orc" => race == "orc",
            "undead" => race == "undead",
            "nightelf" => race == "nightelf",
            "creeps" => race is "creeps" or "demon",
            "neutral_passive" => race is "naga" or "commoner" or "other" or "critters" or "unknown" or "",
            _ => race == filter
        };
    }

    public static bool MatchesTypeFilter(string? typeFilter, bool isHero, bool isBuilding, bool isItem, string? rawcode = null)
    {
        string filter = (typeFilter ?? "all").Trim().ToLowerInvariant();
        if (filter is "" or "all")
            return true;

        if (string.Equals(rawcode, "sloc", StringComparison.OrdinalIgnoreCase))
            return false;

        return filter switch
        {
            "units" => !isHero && !isBuilding && !isItem,
            "heroes" => isHero && !isItem,
            "buildings" => isBuilding && !isItem,
            "items" => isItem,
            _ => true
        };
    }
}

