using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Models;

namespace W3Studio;

public partial class EditTriggerEventDialog : Window
{
    public GuiTriggerElement ResultElement { get; private set; }

    public EditTriggerEventDialog(GuiTriggerElement? existing = null, MapProject? project = null)
    {
        InitializeComponent();

        PopulateRegions(project);

        ResultElement = existing?.Clone() ?? new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Event,
            Category = "General",
            FunctionName = "MapInitialization",
            DisplayText = "Map initialization"
        };

        LoadExistingData();
        UpdateUiPanels();
        UpdatePreview();
    }

    private void PopulateRegions(MapProject? project)
    {
        CboRegionName.Items.Clear();
        CboRegionName.Items.Add("EntireMap");
        if (project != null)
        {
            foreach (var reg in project.Regions)
            {
                var clean = Regex.Replace(reg.Name ?? string.Empty, @"[^A-Za-z0-9_]", "_");
                if (!string.IsNullOrWhiteSpace(clean))
                    CboRegionName.Items.Add(clean);
            }
        }
        CboRegionName.Text = "EntireMap";
    }

    private void LoadExistingData()
    {
        if (string.IsNullOrEmpty(ResultElement.FunctionName))
            return;

        bool matched = false;
        foreach (ComboBoxItem item in CboEventType.Items)
        {
            if (string.Equals(item.Tag?.ToString(), ResultElement.FunctionName, StringComparison.OrdinalIgnoreCase))
            {
                CboEventType.SelectedItem = item;
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            foreach (ComboBoxItem item in CboEventType.Items)
            {
                if (string.Equals(item.Tag?.ToString(), "CustomEventScript", StringComparison.OrdinalIgnoreCase))
                {
                    CboEventType.SelectedItem = item;
                    break;
                }
            }
        }

        var p = ResultElement.Parameters;
        if (p.TryGetValue("Interval", out var interval))
            TxtSeconds.Text = interval;
        else if (p.TryGetValue("Seconds", out var sec))
            TxtSeconds.Text = sec;

        if (p.TryGetValue("ChatMessage", out var chat))
            TxtChatMessage.Text = chat;

        if (p.TryGetValue("ExactMatch", out var exact))
            ChkExactMatch.IsChecked = !string.Equals(exact, "false", StringComparison.OrdinalIgnoreCase);

        if (p.TryGetValue("PlayerId", out var pid))
        {
            foreach (ComboBoxItem item in CboChatPlayer.Items)
            {
                if (item.Tag?.ToString() == pid)
                {
                    CboChatPlayer.SelectedItem = item;
                    break;
                }
            }
        }

        if (p.TryGetValue("RegionName", out var reg))
            CboRegionName.Text = reg;

        if (p.TryGetValue("Code", out var code))
            TxtCustomEventCode.Text = code;
        else if (p.TryGetValue("RawJass", out var rawJass))
            TxtCustomEventCode.Text = rawJass;
    }

    private void CboEventType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboEventType == null) return;
        UpdateUiPanels();
        UpdatePreview();
    }

    private void ParamChanged(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdateUiPanels()
    {
        if (PanelTime == null || PanelChat == null || PanelRegion == null || PanelCustomEvent == null || TxtNoParams == null)
            return;

        PanelTime.Visibility = Visibility.Collapsed;
        PanelChat.Visibility = Visibility.Collapsed;
        PanelChatMessageFields.Visibility = Visibility.Visible;
        PanelRegion.Visibility = Visibility.Collapsed;
        PanelCustomEvent.Visibility = Visibility.Collapsed;
        TxtNoParams.Visibility = Visibility.Collapsed;

        var selectedTag = (CboEventType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "MapInitialization";

        switch (selectedTag)
        {
            case "PeriodicTimer":
            case "ElapsedTime":
                PanelTime.Visibility = Visibility.Visible;
                break;
            case "PlayerChat":
                PanelChat.Visibility = Visibility.Visible;
                PanelChatMessageFields.Visibility = Visibility.Visible;
                break;
            case "PlayerLeavesGame":
                PanelChat.Visibility = Visibility.Visible;
                PanelChatMessageFields.Visibility = Visibility.Collapsed;
                break;
            case "UnitEntersRegion":
            case "UnitLeavesRegion":
                PanelRegion.Visibility = Visibility.Visible;
                break;
            case "CustomEventScript":
                PanelCustomEvent.Visibility = Visibility.Visible;
                break;
            default:
                TxtNoParams.Visibility = Visibility.Visible;
                break;
        }
    }

    private void UpdatePreview()
    {
        if (TxtPreview == null)
            return;

        var selectedTag = (CboEventType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "MapInitialization";
        var pName = (CboChatPlayer.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Player 1 (Red)";
        var regName = CboRegionName.Text?.Trim() ?? "EntireMap";
        var dispRegion = string.Equals(regName, "EntireMap", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(regName)
            ? "(Playable map area)"
            : $"{regName} <gen>";

        var preview = selectedTag switch
        {
            "MapInitialization" => "Map initialization",
            "PeriodicTimer" => $"Time - Every {TxtSeconds.Text.Trim()} seconds of game time",
            "ElapsedTime" => $"Time - Elapsed game time is {TxtSeconds.Text.Trim()} seconds",
            "PlayerChat" => $"Player - {pName} types a chat message containing \"{TxtChatMessage.Text.Trim()}\" as {(ChkExactMatch.IsChecked == true ? "an exact match" : "a substring")}",
            "PlayerLeavesGame" => $"Player - {pName} leaves the game",
            "UnitDies" => "Unit - A unit Dies",
            "UnitAttacked" => "Unit - A unit Is attacked",
            "UnitSpell" => "Unit - A unit Starts the effect of an ability",
            "UnitSpellChannel" => "Unit - A unit Begins channeling an ability",
            "UnitSpellFinish" => "Unit - A unit Finishes casting an ability",
            "UnitIssuedOrder" => "Unit - A unit Is issued an order with no target",
            "UnitIssuedPointOrder" => "Unit - A unit Is issued an order targeting a point",
            "UnitIssuedTargetOrder" => "Unit - A unit Is issued an order targeting an object",
            "UnitEntersRegion" => $"Unit - A unit enters {dispRegion}",
            "UnitLeavesRegion" => $"Unit - A unit leaves {dispRegion}",
            "HeroLevelUp" => "Hero - A hero Gains a level",
            "HeroLearnSkill" => "Hero - A hero Learns a skill",
            "ItemPickup" => "Item - A unit Acquires an item",
            "ItemUse" => "Item - A unit Uses an item",
            "ItemDrop" => "Item - A unit Loses an item",
            "CustomEventScript" => $"Custom event: {TxtCustomEventCode.Text.Trim()}",
            _ => "Map initialization"
        };

        TxtPreview.Text = preview;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var selectedItem = CboEventType.SelectedItem as ComboBoxItem;
        var tag = selectedItem?.Tag?.ToString() ?? "MapInitialization";

        ResultElement.ElementType = GuiTriggerElementType.Event;
        ResultElement.FunctionName = tag;
        ResultElement.DisplayText = TxtPreview.Text;

        ResultElement.Parameters.Clear();
        switch (tag)
        {
            case "PeriodicTimer":
                ResultElement.Category = "Time";
                ResultElement.Parameters["Interval"] = TxtSeconds.Text.Trim();
                break;
            case "ElapsedTime":
                ResultElement.Category = "Time";
                ResultElement.Parameters["Seconds"] = TxtSeconds.Text.Trim();
                break;
            case "PlayerChat":
                ResultElement.Category = "Player";
                ResultElement.Parameters["PlayerId"] = (CboChatPlayer.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
                ResultElement.Parameters["ChatMessage"] = TxtChatMessage.Text.Trim();
                ResultElement.Parameters["ExactMatch"] = (ChkExactMatch.IsChecked == true).ToString().ToLower();
                break;
            case "PlayerLeavesGame":
                ResultElement.Category = "Player";
                ResultElement.Parameters["PlayerId"] = (CboChatPlayer.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
                break;
            case "UnitEntersRegion":
            case "UnitLeavesRegion":
                ResultElement.Category = "Unit";
                ResultElement.Parameters["RegionName"] = CboRegionName.Text?.Trim() ?? "EntireMap";
                break;
            case "HeroLevelUp":
            case "HeroLearnSkill":
                ResultElement.Category = "Hero";
                break;
            case "ItemPickup":
            case "ItemUse":
            case "ItemDrop":
                ResultElement.Category = "Item";
                break;
            case "CustomEventScript":
                ResultElement.Category = "Custom";
                ResultElement.Parameters["Code"] = TxtCustomEventCode.Text.Trim();
                break;
            case "MapInitialization":
                ResultElement.Category = "Map";
                break;
            default:
                ResultElement.Category = "Unit";
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
