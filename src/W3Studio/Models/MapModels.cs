using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using War3Net.Build.Info;
using War3Net.Build.Object;
using War3Net.Build.Widget;

namespace W3Studio.Models;

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum TriggerNodeKind
{
    RootScript,
    MapHeader,
    Category,
    LibraryOrScope,
    GuiTrigger,
    CustomTextTrigger,
    FunctionBlock
}

public enum GuiTriggerElementType
{
    Event,
    Condition,
    Action
}

public class GuiTriggerElement : ObservableObject
{
    private string _category = "General";
    private string _functionName = string.Empty;
    private string _displayText = string.Empty;
    private bool _isEnabled = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public GuiTriggerElementType ElementType { get; set; }

    public string Category
    {
        get => _category;
        set { _category = value; OnPropertyChanged(); }
    }

    public string FunctionName
    {
        get => _functionName;
        set { _functionName = value; OnPropertyChanged(); }
    }

    public string DisplayText
    {
        get => _displayText;
        set { _displayText = value; OnPropertyChanged(); }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string IconGlyph => ElementType switch
    {
        GuiTriggerElementType.Event => "⚡",
        GuiTriggerElementType.Condition => "❓",
        GuiTriggerElementType.Action => "⚡",
        _ => "🔹"
    };

    public GuiTriggerElement Clone()
    {
        var clone = new GuiTriggerElement
        {
            ElementType = this.ElementType,
            Category = this.Category,
            FunctionName = this.FunctionName,
            DisplayText = this.DisplayText,
            IsEnabled = this.IsEnabled,
            Parameters = new Dictionary<string, string>(this.Parameters, StringComparer.OrdinalIgnoreCase)
        };
        return clone;
    }
}

public class TriggerNode : ObservableObject
{
    private string _name = string.Empty;
    private string _code = string.Empty;
    private string _comment = string.Empty;
    private bool _isEnabled = true;
    private bool _isInitiallyOn = true;
    private bool _isModified;
    private bool _isCustomText;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TriggerNodeKind Kind { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = "General";
    public int StartLineInMapJ { get; set; } = 1;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); }
    }

    public string Code
    {
        get => _code;
        set { _code = value; OnPropertyChanged(); }
    }

    public string Comment
    {
        get => _comment;
        set { _comment = value; OnPropertyChanged(); }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    public bool IsInitiallyOn
    {
        get => _isInitiallyOn;
        set { _isInitiallyOn = value; OnPropertyChanged(); }
    }

    public bool IsModified
    {
        get => _isModified;
        set { _isModified = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); }
    }

    public bool IsCustomText
    {
        get => _isCustomText || Kind == TriggerNodeKind.CustomTextTrigger || Kind == TriggerNodeKind.RootScript || Kind == TriggerNodeKind.MapHeader || Kind == TriggerNodeKind.LibraryOrScope;
        set
        {
            _isCustomText = value;
            if (value && Kind == TriggerNodeKind.GuiTrigger)
            {
                Kind = TriggerNodeKind.CustomTextTrigger;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGui));
            OnPropertyChanged(nameof(IconGlyph));
            OnPropertyChanged(nameof(IconUri));
        }
    }

    public bool IsGui => Kind == TriggerNodeKind.GuiTrigger && !_isCustomText;

    public string IconGlyph => Kind switch
    {
        TriggerNodeKind.RootScript => "📜",
        TriggerNodeKind.MapHeader => "🏛️",
        TriggerNodeKind.Category => "📁",
        TriggerNodeKind.LibraryOrScope => "📦",
        TriggerNodeKind.CustomTextTrigger => "📄",
        TriggerNodeKind.GuiTrigger => "⚡",
        _ => "🔹"
    };

    public string IconUri => Kind switch
    {
        TriggerNodeKind.RootScript => "pack://application:,,,/Assets/Icons/script_vjass.png",
        TriggerNodeKind.MapHeader => "pack://application:,,,/Assets/Icons/map_header.png",
        TriggerNodeKind.Category => "pack://application:,,,/Assets/Icons/category_folder.png",
        TriggerNodeKind.LibraryOrScope => "pack://application:,,,/Assets/Icons/library_scope.png",
        TriggerNodeKind.CustomTextTrigger => "pack://application:,,,/Assets/Icons/script_vjass.png",
        TriggerNodeKind.GuiTrigger => "pack://application:,,,/Assets/Icons/triggers.png",
        _ => "pack://application:,,,/Assets/Icons/trigger_gui.png"
    };

    public string DisplayTitle => IsModified ? $"{Name} *" : Name;

    public ObservableCollection<TriggerNode> Children { get; } = new();
    public ObservableCollection<GuiTriggerElement> Events { get; } = new();
    public ObservableCollection<GuiTriggerElement> Conditions { get; } = new();
    public ObservableCollection<GuiTriggerElement> Actions { get; } = new();

    public string GenerateJassFromGui()
    {
        var cleanName = System.Text.RegularExpressions.Regex.Replace(Name, @"[^A-Za-z0-9_]", "_");
        if (string.IsNullOrWhiteSpace(cleanName)) cleanName = "Untitled";

        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(Comment))
        {
            foreach (var cLine in Comment.Replace("\r\n", "\n").Split('\n'))
                sb.AppendLine($"// {cLine}");
        }

        // 1. Conditions function
        sb.AppendLine($"function Trig_{cleanName}_Conditions takes nothing returns boolean");
        if (Conditions.Count == 0)
        {
            sb.AppendLine("    return true");
        }
        else
        {
            bool hasActive = false;
            foreach (var cond in Conditions)
            {
                if (!cond.IsEnabled)
                {
                    sb.AppendLine($"    // [DESHABILITADO] {cond.DisplayText}");
                    continue;
                }
                hasActive = true;
                sb.AppendLine($"    {GenerateConditionJass(cond)}");
            }
            sb.AppendLine(hasActive ? "    return true" : "    return true");
        }
        sb.AppendLine("endfunction\n");

        // 2. Actions function
        sb.AppendLine($"function Trig_{cleanName}_Actions takes nothing returns nothing");
        if (Actions.Count == 0)
        {
            sb.AppendLine("    // No actions");
        }
        else
        {
            foreach (var act in Actions)
            {
                if (!act.IsEnabled)
                {
                    sb.AppendLine($"    // [DESHABILITADO] {act.DisplayText}");
                    continue;
                }
                sb.AppendLine($"    {GenerateActionJass(act)}");
            }
        }
        sb.AppendLine("endfunction\n");

        // 3. InitTrig function
        sb.AppendLine("//===========================================================================");
        sb.AppendLine($"function InitTrig_{cleanName} takes nothing returns nothing");
        sb.AppendLine($"    set gg_trg_{cleanName} = CreateTrigger()");
        if (!IsEnabled || !IsInitiallyOn)
        {
            sb.AppendLine($"    call DisableTrigger(gg_trg_{cleanName})");
        }
        foreach (var ev in Events)
        {
            if (!ev.IsEnabled)
            {
                sb.AppendLine($"    // [DESHABILITADO] {ev.DisplayText}");
                continue;
            }
            sb.AppendLine($"    {GenerateEventJass(cleanName, ev)}");
        }
        sb.AppendLine($"    call TriggerAddCondition(gg_trg_{cleanName}, Condition(function Trig_{cleanName}_Conditions))");
        sb.AppendLine($"    call TriggerAddAction(gg_trg_{cleanName}, function Trig_{cleanName}_Actions)");
        sb.AppendLine("endfunction");

        return sb.ToString();
    }

    public TriggerNode Clone(string? newName = null)
    {
        var copy = new TriggerNode
        {
            Kind = this.Kind,
            CategoryId = this.CategoryId,
            CategoryName = this.CategoryName,
            Name = newName ?? $"{this.Name}_Copia",
            Code = this.Code,
            Comment = this.Comment,
            IsEnabled = this.IsEnabled,
            IsInitiallyOn = this.IsInitiallyOn,
            IsModified = true,
            IsCustomText = this.Kind != TriggerNodeKind.GuiTrigger && this.IsCustomText
        };
        foreach (var ev in Events) copy.Events.Add(ev.Clone());
        foreach (var cond in Conditions) copy.Conditions.Add(cond.Clone());
        foreach (var act in Actions) copy.Actions.Add(act.Clone());
        return copy;
    }

    private static string ResolvePlayerExpr(string? rawPid)
    {
        if (string.Equals(rawPid, "TriggeringPlayer", StringComparison.OrdinalIgnoreCase))
            return "GetTriggerPlayer()";
        if (string.Equals(rawPid, "OwnerOfTriggering", StringComparison.OrdinalIgnoreCase))
            return "GetOwningPlayer(GetTriggerUnit())";
        return $"Player({(string.IsNullOrWhiteSpace(rawPid) ? "0" : rawPid)})";
    }

    private static string GenerateEventJass(string cleanName, GuiTriggerElement ev)
    {
        var p = ev.Parameters;
        if (p.TryGetValue("RawJass", out var rawJass) && !string.IsNullOrWhiteSpace(rawJass))
            return rawJass;

        string GetParam(string key, string fallback) => p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        return ev.FunctionName switch
        {
            "MapInitialization" => $"call TriggerRegisterTimerEvent(gg_trg_{cleanName}, 0.00, false)",
            "PeriodicTimer" => $"call TriggerRegisterTimerEventPeriodic(gg_trg_{cleanName}, {GetParam("Interval", "1.00")})",
            "ElapsedTime" => $"call TriggerRegisterTimerEvent(gg_trg_{cleanName}, {GetParam("Seconds", "5.00")}, false)",
            "PlayerChat" => $"call TriggerRegisterPlayerChatEvent(gg_trg_{cleanName}, {ResolvePlayerExpr(GetParam("PlayerId", "0"))}, \"{GetParam("ChatMessage", "-test")}\", {GetParam("ExactMatch", "true").ToLower()})",
            "PlayerLeavesGame" => $"call TriggerRegisterPlayerEventLeave(gg_trg_{cleanName}, {ResolvePlayerExpr(GetParam("PlayerId", "0"))})",
            "UnitDies" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_DEATH)",
            "UnitAttacked" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_ATTACKED)",
            "UnitSpell" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_SPELL_EFFECT)",
            "UnitSpellChannel" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_SPELL_CHANNEL)",
            "UnitSpellFinish" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_SPELL_FINISH)",
            "UnitIssuedOrder" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_ISSUED_ORDER)",
            "UnitIssuedPointOrder" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_ISSUED_POINT_ORDER)",
            "UnitIssuedTargetOrder" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_ISSUED_TARGET_ORDER)",
            "HeroLevelUp" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_HERO_LEVEL)",
            "HeroLearnSkill" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_HERO_SKILL)",
            "ItemPickup" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_PICKUP_ITEM)",
            "ItemUse" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_USE_ITEM)",
            "ItemDrop" => $"call TriggerRegisterAnyUnitEventBJ(gg_trg_{cleanName}, EVENT_PLAYER_UNIT_DROP_ITEM)",
            "UnitEntersRegion" => $"call TriggerRegisterEnterRectSimple(gg_trg_{cleanName}, {ResolveRegionArg(GetParam("RegionName", "EntireMap"))})",
            "UnitLeavesRegion" => $"call TriggerRegisterLeaveRectSimple(gg_trg_{cleanName}, {ResolveRegionArg(GetParam("RegionName", "EntireMap"))})",
            "CustomEventScript" => $"{GetParam("Code", "// Custom Event")}",
            _ => $"call TriggerRegisterTimerEvent(gg_trg_{cleanName}, 0.00, false)"
        };
    }

    private static string ResolveRegionArg(string reg)
    {
        if (string.IsNullOrWhiteSpace(reg) ||
            reg.Equals("EntireMap", StringComparison.OrdinalIgnoreCase) ||
            reg.Equals("PlayableMapArea", StringComparison.OrdinalIgnoreCase) ||
            reg.Contains("Entire", StringComparison.OrdinalIgnoreCase) ||
            reg.Contains("Playable", StringComparison.OrdinalIgnoreCase))
        {
            return "GetPlayableMapRect()";
        }
        if (reg.StartsWith("gg_rct_", StringComparison.OrdinalIgnoreCase))
            return reg;
        return $"gg_rct_{reg}";
    }

    private static string GenerateConditionJass(GuiTriggerElement cond)
    {
        var p = cond.Parameters;
        if (p.TryGetValue("RawJass", out var rawJass) && !string.IsNullOrWhiteSpace(rawJass))
            return rawJass;

        string GetParam(string key, string fallback) => p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        return cond.FunctionName switch
        {
            "UnitTypeEqual" => $"if (GetUnitTypeId(GetTriggerUnit()) != '{GetParam("UnitType", "hfoo")}') then\n        return false\n    endif",
            "AbilityEqual" => $"if (GetSpellAbilityId() != '{GetParam("AbilityId", "AHtb")}') then\n        return false\n    endif",
            "ItemTypeEqual" => $"if (GetItemTypeId(GetManipulatedItem()) != '{GetParam("ItemType", "phea")}') then\n        return false\n    endif",
            "UnitHasBuff" => $"if (GetUnitAbilityLevel(GetTriggerUnit(), '{GetParam("BuffId", "BHab")}') <= 0) then\n        return false\n    endif",
            "UnitInRegion" => $"if not (RectContainsUnit({ResolveRegionArg(GetParam("RegionName", "EntireMap"))}, GetTriggerUnit())) then\n        return false\n    endif",
            "PlayerEqual" => $"if (GetOwningPlayer(GetTriggerUnit()) != {ResolvePlayerExpr(GetParam("PlayerId", "0"))}) then\n        return false\n    endif",
            "IntegerComparison" => $"if not ({GetParam("Var1", "udg_MyVar")} {GetParam("Op", "==")} {GetParam("Var2", "0")}) then\n        return false\n    endif",
            "RealComparison" => $"if not ({GetParam("Var1", "GetWidgetLife(GetTriggerUnit())")} {GetParam("Op", ">")} {GetParam("Var2", "0.0")}) then\n        return false\n    endif",
            "HeroLevelComparison" => $"if not (GetHeroLevel(GetTriggerUnit()) {GetParam("Op", ">=")} {GetParam("Level", "5")}) then\n        return false\n    endif",
            "StringComparison" => $"if not (GetEventPlayerChatString() {GetParam("Op", "==")} \"{GetParam("Text", "-test")}\") then\n        return false\n    endif",
            "BooleanComparison" => $"if not ({GetParam("Expr", "true")}) then\n        return false\n    endif",
            "CustomScript" => $"if not ({GetParam("Script", "true")}) then\n        return false\n    endif",
            _ => "if (false) then\n        return false\n    endif"
        };
    }

    private static string GenerateActionJass(GuiTriggerElement act)
    {
        var p = act.Parameters;
        if (p.TryGetValue("RawJass", out var rawJass) && !string.IsNullOrWhiteSpace(rawJass))
            return rawJass;

        string GetParam(string key, string fallback) => p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        return act.FunctionName switch
        {
            "CreateUnitsAtPoint" => $"call CreateNUnitsAtLoc({GetParam("Count", "1")}, '{GetParam("UnitType", "hfoo")}', {ResolvePlayerExpr(GetParam("PlayerId", "0"))}, {GetParam("Location", "GetRectCenter(GetPlayableMapRect())")}, {GetParam("Angle", "270.0")})",
            "KillUnit" => "call KillUnit(GetTriggerUnit())",
            "RemoveUnit" => "call RemoveUnit(GetTriggerUnit())",
            "MoveUnitInstant" => $"call SetUnitPositionLoc(GetTriggerUnit(), {GetParam("Location", "GetRectCenter(GetPlayableMapRect())")})",
            "SetUnitFacing" => $"call SetUnitFacing(GetTriggerUnit(), {GetParam("Angle", "270.0")})",
            "PauseUnit" => $"call PauseUnit(GetTriggerUnit(), {GetParam("Paused", "true")})",
            "ShowHideUnit" => $"call ShowUnit(GetTriggerUnit(), {GetParam("Show", "false")})",
            "IssueOrderNoTarget" => $"call IssueImmediateOrder(GetTriggerUnit(), \"{GetParam("Order", "stop")}\")",
            "UnitAddAbility" => $"call UnitAddAbility(GetTriggerUnit(), '{GetParam("AbilityId", "AHtb")}')",
            "UnitRemoveAbility" => $"call UnitRemoveAbility(GetTriggerUnit(), '{GetParam("AbilityId", "AHtb")}')",
            "SetUnitLife" => $"call SetUnitState(GetTriggerUnit(), UNIT_STATE_LIFE, GetUnitState(GetTriggerUnit(), UNIT_STATE_MAX_LIFE) * ({GetParam("Percent", "100.0")} / 100.0))",
            "SetUnitMana" => $"call SetUnitState(GetTriggerUnit(), UNIT_STATE_MANA, GetUnitState(GetTriggerUnit(), UNIT_STATE_MAX_MANA) * ({GetParam("Percent", "100.0")} / 100.0))",
            "DamageTarget" => $"call UnitDamageTargetBJ(GetTriggerUnit(), GetSpellTargetUnit(), {GetParam("Damage", "100.0")}, ATTACK_TYPE_HERO, DAMAGE_TYPE_NORMAL)",
            "AddHeroXP" => $"call AddHeroXP(GetTriggerUnit(), {GetParam("Amount", "100")}, true)",
            "SetHeroLevel" => $"call SetHeroLevel(GetTriggerUnit(), {GetParam("Level", "10")}, true)",
            "HeroGiveItem" => $"call UnitAddItemById(GetTriggerUnit(), '{GetParam("ItemType", "phea")}')",
            "ReviveHero" => $"call ReviveHeroLoc(GetTriggerUnit(), {GetParam("Location", "GetRectCenter(GetPlayableMapRect())")}, true)",
            "AddPlayerGold" => $"call SetPlayerState({ResolvePlayerExpr(GetParam("PlayerId", "0"))}, PLAYER_STATE_RESOURCE_GOLD, GetPlayerState({ResolvePlayerExpr(GetParam("PlayerId", "0"))}, PLAYER_STATE_RESOURCE_GOLD) + {GetParam("Amount", "500")})",
            "AddPlayerLumber" => $"call SetPlayerState({ResolvePlayerExpr(GetParam("PlayerId", "0"))}, PLAYER_STATE_RESOURCE_LUMBER, GetPlayerState({ResolvePlayerExpr(GetParam("PlayerId", "0"))}, PLAYER_STATE_RESOURCE_LUMBER) + {GetParam("Amount", "250")})",
            "CustomVictory" => $"call CustomVictoryBJ({ResolvePlayerExpr(GetParam("PlayerId", "0"))}, true, true)",
            "CustomDefeat" => $"call CustomDefeatBJ({ResolvePlayerExpr(GetParam("PlayerId", "0"))}, \"{GetParam("Reason", "Has sido derrotado.")}\")",
            "WaitSeconds" => $"call TriggerSleepAction({GetParam("Seconds", "2.0")})",
            "TurnOffTrigger" => $"call DisableTrigger({(string.IsNullOrWhiteSpace(GetParam("TriggerName", "")) ? "GetTriggeringTrigger()" : $"gg_trg_{GetParam("TriggerName", "")}")})",
            "TurnOnTrigger" => $"call EnableTrigger(gg_trg_{GetParam("TriggerName", "Trigger")})",
            "RunTrigger" => $"call TriggerExecute(gg_trg_{GetParam("TriggerName", "Trigger")})",
            "DisplayTimedText" => $"call DisplayTimedTextToForce(GetPlayersAll(), {GetParam("Duration", "10.0")}, \"{GetParam("Text", "Mensaje de Juego")}\")",
            "DisplayTimedTextPlayer" => $"call DisplayTimedTextToPlayer({ResolvePlayerExpr(GetParam("PlayerId", "0"))}, 0, 0, {GetParam("Duration", "10.0")}, \"{GetParam("Text", "Mensaje Privado")}\")",
            "ClearTextMessages" => "call ClearTextMessages()",
            "CreateFloatingText" => $"call CreateTextTagUnitBJ(\"{GetParam("Text", "¡Crítico!")}\", GetTriggerUnit(), 0.0, 10.0, 100.0, 80.0, 0.0, 0.0)",
            "CreateSpecialEffect" => $"call AddSpecialEffectLoc(\"{GetParam("ModelPath", "Abilities\\\\Spells\\\\Human\\\\Thunderclap\\\\ThunderClapCaster.mdl")}\", {GetParam("Location", "GetRectCenter(GetPlayableMapRect())")})",
            "DestroyLastCreatedEffect" => "call DestroyEffect(GetLastCreatedEffectBJ())",
            "PlaySound" => $"call PlaySoundBJ(gg_snd_{GetParam("SoundName", "Sound")})",
            "SetVariable" => $"set {GetParam("VarName", "udg_MyVar")} = {GetParam("Value", "0")}",
            "CustomScript" => $"{GetParam("Code", "// Custom Script")}",
            _ => "call DoNothing()"
        };
    }
}

