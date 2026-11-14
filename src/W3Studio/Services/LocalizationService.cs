using System;
using System.Collections.Generic;

namespace W3Studio.Services;

/// <summary>
/// Centraliza el idioma activo del editor ("es" = Español, "en" = English)
/// eliminando por completo las etiquetas mixtas "Español / English".
/// </summary>
public static class LocalizationService
{
    private static string _currentLanguage = "es";

    public static string CurrentLanguage => _currentLanguage;
    public static bool IsSpanish => string.Equals(_currentLanguage, "es", StringComparison.OrdinalIgnoreCase);
    public static bool IsEnglish => !IsSpanish;

    public static event Action<string>? LanguageChanged;

    public static void SetLanguage(string? languageCode, bool notify = true)
    {
        var normalized = NormalizeLanguage(languageCode);
        var changed = !string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase);
        _currentLanguage = normalized;
        if (notify && changed)
        {
            LanguageChanged?.Invoke(_currentLanguage);
        }
    }

    public static string NormalizeLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return "es";

        var code = languageCode.Trim().ToLowerInvariant();
        if (code.StartsWith("en") || code.Contains("english") || code.Contains("ingl"))
            return "en";

        return "es";
    }

    /// <summary>
    /// Devuelve el texto en el idioma activo (nunca combinado con "/").
    /// </summary>
    public static string L(string es, string en) => IsSpanish ? es : en;

    /// <summary>
    /// Traduce la sección/categoría de un campo del Object Editor (ej. Combat -> Combate / Combat).
    /// </summary>
    public static string TranslateFieldSection(string rawSection)
    {
        var key = (rawSection ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "art" => L("Arte", "Art"),
            "combat" => L("Combate", "Combat"),
            "stats" => L("Estadísticas", "Stats"),
            "movement" => L("Movimiento", "Movement"),
            "path" or "pathing" => L("Ruta y Colisión", "Pathing"),
            "sound" => L("Sonido", "Sound"),
            "tech" or "techtree" => L("Árbol Tecnológico", "Techtree"),
            "text" => L("Texto", "Text"),
            "abilities" or "ability" => L("Habilidades", "Abilities"),
            "editor" => L("Editor", "Editor"),
            "data" => L("Datos", "Data"),
            _ => string.IsNullOrWhiteSpace(rawSection) ? L("General", "General") : rawSection
        };
    }

    /// <summary>
    /// Traduce una etiqueta de campo (DisplayName) proveniente de UnitMetaData.slk / WorldEditStrings.txt
    /// cuando el idioma activo es Español, o limpia textos bilingües si el idioma activo es Inglés.
    /// </summary>
    public static string LocalizeFieldTitle(string fieldId, string esTitle, string enTitle)
    {
        if (IsSpanish)
        {
            if (!string.IsNullOrWhiteSpace(esTitle))
                return esTitle;
            return TranslateEnglishFieldNameToSpanish(enTitle);
        }

        if (!string.IsNullOrWhiteSpace(enTitle))
            return enTitle;
        return esTitle;
    }

    /// <summary>
    /// Traduce automáticamente nombres de campos en inglés de *MetaData.slk (WESTRING_UE_...) al español
    /// si no tienen traducción explícita en el catálogo base.
    /// </summary>
    public static string TranslateEnglishFieldNameToSpanish(string englishName)
    {
        if (string.IsNullOrWhiteSpace(englishName))
            return englishName;

        if (ExactEnglishToSpanishFieldNames.TryGetValue(englishName.Trim(), out var exact))
            return exact;

        var result = englishName.Trim();
        foreach (var (enTerm, esTerm) in PhraseReplacementsEs)
        {
            result = result.Replace(enTerm, esTerm, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static readonly Dictionary<string, string> ExactEnglishToSpanishFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Animation - Blend Time (seconds)"] = "Tiempo de Mezcla de Animación (seg)",
        ["Animation - Cast Backswing"] = "Animación de Retroceso de Hechizo",
        ["Animation - Cast Point"] = "Punto de Lanzamiento de Hechizo",
        ["Animation - Run Speed"] = "Velocidad de Animación al Correr",
        ["Animation - Walk Speed"] = "Velocidad de Animación al Caminar",
        ["Maximum Pitch Angle (degrees)"] = "Ángulo Máximo de Inclinación Pitch (°)",
        ["Maximum Roll Angle (degrees)"] = "Ángulo Máximo de Balanceo Roll (°)",
        ["Elevation - Sample Points"] = "Puntos de Muestreo de Elevación",
        ["Elevation - Sample Radius"] = "Radio de Muestreo de Elevación",
        ["Fog of War - Sample Radius"] = "Radio de Muestreo de Niebla de Guerra",
        ["Model File"] = "Archivo de Modelo 3D (.mdx)",
        ["Model File - Extra Versions"] = "Versiones Extra del Modelo (RoC/TFT)",
        ["Icon - Game Interface"] = "Icono de Interfaz",
        ["Icon - Score Screen"] = "Icono de Pantalla de Puntuación",
        ["Scaling Value"] = "Escala del Modelo",
        ["Selection Scale"] = "Escala del Círculo de Selección",
        ["Selection Circle - Height"] = "Altura del Círculo de Selección",
        ["Selection Circle On Water"] = "Círculo de Selección sobre Agua",
        ["Occluder Height"] = "Altura de Oclusión de Visión",
        ["Orientation Interpolation"] = "Interpolación de Orientación",
        ["Projectile Impact - Z"] = "Impacto de Proyectil - Altura Z",
        ["Projectile Impact - Z (Swimming)"] = "Impacto de Proyectil - Altura Z (Nadando)",
        ["Projectile Launch - X"] = "Lanzamiento de Proyectil - Desplazamiento X",
        ["Projectile Launch - Y"] = "Lanzamiento de Proyectil - Desplazamiento Y",
        ["Projectile Launch - Z"] = "Lanzamiento de Proyectil - Altura Z",
        ["Projectile Launch - Z (Swimming)"] = "Lanzamiento de Proyectil - Altura Z (Nadando)",
        ["Propulsion Window (degrees)"] = "Ventana de Propulsión (°)",
        ["Required Animation Names"] = "Nombres de Animación Requeridos",
        ["Required Animation Names - Attachments"] = "Nombres de Animación para Adjuntos",
        ["Required Attachment Link Names"] = "Puntos de Anclaje Requeridos",
        ["Required Bone Names"] = "Nombres de Huesos Requeridos",
        ["Scale Projectiles"] = "Escalar Proyectiles con la Unidad",
        ["Shadow Image (Unit)"] = "Imagen de Sombra de Unidad",
        ["Shadow Image - Center X"] = "Sombra - Centro X",
        ["Shadow Image - Center Y"] = "Sombra - Centro Y",
        ["Shadow Image - Height"] = "Sombra - Alto",
        ["Shadow Image - Width"] = "Sombra - Ancho",
        ["Shadow Texture (Building)"] = "Textura de Sombra (Edificio)",
        ["Special"] = "Arte Especial / Efecto al Morir",
        ["Target"] = "Efecto sobre Objetivo",
        ["Team Color"] = "Color de Equipo",
        ["Tinting Color 1 (Red)"] = "Color de Tinte 1 (Rojo 0-255)",
        ["Tinting Color 2 (Green)"] = "Color de Tinte 2 (Verde 0-255)",
        ["Tinting Color 3 (Blue)"] = "Color de Tinte 3 (Azul 0-255)",
        ["Allow Custom Team Color"] = "Permitir Color de Equipo Personalizado",
        ["Use Extended Line of Sight"] = "Usar Línea de Visión Extendida",
        ["Has Water Shadow"] = "Tiene Sombra en el Agua"
    };

    private static readonly (string En, string Es)[] PhraseReplacementsEs =
    {
        ("Attack 1 - ", "Ataque 1 - "),
        ("Attack 2 - ", "Ataque 2 - "),
        ("Animation Backswing Point", "Punto de Retroceso de Animación"),
        ("Animation Damage Point", "Punto de Daño de Animación"),
        ("Area of Effect (Full Damage)", "Área de Efecto (Daño Total)"),
        ("Area of Effect (Medium Damage)", "Área de Efecto (Daño Medio)"),
        ("Area of Effect (Small Damage)", "Área de Efecto (Daño Pequeño)"),
        ("Area of Effect Targets", "Objetivos de Área de Efecto"),
        ("Attack Type", "Tipo de Ataque"),
        ("Cooldown Time", "Tiempo de Enfriamiento"),
        ("Damage Base", "Daño Base"),
        ("Damage Factor - Medium", "Factor de Daño - Medio"),
        ("Damage Factor - Small", "Factor de Daño - Pequeño"),
        ("Damage Loss Factor", "Factor de Pérdida de Daño"),
        ("Damage Number of Dice", "Número de Dados de Daño"),
        ("Damage Sides per Die", "Caras por Dado de Daño"),
        ("Damage Spill Distance", "Distancia de Dispersión de Daño"),
        ("Damage Spill Radius", "Radio de Dispersión de Daño"),
        ("Damage Upgrade Amount", "Bonificación de Daño por Mejora"),
        ("Maximum Number of Targets", "Número Máximo de Objetivos"),
        ("Projectile Arc", "Arco del Proyectil"),
        ("Projectile Art", "Modelo del Proyectil"),
        ("Projectile Homing Enabled", "Proyectil Teledirigido"),
        ("Projectile Speed", "Velocidad del Proyectil"),
        ("Range Motion Buffer", "Margen de Movimiento en Rango"),
        ("Range", "Rango"),
        ("Show UI", "Mostrar en Interfaz"),
        ("Targets Allowed", "Objetivos Permitidos"),
        ("Weapon Sound", "Sonido de Arma"),
        ("Weapon Type", "Tipo de Arma"),
        ("Acquisition Range", "Rango de Adquisición"),
        ("Armor Type", "Tipo de Armadura"),
        ("Attacks Enabled", "Ataques Habilitados"),
        ("Death Type", "Tipo de Muerte"),
        ("Defense Base", "Armadura Base"),
        ("Defense Type", "Tipo de Defensa"),
        ("Defense Upgrade Bonus", "Bonificación de Armadura por Mejora"),
        ("Minimum Attack Range", "Rango Mínimo de Ataque"),
        ("Targeted as", "Clasificado como Objetivo"),
        ("Hit Points Maximum (Base)", "Vida Máxima (Base)"),
        ("Hit Points Regeneration Rate", "Regeneración de Vida por Segundo"),
        ("Hit Points Regeneration Type", "Tipo de Regeneración de Vida"),
        ("Mana Initial Amount", "Maná Inicial"),
        ("Mana Maximum", "Maná Máximo"),
        ("Mana Regeneration", "Regeneración de Maná por Segundo"),
        ("Gold Cost", "Costo de Oro"),
        ("Lumber Cost", "Costo de Madera"),
        ("Gold Bounty Awarded - Base", "Recompensa de Oro - Base"),
        ("Gold Bounty Awarded - Number of Dice", "Recompensa de Oro - Número de Dados"),
        ("Gold Bounty Awarded - Sides per Die", "Recompensa de Oro - Caras por Dado"),
        ("Lumber Bounty Awarded - Base", "Recompensa de Madera - Base"),
        ("Lumber Bounty Awarded - Number of Dice", "Recompensa de Madera - Número de Dados"),
        ("Lumber Bounty Awarded - Sides per Die", "Recompensa de Madera - Caras por Dado"),
        ("Food Cost", "Consumo de Comida"),
        ("Food Produced", "Comida Producida"),
        ("Build Time", "Tiempo de Construcción / Entrenamiento"),
        ("Repair Gold Cost", "Costo de Oro de Reparación"),
        ("Repair Lumber Cost", "Costo de Madera de Reparación"),
        ("Repair Time", "Tiempo de Reparación"),
        ("Sight Radius (Day)", "Radio de Visión (Día)"),
        ("Sight Radius (Night)", "Radio de Visión (Noche)"),
        ("Sleeps", "Duerme de Noche"),
        ("Stock Maximum", "Stock Máximo en Tienda"),
        ("Stock Replenish Interval", "Intervalo de Reposición de Stock"),
        ("Stock Start Delay", "Retraso Inicial de Stock"),
        ("Transported Size", "Tamaño de Transporte"),
        ("Point Value", "Valor en Puntos"),
        ("Priority", "Prioridad"),
        ("Formation Rank", "Rango de Formación"),
        ("Primary Attribute", "Atributo Primario"),
        ("Starting Strength", "Fuerza Inicial"),
        ("Starting Agility", "Agilidad Inicial"),
        ("Starting Intelligence", "Inteligencia Inicial"),
        ("Strength per Level", "Fuerza por Nivel"),
        ("Agility per Level", "Agilidad por Nivel"),
        ("Intelligence per Level", "Inteligencia por Nivel"),
        ("Speed Base", "Velocidad Base"),
        ("Speed Minimum", "Velocidad Mínima"),
        ("Speed Maximum", "Velocidad Máxima"),
        ("Turn Rate", "Velocidad de Giro"),
        ("Height Minimum", "Altura Mínima de Vuelo"),
        ("Height", "Altura de Vuelo"),
        ("Collision Size", "Tamaño de Colisión"),
        ("Button Position (X)", "Posición de Botón (X)"),
        ("Button Position (Y)", "Posición de Botón (Y)"),
        ("Tooltip - Basic", "Tooltip Básico"),
        ("Tooltip - Extended", "Tooltip Extendido"),
        ("Tooltip - Awaken", "Tooltip - Despertar Héroe"),
        ("Tooltip - Revive", "Tooltip - Revivir Héroe"),
        ("Hotkey", "Tecla Rápida (Hotkey)"),
        ("Name - Editor Suffix", "Sufijo del Editor"),
        ("Proper Names Used", "Cantidad de Nombres Propios Usados"),
        ("Proper Names", "Nombres Propios del Héroe"),
        ("Description", "Descripción"),
        ("Requirements", "Requisitos Tecnológicos"),
        ("Requirements - Levels", "Niveles de Requisitos"),
        ("Units Trained", "Unidades Entrenadas"),
        ("Units Sold", "Unidades Vendidas"),
        ("Items Sold", "Objetos Vendidos"),
        ("Items Made", "Objetos Fabricados"),
        ("Researches Available", "Investigaciones Disponibles"),
        ("Structures Built", "Estructuras Construidas"),
        ("Upgrades To", "Mejora a Estructura"),
        ("Upgrades Used", "Mejoras Aplicables"),
        ("Revives Dead Heroes", "Revive Héroes Caídos"),
        ("Hero Abilities", "Habilidades de Héroe"),
        ("Normal", "Habilidades Normales"),
        ("Default Active Ability", "Habilidad Activa por Defecto")
    };
}

