using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using W3Studio.Models;

namespace W3Studio.Services;

public sealed class EntityArchetypeInfo
{
    public W3ObjectEntry? CustomObject { get; init; }
    public BaseObjectMetadata? BaseMetadata { get; init; }
    public string IconPath { get; init; } = string.Empty;

    public bool IsItem { get; init; }
    public bool IsHero { get; init; }
    public bool IsBuilding { get; init; }
    public bool IsGoldMine { get; init; }
    public bool IsWaygate { get; init; }
    public bool IsInvulnerable { get; init; }

    public int BaseHp { get; init; }
    public int BaseMp { get; init; }
    public int BaseStr { get; init; }
    public int BaseAgi { get; init; }
    public int BaseInt { get; init; }
    public int HeaderLevel { get; init; }

    public List<string> AbilityRawcodes { get; } = new();
    public string DescriptionText { get; init; } = string.Empty;
}

public static class EntityArchetypeHelper
{
    private static readonly Regex ColorCodeRegex = new(@"\|c[0-9a-fA-F]{8}|\|r", RegexOptions.Compiled);

    public static EntityArchetypeInfo Analyze(W3PlacedUnit u, MapProject? project, WarcraftBaseDataService? baseData)
    {
        var customObj = project?.Objects.FirstOrDefault(
            o => string.Equals(o.Rawcode, u.Rawcode, StringComparison.OrdinalIgnoreCase));

        var baseMeta = baseData?.GetBaseObject(u.Rawcode);
        if (baseMeta == null && !string.IsNullOrWhiteSpace(customObj?.BaseRawcode))
        {
            baseMeta = baseData?.GetBaseObject(customObj.BaseRawcode);
        }

        string ReadField(string slkKey, params string[] modIds)
        {
            if (customObj != null)
            {
                foreach (var modId in modIds)
                {
                    var field = customObj.Fields.FirstOrDefault(
                        f => string.Equals(f.FieldId, modId, StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(f.ResolvedValue));
                    if (field != null)
                    {
                        return project?.ResolveTrigStr(field.ResolvedValue) ?? field.ResolvedValue;
                    }
                }
            }

            if (baseMeta != null)
            {
                if (!string.IsNullOrEmpty(slkKey) &&
                    baseMeta.DefaultFields.TryGetValue(slkKey, out var slkVal) &&
                    !string.IsNullOrWhiteSpace(slkVal))
                {
                    return slkVal;
                }

                foreach (var modId in modIds)
                {
                    if (baseMeta.DefaultFields.TryGetValue(modId, out var fallbackVal) &&
                        !string.IsNullOrWhiteSpace(fallbackVal))
                    {
                        return fallbackVal;
                    }
                }
            }

            return string.Empty;
        }

        var isItem = u.IsItem ||
                     customObj?.Category == W3ObjectCategory.Items ||
                     baseMeta?.Category == W3ObjectCategory.Items;

        var abilUnit = ReadField("abilList", "uabi");
        var abilHero = ReadField("heroAbilList", "uhab");
        var abilCombined = string.Join(",", abilUnit, abilHero);

        var isBuilding = !isItem && (
            u.IsBuilding ||
            ParseBool(ReadField("isbldg", "ubdg")) ||
            PathingService.IsUnitBuilding(u.Rawcode, project, baseData));

        var baseStr = ParseInt(ReadField("STR", "ustr"), 0);
        var baseAgi = ParseInt(ReadField("AGI", "uagi"), 0);
        var baseInt = ParseInt(ReadField("INT", "uint"), 0);

        var rawcodeToCheck = !string.IsNullOrWhiteSpace(customObj?.BaseRawcode) ? customObj!.BaseRawcode : u.Rawcode;
        var isHero = !isItem && !isBuilding && (
            (rawcodeToCheck.Length > 0 && char.IsUpper(rawcodeToCheck[0])) ||
            baseStr > 0 || baseAgi > 0 || baseInt > 0);

        var isGoldMine = !isItem && (
            string.Equals(rawcodeToCheck, "ngol", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawcodeToCheck, "ugol", StringComparison.OrdinalIgnoreCase) ||
            ContainsAbility(abilCombined, "Agld") ||
            ContainsAbility(abilCombined, "Abgm"));

        var isWaygate = !isItem && (
            string.Equals(rawcodeToCheck, "nwgt", StringComparison.OrdinalIgnoreCase) ||
            ContainsAbility(abilCombined, "Awrp"));

        var isInvulnerable = !isItem && ContainsAbility(abilCombined, "Avul");

        var baseHp = Math.Max(1, ParseInt(ReadField("HP", "uhpm"), 100));
        var baseMp = Math.Max(0, ParseInt(ReadField("manaN", "umpm"), 0));

        var headerLevel = 0;
        if (isItem)
        {
            headerLevel = Math.Max(0, ParseInt(ReadField("Level", "ilev"), 0));
        }
        else if (isHero)
        {
            headerLevel = Math.Max(1, u.Level);
        }
        else if (!isBuilding)
        {
            headerLevel = Math.Max(0, ParseInt(ReadField("level", "ulev"), u.Level > 1 ? u.Level : 0));
        }

        var iconPath = customObj?.IconPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(iconPath))
            iconPath = ReadField("Art", "uico", "iico");
        if (string.IsNullOrWhiteSpace(iconPath))
            iconPath = baseMeta?.IconPath ?? string.Empty;

        var descRaw = isItem
            ? (string.IsNullOrWhiteSpace(customObj?.TooltipExtended)
                ? ReadField("Ubertip", "iutp", "Description", "ides")
                : customObj!.TooltipExtended)
            : (string.IsNullOrWhiteSpace(customObj?.TooltipExtended)
                ? ReadField("Ubertip", "utub")
                : customObj!.TooltipExtended);
        if (string.IsNullOrWhiteSpace(descRaw))
            descRaw = baseMeta?.TooltipExtended ?? baseMeta?.TooltipBasic ?? string.Empty;

        var info = new EntityArchetypeInfo
        {
            CustomObject = customObj,
            BaseMetadata = baseMeta,
            IconPath = iconPath,
            IsItem = isItem,
            IsHero = isHero,
            IsBuilding = isBuilding,
            IsGoldMine = isGoldMine,
            IsWaygate = isWaygate,
            IsInvulnerable = isInvulnerable,
            BaseHp = baseHp,
            BaseMp = baseMp,
            BaseStr = baseStr,
            BaseAgi = baseAgi,
            BaseInt = baseInt,
            HeaderLevel = headerLevel,
            DescriptionText = CleanWarcraftText(descRaw)
        };

        foreach (var rawToken in abilCombined.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var code = rawToken.Trim();
            if (code.Length == 4 && code != "____" &&
                !info.AbilityRawcodes.Any(a => string.Equals(a, code, StringComparison.OrdinalIgnoreCase)))
            {
                info.AbilityRawcodes.Add(code);
            }
        }

        return info;
    }

    public static string CleanWarcraftText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = ColorCodeRegex.Replace(text, string.Empty);
        return cleaned.Replace("|n", Environment.NewLine, StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static bool ContainsAbility(string list, string code)
    {
        if (string.IsNullOrWhiteSpace(list))
            return false;
        foreach (var token in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token.Trim(), code, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ParseBool(string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
            return false;
        var t = val.Trim();
        return t == "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseInt(string? val, int fallback)
    {
        if (string.IsNullOrWhiteSpace(val))
            return fallback;
        var t = val.Trim();
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return (int)Math.Round(d);
        return fallback;
    }
}