public class W3GlobalVariable
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsArray { get; set; }
    public int ArraySize { get; set; } = 1;
    public string InitialValue { get; set; } = string.Empty;
    public string JassIdentifier => $"udg_{Name}";
    public string ArrayDisplay => IsArray ? $"Sí [{ArraySize}]" : "No";
}

public class W3PlacedUnit
{
    public string Rawcode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CreationNumber { get; set; }
    public int Variation { get; set; }
    public int PlayerId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float AngleRadians { get; set; }
    public float Scale { get; set; } = 1.0f;
    public float ScaleY { get; set; } = 1.0f;
    public float ScaleZ { get; set; } = 1.0f;
    public byte Flags { get; set; } = 2;
    public int HP { get; set; } = -1;
    public int MP { get; set; } = -1;
    public int GoldAmount { get; set; } = 12500;
    public float TargetAcquisition { get; set; } = -1.0f;
    public int Level { get; set; } = 1;
    public int HeroStrength { get; set; }
    public int HeroAgility { get; set; }
    public int HeroIntelligence { get; set; }
    public bool IsItem { get; set; }
    public bool IsBuilding { get; set; }

    public double AngleDegrees
    {
        get
        {
            var deg = (AngleRadians * 180.0 / Math.PI) % 360.0;
            return deg < 0 ? deg + 360.0 : deg;
        }
        set => AngleRadians = (float)(value * Math.PI / 180.0);
    }

