using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using W3Studio.Models;

namespace W3Studio.Services;

public class JassSymbol
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "Native"; // Native, BJ, Constant, Type, Keyword, Snippet, Global, Local, Region, Unit, Trigger, Function
    public string ReturnType { get; set; } = string.Empty;
    public string Parameters { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string? InsertTemplate { get; set; }

    public string DisplayKind => Kind switch
    {
        "Native" => "Native 1.27b",
        "BJ" => "Blizzard.j",
        "Constant" => "Constante",
        "Global" => "Variable udg_",
        "Local" => "Local",
        "Region" => "Región gg_rct_",
        "Unit" => "Unidad gg_unit_",
        "Trigger" => "Trigger gg_trg_",
        "Function" => "Función",
        "Snippet" => "Snippet",
        _ => Kind
    };
}

public static class JassKnowledgeService
{
    private static readonly object _cacheLock = new();
    private static List<JassSymbol>? _cachedSdkSymbols;
    private static Dictionary<string, JassSymbol>? _cachedSdkLookup;
    private static string _cachedSdkKey = string.Empty;

    private static readonly Regex NativeRegex = new(
        @"^\s*(?:constant\s+)?native\s+([A-Za-z_][A-Za-z0-9_]*)\s+takes\s+(.+?)\s+returns\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex FunctionRegex = new(
        @"^\s*(?:public\s+|private\s+|static\s+|constant\s+)*function\s+([A-Za-z_][A-Za-z0-9_]*)\s+takes\s+(.+?)\s+returns\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex ConstantRegex = new(
        @"^\s*constant\s+([A-Za-z_][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex TypeRegex = new(
        @"^\s*type\s+([A-Za-z_][A-Za-z0-9_]*)\s+extends\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex LocalDeclRegex = new(
        @"^\s*local\s+([A-Za-z_][A-Za-z0-9_]*)\s+(?:array\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> NonLeakHandleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "integer", "real", "boolean", "string", "code", "player", "unitpool", "itempool",
        "damagetype", "attacktype", "weapontype", "alliancetype", "race", "racepreference",
        "gamestate", "playerstate", "playerscore", "playergameresult", "unitstate", "aidifficulty",
        "eventid", "gameevent", "playerevent", "playerunitevent", "unitevent", "limitop",
        "widgetevent", "dialogevent", "unittype", "heroattribute", "mapcontrol", "mapvisibility",
        "mapsetting", "mapdensity", "mapflag", "placement", "startlocprio", "raritycontrol",
        "blendmode", "texmapflags", "effecttype", "version", "itemtype", "soundtype", "pathingtype",
        "volumegroup", "camerafield", "igamestate", "fgamestate"
    };

