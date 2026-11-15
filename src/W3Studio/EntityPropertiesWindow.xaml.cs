using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class EntityPropertiesWindow : Window
{
    private sealed class WaygateRegionOption
    {
        public int RegionId { get; init; }
        public string Label { get; init; } = string.Empty;
        public override string ToString() => Label;
    }

    private sealed class AbilityDisplayItem
    {
        public ImageSource? Icon { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
    }

    private sealed class DropSetViewModel
    {
        public int Index { get; init; }
        public List<W3UnitItemDrop> Items { get; init; } = new();
        public override string ToString() => $"Set {Index + 1} ({Items.Count} ítems)";
    }

    private sealed class DropEntryViewModel
    {
        public W3UnitItemDrop Drop { get; init; } = null!;
        public string Display { get; init; } = string.Empty;
        public override string ToString() => Display;
    }

    private readonly MapProject _project;
    private readonly WarcraftBaseDataService? _baseData;
    private readonly BlpIconService? _blpService;
    private readonly object _entity;
    private readonly List<List<W3UnitItemDrop>> _workingSets = new();

    private EntityArchetypeInfo? _archetype;
    private int _originalUnitHp = -1;
    private int _originalUnitMp = -1;
    private bool _isInitializing = true;

    public bool ChangesApplied { get; private set; }

    public EntityPropertiesWindow(
        MapProject project,
        WarcraftBaseDataService? baseData,
        object entity,
        IEnumerable<PlayerOwnerOption> owners,
        BlpIconService? blpService = null)
    {
        InitializeComponent();
        _project = project;
        _baseData = baseData;
        _blpService = blpService;
        _entity = entity;

        CmbOwner.ItemsSource = owners.ToList();
        PopulateFromEntity();
        _isInitializing = false;
    }

    private void PopulateFromEntity()
    {
        if (_entity is W3PlacedUnit u)
        {
            _archetype = EntityArchetypeHelper.Analyze(u, _project, _baseData);
            _originalUnitHp = u.HP;
            _originalUnitMp = u.MP;

            var displayName = !string.IsNullOrWhiteSpace(u.Name)
                ? u.Name
                : (_archetype.BaseMetadata?.Name ?? u.Rawcode);

            Title = _archetype.IsItem ? "Propiedades de Objeto" : "Propiedades de Unidad";
            TxtEntityTitle.Text = _archetype.IsItem ? displayName : $"{displayName} ({u.CreationNumber})";
            TxtEntitySubtitle.Text = $"{u.JassVariableName}  |  ID: {u.Rawcode}";

            if (_archetype.HeaderLevel > 0)
            {
                TxtEntityLevel.Text = $"Nivel: {_archetype.HeaderLevel}";
                TxtEntityLevel.Visibility = Visibility.Visible;
            }
            else
            {
                TxtEntityLevel.Visibility = Visibility.Collapsed;
            }

            if (_blpService != null && !string.IsNullOrWhiteSpace(_archetype.IconPath))
            {
                ImgEntityIcon.Source = _blpService.GetIcon(_archetype.IconPath, _project);
            }

            TxtPosX.Text = u.X.ToString("0.##", CultureInfo.InvariantCulture);
            TxtPosY.Text = u.Y.ToString("0.##", CultureInfo.InvariantCulture);
            TxtScale.Text = u.Scale.ToString("0.##", CultureInfo.InvariantCulture);
            TxtAngle.Text = u.AngleDegrees.ToString("0.##", CultureInfo.InvariantCulture);

            if (_archetype.IsItem)
            {
                TabGeneral.Visibility = Visibility.Collapsed;
                TabAbilities.Visibility = Visibility.Collapsed;
                TabInventory.Visibility = Visibility.Collapsed;
                TabItemDrops.Visibility = Visibility.Collapsed;
                TabDescription.Visibility = Visibility.Visible;

                TxtItemDescription.Text = string.IsNullOrWhiteSpace(_archetype.DescriptionText)
                    ? $"{displayName} (Sin descripción adicional disponible en los datos base del mapa)."
                    : _archetype.DescriptionText;

                MainTabs.SelectedItem = TabDescription;
                return;
            }

            // Unidad / Héroe / Edificio / Mina
            TabDescription.Visibility = Visibility.Collapsed;
            TabGeneral.Visibility = Visibility.Visible;
            TabAbilities.Visibility = Visibility.Visible;
            TabItemDrops.Visibility = Visibility.Visible;
            TabInventory.Visibility = _archetype.IsHero ? Visibility.Visible : Visibility.Collapsed;
            MainTabs.SelectedItem = TabGeneral;

            foreach (PlayerOwnerOption opt in CmbOwner.Items)
            {
                if (opt.PlayerId == u.PlayerId)
                {
                    CmbOwner.SelectedItem = opt;
                    break;
                }
            }

            PopulateAbilitiesTab();
            PopulateDropSets(u);

            PnlDoodadStats.Visibility = Visibility.Collapsed;
            PnlUnitOwner.Visibility = Visibility.Visible;

            if (_archetype.IsGoldMine)
            {
                PnlFacingRow.Visibility = Visibility.Collapsed;
                PnlGoldRow.Visibility = Visibility.Visible;
                PnlWaygateRow.Visibility = Visibility.Collapsed;
                GrdUnitStats.Visibility = Visibility.Collapsed;
                PnlTargetAcqRow.Visibility = Visibility.Collapsed;
                TxtGoldAmount.Text = u.GoldAmount.ToString(CultureInfo.InvariantCulture);
                return;
            }

            if (_archetype.IsBuilding && _archetype.IsInvulnerable)
            {
                PnlFacingRow.Visibility = Visibility.Collapsed;
                PnlGoldRow.Visibility = Visibility.Collapsed;
                GrdUnitStats.Visibility = Visibility.Collapsed;
                PnlTargetAcqRow.Visibility = Visibility.Collapsed;
                PopulateWaygateCombo(u);
                PnlWaygateRow.Visibility = _archetype.IsWaygate ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            PnlGoldRow.Visibility = Visibility.Collapsed;
            PopulateWaygateCombo(u);
            PnlWaygateRow.Visibility = _archetype.IsWaygate ? Visibility.Visible : Visibility.Collapsed;

            GrdUnitStats.Visibility = Visibility.Visible;
            PnlUnitHpRow.Visibility = Visibility.Visible;

            var displayedHp = u.HP < 0 ? 100 : u.HP;
            TxtUnitHp.Text = displayedHp.ToString(CultureInfo.InvariantCulture);
            TxtUnitHpOf.Text = $" de {_archetype.BaseHp}";

            var showMana = _archetype.IsHero || _archetype.BaseMp > 0 || u.MP >= 0;
            PnlUnitMpRow.Visibility = showMana ? Visibility.Visible : Visibility.Collapsed;
            if (showMana)
            {
                var displayedMp = u.MP < 0 ? _archetype.BaseMp : u.MP;
                TxtUnitMp.Text = displayedMp.ToString(CultureInfo.InvariantCulture);
                TxtUnitMpOf.Text = $" de {_archetype.BaseMp}";
            }

            if (_archetype.IsBuilding)
            {
                // Torre o edificio destructible con combate: sin Facing ni Adquisición ni Atributos
                PnlFacingRow.Visibility = Visibility.Collapsed;
                PnlHeroLevelRow.Visibility = Visibility.Collapsed;
                PnlHeroAttrsCol.Visibility = Visibility.Collapsed;
                PnlTargetAcqRow.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Unidad normal / Creep / Héroe
                PnlFacingRow.Visibility = Visibility.Visible;
                PnlTargetAcqRow.Visibility = Visibility.Visible;

                if (u.TargetAcquisition == -2f || Math.Abs(u.TargetAcquisition - 200f) < 0.01f)
                {
                    RadAcqCamp.IsChecked = true;
                    PnlAcqCustom.Visibility = Visibility.Collapsed;
                }
                else if (u.TargetAcquisition < 0f || Math.Abs(u.TargetAcquisition - 500f) < 0.01f)
                {
                    RadAcqNormal.IsChecked = true;
                    PnlAcqCustom.Visibility = Visibility.Collapsed;
                }
                else
                {
                    PnlAcqCustom.Visibility = Visibility.Visible;
                    RadAcqCustom.IsChecked = true;
                    TxtTargetAcqCustom.Text = u.TargetAcquisition.ToString("0.##", CultureInfo.InvariantCulture);
                }

                if (_archetype.IsHero)
                {
                    ChkUseDefaultAttrs.Visibility = Visibility.Visible;
                    PnlHeroLevelRow.Visibility = Visibility.Visible;
                    PnlHeroAttrsCol.Visibility = Visibility.Visible;

                    TxtHeroLevel.Text = Math.Max(1, u.Level).ToString(CultureInfo.InvariantCulture);
                    var useDefault = u.HeroStrength <= 0 && u.HeroAgility <= 0 && u.HeroIntelligence <= 0;
                    ChkUseDefaultAttrs.IsChecked = useDefault;

                    TxtHeroStr.Text = (useDefault ? _archetype.BaseStr : u.HeroStrength).ToString(CultureInfo.InvariantCulture);
                    TxtHeroAgi.Text = (useDefault ? _archetype.BaseAgi : u.HeroAgility).ToString(CultureInfo.InvariantCulture);
                    TxtHeroInt.Text = (useDefault ? _archetype.BaseInt : u.HeroIntelligence).ToString(CultureInfo.InvariantCulture);
                    UpdateHeroAttrsEnabledState(useDefault);
                }
                else
                {
                    ChkUseDefaultAttrs.Visibility = Visibility.Collapsed;
                    PnlHeroLevelRow.Visibility = Visibility.Collapsed;
                    PnlHeroAttrsCol.Visibility = Visibility.Collapsed;
                }
            }
        }
        else if (_entity is W3PlacedDoodad d)
        {
            Title = d.IsDestructible ? "Propiedades de Destructible" : "Propiedades de Decorado";
            TxtEntityTitle.Text = $"{(string.IsNullOrWhiteSpace(d.Name) ? d.Rawcode : d.Name)} ({d.CreationNumber})";
            TxtEntitySubtitle.Text = $"{(d.IsDestructible ? "Destructible" : "Doodad")}  |  ID: {d.Rawcode}";
            TxtEntityLevel.Visibility = Visibility.Collapsed;

            var baseMeta = _baseData?.GetBaseObject(d.Rawcode);
            if (_blpService != null && baseMeta != null && !string.IsNullOrWhiteSpace(baseMeta.IconPath))
            {
                ImgEntityIcon.Source = _blpService.GetIcon(baseMeta.IconPath, _project);
            }

            TabGeneral.Visibility = Visibility.Visible;
            TabAbilities.Visibility = Visibility.Collapsed;
            TabInventory.Visibility = Visibility.Collapsed;
            TabItemDrops.Visibility = Visibility.Collapsed;
            TabDescription.Visibility = Visibility.Collapsed;
            MainTabs.SelectedItem = TabGeneral;

            PnlUnitOwner.Visibility = Visibility.Collapsed;
            PnlFacingRow.Visibility = Visibility.Visible;
            ChkUseDefaultAttrs.Visibility = Visibility.Collapsed;
            PnlGoldRow.Visibility = Visibility.Collapsed;
            PnlWaygateRow.Visibility = Visibility.Collapsed;
            GrdUnitStats.Visibility = Visibility.Collapsed;
            PnlTargetAcqRow.Visibility = Visibility.Collapsed;
            PnlDoodadStats.Visibility = Visibility.Visible;
            PnlDoodadLifeRow.Visibility = d.IsDestructible ? Visibility.Visible : Visibility.Collapsed;

            TxtAngle.Text = d.AngleDegrees.ToString("0.##", CultureInfo.InvariantCulture);
            TxtDoodadLife.Text = d.Life.ToString(CultureInfo.InvariantCulture);
            TxtDoodadVariation.Text = d.Variation.ToString(CultureInfo.InvariantCulture);
            TxtPosX.Text = d.X.ToString("0.##", CultureInfo.InvariantCulture);
            TxtPosY.Text = d.Y.ToString("0.##", CultureInfo.InvariantCulture);
            TxtScale.Text = d.Scale.ToString("0.##", CultureInfo.InvariantCulture);
            ExpAdvancedPos.IsExpanded = true;
        }
    }

    private void PopulateWaygateCombo(W3PlacedUnit u)
    {
        var waygateOpts = new List<WaygateRegionOption>
        {
            new() { RegionId = -1, Label = "(Ninguna)" }
        };
        foreach (var reg in _project.Regions.OrderBy(r => r.CreationNumber))
        {
            waygateOpts.Add(new WaygateRegionOption
            {
                RegionId = reg.CreationNumber,
                Label = $"{reg.Name} (#{reg.CreationNumber})"
            });
        }
        CmbWaygateRegion.ItemsSource = waygateOpts;
        CmbWaygateRegion.SelectedItem = waygateOpts.FirstOrDefault(r => r.RegionId == u.WaygateDestinationRegionId) ?? waygateOpts[0];
    }

    private void PopulateAbilitiesTab()
    {
        var items = new List<AbilityDisplayItem>();
        if (_archetype != null)
        {
            foreach (var code in _archetype.AbilityRawcodes)
            {
                var customAbil = _project.Objects.FirstOrDefault(
                    o => string.Equals(o.Rawcode, code, StringComparison.OrdinalIgnoreCase));
                var baseAbil = _baseData?.GetBaseObject(code);
                var name = customAbil?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = baseAbil?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = code;

                var iconPath = customAbil?.IconPath;
                if (string.IsNullOrWhiteSpace(iconPath))
                    iconPath = baseAbil?.IconPath;

                items.Add(new AbilityDisplayItem
                {
                    Icon = _blpService != null && !string.IsNullOrWhiteSpace(iconPath)
                        ? _blpService.GetIcon(iconPath, _project)
                        : null,
                    Name = name!,
                    Subtitle = $"Rawcode: {code}"
                });
            }
        }

        LstAbilities.ItemsSource = items;
        TxtNoAbilities.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PopulateDropSets(W3PlacedUnit u)
    {
        _workingSets.Clear();
        foreach (var set in u.ItemDropSets)
        {
            _workingSets.Add(set.Select(i => new W3UnitItemDrop { ItemRawcode = i.ItemRawcode, Chance = i.Chance }).ToList());
        }
        RefreshItemSetsList();
    }

    private void UpdateHeroAttrsEnabledState(bool useDefault)
    {
        var editable = !useDefault;
        TxtHeroStr.IsEnabled = editable;
        TxtHeroAgi.IsEnabled = editable;
        TxtHeroInt.IsEnabled = editable;

        if (!_isInitializing && useDefault && _archetype != null)
        {
            TxtHeroStr.Text = _archetype.BaseStr.ToString(CultureInfo.InvariantCulture);
            TxtHeroAgi.Text = _archetype.BaseAgi.ToString(CultureInfo.InvariantCulture);
            TxtHeroInt.Text = _archetype.BaseInt.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void ChkUseDefaultAttrs_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateHeroAttrsEnabledState(ChkUseDefaultAttrs.IsChecked == true);
    }

    private void RadAcq_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (TxtTargetAcqCustom != null)
        {
            TxtTargetAcqCustom.IsEnabled = RadAcqCustom.IsChecked == true;
        }
    }

    private void RefreshItemSetsList()
    {
        var prevIdx = LstItemSets.SelectedIndex;
        var vms = _workingSets.Select((s, i) => new DropSetViewModel { Index = i, Items = s }).ToList();
        LstItemSets.ItemsSource = vms;
        if (vms.Count > 0)
        {
            LstItemSets.SelectedIndex = prevIdx >= 0 && prevIdx < vms.Count ? prevIdx : 0;
        }
        else
        {
            PnlSelectedSet.IsEnabled = false;
            LstSetItems.ItemsSource = null;
        }
    }

    private void RefreshSetItemsList()
    {
        if (LstItemSets.SelectedItem is DropSetViewModel vm)
        {
            PnlSelectedSet.IsEnabled = true;
            TxtSetTitle.Text = $"Contenido de Set {vm.Index + 1} (Probabilidad total: {vm.Items.Sum(i => i.Chance)}%)";
            LstSetItems.ItemsSource = vm.Items.Select(drop =>
            {
                var itemMeta = _baseData?.GetBaseObject(drop.ItemRawcode);
                var name = itemMeta?.Name ?? drop.ItemRawcode;
                return new DropEntryViewModel
                {
                    Drop = drop,
                    Display = $"[{drop.ItemRawcode}] {name} — {drop.Chance}%"
                };
            }).ToList();
        }
        else
        {
            PnlSelectedSet.IsEnabled = false;
            LstSetItems.ItemsSource = null;
        }
    }

    private void LstItemSets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSetItemsList();
    }

    private void BtnAddItemSet_Click(object sender, RoutedEventArgs e)
    {
        _workingSets.Add(new List<W3UnitItemDrop>());
        RefreshItemSetsList();
        LstItemSets.SelectedIndex = _workingSets.Count - 1;
    }

    private void BtnRemoveItemSet_Click(object sender, RoutedEventArgs e)
    {
        if (LstItemSets.SelectedItem is DropSetViewModel vm && vm.Index >= 0 && vm.Index < _workingSets.Count)
        {
            _workingSets.RemoveAt(vm.Index);
            RefreshItemSetsList();
        }
    }

    private void BtnClearAllSets_Click(object sender, RoutedEventArgs e)
    {
        _workingSets.Clear();
        RefreshItemSetsList();
    }

    private void BtnAddItemToSet_Click(object sender, RoutedEventArgs e)
    {
        if (LstItemSets.SelectedItem is not DropSetViewModel vm) return;
        var rawcode = TxtItemId.Text.Trim();
        if (rawcode.Length != 4)
        {
            MessageBox.Show("El Rawcode del ítem debe tener exactamente 4 caracteres (ej. 'rat6', 'phea').", "Rawcode inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(TxtItemChance.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chance))
        {
            chance = 100;
        }
        chance = Math.Clamp(chance, 0, 100);
        vm.Items.Add(new W3UnitItemDrop { ItemRawcode = rawcode, Chance = chance });
        RefreshItemSetsList();
        RefreshSetItemsList();
    }

    private void BtnRemoveItemFromSet_Click(object sender, RoutedEventArgs e)
    {
        if (LstItemSets.SelectedItem is not DropSetViewModel vm || LstSetItems.SelectedItem is not DropEntryViewModel entry) return;
        vm.Items.Remove(entry.Drop);
        RefreshItemSetsList();
        RefreshSetItemsList();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var inv = CultureInfo.InvariantCulture;

        if (_entity is W3PlacedUnit u)
        {
            if (_archetype?.IsItem == true)
            {
                ChangesApplied = false;
                DialogResult = true;
                return;
            }

            if (CmbOwner.SelectedItem is PlayerOwnerOption owner)
            {
                u.PlayerId = owner.PlayerId;
            }

            if (float.TryParse(TxtPosX.Text.Replace(',', '.'), NumberStyles.Float, inv, out var px)) u.X = px;
            if (float.TryParse(TxtPosY.Text.Replace(',', '.'), NumberStyles.Float, inv, out var py)) u.Y = py;
            if (float.TryParse(TxtScale.Text.Replace(',', '.'), NumberStyles.Float, inv, out var sc) && sc > 0)
            {
                u.Scale = sc;
                u.ScaleY = sc;
                u.ScaleZ = sc;
            }

            if (_archetype?.IsGoldMine == true)
            {
                if (int.TryParse(TxtGoldAmount.Text.Trim(), NumberStyles.Integer, inv, out var gold))
                    u.GoldAmount = Math.Max(0, gold);
            }
            else if (!(_archetype?.IsBuilding == true && _archetype?.IsInvulnerable == true))
            {
                if (PnlUnitHpRow.Visibility == Visibility.Visible &&
                    int.TryParse(TxtUnitHp.Text.Trim(), NumberStyles.Integer, inv, out var hp))
                {
                    u.HP = (_originalUnitHp < 0 && hp == 100) ? -1 : Math.Clamp(hp, 1, 10000);
                }

                if (PnlUnitMpRow.Visibility == Visibility.Visible &&
                    int.TryParse(TxtUnitMp.Text.Trim(), NumberStyles.Integer, inv, out var mp))
                {
                    u.MP = (_originalUnitMp < 0 && _archetype != null && mp == _archetype.BaseMp) ? -1 : Math.Max(0, mp);
                }

                if (_archetype?.IsBuilding != true)
                {
                    if (double.TryParse(TxtAngle.Text.Replace(',', '.'), NumberStyles.Float, inv, out var ang))
                        u.AngleDegrees = ang;

                    if (RadAcqCamp.IsChecked == true)
                        u.TargetAcquisition = -2f;
                    else if (RadAcqCustom.IsChecked == true &&
                             float.TryParse(TxtTargetAcqCustom.Text.Replace(',', '.'), NumberStyles.Float, inv, out var acq))
                        u.TargetAcquisition = acq;
                    else
                        u.TargetAcquisition = -1f;

                    if (_archetype?.IsHero == true)
                    {
                        if (int.TryParse(TxtHeroLevel.Text.Trim(), NumberStyles.Integer, inv, out var lvl))
                            u.Level = Math.Clamp(lvl, 1, 100);

                        if (ChkUseDefaultAttrs.IsChecked == true)
                        {
                            u.HeroStrength = 0;
                            u.HeroAgility = 0;
                            u.HeroIntelligence = 0;
                        }
                        else
                        {
                            if (int.TryParse(TxtHeroStr.Text.Trim(), NumberStyles.Integer, inv, out var hstr)) u.HeroStrength = Math.Max(0, hstr);
                            if (int.TryParse(TxtHeroAgi.Text.Trim(), NumberStyles.Integer, inv, out var hagi)) u.HeroAgility = Math.Max(0, hagi);
                            if (int.TryParse(TxtHeroInt.Text.Trim(), NumberStyles.Integer, inv, out var hint)) u.HeroIntelligence = Math.Max(0, hint);
                        }
                    }
                }
            }

            if (_archetype?.IsWaygate == true && CmbWaygateRegion.SelectedItem is WaygateRegionOption wOpt)
            {
                u.WaygateDestinationRegionId = wOpt.RegionId;
            }

            u.ItemDropSets.Clear();
            foreach (var s in _workingSets)
            {
                if (s.Count > 0)
                    u.ItemDropSets.Add(s);
            }

            ChangesApplied = true;
        }
        else if (_entity is W3PlacedDoodad d)
        {
            if (double.TryParse(TxtAngle.Text.Replace(',', '.'), NumberStyles.Float, inv, out var ang)) d.AngleDegrees = ang;
            if (float.TryParse(TxtPosX.Text.Replace(',', '.'), NumberStyles.Float, inv, out var px)) d.X = px;
            if (float.TryParse(TxtPosY.Text.Replace(',', '.'), NumberStyles.Float, inv, out var py)) d.Y = py;
            if (float.TryParse(TxtScale.Text.Replace(',', '.'), NumberStyles.Float, inv, out var sc) && sc > 0)
            {
                d.Scale = sc;
                d.ScaleY = sc;
                d.ScaleZ = sc;
            }
            if (int.TryParse(TxtDoodadVariation.Text.Trim(), NumberStyles.Integer, inv, out var varIdx)) d.Variation = Math.Max(0, varIdx);
            if (d.IsDestructible && byte.TryParse(TxtDoodadLife.Text.Trim(), NumberStyles.Integer, inv, out var life)) d.Life = Math.Clamp(life, (byte)0, (byte)100);
            ChangesApplied = true;
        }

        DialogResult = true;
    }
}