    public string JassVariableName => Rawcode == "sloc"
        ? $"Player {PlayerId + 1} Start Location"
        : $"gg_unit_{Rawcode}_{CreationNumber:D4}";

    public bool IsStartLocation => Rawcode == "sloc";
    public bool IsNeutralBuilding => PlayerId == 15 && !IsStartLocation;
    public bool IsCreep => PlayerId == 12 && !IsStartLocation;

    public string PlayerName => PlayerId switch
    {
        0 => "Jugador 1 (Rojo)",
        1 => "Jugador 2 (Azul)",
        2 => "Jugador 3 (Teal)",
        3 => "Jugador 4 (Púrpura)",
        4 => "Jugador 5 (Amarillo)",
        5 => "Jugador 6 (Naranja)",
        6 => "Jugador 7 (Verde)",
        7 => "Jugador 8 (Rosa)",
        8 => "Jugador 9 (Gris)",
        9 => "Jugador 10 (Azul Claro)",
        10 => "Jugador 11 (Verde Oscuro)",
        11 => "Jugador 12 (Marrón)",
        12 => "Neutral Hostil (Creeps)",
        13 => "Neutral Víctima",
        14 => "Neutral Extra",
        _ => "Neutral Pasivo (Edificios/Tiendas)"
    };

    public Color PlayerColor => PlayerId switch
    {
        0 => Color.FromRgb(255, 3, 3),
        1 => Color.FromRgb(0, 66, 255),
        2 => Color.FromRgb(28, 230, 185),
        3 => Color.FromRgb(84, 0, 129),
        4 => Color.FromRgb(255, 252, 1),
        5 => Color.FromRgb(254, 138, 14),
        6 => Color.FromRgb(32, 192, 0),
        7 => Color.FromRgb(229, 91, 176),
        8 => Color.FromRgb(149, 150, 151),
        9 => Color.FromRgb(126, 191, 241),
        10 => Color.FromRgb(16, 98, 70),
        11 => Color.FromRgb(78, 42, 4),
        12 => Color.FromRgb(220, 50, 47),
        _ => Color.FromRgb(255, 215, 0)
    };

