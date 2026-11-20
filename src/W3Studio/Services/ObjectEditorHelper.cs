using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using W3Studio.Models;

namespace W3Studio.Services;

public enum AutoFillProgressionMode
{
    Linear = 0,
    Geometric = 1,
    Wc3Formula = 2,
    Constant = 3
}

/// <summary>
/// Pure WC3 1.27b Object Editor rules: rawcode generation/validation (including Hero uppercase invariant),
/// category-specific field IDs, and World Editor Auto-Fill Levels calculations.
/// </summary>
public static class ObjectEditorHelper
{
    public static char ResolveCustomRawcodePrefix(W3ObjectCategory category, string? baseRawcode)
    {
        return category switch
        {
            W3ObjectCategory.Items => 'I',
            W3ObjectCategory.Abilities => 'A',
            W3ObjectCategory.Buffs => 'B',
            W3ObjectCategory.Destructables => 'B',
            W3ObjectCategory.Doodads => 'D',
            W3ObjectCategory.Upgrades => 'R',
            W3ObjectCategory.Units => ResolveUnitRawcodePrefix(baseRawcode),
            _ => 'h'
        };
    }

    private static char ResolveUnitRawcodePrefix(string? baseRawcode)
    {
        bool isHero = UnitPaletteFilterHelper.IsHeroUnit(W3ObjectCategory.Units, baseRawcode, baseRawcode);
        char first = !string.IsNullOrWhiteSpace(baseRawcode) && char.IsLetter(baseRawcode[0])
            ? baseRawcode[0]
            : 'h';
        return isHero ? char.ToUpperInvariant(first) : char.ToLowerInvariant(first);
    }

    public static string GetNameFieldId(W3ObjectCategory category) => category switch
    {
        W3ObjectCategory.Abilities => "anam",
        W3ObjectCategory.Buffs => "fnam",
        W3ObjectCategory.Upgrades => "gnam",
        W3ObjectCategory.Destructables => "bnam",
        W3ObjectCategory.Doodads => "dnam",
        _ => "unam"
    };

    public static (string XField, string YField) GetButtonPositionFieldIds(W3ObjectCategory category) => category switch
    {
        W3ObjectCategory.Abilities => ("abpx", "abpy"),
        W3ObjectCategory.Upgrades => ("gbpx", "gbpy"),
        _ => ("ubpx", "ubpy")
    };

    public static string GenerateNextFreeRawcode(char prefix, IEnumerable<string> usedRawcodes)
    {
        var used = new HashSet<string>(usedRawcodes, StringComparer.OrdinalIgnoreCase);
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (var i = 0; i < 46656; i++)
        {
            var c1 = chars[(i / 1296) % 36];
            var c2 = chars[(i / 36) % 36];
            var c3 = chars[i % 36];
            var cand = $"{prefix}{c1}{c2}{c3}";
            if (!used.Contains(cand))
                return cand;
        }
        return $"{prefix}99Z";
    }

