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