    public string PaletteDisplay => $"[{Rawcode}] {Name} ({ (int)X }, { (int)Y })";
}

public class W3PlacedDoodad
{
    public string Rawcode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CreationNumber { get; set; }
    public int Variation { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float AngleRadians { get; set; }
    public float Scale { get; set; } = 1.0f;
    public float ScaleY { get; set; } = 1.0f;
    public float ScaleZ { get; set; } = 1.0f;
    public byte Flags { get; set; } = 2;
    public byte Life { get; set; } = 100;
    public bool IsDestructible { get; set; }

    public double AngleDegrees
    {
        get
        {
            var deg = (AngleRadians * 180.0 / Math.PI) % 360.0;
            return deg < 0 ? deg + 360.0 : deg;
        }
        set => AngleRadians = (float)(value * Math.PI / 180.0);
    }

    public bool IsTree => Rawcode.EndsWith("tr", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.EndsWith("tw", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.EndsWith("lt", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.StartsWith("AT", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.StartsWith("LT", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.StartsWith("NT", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.StartsWith("WT", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.StartsWith("BT", StringComparison.OrdinalIgnoreCase) ||
                          Rawcode.StartsWith("FT", StringComparison.OrdinalIgnoreCase);

    public string PaletteDisplay => $"[{Rawcode}] {(string.IsNullOrEmpty(Name) ? "Doodad/Tree" : Name)} #{CreationNumber} ({(int)X}, {(int)Y})";
}

public class W3MapRegion
{
    public string Name { get; set; } = string.Empty;
    public int CreationNumber { get; set; }
    public float Left { get; set; }
    public float Bottom { get; set; }
    public float Right { get; set; }
    public float Top { get; set; }
    public byte ColorR { get; set; } = 128;
    public byte ColorG { get; set; } = 190;
    public byte ColorB { get; set; } = 255;

    public float CenterX => (Left + Right) * 0.5f;
    public float CenterY => (Bottom + Top) * 0.5f;
    public float Width => Math.Abs(Right - Left);
    public float Height => Math.Abs(Top - Bottom);

    public string JassVariableName => "gg_rct_" + Name.Replace(' ', '_');
    public string PaletteDisplay => $"[Rect #{CreationNumber}] {Name} ({(int)CenterX}, {(int)CenterY})";
}

public class W3TerrainData
{
    public char Tileset { get; set; } = 'A';
    public int GridWidth { get; set; } = 129; // vertices
    public int GridHeight { get; set; } = 129; // vertices
    public float OffsetX { get; set; } = -8192f;
    public float OffsetY { get; set; } = -8192f;

    public float WorldWidth => Math.Max(128f, (GridWidth - 1) * 128f);
    public float WorldHeight => Math.Max(128f, (GridHeight - 1) * 128f);
    public float MaxX => OffsetX + WorldWidth;
    public float MaxY => OffsetY + WorldHeight;

    public List<string> GroundTiles { get; } = new();
    public List<string> CliffTiles { get; } = new();

    public float[,]? GroundHeights { get; set; }
    public float[,]? WaterDepths { get; set; }
    public byte[,]? TextureIndices { get; set; }
    public byte[,]? CliffLevels { get; set; }
    public bool[,]? WaterFlags { get; set; }
    public bool[,]? RampFlags { get; set; }
    public byte[]? RawW3eBytes { get; set; }

    public byte[]? MinimapTgaBytes { get; set; }

    public string TilesetName => Tileset switch
    {
        'A' => "Ashenvale",
        'B' => "Barrens",
        'C' => "Felwood",
        'D' => "Dungeon",
        'F' => "Lordaeron Fall",
        'G' => "Underground",
        'I' => "Icecrown Glacier",
        'J' => "Dalaran Ruins",
        'K' => "Black Citadel",
        'L' => "Lordaeron Summer",
        'N' => "Northrend",
        'O' => "Outland",
        'Q' => "Village Fall",
        'V' => "Village",
        'W' => "Lordaeron Winter",
        'X' => "Dalaran",
        'Y' => "Cityscape",
        'Z' => "Sunken Ruins",
        _ => $"Tileset '{Tileset}'"
    };
}

public enum W3ObjectCategory
{
    Units,
    Items,
    Abilities,
    Buffs,
    Destructables,
    Doodads,
    Upgrades
}

public class FieldChoiceOption
{
    private string _displayText = string.Empty;

    public string Value { get; set; } = string.Empty;
    public string LabelEs { get; set; } = string.Empty;
    public string LabelEn { get; set; } = string.Empty;

    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrEmpty(LabelEs) || !string.IsNullOrEmpty(LabelEn))
                return Services.LocalizationService.L(
                    !string.IsNullOrEmpty(LabelEs) ? LabelEs : LabelEn,
                    !string.IsNullOrEmpty(LabelEn) ? LabelEn : LabelEs);
            return _displayText;
        }
        set => _displayText = value;
    }

    public string? Description { get; set; }

    public FieldChoiceOption() { }

    public FieldChoiceOption(string value, string displayText, string? description = null)
    {
        Value = value;
        _displayText = displayText;
        Description = description;
    }

    public FieldChoiceOption(string value, string labelEs, string labelEn, string? description, bool isLocalized)
    {
        Value = value;
        LabelEs = labelEs;
        LabelEn = labelEn;
        Description = description;
    }

    public static FieldChoiceOption Localized(string value, string labelEs, string labelEn, string? description = null)
        => new(value, labelEs, labelEn, description, true);

    public override string ToString() => DisplayText;
}

public class W3ObjectField : ObservableObject
{
    private string _rawValue = string.Empty;
    private string _resolvedValue = string.Empty;
    private string _fieldName = string.Empty;
    private bool _isModified;

    public string FieldId { get; set; } = string.Empty;
    public int FieldIntId { get; set; }
    public string FieldName
    {
        get => _fieldName;
        set
        {
            if (_fieldName != value)
            {
                _fieldName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayFieldTitle));
                OnPropertyChanged(nameof(DisplayValueText));
                OnPropertyChanged(nameof(StatusBadge));
            }
        }
    }
    public int Level { get; set; }
    public int Variation { get; set; }
    public int DataPointer { get; set; }
    public ObjectDataType DataType { get; set; } = ObjectDataType.String;
    public string SlkTypeName { get; set; } = string.Empty;

    // Validación y límites específicos de Warcraft III
    public List<FieldChoiceOption>? Choices { get; set; }
    public int? MinInt { get; set; }
    public int? MaxInt { get; set; }
    public double? MinReal { get; set; }
    public double? MaxReal { get; set; }
    public int DecimalPlaces { get; set; } = 2;

    public string RawValue
    {
        get => _rawValue;
        set { _rawValue = value; OnPropertyChanged(); }
    }

    public string ResolvedValue
    {
        get => _resolvedValue;
        set
        {
            if (_resolvedValue != value)
            {
                _resolvedValue = value;
                _isModified = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedChoice));
                OnPropertyChanged(nameof(DisplayValueText));
                OnPropertyChanged(nameof(StatusBadge));
                OnPropertyChanged(nameof(IsModified));
                ValueUpdatedCallback?.Invoke(this);
            }
        }
    }

    public bool IsModified
    {
        get => _isModified;
        set { _isModified = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusBadge)); }
    }

    public bool HasChoices => Choices != null && Choices.Count > 0;
    public bool IsPlainValue => !HasChoices;

    public FieldChoiceOption? SelectedChoice
    {
        get => Choices?.FirstOrDefault(c => c.Value.Equals(_resolvedValue.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value != null && !_resolvedValue.Equals(value.Value, StringComparison.OrdinalIgnoreCase))
            {
                ResolvedValue = value.Value;
            }
        }
    }

    public string? FormattedDisplayOverride { get; set; }

    public string DisplayValueText
    {
        get
        {
            if (HasChoices)
            {
                var match = SelectedChoice;
                if (match != null)
                    return match.DisplayText;
            }
            if (!string.IsNullOrEmpty(FormattedDisplayOverride))
                return FormattedDisplayOverride;
            return _resolvedValue;
        }
    }

    public string LevelLabel => Level > 0 ? Services.LocalizationService.L($"Nv. {Level}", $"Lv. {Level}") : "Base";
    public string DisplayFieldTitle => Level > 0
        ? $"{FieldName} ({Services.LocalizationService.L("Nivel", "Level")} {Level})"
        : FieldName;
    public string StatusBadge => IsModified
        ? Services.LocalizationService.L("★ Modificado", "★ Modified")
        : "Base SLK";
    public Action<W3ObjectField>? ValueUpdatedCallback { get; set; }

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(FieldName));
        OnPropertyChanged(nameof(DisplayFieldTitle));
        OnPropertyChanged(nameof(DisplayValueText));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(LevelLabel));
    }
}