    private static readonly HashSet<string> LeakableHandleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "unit", "location", "group", "force", "effect", "timer", "trigger", "rect", "region",
        "item", "destructable", "lightning", "fogmodifier", "boolexpr", "conditionfunc",
        "filterfunc", "triggercondition", "triggeraction", "multiboard", "leaderboard", "timerdialog",
        "texttag", "trackable", "ubersplat", "image", "weathereffect"
    };

    private static readonly JassSymbol[] BuiltinKeywordsAndSnippets =
    {
        new() { Name = "function", Kind = "Keyword", Signature = "function <nombre> takes <args> returns <tipo>", Description = "Declara una función JASS." },
        new() { Name = "endfunction", Kind = "Keyword", Signature = "endfunction", Description = "Cierra una función JASS." },
        new() { Name = "takes", Kind = "Keyword", Signature = "takes <tipo nombre | nothing>", Description = "Lista de parámetros de una función." },
        new() { Name = "returns", Kind = "Keyword", Signature = "returns <tipo | nothing>", Description = "Tipo de retorno de una función." },
        new() { Name = "nothing", Kind = "Keyword", Signature = "nothing", Description = "Sin parámetros ni retorno." },
        new() { Name = "local", Kind = "Keyword", Signature = "local <tipo> <nombre> = <valor>", Description = "Variable local al principio de una función." },
        new() { Name = "set", Kind = "Keyword", Signature = "set <variable> = <expresión>", Description = "Asigna un valor a una variable." },
        new() { Name = "call", Kind = "Keyword", Signature = "call <funcion>(<args>)", Description = "Invoca una función o nativa." },
        new() { Name = "return", Kind = "Keyword", Signature = "return [<valor>]", Description = "Devuelve resultado y sale de la función." },
        new() { Name = "globals", Kind = "Keyword", Signature = "globals ... endglobals", Description = "Bloque de variables globales." },
        new() { Name = "endglobals", Kind = "Keyword", Signature = "endglobals", Description = "Cierra un bloque globals." },
        new() { Name = "if", Kind = "Keyword", Signature = "if <cond> then ... endif", Description = "Condicional JASS." },
        new() { Name = "elseif", Kind = "Keyword", Signature = "elseif <cond> then", Description = "Rama condicional alternativa." },
        new() { Name = "else", Kind = "Keyword", Signature = "else", Description = "Rama por defecto de un if." },
        new() { Name = "endif", Kind = "Keyword", Signature = "endif", Description = "Cierra un bloque if." },
        new() { Name = "loop", Kind = "Keyword", Signature = "loop ... exitwhen <cond> ... endloop", Description = "Bucle JASS." },
        new() { Name = "exitwhen", Kind = "Keyword", Signature = "exitwhen <condición>", Description = "Sale del loop cuando la condición es true." },
        new() { Name = "endloop", Kind = "Keyword", Signature = "endloop", Description = "Cierra un loop." },
        new() { Name = "library", Kind = "Keyword", Signature = "library <Nombre> initializer <Init>", Description = "Librería vJASS (JassHelper)." },
        new() { Name = "endlibrary", Kind = "Keyword", Signature = "endlibrary", Description = "Cierra una librería vJASS." },
        new() { Name = "scope", Kind = "Keyword", Signature = "scope <Nombre> initializer <Init>", Description = "Scope vJASS para encapsular código." },
        new() { Name = "endscope", Kind = "Keyword", Signature = "endscope", Description = "Cierra un scope vJASS." },
        new() { Name = "struct", Kind = "Keyword", Signature = "struct <Nombre> ... endstruct", Description = "Estructura OOP vJASS." },
        new() { Name = "endstruct", Kind = "Keyword", Signature = "endstruct", Description = "Cierra un struct vJASS." },
        new() { Name = "private", Kind = "Keyword", Signature = "private <función|global|struct>", Description = "Visibilidad privada dentro de library/scope." },
        new() { Name = "public", Kind = "Keyword", Signature = "public <función|global|struct>", Description = "Visibilidad con prefijo de library/scope." },
        new() { Name = "integer", Kind = "Type", Signature = "integer", Description = "Entero de 32 bits con signo." },
        new() { Name = "real", Kind = "Type", Signature = "real", Description = "Número flotante de 32 bits." },
        new() { Name = "boolean", Kind = "Type", Signature = "boolean", Description = "true o false." },
        new() { Name = "string", Kind = "Type", Signature = "string", Description = "Cadena de texto." },
        new() { Name = "code", Kind = "Type", Signature = "code", Description = "Referencia a función callback." },
        new() { Name = "handle", Kind = "Type", Signature = "handle", Description = "Tipo base de objetos en Warcraft III." },
        new()
        {
            Name = "snip_scope",
            Kind = "Snippet",
            Signature = "scope <Nombre> initializer Init ... endscope",
            Description = "Plantilla completa de Scope vJASS (1.27b).",
            InsertTemplate =
                "scope MiSistema initializer Init\n" +
                "    globals\n" +
                "        private trigger TrgMain = null\n" +
                "    endglobals\n\n" +
                "    private function Conditions takes nothing returns boolean\n" +
                "        return true\n" +
                "    endfunction\n\n" +
                "    private function Actions takes nothing returns nothing\n" +
                "        \n" +
                "    endfunction\n\n" +
                "    private function Init takes nothing returns nothing\n" +
                "        set TrgMain = CreateTrigger()\n" +
                "        call TriggerRegisterAnyUnitEventBJ(TrgMain, EVENT_PLAYER_UNIT_SPELL_EFFECT)\n" +
                "        call TriggerAddCondition(TrgMain, Condition(function Conditions))\n" +
                "        call TriggerAddAction(TrgMain, function Actions)\n" +
                "    endfunction\n" +
                "endscope"
        },
        new()
        {
            Name = "snip_library",
            Kind = "Snippet",
            Signature = "library <Nombre> ... endlibrary",
            Description = "Plantilla de librería reutilizable vJASS.",
            InsertTemplate =
                "library MiLibreria\n" +
                "    public function Helper takes unit u returns nothing\n" +
                "        \n" +
                "    endfunction\n" +
                "endlibrary"
        },
        new()
        {
            Name = "snip_struct",
            Kind = "Snippet",
            Signature = "struct <Nombre> ... endstruct",
            Description = "Plantilla de Struct vJASS con create y onDestroy.",
            InsertTemplate =
                "struct SpellData\n" +
                "    unit caster\n" +
                "    location targetLoc\n" +
                "    real duration\n\n" +
                "    static method create takes unit u, location loc returns thistype\n" +
                "        local thistype this = thistype.allocate()\n" +
                "        set .caster = u\n" +
                "        set .targetLoc = loc\n" +
                "        set .duration = 3.0\n" +
                "        return this\n" +
                "    endmethod\n\n" +
                "    method onDestroy takes nothing returns nothing\n" +
                "        if .targetLoc != null then\n" +
                "            call RemoveLocation(.targetLoc)\n" +
                "            set .targetLoc = null\n" +
                "        endif\n" +
                "        set .caster = null\n" +
                "    endmethod\n" +
                "endstruct"
        },
        new()
        {
            Name = "snip_group_loop",
            Kind = "Snippet",
            Signature = "FirstOfGroup loop (sin leak)",
            Description = "Iteración de grupo segura sin leak en WC3 1.27b.",
            InsertTemplate =
                "local group g = CreateGroup()\n" +
                "local unit u\n" +
                "call GroupEnumUnitsInRange(g, GetUnitX(GetTriggerUnit()), GetUnitY(GetTriggerUnit()), 400.0, null)\n" +
                "loop\n" +
                "    set u = FirstOfGroup(g)\n" +
                "    exitwhen u == null\n" +
                "    call GroupRemoveUnit(g, u)\n" +
                "    // Tu acción aquí con u\n" +
                "endloop\n" +
                "call DestroyGroup(g)\n" +
                "set g = null"
        },
        new()
        {
            Name = "snip_timer_loop",
            Kind = "Snippet",
            Signature = "Timer periódico con limpieza",
            Description = "Timer JASS con función callback.",
            InsertTemplate =
                "local timer t = CreateTimer()\n" +
                "call TimerStart(t, 0.03, true, function OnTimerTick)\n" +
                "set t = null"
        }
    };

    public static IReadOnlyList<JassSymbol> GetSdkSymbols(WorkspaceSettings? settings = null)
    {
        var commonPath = ResolveSdkPath(settings?.CommonJPath, "common.j");
        var blizzardPath = ResolveSdkPath(settings?.BlizzardJPath, "blizzard.j");
        var cacheKey = $"{commonPath}|{blizzardPath}";

        lock (_cacheLock)
        {
            if (_cachedSdkSymbols != null && string.Equals(_cachedSdkKey, cacheKey, StringComparison.OrdinalIgnoreCase))
                return _cachedSdkSymbols;

            var symbols = new List<JassSymbol>();
            var lookup = new Dictionary<string, JassSymbol>(StringComparer.OrdinalIgnoreCase);

            foreach (var b in BuiltinKeywordsAndSnippets)
            {
                symbols.Add(b);
                lookup.TryAdd(b.Name, b);
            }

            if (File.Exists(commonPath))
                ParseJassFile(commonPath, "common.j", isBlizzard: false, symbols, lookup);

            if (File.Exists(blizzardPath))
                ParseJassFile(blizzardPath, "blizzard.j", isBlizzard: true, symbols, lookup);

            _cachedSdkSymbols = symbols;
            _cachedSdkLookup = lookup;
            _cachedSdkKey = cacheKey;
            return _cachedSdkSymbols;
        }
    }

    public static JassSymbol? FindSymbol(string name, WorkspaceSettings? settings = null, MapProject? project = null, string? currentCode = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        GetSdkSymbols(settings);
        if (_cachedSdkLookup != null && _cachedSdkLookup.TryGetValue(name, out var sdkMatch))
            return sdkMatch;

        if (!string.IsNullOrEmpty(currentCode))
        {
            var localMatch = ExtractScriptSymbols(currentCode, "Editor")
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (localMatch != null) return localMatch;
        }

        if (project != null)
        {
            var mapMatch = GetProjectSymbols(project)
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (mapMatch != null) return mapMatch;
        }

        return null;
    }

    public static List<JassSymbol> GetProjectSymbols(MapProject? project)
    {
        var result = new List<JassSymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (project == null)
            return result;

        foreach (var gv in project.GlobalVariables)
        {
            var id = gv.JassIdentifier;
            if (seen.Add(id))
            {
                result.Add(new JassSymbol
                {
                    Name = id,
                    Kind = "Global",
                    ReturnType = gv.Type,
                    Signature = gv.IsArray ? $"{gv.Type} array {id}" : $"{gv.Type} {id}",
                    Description = string.IsNullOrWhiteSpace(gv.InitialValue)
                        ? $"Variable global del mapa ({gv.Type})"
                        : $"Variable global del mapa ({gv.Type} = {gv.InitialValue})",
                    SourceFile = "war3map.j"
                });
            }
        }

        foreach (var reg in project.Regions)
        {
            var sanitized = Regex.Replace(reg.Name ?? string.Empty, @"[^A-Za-z0-9_]", "_");
            if (string.IsNullOrWhiteSpace(sanitized)) continue;
            var id = sanitized.StartsWith("gg_rct_", StringComparison.OrdinalIgnoreCase) ? sanitized : $"gg_rct_{sanitized}";
            if (seen.Add(id))
            {
                result.Add(new JassSymbol
                {
                    Name = id,
                    Kind = "Region",
                    ReturnType = "rect",
                    Signature = $"rect {id}",
                    Description = $"Región '{reg.Name}' ({reg.Left:F0},{reg.Bottom:F0} → {reg.Right:F0},{reg.Top:F0})",
                    SourceFile = "war3map.w3r"
                });
            }
        }

        foreach (var u in project.PlacedUnits)
        {
            if (u.IsStartLocation) continue;
            var id = u.JassVariableName;
            if (seen.Add(id))
            {
                result.Add(new JassSymbol
                {
                    Name = id,
                    Kind = "Unit",
                    ReturnType = "unit",
                    Signature = $"unit {id}",
                    Description = $"{u.Name} [{u.Rawcode}] en ({u.X:F0}, {u.Y:F0})",
                    SourceFile = "war3mapUnits.doo"
                });
            }
        }

        foreach (var trg in project.AllTriggerNodes)
        {
            if (trg.Kind == TriggerNodeKind.GuiTrigger || trg.Kind == TriggerNodeKind.CustomTextTrigger)
            {
                var cleanName = Regex.Replace(trg.Name ?? string.Empty, @"[^A-Za-z0-9_]", "_");
                if (!string.IsNullOrWhiteSpace(cleanName))
                {
                    var trgVar = $"gg_trg_{cleanName}";
                    if (seen.Add(trgVar))
                    {
                        result.Add(new JassSymbol
                        {
                            Name = trgVar,
                            Kind = "Trigger",
                            ReturnType = "trigger",
                            Signature = $"trigger {trgVar}",
                            Description = $"Disparador '{trg.Name}'",
                            SourceFile = trg.Name ?? "Trigger"
                        });
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(trg.Code))
            {
                foreach (var sym in ExtractScriptSymbols(trg.Code, trg.Name ?? "Trigger"))
                {
                    if (seen.Add(sym.Name))
                        result.Add(sym);
                }
            }
        }

        return result;
    }

    public static List<JassSymbol> ExtractLocalsInScope(string code, int caretOffset)
    {
        var result = new List<JassSymbol>();
        if (string.IsNullOrEmpty(code))
            return result;

        caretOffset = Math.Clamp(caretOffset, 0, code.Length);
        var beforeCaret = code[..caretOffset];
        var lines = beforeCaret.Replace("\r\n", "\n").Split('\n');

        // Walk backward to the closest enclosing function/method header
        int startIdx = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("endfunction", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("endmethod", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (FunctionRegex.IsMatch(lines[i]) || Regex.IsMatch(lines[i], @"^\s*(?:public\s+|private\s+|static\s+)*method\s+([A-Za-z_][A-Za-z0-9_]*)\s+takes\s+(.+?)\s+returns\s+([A-Za-z_][A-Za-z0-9_]*)"))
            {
                startIdx = i;
                break;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Parse function takes parameters if found
        var headerMatch = FunctionRegex.Match(lines[startIdx]);
        if (headerMatch.Success)
        {
            var takes = headerMatch.Groups[2].Value.Trim();
            if (!takes.Equals("nothing", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var param in takes.Split(','))
                {
                    var parts = param.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && seen.Add(parts[^1]))
                    {
                        result.Add(new JassSymbol
                        {
                            Name = parts[^1],
                            Kind = "Local",
                            ReturnType = parts[0],
                            Signature = $"param {parts[0]} {parts[^1]}",
                            Description = "Parámetro de la función actual"
                        });
                    }
                }
            }
        }

        // Parse locals inside current function body up to caret
        for (int i = startIdx; i < lines.Length; i++)
        {
            var m = LocalDeclRegex.Match(lines[i]);
            if (m.Success)
            {
                var typeName = m.Groups[1].Value;
                var varName = m.Groups[2].Value;
                if (seen.Add(varName))
                {
                    result.Add(new JassSymbol
                    {
                        Name = varName,
                        Kind = "Local",
                        ReturnType = typeName,
                        Signature = $"local {typeName} {varName}",
                        Description = "Variable local en el ámbito actual"
                    });
                }
            }
        }

        return result;
    }

    public static List<JassSymbol> ExtractScriptSymbols(string code, string sourceName)
    {
        var list = new List<JassSymbol>();
        if (string.IsNullOrWhiteSpace(code))
            return list;

        var lines = code.Replace("\r\n", "\n").Split('\n');
        bool inGlobals = false;

        foreach (var rawLine in lines)
        {
            var line = StripLineComment(rawLine).Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (line.Equals("globals", StringComparison.OrdinalIgnoreCase))
            {
                inGlobals = true;
                continue;
            }
            if (line.Equals("endglobals", StringComparison.OrdinalIgnoreCase))
            {
                inGlobals = false;
                continue;
            }

            if (inGlobals)
            {
                var gm = Regex.Match(line, @"^(?:(?:private|public|constant)\s+)*([A-Za-z_][A-Za-z0-9_]*)\s+(?:array\s+)?([A-Za-z_][A-Za-z0-9_]*)");
                if (gm.Success)
                {
                    var type = gm.Groups[1].Value;
                    var name = gm.Groups[2].Value;
                    list.Add(new JassSymbol
                    {
                        Name = name,
                        Kind = "Global",
                        ReturnType = type,
                        Signature = $"{type} {name}",
                        Description = $"Variable global declarada en {sourceName}",
                        SourceFile = sourceName
                    });
                }
                continue;
            }

            var fm = FunctionRegex.Match(line);
            if (fm.Success)
            {
                var name = fm.Groups[1].Value;
                var takes = fm.Groups[2].Value.Trim();
                var ret = fm.Groups[3].Value.Trim();
                list.Add(new JassSymbol
                {
                    Name = name,
                    Kind = "Function",
                    Parameters = takes,
                    ReturnType = ret,
                    Signature = $"function {name} takes {takes} returns {ret}",
                    Description = $"Definida en {sourceName}",
                    SourceFile = sourceName
                });
            }
        }

        return list;
    }

    public static List<CompilerDiagnostic> AnalyzeLeaks(string code, string sourceLabel = "Trigger")
    {
        var diagnostics = new List<CompilerDiagnostic>();
        if (string.IsNullOrWhiteSpace(code))
            return diagnostics;

        var lines = code.Replace("\r\n", "\n").Split('\n');
        bool inFunction = false;
        bool inBlockComment = false;
        string currentFuncName = string.Empty;
        string currentFuncReturn = "nothing";
        int currentFuncLine = 1;
        var currentFuncBody = new StringBuilder();
        var localHandles = new List<(string Type, string Name, int Line)>();

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            var rawLine = lines[i];

            if (inBlockComment)
            {
                int endIdx = rawLine.IndexOf("*/", StringComparison.Ordinal);
                if (endIdx >= 0)
                {
                    inBlockComment = false;
                    rawLine = rawLine[(endIdx + 2)..];
                }
                else
                {
                    continue;
                }
            }

            int blockStart = rawLine.IndexOf("/*", StringComparison.Ordinal);
            if (blockStart >= 0)
            {
                int blockEnd = rawLine.IndexOf("*/", blockStart + 2, StringComparison.Ordinal);
                if (blockEnd >= 0)
                    rawLine = rawLine[..blockStart] + rawLine[(blockEnd + 2)..];
                else
                {
                    inBlockComment = true;
                    rawLine = rawLine[..blockStart];
                }
            }

            var line = StripLineComment(rawLine).Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            // Check Reforged/1.29+ Blz natives live
            var blzMatch = Regex.Match(line, @"\b(Blz[A-Za-z0-9_]+)\s*\(");
            if (blzMatch.Success)
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Error",
                    Line = lineNum,
                    Source = "WC3 1.27b",
                    TriggerOrFile = sourceLabel,
                    Message = $"Nativa '{blzMatch.Groups[1].Value}' es de parche 1.29+/Reforged y fallará en Warcraft III 1.27b."
                });
            }

            // Detect inline location leak expressions
            if (line.StartsWith("call ", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(line, @"\b(GetUnitLoc|GetSpellTargetLoc|GetOrderPointLoc|GetRectCenter|GetRandomLocInRect|PolarProjectionBJ|Location)\s*\("))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Warning",
                    Line = lineNum,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = "Posible leak de Point (location) en llamada directa: guarda la ubicación en una variable local y llama a RemoveLocation(loc)."
                });
            }

            // Detect inline group leak expressions
            if (line.StartsWith("call ", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(line, @"\b(GetUnitsInRectAll|GetUnitsInRangeOfLocAll|GetUnitsOfPlayerAll|GetUnitsInRectMatching|GetUnitsSelectedAll|CreateGroup)\s*\(") &&
                !line.Contains("DestroyGroup", StringComparison.Ordinal))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Warning",
                    Line = lineNum,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = "Posible leak de Unit Group en llamada directa: guarda el grupo en variable local y llama a DestroyGroup(g)."
                });
            }

            // Detect inline force leak expressions
            if (line.StartsWith("call ", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(line, @"\b(GetPlayersByMapControl|GetPlayersMatching|GetForceOfPlayer)\s*\(") &&
                !line.Contains("DestroyForce", StringComparison.Ordinal))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Warning",
                    Line = lineNum,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = "Posible leak de Player Force en llamada directa: guarda el force en variable local y llama a DestroyForce(f)."
                });
            }

            // Track function scope for local handle cleanup checks & waits
            var fm = FunctionRegex.Match(line);
            if (fm.Success)
            {
                inFunction = true;
                currentFuncName = fm.Groups[1].Value;
                currentFuncReturn = fm.Groups[3].Value.Trim();
                currentFuncLine = lineNum;
                currentFuncBody.Clear();
                localHandles.Clear();
                continue;
            }

            if (inFunction)
            {
                if (line.StartsWith("endfunction", StringComparison.OrdinalIgnoreCase))
                {
                    EvaluateFunctionLeaks(currentFuncName, currentFuncBody.ToString(), localHandles, sourceLabel, diagnostics);
                    inFunction = false;
                    localHandles.Clear();
                    currentFuncBody.Clear();
                    continue;
                }

                currentFuncBody.AppendLine(line);

                if (currentFuncReturn.Equals("boolean", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(line, @"\b(TriggerSleepAction|PolledWait)\s*\("))
                {
                    diagnostics.Add(new CompilerDiagnostic
                    {
                        Severity = "Warning",
                        Line = lineNum,
                        Source = "Leak Detector",
                        TriggerOrFile = sourceLabel,
                        Message = $"TriggerSleepAction/PolledWait dentro de '{currentFuncName}' (returns boolean) rompe el hilo en Condiciones/Filtros en WC3 1.27b."
                    });
                }

                var lm = LocalDeclRegex.Match(line);
                if (lm.Success)
                {
                    var varType = lm.Groups[1].Value;
                    var varName = lm.Groups[2].Value;
                    if (!NonLeakHandleTypes.Contains(varType) && LeakableHandleTypes.Contains(varType))
                    {
                        localHandles.Add((varType, varName, lineNum));
                    }
                }
            }
        }

        return diagnostics;
    }

    private static void EvaluateFunctionLeaks(
        string funcName,
        string body,
        List<(string Type, string Name, int Line)> localHandles,
        string sourceLabel,
        List<CompilerDiagnostic> diagnostics)
    {
        if (localHandles.Count == 0)
            return;

        // Skip initialization routines where triggers/regions stay alive permanently
        if (funcName.StartsWith("InitTrig_", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            funcName.StartsWith("InitCustom", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var (type, name, line) in localHandles)
        {
            if (type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(body, $@"\bRemoveLocation\s*\(\s*{Regex.Escape(name)}\s*\)"))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Warning",
                    Line = line,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = $"Leak de Point: la variable local 'location {name}' en '{funcName}' no llama a RemoveLocation({name})."
                });
            }
            else if (type.Equals("group", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(body, $@"\bDestroyGroup\s*\(\s*{Regex.Escape(name)}\s*\)"))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Warning",
                    Line = line,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = $"Leak de Group: la variable local 'group {name}' en '{funcName}' no llama a DestroyGroup({name})."
                });
            }
            else if (type.Equals("force", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(body, $@"\bDestroyForce\s*\(\s*{Regex.Escape(name)}\s*\)"))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Warning",
                    Line = line,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = $"Leak de Force: la variable local 'force {name}' en '{funcName}' no llama a DestroyForce({name})."
                });
            }
            else if (type.Equals("effect", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(body, $@"\bDestroyEffect\s*\(\s*{Regex.Escape(name)}\s*\)"))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Warning",
                    Line = line,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = $"Posible leak de Special Effect: 'effect {name}' en '{funcName}' no llama a DestroyEffect({name})."
                });
            }

            if (!Regex.IsMatch(body, $@"\bset\s+{Regex.Escape(name)}\s*=\s*null\b"))
            {
                diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Info",
                    Line = line,
                    Source = "Leak Detector",
                    TriggerOrFile = sourceLabel,
                    Message = $"Handle sin nulificar: añade 'set {name} = null' al final de '{funcName}' para liberar la referencia de ID en WC3 1.27b."
                });
            }
        }
    }

    public static string FormatJassCode(string code)
    {
        if (string.IsNullOrEmpty(code))
            return string.Empty;

        var lines = code.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        int indent = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                if (i < lines.Length - 1)
                    sb.AppendLine();
                continue;
            }

            var codeOnly = StripLineComment(trimmed).Trim();
            bool dedentBefore =
                StartsWithWord(codeOnly, "endfunction") ||
                StartsWithWord(codeOnly, "endmethod") ||
                StartsWithWord(codeOnly, "endglobals") ||
                StartsWithWord(codeOnly, "endif") ||
                StartsWithWord(codeOnly, "else") ||
                StartsWithWord(codeOnly, "elseif") ||
                StartsWithWord(codeOnly, "endloop") ||
                StartsWithWord(codeOnly, "endlibrary") ||
                StartsWithWord(codeOnly, "endscope") ||
                StartsWithWord(codeOnly, "endstruct") ||
                StartsWithWord(codeOnly, "endinterface") ||
                StartsWithWord(codeOnly, "endmodule") ||
                trimmed.StartsWith("//! endtextmacro", StringComparison.OrdinalIgnoreCase);

            if (dedentBefore && indent > 0)
                indent--;

            sb.Append(new string(' ', indent * 4)).Append(trimmed);
            if (i < lines.Length - 1)
                sb.AppendLine();

            bool indentAfter =
                Regex.IsMatch(codeOnly, @"^(?:public\s+|private\s+|static\s+|constant\s+)*(?:function|method)\s+") ||
                StartsWithWord(codeOnly, "globals") ||
                StartsWithWord(codeOnly, "loop") ||
                StartsWithWord(codeOnly, "else") ||
                Regex.IsMatch(codeOnly, @"^(?:if|elseif)\b.*\bthen$") ||
                Regex.IsMatch(codeOnly, @"^(?:public\s+|private\s+)?(?:library|scope|struct|interface|module)\b") ||
                trimmed.StartsWith("//! textmacro", StringComparison.OrdinalIgnoreCase);

            if (indentAfter)
                indent++;
        }

        return sb.ToString();
    }

    public static void SyncGlobalVariablesToScript(MapProject project)
    {
        if (project == null) return;
        var script = project.RawMapScript ?? string.Empty;
        if (string.IsNullOrWhiteSpace(script)) return;

        var globalsStart = Regex.Match(script, @"^[ \t]*globals\b", RegexOptions.Multiline);
        var globalsEnd = Regex.Match(script, @"^[ \t]*endglobals\b", RegexOptions.Multiline);
        if (!globalsStart.Success || !globalsEnd.Success || globalsEnd.Index <= globalsStart.Index)
            return;

        int blockBodyStart = globalsStart.Index + globalsStart.Length;
        int blockBodyEnd = globalsEnd.Index;
        var existingBody = script[blockBodyStart..blockBodyEnd];

        var retainedLines = new List<string>();
        var rawLines = existingBody.Replace("\r\n", "\n").Split('\n');
        foreach (var l in rawLines)
        {
            var trimmed = l.Trim();
            // Keep non-udg declarations (gg_trg_*, gg_rct_*, gg_unit_*, gg_snd_*, user custom globals)
            if (Regex.IsMatch(trimmed, @"^(?:[A-Za-z_][A-Za-z0-9_]*)\s+(?:array\s+)?udg_[A-Za-z0-9_]+"))
                continue;
            retainedLines.Add(l.TrimEnd('\r'));
        }

        var newUdgLines = new List<string>();
        foreach (var gv in project.GlobalVariables)
        {
            var id = gv.JassIdentifier;
            var type = string.IsNullOrWhiteSpace(gv.Type) ? "integer" : gv.Type.Trim();
            if (gv.IsArray)
            {
                newUdgLines.Add($"    {type} array {id}");
            }
            else
            {
                var defaultVal = ResolveDefaultGlobalInit(type, gv.InitialValue);
                newUdgLines.Add($"    {type} {id} = {defaultVal}");
            }
        }

        var bodyBuilder = new StringBuilder();
        bodyBuilder.AppendLine();
        foreach (var l in newUdgLines)
            bodyBuilder.AppendLine(l);
        foreach (var r in retainedLines)
        {
            if (!string.IsNullOrWhiteSpace(r))
                bodyBuilder.AppendLine(r);
        }

        project.RawMapScript = script[..blockBodyStart] + bodyBuilder.ToString() + script[blockBodyEnd..];

        // Update RootScript node if present so Script Principal stays synchronized
        var rootNode = project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
        if (rootNode != null)
        {
            rootNode.Code = project.RawMapScript;
        }
    }

    public static void SyncNodeCodeToProject(MapProject? project, TriggerNode? node, string oldCode, string newCode)
    {
        if (project == null || node == null)
            return;

        if (node.Kind == TriggerNodeKind.RootScript)
        {
            project.RawMapScript = newCode;
            return;
        }

        var script = project.RawMapScript ?? string.Empty;

        if (!string.IsNullOrEmpty(oldCode) && script.Contains(oldCode, StringComparison.Ordinal))
        {
            script = script.Replace(oldCode, newCode, StringComparison.Ordinal);
        }
        else if (!string.IsNullOrWhiteSpace(newCode))
        {
            var startMarker = $"// [W3S:{node.Id}]";
            var endMarker = $"// [/W3S:{node.Id}]";
            int sIdx = script.IndexOf(startMarker, StringComparison.Ordinal);
            int eIdx = sIdx >= 0 ? script.IndexOf(endMarker, sIdx, StringComparison.Ordinal) : -1;

            if (sIdx >= 0 && eIdx > sIdx)
            {
                int fullEnd = eIdx + endMarker.Length;
                script = script[..sIdx] + $"{startMarker}\n{newCode}\n{endMarker}" + script[fullEnd..];
            }
            else if (!script.Contains(newCode, StringComparison.Ordinal))
            {
                var block = $"\n{startMarker}\n{newCode}\n{endMarker}\n";
                int insertPos = script.IndexOf("function InitCustomTriggers takes nothing returns nothing", StringComparison.Ordinal);
                if (insertPos < 0)
                    insertPos = script.IndexOf("function main takes nothing returns nothing", StringComparison.Ordinal);

                if (insertPos >= 0)
                    script = script.Insert(insertPos, block + "\n");
                else
                    script += block;
            }
        }

        project.RawMapScript = script;
        var root = project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.RootScript);
        if (root != null)
            root.Code = script;
    }

    private static string ResolveDefaultGlobalInit(string type, string? initialValue)
    {
        if (!string.IsNullOrWhiteSpace(initialValue))
        {
            var v = initialValue.Trim();
            if (type.Equals("string", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("\""))
                return $"\"{v.Replace("\"", "\\\"")}\"";
            return v;
        }

        return type.ToLowerInvariant() switch
        {
            "integer" => "0",
            "real" => "0.0",
            "boolean" => "false",
            "string" => "\"\"",
            _ => "null"
        };
    }

    private static bool StartsWithWord(string input, string word)
    {
        if (!input.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return false;
        if (input.Length == word.Length)
            return true;
        char next = input[word.Length];
        return !char.IsLetterOrDigit(next) && next != '_';
    }

    private static string StripLineComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            else if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                return line[..i];
        }
        return line;
    }

    private static string ResolveSdkPath(string? configuredPath, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var baseTools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", fileName);
        if (File.Exists(baseTools))
            return baseTools;

        return configuredPath ?? baseTools;
    }

    private static void ParseJassFile(
        string path,
        string sourceFile,
        bool isBlizzard,
        List<JassSymbol> target,
        Dictionary<string, JassSymbol> lookup)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var nm = NativeRegex.Match(line);
            if (nm.Success)
            {
                var name = nm.Groups[1].Value;
                var takes = nm.Groups[2].Value.Trim();
                var ret = nm.Groups[3].Value.Trim();
                var sym = new JassSymbol
                {
                    Name = name,
                    Kind = "Native",
                    Parameters = takes,
                    ReturnType = ret,
                    Signature = $"native {name} takes {takes} returns {ret}",
                    Description = $"Nativa de Warcraft III 1.27b ({sourceFile})",
                    SourceFile = sourceFile
                };
                if (lookup.TryAdd(name, sym))
                    target.Add(sym);
                continue;
            }

            if (isBlizzard)
            {
                var fm = FunctionRegex.Match(line);
                if (fm.Success)
                {
                    var name = fm.Groups[1].Value;
                    var takes = fm.Groups[2].Value.Trim();
                    var ret = fm.Groups[3].Value.Trim();
                    var sym = new JassSymbol
                    {
                        Name = name,
                        Kind = "BJ",
                        Parameters = takes,
                        ReturnType = ret,
                        Signature = $"function {name} takes {takes} returns {ret}",
                        Description = $"Helper Blizzard.j (WC3 1.27b)",
                        SourceFile = sourceFile
                    };
                    if (lookup.TryAdd(name, sym))
                        target.Add(sym);
                    continue;
                }
            }

            var cm = ConstantRegex.Match(line);
            if (cm.Success)
            {
                var type = cm.Groups[1].Value;
                var name = cm.Groups[2].Value;
                var val = StripLineComment(cm.Groups[3].Value).Trim();
                var sym = new JassSymbol
                {
                    Name = name,
                    Kind = "Constant",
                    ReturnType = type,
                    Signature = $"constant {type} {name} = {val}",
                    Description = $"Constante WC3 1.27b ({sourceFile})",
                    SourceFile = sourceFile
                };
                if (lookup.TryAdd(name, sym))
                    target.Add(sym);
                continue;
            }

            var tm = TypeRegex.Match(line);
            if (tm.Success)
            {
                var name = tm.Groups[1].Value;
                var baseType = tm.Groups[2].Value;
                var sym = new JassSymbol
                {
                    Name = name,
                    Kind = "Type",
                    ReturnType = baseType,
                    Signature = $"type {name} extends {baseType}",
                    Description = $"Tipo handle WC3 ({sourceFile})",
                    SourceFile = sourceFile
                };
                if (lookup.TryAdd(name, sym))
                    target.Add(sym);
            }
        }
    }
}
