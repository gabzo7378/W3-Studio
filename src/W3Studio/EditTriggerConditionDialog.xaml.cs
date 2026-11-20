using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Models;

namespace W3Studio;

public partial class EditTriggerConditionDialog : Window
{
    private bool _isLoadingExisting;
    public GuiTriggerElement ResultElement { get; private set; }

    public EditTriggerConditionDialog(GuiTriggerElement? existing = null, MapProject? project = null)
    {
        InitializeComponent();

        PopulateRegions(project);
        PopulateProjectUnits(project);

        ResultElement = existing?.Clone() ?? new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Condition,
            Category = "Unit",
            FunctionName = "UnitTypeEqual",
            DisplayText = "(Unit-type of (Triggering unit)) Equal to Footman"
        };

        _isLoadingExisting = true;
        LoadExistingData();
        _isLoadingExisting = false;
        UpdateUiPanels();
        UpdatePreview();
    }

    private void PopulateProjectUnits(MapProject? project)
    {
        if (project == null) return;
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
    }

    private void PopulateRegions(MapProject? project)
    {
        CboCondRegion.Items.Clear();
        CboCondRegion.Items.Add("EntireMap");
        if (project != null)
        {
            foreach (var reg in project.Regions)
            {
                var clean = Regex.Replace(reg.Name ?? string.Empty, @"[^A-Za-z0-9_]", "_");
                if (!string.IsNullOrWhiteSpace(clean))
                    CboCondRegion.Items.Add(clean);
            }
        }
        CboCondRegion.Text = "EntireMap";
    }

    private void LoadExistingData()
    {
        if (string.IsNullOrEmpty(ResultElement.FunctionName))
            return;

        bool matched = false;
        foreach (ComboBoxItem item in CboConditionType.Items)
        {
            if (string.Equals(item.Tag?.ToString(), ResultElement.FunctionName, StringComparison.OrdinalIgnoreCase))
            {
                CboConditionType.SelectedItem = item;
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            foreach (ComboBoxItem item in CboConditionType.Items)
            {
                if (string.Equals(item.Tag?.ToString(), "CustomScript", StringComparison.OrdinalIgnoreCase))
                {
                    CboConditionType.SelectedItem = item;
                    break;
                }
            }
        }

        var p = ResultElement.Parameters;
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

        if (p.TryGetValue("AbilityId", out var ab)) TxtRawcodeParam.Text = ab;
        else if (p.TryGetValue("ItemType", out var it)) TxtRawcodeParam.Text = it;
        else if (p.TryGetValue("BuffId", out var bf)) TxtRawcodeParam.Text = bf;

        if (p.TryGetValue("RegionName", out var reg)) CboCondRegion.Text = reg;

        if (p.TryGetValue("PlayerId", out var pid))
        {
            foreach (ComboBoxItem item in CboPlayer.Items)
            {
                if (string.Equals(item.Tag?.ToString(), pid, StringComparison.OrdinalIgnoreCase))
                {
                    CboPlayer.SelectedItem = item;
                    break;
                }
            }
        }

        if (p.TryGetValue("Var1", out var v1)) TxtIntVal1.Text = v1;
        if (p.TryGetValue("Var2", out var v2)) TxtIntVal2.Text = v2;
        if (p.TryGetValue("Level", out var lvl))
        {
            TxtIntVal1.Text = "Hero level of (Triggering unit)";
            TxtIntVal2.Text = lvl;
        }
        if (p.TryGetValue("Op", out var op))
        {
            foreach (ComboBoxItem item in CboIntOp.Items)
            {
                if (item.Tag?.ToString() == op)
                {
                    CboIntOp.SelectedItem = item;
                    break;
                }
            }
        }

        if (p.TryGetValue("Text", out var txt)) TxtStringValue.Text = txt;
        if (p.TryGetValue("Script", out var sc)) TxtCustomScript.Text = sc;
        else if (p.TryGetValue("RawJass", out var rawSc)) TxtCustomScript.Text = rawSc;
    }

    private void CboConditionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingExisting || CboConditionType == null) return;
        var selectedTag = (CboConditionType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (selectedTag == "AbilityEqual") TxtRawcodeParam.Text = "AHtb";
        else if (selectedTag == "ItemTypeEqual") TxtRawcodeParam.Text = "phea";
        else if (selectedTag == "UnitHasBuff") TxtRawcodeParam.Text = "BHab";
        else if (selectedTag == "RealComparison" && TxtIntVal1.Text == "udg_Count")
        {
            TxtIntVal1.Text = "GetWidgetLife(GetTriggerUnit())";
            TxtIntVal2.Text = "100.0";
        }
        else if (selectedTag == "HeroLevelComparison")
        {
            TxtIntVal1.Text = "Hero level of (Triggering unit)";
            TxtIntVal2.Text = "5";
        }

        UpdateUiPanels();
        UpdatePreview();
    }

    private void ParamChanged(object sender, RoutedEventArgs e) => UpdatePreview();
    private void TxtCustomRawcode_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdateUiPanels()
    {
        if (PanelUnitType == null || PanelPlayer == null || PanelInteger == null || PanelBoolean == null || PanelCustomScript == null)
            return;

        PanelUnitType.Visibility = Visibility.Collapsed;
        PanelRawcode.Visibility = Visibility.Collapsed;
        PanelCondRegion.Visibility = Visibility.Collapsed;
        PanelPlayer.Visibility = Visibility.Collapsed;
        PanelInteger.Visibility = Visibility.Collapsed;
        PanelStringComp.Visibility = Visibility.Collapsed;
        PanelBoolean.Visibility = Visibility.Collapsed;
        PanelCustomScript.Visibility = Visibility.Collapsed;

        var selectedTag = (CboConditionType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "UnitTypeEqual";
        switch (selectedTag)
        {
            case "UnitTypeEqual":
                PanelUnitType.Visibility = Visibility.Visible;
                break;
            case "AbilityEqual":
                TxtRawcodeLabel.Text = "Rawcode de la Habilidad lanzada (ej: 'AHtb', 'AHfs'):";
                PanelRawcode.Visibility = Visibility.Visible;
                break;
            case "ItemTypeEqual":
                TxtRawcodeLabel.Text = "Rawcode del Objeto manipulado (ej: 'phea', 'ckng'):";
                PanelRawcode.Visibility = Visibility.Visible;
                break;
            case "UnitHasBuff":
                TxtRawcodeLabel.Text = "Rawcode del Buff/Aura (ej: 'BHab', 'BHds'):";
                PanelRawcode.Visibility = Visibility.Visible;
                break;
            case "UnitInRegion":
                PanelCondRegion.Visibility = Visibility.Visible;
                break;
            case "PlayerEqual":
                PanelPlayer.Visibility = Visibility.Visible;
                break;
            case "IntegerComparison":
                TxtCompLeftLabel.Text = "Variable / Expresión entera:";
                TxtCompRightLabel.Text = "Valor entero 2:";
                PanelInteger.Visibility = Visibility.Visible;
                break;
            case "RealComparison":
                TxtCompLeftLabel.Text = "Expresión Real (ej: GetWidgetLife(GetTriggerUnit())):";
                TxtCompRightLabel.Text = "Valor Real 2:";
                PanelInteger.Visibility = Visibility.Visible;
                break;
            case "HeroLevelComparison":
                TxtCompLeftLabel.Text = "Objetivo (solo lectura):";
                TxtCompRightLabel.Text = "Nivel de Héroe:";
                PanelInteger.Visibility = Visibility.Visible;
                break;
            case "StringComparison":
                PanelStringComp.Visibility = Visibility.Visible;
                break;
            case "BooleanComparison":
                PanelBoolean.Visibility = Visibility.Visible;
                break;
            case "CustomScript":
                PanelCustomScript.Visibility = Visibility.Visible;
                break;
        }
    }

    private void UpdatePreview()
    {
        if (TxtPreview == null) return;

        var selectedTag = (CboConditionType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "UnitTypeEqual";
        var op = (CboIntOp.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "==";
        string preview;

        switch (selectedTag)
        {
            case "UnitTypeEqual":
                var raw = !string.IsNullOrWhiteSpace(TxtCustomRawcode.Text)
                    ? TxtCustomRawcode.Text.Trim()
                    : ((CboUnitType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Footman");
                preview = $"(Unit-type of (Triggering unit)) Equal to {raw}";
                break;
            case "AbilityEqual":
                preview = $"(Ability being cast) Equal to '{TxtRawcodeParam.Text.Trim()}'";
                break;
            case "ItemTypeEqual":
                preview = $"(Item-type of (Item being manipulated)) Equal to '{TxtRawcodeParam.Text.Trim()}'";
                break;
            case "UnitHasBuff":
                preview = $"((Triggering unit) has buff '{TxtRawcodeParam.Text.Trim()}') Equal to True";
                break;
            case "UnitInRegion":
                preview = $"({CboCondRegion.Text?.Trim()} <gen> contains (Triggering unit)) Equal to True";
                break;
            case "PlayerEqual":
                var pName = (CboPlayer.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Player 1 (Red)";
                preview = $"(Owner of (Triggering unit)) Equal to {pName}";
                break;
            case "HeroLevelComparison":
                preview = $"(Hero level of (Triggering unit)) {op} {TxtIntVal2.Text.Trim()}";
                break;
            case "IntegerComparison":
            case "RealComparison":
                preview = $"{TxtIntVal1.Text.Trim()} {op} {TxtIntVal2.Text.Trim()}";
                break;
            case "StringComparison":
                preview = $"(Entered chat string) Equal to \"{TxtStringValue.Text.Trim()}\"";
                break;
            case "BooleanComparison":
                preview = (CboBoolExpr.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "((Triggering unit) is alive) Equal to True";
                break;
            case "CustomScript":
                preview = $"Custom script: {TxtCustomScript.Text.Trim()}";
                break;
            default:
                preview = "(Unit-type of (Triggering unit)) Equal to Footman";
                break;
        }

        TxtPreview.Text = preview;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var selectedItem = CboConditionType.SelectedItem as ComboBoxItem;
        var tag = selectedItem?.Tag?.ToString() ?? "UnitTypeEqual";

        ResultElement.ElementType = GuiTriggerElementType.Condition;
        ResultElement.FunctionName = tag;
        ResultElement.DisplayText = TxtPreview.Text;

        ResultElement.Parameters.Clear();
        switch (tag)
        {
            case "UnitTypeEqual":
                ResultElement.Category = "Unit";
                ResultElement.Parameters["UnitType"] = !string.IsNullOrWhiteSpace(TxtCustomRawcode.Text)
                    ? TxtCustomRawcode.Text.Trim()
                    : ((CboUnitType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "hfoo");
                break;
            case "AbilityEqual":
                ResultElement.Category = "Ability";
                ResultElement.Parameters["AbilityId"] = TxtRawcodeParam.Text.Trim();
                break;
            case "ItemTypeEqual":
                ResultElement.Category = "Item";
                ResultElement.Parameters["ItemType"] = TxtRawcodeParam.Text.Trim();
                break;
            case "UnitHasBuff":
                ResultElement.Category = "Unit";
                ResultElement.Parameters["BuffId"] = TxtRawcodeParam.Text.Trim();
                break;
            case "UnitInRegion":
                ResultElement.Category = "Region";
                ResultElement.Parameters["RegionName"] = CboCondRegion.Text?.Trim() ?? "EntireMap";
                break;
            case "PlayerEqual":
                ResultElement.Category = "Player";
                ResultElement.Parameters["PlayerId"] = (CboPlayer.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
                break;
            case "HeroLevelComparison":
                ResultElement.Category = "Hero";
                ResultElement.Parameters["Op"] = (CboIntOp.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ">=";
                ResultElement.Parameters["Level"] = TxtIntVal2.Text.Trim();
                break;
            case "IntegerComparison":
            case "RealComparison":
                ResultElement.Category = "General";
                ResultElement.Parameters["Var1"] = TxtIntVal1.Text.Trim();
                ResultElement.Parameters["Op"] = (CboIntOp.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "==";
                ResultElement.Parameters["Var2"] = TxtIntVal2.Text.Trim();
                break;
            case "StringComparison":
                ResultElement.Category = "General";
                ResultElement.Parameters["Op"] = "==";
                ResultElement.Parameters["Text"] = TxtStringValue.Text.Trim();
                break;
            case "BooleanComparison":
                ResultElement.Category = "General";
                var bTag = (CboBoolExpr.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "UnitAlive";
                var expr = bTag switch
                {
                    "UnitAlive" => "GetWidgetLife(GetTriggerUnit()) > 0.405",
                    "UnitDead" => "GetWidgetLife(GetTriggerUnit()) <= 0.405",
                    "UnitIsHero" => "IsUnitType(GetTriggerUnit(), UNIT_TYPE_HERO)",
                    "UnitIsBuilding" => "IsUnitType(GetTriggerUnit(), UNIT_TYPE_STRUCTURE)",
                    "UnitIsFlying" => "IsUnitType(GetTriggerUnit(), UNIT_TYPE_FLYING)",
                    "UnitEnemyP1" => "IsUnitEnemy(GetTriggerUnit(), Player(0))",
                    "PlayerIsUser" => "GetPlayerController(GetTriggerPlayer()) == MAP_CONTROL_USER",
                    _ => "true"
                };
                ResultElement.Parameters["Expr"] = expr;
                break;
            case "CustomScript":
                ResultElement.Category = "Custom";
                ResultElement.Parameters["Script"] = TxtCustomScript.Text.Trim();
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