public class W3ObjectTreeNode : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string FolderKey { get; set; } = string.Empty;
    public int LeafCount { get; set; }
    public bool IsFolder { get; set; }
    public bool IsExpanded { get; set; } = true;
    public ImageSource? IconSource { get; set; }
    public W3ObjectEntry? ObjectEntry { get; set; }
    public ObservableCollection<W3ObjectTreeNode> Children { get; } = new();
}

public class W3ObjectEntry : ObservableObject
{
    private string _name = string.Empty;
    private string _tooltipBasic = string.Empty;
    private string _tooltipExtended = string.Empty;
    private string _hotkey = string.Empty;
    private string _iconPath = string.Empty;
    private int _buttonPosX;
    private int _buttonPosY;
    private bool _isModified;
    private ImageSource? _iconBitmap;

    public W3ObjectCategory Category { get; set; }
    public string SubcategoryFolder { get; set; } = "General";
    public string Rawcode { get; set; } = string.Empty;
    public string BaseRawcode { get; set; } = string.Empty;
    public int OldId { get; set; }
    public int NewId { get; set; }
    public bool IsCustom => NewId != 0 && NewId != OldId;
    public bool IsStandardUnmodified { get; set; }

    public ImageSource? IconBitmap
    {
        get => _iconBitmap;
        set { _iconBitmap = value; OnPropertyChanged(); }
    }

