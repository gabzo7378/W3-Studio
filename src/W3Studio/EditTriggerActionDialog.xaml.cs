using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Models;

namespace W3Studio;

public partial class EditTriggerActionDialog : Window
{
    private bool _isLoadingExisting;
    public GuiTriggerElement ResultElement { get; private set; }

    public EditTriggerActionDialog(GuiTriggerElement? existing = null, MapProject? project = null)
    {
        InitializeComponent();

        PopulateProjectSuggestions(project);

        ResultElement = existing?.Clone() ?? new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Action,
            Category = "Unit",
            FunctionName = "CreateUnitsAtPoint",
            DisplayText = "Unit - Create 1 Footman for Player 1 (Red) at (Center of (Playable map area)) facing 270.00 degrees"
        };

        _isLoadingExisting = true;
        PopulateActionsForCategory("Unit");
        LoadExistingData();
        _isLoadingExisting = false;
        UpdateUiPanels();
        UpdatePreview();
    }

    private void PopulateProjectSuggestions(MapProject? project)
    {
        if (project == null) return;

        foreach (var gv in project.GlobalVariables)
            CboVarName.Items.Add(gv.JassIdentifier);

        foreach (var obj in project.Objects)
        {
            if (obj.Category == W3ObjectCategory.Units && !string.IsNullOrWhiteSpace(obj.Rawcode))
            {
                CboUnitType.Items.Add(new ComboBoxItem
                {
                    Content = $"{obj.Name} ({obj.Rawcode}) [Mapa]",
                    Tag = obj.Rawcode
                });
            }
        }

        foreach (var trg in project.AllTriggerNodes)
        {
            if (trg.Kind == TriggerNodeKind.GuiTrigger || trg.Kind == TriggerNodeKind.CustomTextTrigger)
            {
                var clean = Regex.Replace(trg.Name ?? string.Empty, @"[^A-Za-z0-9_]", "_");
                if (!string.IsNullOrWhiteSpace(clean))
                    TxtTargetTrigger.Items.Add(clean);
            }
        }

        foreach (var reg in project.Regions)
        {
            var clean = Regex.Replace(reg.Name ?? string.Empty, @"[^A-Za-z0-9_]", "_");
            if (!string.IsNullOrWhiteSpace(clean))
            {
                CboTargetLocation.Items.Add($"GetRectCenter(gg_rct_{clean})");
                CboLocation.Items.Add(new ComboBoxItem { Content = $"Center of {clean} <gen>", Tag = $"GetRectCenter(gg_rct_{clean})" });
            }
        }
    }

    private void PopulateActionsForCategory(string category)
    {
        if (CboAction == null) return;
        CboAction.Items.Clear();

        switch (category)
        {
            case "Unit":
                AddActionItem("Create Units At Point (Crear unidades en punto)", "CreateUnitsAtPoint", true);
                AddActionItem("Move Unit Instantly to Point (Mover unidad al instante)", "MoveUnitInstant");
                AddActionItem("Set Unit Facing Angle (Cambiar orientación °)", "SetUnitFacing");
                AddActionItem("Pause / Unpause Unit (Pausar / Despausar unidad)", "PauseUnit");
                AddActionItem("Show / Hide Unit (Mostrar / Ocultar unidad)", "ShowHideUnit");
                AddActionItem("Issue Order With No Target (Orden inmediata: stop/holdposition)", "IssueOrderNoTarget");
                AddActionItem("Add Ability to Unit (Añadir habilidad a unidad)", "UnitAddAbility");
                AddActionItem("Remove Ability from Unit (Quitar habilidad a unidad)", "UnitRemoveAbility");
                AddActionItem("Kill Unit (Matar unidad)", "KillUnit");
                AddActionItem("Remove Unit (Eliminar unidad del juego)", "RemoveUnit");
                AddActionItem("Set Unit Life (Establecer vida %)", "SetUnitLife");
                AddActionItem("Set Unit Mana (Establecer maná %)", "SetUnitMana");
                AddActionItem("Damage Target (Dañar objetivo)", "DamageTarget");
                break;
            case "Hero":
                AddActionItem("Add Hero Experience (Añadir experiencia)", "AddHeroXP", true);
                AddActionItem("Set Hero Level (Establecer nivel)", "SetHeroLevel");
                AddActionItem("Give Item-Type to Hero (Dar objeto por Rawcode)", "HeroGiveItem");
                AddActionItem("Revive Hero Instantly at Point (Revivir héroe)", "ReviveHero");
                break;
            case "Player":
                AddActionItem("Add Gold to Player (Añadir oro)", "AddPlayerGold", true);
                AddActionItem("Add Lumber to Player (Añadir madera)", "AddPlayerLumber");
                AddActionItem("Victory for Player (Victoria)", "CustomVictory");
                AddActionItem("Defeat for Player (Derrota)", "CustomDefeat");
                break;
            case "Variable":
                AddActionItem("Set Variable (Establecer Variable global/local)", "SetVariable", true);
                break;
            case "Time":
                AddActionItem("Wait seconds of game-time (Esperar)", "WaitSeconds", true);
                break;
            case "Trigger":
                AddActionItem("Turn off trigger (Desactivar disparador)", "TurnOffTrigger", true);
                AddActionItem("Turn on trigger (Activar disparador)", "TurnOnTrigger");
                AddActionItem("Run trigger (Ejecutar disparador)", "RunTrigger");
                break;
            case "Game":
                AddActionItem("Display timed text to all players (Mostrar texto)", "DisplayTimedText", true);
                AddActionItem("Display timed text to player (Mensaje privado)", "DisplayTimedTextPlayer");
                AddActionItem("Create Floating Text on Triggering Unit (Texto flotante)", "CreateFloatingText");
                AddActionItem("Clear Text Messages (Limpiar mensajes de pantalla)", "ClearTextMessages");
                break;
            case "Sound":
                AddActionItem("Play Sound (Reproducir sonido)", "PlaySound", true);
                break;
            case "SpecialEffect":
                AddActionItem("Create Special Effect at Point (Crear efecto especial)", "CreateSpecialEffect", true);
                AddActionItem("Destroy Last Created Special Effect (Limpiar efecto)", "DestroyLastCreatedEffect");
                break;
            case "MeleeGame":
                AddActionItem("Use melee time of day (Ciclo día/noche melee)", "MeleeStartingVisibility", true);
                AddActionItem("Limit Heroes to 1 per Hero-type (Limitar Héroes a 1)", "MeleeStartingHeroLimit");
                AddActionItem("Give Heroes a Scroll of Town Portal (Pergamino inicial)", "MeleeGrantHeroItems");
                AddActionItem("Set starting resources (Recursos iniciales melee)", "MeleeStartingResources");
                AddActionItem("Remove creeps/critters near start locations (Limpiar creeps)", "MeleeClearExcessUnits");
                AddActionItem("Create starting units (Crear unidades iniciales melee)", "MeleeStartingUnits");
                AddActionItem("Run melee AI scripts (Ejecutar scripts IA melee)", "MeleeStartingAI");
                AddActionItem("Enforce victory/defeat conditions (Victoria/Derrota melee)", "MeleeInitVictoryDefeat");
                break;
            case "CustomScript":
                AddActionItem("Custom Script (Código JASS libre)", "CustomScript", true);
                break;
        }

        if (CboAction.Items.Count > 0)
            CboAction.SelectedIndex = 0;
    }

    private void AddActionItem(string text, string tag, bool isSelected = false)
    {
        CboAction.Items.Add(new ComboBoxItem { Content = text, Tag = tag, IsSelected = isSelected });
    }

    private void LoadExistingData()
    {
        string targetCategory = ResultElement.Category;
        if (string.Equals(ResultElement.FunctionName, "CustomScript", StringComparison.OrdinalIgnoreCase))
            targetCategory = "CustomScript";
        else if (string.Equals(targetCategory, "Melee Game", StringComparison.OrdinalIgnoreCase))
            targetCategory = "MeleeGame";

        bool matchedCategory = false;
        if (!string.IsNullOrEmpty(targetCategory))
        {
            foreach (ComboBoxItem item in CboCategory.Items)
            {
                if (string.Equals(item.Tag?.ToString(), targetCategory, StringComparison.OrdinalIgnoreCase))
                {
                    CboCategory.SelectedItem = item;
                    PopulateActionsForCategory(item.Tag?.ToString() ?? "Unit");
                    matchedCategory = true;
                    break;
                }
            }
        }

        bool matchedAction = false;
        foreach (ComboBoxItem item in CboAction.Items)
        {
            if (string.Equals(item.Tag?.ToString(), ResultElement.FunctionName, StringComparison.OrdinalIgnoreCase))
            {
                CboAction.SelectedItem = item;
                matchedAction = true;
                break;
            }
        }

        // If the loaded action isn't matched (e.g. arbitrary JASS call from an existing map),
        // preserve it in CustomScript instead of resetting it to "Create 1 Footman".
        if (!matchedCategory || !matchedAction)
        {
            foreach (ComboBoxItem item in CboCategory.Items)
            {
                if (string.Equals(item.Tag?.ToString(), "CustomScript", StringComparison.OrdinalIgnoreCase))
                {
                    CboCategory.SelectedItem = item;
                    PopulateActionsForCategory("CustomScript");
                    break;
                }
            }
        }

        var p = ResultElement.Parameters;
        if (p.TryGetValue("Count", out var cnt)) TxtUnitCount.Text = cnt;
        if (p.TryGetValue("UnitType", out var ut))
        {
            bool found = false;
            foreach (ComboBoxItem item in CboUnitType.Items)
            {
                if (string.Equals(item.Tag?.ToString(), ut, StringComparison.OrdinalIgnoreCase))
                {
                    CboUnitType.SelectedItem = item;
                    found = true;
                    break;
                }
            }
            if (!found) TxtCustomRawcode.Text = ut;
        }
        if (p.TryGetValue("PlayerId", out var pid))
        {
            foreach (ComboBoxItem item in CboCreatePlayer.Items)
            {
                if (string.Equals(item.Tag?.ToString(), pid, StringComparison.OrdinalIgnoreCase))
                {
                    CboCreatePlayer.SelectedItem = item;
                    break;
                }
            }
            foreach (ComboBoxItem item in CboResourcePlayer.Items)
            {
                if (string.Equals(item.Tag?.ToString(), pid, StringComparison.OrdinalIgnoreCase))
                {
                    CboResourcePlayer.SelectedItem = item;
                    break;
                }
            }
            foreach (ComboBoxItem item in CboMessageTarget.Items)
            {
                if (string.Equals(item.Tag?.ToString(), pid, StringComparison.OrdinalIgnoreCase))
                {
                    CboMessageTarget.SelectedItem = item;
                    break;
                }
            }
        }
        if (p.TryGetValue("Location", out var loc)) CboTargetLocation.Text = loc;
        if (p.TryGetValue("Angle", out var ang))
        {
            TxtAngle.Text = ang;
            TxtSingleParamValue.Text = ang;
        }
        if (p.TryGetValue("Seconds", out var sec)) TxtWaitSeconds.Text = sec;
        if (p.TryGetValue("Text", out var text)) TxtGameMessage.Text = text;
        if (p.TryGetValue("Duration", out var dur)) TxtMessageDuration.Text = dur;
        if (p.TryGetValue("Amount", out var amt))
        {
            TxtResourceAmount.Text = amt;
            TxtHeroValue.Text = amt;
        }
        if (p.TryGetValue("Level", out var lvl)) TxtHeroValue.Text = lvl;
        if (p.TryGetValue("Percent", out var pct)) TxtUnitPercent.Text = pct;
        if (p.TryGetValue("Damage", out var dmg)) TxtUnitPercent.Text = dmg;
        if (p.TryGetValue("ModelPath", out var mp)) TxtCustomEffectPath.Text = mp;
        if (p.TryGetValue("SoundName", out var snd)) TxtSoundName.Text = snd;
        if (p.TryGetValue("TriggerName", out var trg)) TxtTargetTrigger.Text = trg;
        if (p.TryGetValue("Code", out var code)) TxtCustomCode.Text = code;
        else if (p.TryGetValue("RawJass", out var rawCode)) TxtCustomCode.Text = rawCode;
        if (p.TryGetValue("Order", out var ord)) TxtSingleParamValue.Text = ord;
        if (p.TryGetValue("AbilityId", out var ab)) TxtSingleParamValue.Text = ab;
        if (p.TryGetValue("ItemType", out var it)) TxtSingleParamValue.Text = it;
        if (p.TryGetValue("Paused", out var paused)) ChkUnitFlagValue.IsChecked = !string.Equals(paused, "false", StringComparison.OrdinalIgnoreCase);
        if (p.TryGetValue("Show", out var show)) ChkUnitFlagValue.IsChecked = !string.Equals(show, "false", StringComparison.OrdinalIgnoreCase);
        if (p.TryGetValue("VarName", out var vname)) CboVarName.Text = vname;
        if (p.TryGetValue("Value", out var vval)) TxtVarValue.Text = vval;
    }

    private void CboCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingExisting || CboCategory == null || CboAction == null) return;
        var cat = (CboCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Unit";
        PopulateActionsForCategory(cat);
        UpdateUiPanels();
        UpdatePreview();
    }

    private void CboAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingExisting || CboAction == null) return;
        var tag = (CboAction.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (tag == "SetUnitFacing") TxtSingleParamValue.Text = "270.0";
        else if (tag == "IssueOrderNoTarget") TxtSingleParamValue.Text = "stop";
        else if (tag == "UnitAddAbility" || tag == "UnitRemoveAbility") TxtSingleParamValue.Text = "AHtb";
        else if (tag == "HeroGiveItem") TxtSingleParamValue.Text = "phea";

        UpdateUiPanels();
        UpdatePreview();
    }

    private void ParamChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdateUiPanels()
    {
        if (PanelCreateUnits == null) return;

        PanelCreateUnits.Visibility = Visibility.Collapsed;
        PanelLocationOnly.Visibility = Visibility.Collapsed;
        PanelUnitFlag.Visibility = Visibility.Collapsed;
        PanelSingleTextParam.Visibility = Visibility.Collapsed;
        PanelSetVariable.Visibility = Visibility.Collapsed;
        PanelWait.Visibility = Visibility.Collapsed;
        PanelMessage.Visibility = Visibility.Collapsed;
        GridMessageTarget.Visibility = Visibility.Visible;
        PanelPlayerResource.Visibility = Visibility.Collapsed;
        PanelHero.Visibility = Visibility.Collapsed;
        PanelUnitState.Visibility = Visibility.Collapsed;
        PanelSpecialEffect.Visibility = Visibility.Collapsed;
        PanelSound.Visibility = Visibility.Collapsed;
        PanelTriggerControl.Visibility = Visibility.Collapsed;
        PanelCustomScript.Visibility = Visibility.Collapsed;
        TxtSimpleActionInfo.Visibility = Visibility.Collapsed;

        var actionTag = (CboAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CreateUnitsAtPoint";

        switch (actionTag)
        {
            case "CreateUnitsAtPoint":
                PanelCreateUnits.Visibility = Visibility.Visible;
                break;
            case "MoveUnitInstant":
            case "ReviveHero":
                PanelLocationOnly.Visibility = Visibility.Visible;
                break;
            case "PauseUnit":
                ChkUnitFlagValue.Content = "Pausar unidad (true = Pausar, false = Despausar)";
                PanelUnitFlag.Visibility = Visibility.Visible;
                break;
            case "ShowHideUnit":
                ChkUnitFlagValue.Content = "Mostrar unidad (true = Mostrar, false = Ocultar)";
                PanelUnitFlag.Visibility = Visibility.Visible;
                break;
            case "SetUnitFacing":
                TxtSingleParamLabel.Text = "Ángulo de orientación en grados (0 - 360):";
                PanelSingleTextParam.Visibility = Visibility.Visible;
                break;
            case "IssueOrderNoTarget":
                TxtSingleParamLabel.Text = "Orden sin objetivo (ej: stop, holdposition, windwalk, berserk):";
                PanelSingleTextParam.Visibility = Visibility.Visible;
                break;
            case "UnitAddAbility":
            case "UnitRemoveAbility":
                TxtSingleParamLabel.Text = "Rawcode de Habilidad (4 caracteres, ej: AHtb, Avul):";
                PanelSingleTextParam.Visibility = Visibility.Visible;
                break;
            case "HeroGiveItem":
                TxtSingleParamLabel.Text = "Rawcode del Objeto (ej: phea, ckng, ofir):";
                PanelSingleTextParam.Visibility = Visibility.Visible;
                break;
            case "SetVariable":
                PanelSetVariable.Visibility = Visibility.Visible;
                break;
            case "WaitSeconds":
                PanelWait.Visibility = Visibility.Visible;
                break;
            case "DisplayTimedText":
            case "DisplayTimedTextPlayer":
                PanelMessage.Visibility = Visibility.Visible;
                GridMessageTarget.Visibility = Visibility.Visible;
                break;
            case "CreateFloatingText":
                PanelMessage.Visibility = Visibility.Visible;
                GridMessageTarget.Visibility = Visibility.Collapsed;
                break;
            case "AddPlayerGold":
            case "AddPlayerLumber":
                PanelPlayerResource.Visibility = Visibility.Visible;
                break;
            case "AddHeroXP":
            case "SetHeroLevel":
                PanelHero.Visibility = Visibility.Visible;
                break;
            case "SetUnitLife":
            case "SetUnitMana":
            case "DamageTarget":
                PanelUnitState.Visibility = Visibility.Visible;
                break;
            case "CreateSpecialEffect":
                PanelSpecialEffect.Visibility = Visibility.Visible;
                break;
            case "PlaySound":
                PanelSound.Visibility = Visibility.Visible;
                break;
            case "RunTrigger":
            case "TurnOffTrigger":
            case "TurnOnTrigger":
                PanelTriggerControl.Visibility = Visibility.Visible;
                break;
            case "CustomScript":
                PanelCustomScript.Visibility = Visibility.Visible;
                break;
            default:
                TxtSimpleActionInfo.Visibility = Visibility.Visible;
                break;
        }
    }

    private void UpdatePreview()
    {
        if (TxtPreview == null) return;

        var actionTag = (CboAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CreateUnitsAtPoint";
        var uType = !string.IsNullOrWhiteSpace(TxtCustomRawcode.Text)
            ? TxtCustomRawcode.Text.Trim()
            : ((CboUnitType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Footman");
        var pName = (CboCreatePlayer.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Player 1 (Red)";
        var loc = (CboLocation.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CboLocation.Text;
        var resPlayer = (CboResourcePlayer.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Player 1 (Red)";
        var eff = !string.IsNullOrWhiteSpace(TxtCustomEffectPath.Text)
            ? TxtCustomEffectPath.Text.Trim()
            : (CboEffectPath.SelectedItem as ComboBoxItem)?.Content?.ToString();

        var preview = actionTag switch
        {
            "CreateUnitsAtPoint" => $"Unit - Create {TxtUnitCount.Text.Trim()} {uType} for {pName} at ({loc}) facing {TxtAngle.Text.Trim()} degrees",
            "MoveUnitInstant" => $"Unit - Move (Triggering unit) instantly to ({CboTargetLocation.Text.Trim()})",
            "SetUnitFacing" => $"Unit - Make (Triggering unit) face {TxtSingleParamValue.Text.Trim()} degrees",
            "PauseUnit" => $"Unit - {(ChkUnitFlagValue.IsChecked == true ? "Pause" : "Unpause")} (Triggering unit)",
            "ShowHideUnit" => $"Unit - {(ChkUnitFlagValue.IsChecked == true ? "Show" : "Hide")} (Triggering unit)",
            "IssueOrderNoTarget" => $"Unit - Order (Triggering unit) to \"{TxtSingleParamValue.Text.Trim()}\"",
            "UnitAddAbility" => $"Unit - Add '{TxtSingleParamValue.Text.Trim()}' to (Triggering unit)",
            "UnitRemoveAbility" => $"Unit - Remove '{TxtSingleParamValue.Text.Trim()}' from (Triggering unit)",
            "KillUnit" => "Unit - Kill (Triggering unit)",
            "RemoveUnit" => "Unit - Remove (Triggering unit) from the game",
            "SetUnitLife" => $"Unit - Set life of (Triggering unit) to {TxtUnitPercent.Text.Trim()}%",
            "SetUnitMana" => $"Unit - Set mana of (Triggering unit) to {TxtUnitPercent.Text.Trim()}%",
            "DamageTarget" => $"Unit - Cause (Triggering unit) to damage (Target unit of ability being cast) for {TxtUnitPercent.Text.Trim()}",
            "AddHeroXP" => $"Hero - Add {TxtHeroValue.Text.Trim()} experience to (Triggering unit)",
            "SetHeroLevel" => $"Hero - Set (Triggering unit) Hero-level to {TxtHeroValue.Text.Trim()}",
            "HeroGiveItem" => $"Hero - Create '{TxtSingleParamValue.Text.Trim()}' and give it to (Triggering unit)",
            "ReviveHero" => $"Hero - Instantly revive (Triggering unit) at ({CboTargetLocation.Text.Trim()})",
            "AddPlayerGold" => $"Player - Add {TxtResourceAmount.Text.Trim()} to {resPlayer} Current gold",
            "AddPlayerLumber" => $"Player - Add {TxtResourceAmount.Text.Trim()} to {resPlayer} Current lumber",
            "CustomVictory" => "Game - Victory for Player (Show dialogs, Show scores)",
            "CustomDefeat" => "Game - Defeat for Player with the message: Defeat!",
            "SetVariable" => $"Set {CboVarName.Text.Trim()} = {TxtVarValue.Text.Trim()}",
            "WaitSeconds" => $"Wait {TxtWaitSeconds.Text.Trim()} game-time seconds",
            "TurnOffTrigger" => $"Trigger - Turn off {(string.IsNullOrWhiteSpace(TxtTargetTrigger.Text) ? "(This trigger)" : TxtTargetTrigger.Text.Trim())}",
            "TurnOnTrigger" => $"Trigger - Turn on {TxtTargetTrigger.Text.Trim()}",
            "RunTrigger" => $"Trigger - Run {TxtTargetTrigger.Text.Trim()} (checking conditions)",
            "DisplayTimedText" => $"Game - Display to (All players) for {TxtMessageDuration.Text.Trim()}s: {TxtGameMessage.Text.Trim()}",
            "DisplayTimedTextPlayer" => $"Game - Display to Player for {TxtMessageDuration.Text.Trim()}s: {TxtGameMessage.Text.Trim()}",
            "CreateFloatingText" => $"Floating Text - Create floating text above (Triggering unit): {TxtGameMessage.Text.Trim()}",
            "ClearTextMessages" => "Game - Clear text messages",
            "CreateSpecialEffect" => $"Special Effect - Create special effect using {eff}",
            "DestroyLastCreatedEffect" => "Special Effect - Destroy (Last created special effect)",
            "PlaySound" => $"Sound - Play {TxtSoundName.Text.Trim()}",
            "MeleeStartingVisibility" => "Partida Melee - Usar ciclo de día/noche melee (para todos los jugadores)",
            "MeleeStartingHeroLimit" => "Partida Melee - Limitar Héroes a 1 por tipo de Héroe (para todos los jugadores)",
            "MeleeGrantHeroItems" => "Partida Melee - Entregar pergamino de teletransporte inicial a Héroes (para todos los jugadores)",
            "MeleeStartingResources" => "Partida Melee - Establecer recursos iniciales (para todos los jugadores)",
            "MeleeClearExcessUnits" => "Partida Melee - Eliminar creeps y criaturas cerca de posiciones iniciales (para todos los jugadores)",
            "MeleeStartingUnits" => "Partida Melee - Crear unidades iniciales (para todos los jugadores)",
            "MeleeStartingAI" => "Partida Melee - Ejecutar scripts de IA melee (para jugadores de computadora)",
            "MeleeInitVictoryDefeat" => "Partida Melee - Imponer condiciones de victoria/derrota melee (para todos los jugadores)",
            "CustomScript" => $"Custom script: {TxtCustomCode.Text.Trim()}",
            _ => "Do nothing"
        };

        TxtPreview.Text = preview;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var actionTag = (CboAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CreateUnitsAtPoint";
        var cat = (CboCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Unit";

        ResultElement.ElementType = GuiTriggerElementType.Action;
        ResultElement.Category = cat;
        ResultElement.FunctionName = actionTag;
        ResultElement.DisplayText = TxtPreview.Text;

        ResultElement.Parameters.Clear();
        switch (actionTag)
        {
            case "CreateUnitsAtPoint":
                ResultElement.Parameters["Count"] = TxtUnitCount.Text.Trim();
                ResultElement.Parameters["UnitType"] = !string.IsNullOrWhiteSpace(TxtCustomRawcode.Text)
                    ? TxtCustomRawcode.Text.Trim()
                    : ((CboUnitType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "hfoo");
                ResultElement.Parameters["PlayerId"] = (CboCreatePlayer.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
                ResultElement.Parameters["Location"] = (CboLocation.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? CboLocation.Text.Trim();
                ResultElement.Parameters["Angle"] = TxtAngle.Text.Trim();
                break;
            case "MoveUnitInstant":
            case "ReviveHero":
                ResultElement.Parameters["Location"] = CboTargetLocation.Text.Trim();
                break;
            case "SetUnitFacing":
                ResultElement.Parameters["Angle"] = TxtSingleParamValue.Text.Trim();
                break;
            case "PauseUnit":
                ResultElement.Parameters["Paused"] = (ChkUnitFlagValue.IsChecked == true).ToString().ToLower();
                break;
            case "ShowHideUnit":
                ResultElement.Parameters["Show"] = (ChkUnitFlagValue.IsChecked == true).ToString().ToLower();
                break;
            case "IssueOrderNoTarget":
                ResultElement.Parameters["Order"] = TxtSingleParamValue.Text.Trim();
                break;
            case "UnitAddAbility":
            case "UnitRemoveAbility":
                ResultElement.Parameters["AbilityId"] = TxtSingleParamValue.Text.Trim();
                break;
            case "HeroGiveItem":
                ResultElement.Parameters["ItemType"] = TxtSingleParamValue.Text.Trim();
                break;
            case "SetVariable":
                ResultElement.Parameters["VarName"] = CboVarName.Text.Trim();
                ResultElement.Parameters["Value"] = TxtVarValue.Text.Trim();
                break;
            case "WaitSeconds":
                ResultElement.Parameters["Seconds"] = TxtWaitSeconds.Text.Trim();
                break;
            case "DisplayTimedText":
            case "DisplayTimedTextPlayer":
                ResultElement.Parameters["Text"] = TxtGameMessage.Text.Trim();
                ResultElement.Parameters["Duration"] = TxtMessageDuration.Text.Trim();
                ResultElement.Parameters["PlayerId"] = (CboMessageTarget.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
                break;
            case "CreateFloatingText":
                ResultElement.Parameters["Text"] = TxtGameMessage.Text.Trim();
                break;
            case "AddPlayerGold":
            case "AddPlayerLumber":
                ResultElement.Parameters["Amount"] = TxtResourceAmount.Text.Trim();
                ResultElement.Parameters["PlayerId"] = (CboResourcePlayer.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
                break;
            case "AddHeroXP":
            case "SetHeroLevel":
                ResultElement.Parameters["Amount"] = TxtHeroValue.Text.Trim();
                ResultElement.Parameters["Level"] = TxtHeroValue.Text.Trim();
                break;
            case "SetUnitLife":
            case "SetUnitMana":
            case "DamageTarget":
                ResultElement.Parameters["Percent"] = TxtUnitPercent.Text.Trim();
                ResultElement.Parameters["Damage"] = TxtUnitPercent.Text.Trim();
                break;
            case "CreateSpecialEffect":
                var eff = !string.IsNullOrWhiteSpace(TxtCustomEffectPath.Text)
                    ? TxtCustomEffectPath.Text.Trim()
                    : (CboEffectPath.SelectedItem as ComboBoxItem)?.Content?.ToString();
                ResultElement.Parameters["ModelPath"] = eff ?? string.Empty;
                ResultElement.Parameters["Location"] = "GetRectCenter(GetPlayableMapRect())";
                break;
            case "PlaySound":
                ResultElement.Parameters["SoundName"] = TxtSoundName.Text.Trim();
                break;
            case "RunTrigger":
            case "TurnOffTrigger":
            case "TurnOnTrigger":
                ResultElement.Parameters["TriggerName"] = TxtTargetTrigger.Text.Trim();
                break;
            case "CustomScript":
                ResultElement.Parameters["Code"] = TxtCustomCode.Text.Trim();
                break;
        }

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