    public static (bool IsValid, string ErrorMessage) ValidateCustomRawcode(
        string? rawcode,
        W3ObjectCategory category,
        string? baseRawcode,
        ISet<string> usedRawcodes)
    {
        if (string.IsNullOrWhiteSpace(rawcode) || rawcode.Length != 4)
            return (false, "El Rawcode debe tener exactamente 4 caracteres ASCII (ej: H000, A001, I000).");

        if (!rawcode.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
            return (false, "El Rawcode solo puede contener letras y dígitos ASCII (A-Z, a-z, 0-9).");

        if (usedRawcodes.Contains(rawcode))
            return (false, $"El Rawcode '{rawcode}' ya está en uso en el mapa o en los datos base.");

        if (category == W3ObjectCategory.Units)
        {
            bool baseIsHero = UnitPaletteFilterHelper.IsHeroUnit(W3ObjectCategory.Units, baseRawcode, baseRawcode);
            if (baseIsHero && !char.IsUpper(rawcode[0]))
                return (false, $"El objeto base '{baseRawcode}' es un Héroe: su Rawcode DEBE empezar con letra MAYÚSCULA (ej: {char.ToUpperInvariant(rawcode[0])}{rawcode[1..]}).");
            if (!baseIsHero && char.IsUpper(rawcode[0]))
                return (false, $"El objeto base '{baseRawcode}' es una unidad/edificio normal: su Rawcode debe empezar con minúscula (ej: {char.ToLowerInvariant(rawcode[0])}{rawcode[1..]}).");
        }

        return (true, string.Empty);
    }

    public const string HeroStrengthIconPath = @"UI\Widgets\Console\Human\infocard-heroattributes-str.blp";
    public const string HeroAgilityIconPath = @"UI\Widgets\Console\Human\infocard-heroattributes-agi.blp";
    public const string HeroIntelligenceIconPath = @"UI\Widgets\Console\Human\infocard-heroattributes-int.blp";

    public static string ResolveHeroPrimaryAttributeIconPath(string? primaryAttr) =>
        (primaryAttr ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "STR" => HeroStrengthIconPath,
            "AGI" => HeroAgilityIconPath,
            "INT" => HeroIntelligenceIconPath,
            _ => HeroStrengthIconPath
        };

    /// <summary>
    /// Resolves the authentic Warcraft III InfoPanel icon path and localized display label for an attack type ('ua1t'/'ua2t').
    /// Uses 'infocard-neutral-attack-*' (clean golden-framed icon without the empty upgrade-level box at bottom-right).
    /// </summary>
    public static (string IconPath, string Label) ResolveAttackTypeHudInfo(string? atkType)
    {
        var key = (atkType ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "normal" or "melee" => (@"UI\Widgets\Console\Human\infocard-neutral-attack-melee.blp", LocalizationService.L("Normal", "Normal")),
            "pierce" or "piercing" => (@"UI\Widgets\Console\Human\infocard-neutral-attack-piercing.blp", LocalizationService.L("Perforación", "Piercing")),
            "siege" => (@"UI\Widgets\Console\Human\infocard-neutral-attack-siege.blp", LocalizationService.L("Asedio", "Siege")),
            "magic" => (@"UI\Widgets\Console\Human\infocard-neutral-attack-magic.blp", LocalizationService.L("Mágico", "Magic")),
            "chaos" => (@"UI\Widgets\Console\Human\infocard-neutral-attack-chaos.blp", LocalizationService.L("Caos", "Chaos")),
            "spells" or "spell" => (@"ReplaceableTextures\CommandButtons\BTNFireBolt.blp", LocalizationService.L("Hechizos", "Spells")),
            "hero" => (@"UI\Widgets\Console\Human\infocard-neutral-attack-hero.blp", LocalizationService.L("Héroe", "Hero")),
            "unknown" or "none" or "" => (@"UI\Widgets\Console\Human\infocard-neutral-attack-melee.blp", LocalizationService.L("Normal", "Normal")),
            _ => (@"UI\Widgets\Console\Human\infocard-neutral-attack-melee.blp", key)
        };
    }

    /// <summary>
    /// Resolves the authentic Warcraft III InfoPanel icon path and localized display label for an armor type ('udty').
    /// Uses 'infocard-neutral-armor-*' where available (clean golden-framed icon without the upgrade-level box).
    /// </summary>
    public static (string IconPath, string Label) ResolveArmorTypeHudInfo(string? defType)
    {
        var key = (defType ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "none" or "unarmored" => (@"UI\Widgets\Console\Human\infocard-neutral-armor-unarmored.blp", LocalizationService.L("Sin armadura", "Unarmored")),
            "small" or "light" => (@"UI\Widgets\Console\Human\infocard-neutral-armor-small.blp", LocalizationService.L("Ligera", "Light")),
            "medium" => (@"UI\Widgets\Console\Human\infocard-neutral-armor-medium.blp", LocalizationService.L("Media", "Medium")),
            "normal" => (@"UI\Widgets\Console\Human\infocard-neutral-armor-medium.blp", LocalizationService.L("Normal", "Normal")),
            "heavy" or "large" => (@"UI\Widgets\Console\Human\infocard-neutral-armor-large.blp", LocalizationService.L("Pesada", "Heavy")),
            "fort" or "fortified" => (@"UI\Widgets\Console\Human\infocard-neutral-armor-fortified.blp", LocalizationService.L("Fortificada", "Fortified")),
            "hero" => (@"UI\Widgets\Console\Human\infocard-armor-hero.blp", LocalizationService.L("Héroe", "Hero")),
            "divine" => (@"UI\Widgets\Console\Human\infocard-armor-divine.blp", LocalizationService.L("Divina", "Divine")),
            _ => (@"UI\Widgets\Console\Human\infocard-neutral-armor-medium.blp", string.IsNullOrEmpty(key) ? LocalizationService.L("Media", "Medium") : key)
        };
    }

    public static List<string> ComputeAutoFillLevelValues(
        double baseValue,
        double levelFactor,
        double prevValueFactor,
        int maxLevels,
        bool isInteger,
        AutoFillProgressionMode mode)
    {
        int count = Math.Clamp(maxLevels, 1, 100);
        var values = new List<string>(count);
        double current = baseValue;

        for (int lv = 1; lv <= count; lv++)
        {
            if (lv == 1)
            {
                current = baseValue;
            }
            else
            {
                current = mode switch
                {
                    AutoFillProgressionMode.Constant => baseValue,
                    AutoFillProgressionMode.Linear => baseValue + levelFactor * (lv - 1),
                    AutoFillProgressionMode.Geometric => baseValue * Math.Pow(levelFactor == 0 ? 1.0 : levelFactor, lv - 1),
                    AutoFillProgressionMode.Wc3Formula => current * prevValueFactor + levelFactor,
                    _ => baseValue
                };
            }

            if (isInteger)
                values.Add(((int)Math.Round(current)).ToString(CultureInfo.InvariantCulture));
            else
                values.Add(Math.Round(current, 3).ToString("0.###", CultureInfo.InvariantCulture));
        }

        return values;
    }
}