    public string CategoryLabel => Category switch
    {
        W3ObjectCategory.Units => "Unidades",
        W3ObjectCategory.Items => "Ítems",
        W3ObjectCategory.Abilities => "Habilidades",
        W3ObjectCategory.Buffs => "Buffs / Efectos",
        W3ObjectCategory.Destructables => "Destructibles",
        W3ObjectCategory.Doodads => "Doodads",
        W3ObjectCategory.Upgrades => "Mejoras",
        _ => "Objetos"
    };

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); }
    }

    public string TooltipBasic
    {
        get => _tooltipBasic;
        set { _tooltipBasic = value; OnPropertyChanged(); }
    }

    public string TooltipExtended
    {
        get => _tooltipExtended;
        set { _tooltipExtended = value; OnPropertyChanged(); }
    }

    public string Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; OnPropertyChanged(); }
    }

    public string IconPath
    {
        get => _iconPath;
        set { _iconPath = value; OnPropertyChanged(); }
    }

    public int ButtonPosX
    {
        get => _buttonPosX;
        set { _buttonPosX = value; OnPropertyChanged(); OnPropertyChanged(nameof(ButtonPosDisplay)); }
    }

    public int ButtonPosY
    {
        get => _buttonPosY;
        set { _buttonPosY = value; OnPropertyChanged(); OnPropertyChanged(nameof(ButtonPosDisplay)); }
    }

    public string ButtonPosDisplay => $"({ButtonPosX}, {ButtonPosY})";

    public bool IsModified
    {
        get => _isModified;
        set { _isModified = value; OnPropertyChanged(); }
    }

    public string DisplaySummary => $"[{Rawcode}] {Name}";

    public ObservableCollection<W3ObjectField> Fields { get; } = new();

    public void RefreshSummaryFromFields()
    {
        foreach (var f in Fields)
        {
            var code = f.FieldId.ToLowerInvariant();
            if (code is "unam" or "anam" or "inam" or "fnam" or "bnam" or "dnam" or "gnam")
            {
                if (!string.IsNullOrWhiteSpace(f.ResolvedValue))
                    Name = f.ResolvedValue;
            }
            else if (code is "utip" or "atp1" or "itip" or "ftip" or "gtp1")
            {
                if (f.Level <= 1 && !string.IsNullOrWhiteSpace(f.ResolvedValue))
                    TooltipBasic = f.ResolvedValue;
            }
            else if (code is "utub" or "aub1" or "iutp" or "fube" or "gub1")
            {
                if (f.Level <= 1 && !string.IsNullOrWhiteSpace(f.ResolvedValue))
                    TooltipExtended = f.ResolvedValue;
            }
            else if (code is "uhot" or "ahky" or "ihot" or "ghk1")
            {
                if (f.Level <= 1 && !string.IsNullOrWhiteSpace(f.ResolvedValue))
                    Hotkey = f.ResolvedValue;
            }
            else if (code is "uico" or "aart" or "iico" or "fart" or "gar1")
            {
                if (!string.IsNullOrWhiteSpace(f.ResolvedValue))
                    IconPath = f.ResolvedValue;
            }
            else if (code is "ubpx" or "abpx")
            {
                if (int.TryParse(f.ResolvedValue, out var x))
                    ButtonPosX = x;
            }
            else if (code is "ubpy" or "abpy")
            {
                if (int.TryParse(f.ResolvedValue, out var y))
                    ButtonPosY = y;
            }
        }
    }
}

public class W3ImportedFile : ObservableObject
{
    private string _mpqPath = string.Empty;
    private long _sizeBytes;
    private string _status = "En MPQ";

    public string MpqPath
    {
        get => _mpqPath;
        set { _mpqPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(FileCategory)); }
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set { _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeFormatted)); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public byte[]? Data { get; set; }
    public bool IsPendingWrite { get; set; }
    public bool IsDeleted { get; set; }

    public bool IsInternalSystemFile => IsStandardInternalMapFile(MpqPath);

    public static bool IsStandardInternalMapFile(string? mpqPath)
    {
        if (string.IsNullOrWhiteSpace(mpqPath))
            return false;

        var norm = mpqPath.Trim().Replace('/', '\\');
        if (norm.Equals(@"Scripts\war3map.j", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = System.IO.Path.GetExtension(norm).ToLowerInvariant();
        if (ext is ".w3e" or ".w3i" or ".wtg" or ".wct" or ".wts" or ".wpm" or ".shd" or ".doo" or ".w3r" or ".w3c" or ".w3s" or ".mmp" or ".imp"
            or ".w3u" or ".w3t" or ".w3a" or ".w3b" or ".w3d" or ".w3q" or ".w3h")
            return true;

        var fileName = System.IO.Path.GetFileName(norm);
        return fileName.Equals("war3map.j", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("war3mapMap.blp", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("war3mapMap.tga", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("war3mapMap.b00", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("war3mapPreview.tga", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("war3mapMisc.txt", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("war3mapSkin.txt", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("war3mapExtra.txt", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("(", StringComparison.Ordinal);
    }

    public string SizeFormatted => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : SizeBytes < 1024 * 1024
            ? $"{SizeBytes / 1024.0:F1} KB"
            : $"{SizeBytes / (1024.0 * 1024.0):F2} MB";

    public string FileCategory
    {
        get
        {
            if (IsInternalSystemFile)
                return Services.LocalizationService.L("⚠ Sistema / Interno del Mapa", "⚠ System / Internal Map File");

            var ext = System.IO.Path.GetExtension(MpqPath).ToLowerInvariant();
            return ext switch
            {
                ".mdx" or ".mdl" => Services.LocalizationService.L("Modelo 3D (.mdx)", "3D Model (.mdx)"),
                ".blp" or ".tga" or ".dds" => Services.LocalizationService.L("Textura / Icono (.blp/.tga)", "Texture / Icon (.blp/.tga)"),
                ".mp3" or ".wav" => Services.LocalizationService.L("Audio / Sonido", "Audio / Sound"),
                ".j" or ".ai" or ".pld" => Services.LocalizationService.L("Script JASS / AI", "JASS / AI Script"),
                ".slk" or ".txt" or ".fdf" or ".toc" => Services.LocalizationService.L("Tablas SLK / UI / Config", "SLK / UI / Config"),
                _ => Services.LocalizationService.L("Recurso Importado", "Imported Asset")
            };
        }
    }
}

public class CompilerDiagnostic
{
    public string Severity { get; set; } = "Error";
    public int Line { get; set; }
    public string Source { get; set; } = "JassHelper";
    public string Message { get; set; } = string.Empty;
    public string TriggerOrFile { get; set; } = "war3map.j";

    public string DisplayLine => Line > 0 ? $"Línea {Line}" : "-";
    public string SeverityIcon => Severity switch
    {
        "Error" => "❌",
        "Warning" => "⚠️",
        _ => "ℹ️"
    };
}

public class MapProject : ObservableObject
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => string.IsNullOrEmpty(FilePath) ? "Sin mapa" : System.IO.Path.GetFileName(FilePath);
    public string MapName { get; set; } = string.Empty;
    public string MapAuthor { get; set; } = string.Empty;
    public string MapDescription { get; set; } = string.Empty;
    public string SuggestedPlayers { get; set; } = string.Empty;
    public int FormatVersion { get; set; }

    public string RawMapScript { get; set; } = string.Empty;
    public Dictionary<int, string> TriggerStrings { get; } = new();

    public W3TerrainData Terrain { get; set; } = new();
    public List<W3PlacedUnit> PlacedUnits { get; } = new();
    public List<W3PlacedDoodad> PlacedDoodads { get; } = new();
    public List<W3MapRegion> Regions { get; } = new();
    public List<W3GlobalVariable> GlobalVariables { get; } = new();

    public Dictionary<string, byte[]> RawMpqFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public UnitObjectData? UnitData { get; set; }
    public ItemObjectData? ItemData { get; set; }
    public AbilityObjectData? AbilityData { get; set; }
    public BuffObjectData? BuffData { get; set; }
    public DestructableObjectData? DestructableData { get; set; }
    public DoodadObjectData? DoodadData { get; set; }
    public UpgradeObjectData? UpgradeData { get; set; }
    public MapInfo? MapInfo { get; set; }
    public MapUnits? RawMapUnitsData { get; set; }
    public MapDoodads? RawMapDoodadsData { get; set; }

    public bool HasTerrainEdits { get; set; }
    public bool HasScenarioEdits { get; set; }
    public bool HasMiscEdits { get; set; }
    public bool HasSkinEdits { get; set; }
    public bool HasUnitDoodadEdits { get; set; }

    public bool HasUnsavedChanges =>
        HasTerrainEdits ||
        HasScenarioEdits ||
        HasMiscEdits ||
        HasSkinEdits ||
        HasUnitDoodadEdits ||
        Objects.Any(o => o.IsModified) ||
        AllTriggerNodes.Any(t => t.IsModified) ||
        Imports.Any(i => i.IsPendingWrite || i.IsDeleted);

    public ObservableCollection<TriggerNode> TriggerTree { get; } = new();
    public ObservableCollection<TriggerNode> AllTriggerNodes { get; } = new();
    public ObservableCollection<W3ObjectEntry> Objects { get; } = new();
    public ObservableCollection<W3ImportedFile> Imports { get; } = new();

    public string ResolveTrigStr(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var trimmed = input.Trim();
        if (trimmed.StartsWith("TRIGSTR_", StringComparison.OrdinalIgnoreCase))
        {
            var numPart = trimmed.Substring(8);
            if (int.TryParse(numPart, out var id) && TriggerStrings.TryGetValue(id, out var resolved))
            {
                return resolved;
            }
        }
        return input;
    }
}
