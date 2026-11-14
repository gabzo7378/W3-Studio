using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public enum ObjectPickerKind
{
    HeroAbilities,
    NormalAbilities,
    SingleAbility,
    ItemAbilities,
    Upgrades,
    TechtreeRequirements,
    UnitsAndBuildings,
    Items,
    Buffs
}

public class ObjectPickerCandidate
{
    public string Rawcode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = "Other";
    public W3ObjectCategory Category { get; set; }
    public bool IsCustom { get; set; }
    public ImageSource? Icon { get; set; }

    public string DisplayTitle => $"{Name} [{Rawcode}]{(IsCustom ? " ★" : "")}";
    public string SubInfo => $"({Race})";
}

public partial class ObjectListPickerWindow : Window
{
    private readonly WarcraftBaseDataService _baseDataService;
    private readonly BlpIconService _blpIconService;
    private readonly MapProject? _project;
    private readonly ObjectPickerKind _kind;
    private readonly int _maxItems;
    private readonly List<ObjectPickerCandidate> _allCandidates = new();
    private readonly ObservableCollection<ObjectPickerCandidate> _assigned = new();
    private bool _suppressRawSync;

    public string SelectedCsvValue { get; private set; } = string.Empty;

    public ObjectListPickerWindow(
        WarcraftBaseDataService baseDataService,
        BlpIconService blpIconService,
        MapProject? project,
        W3ObjectEntry ownerEntry,
        W3ObjectField field,
        ObjectPickerKind kind)
    {
        InitializeComponent();

        _baseDataService = baseDataService;
        _blpIconService = blpIconService;
        _project = project;
        _kind = kind;
        _maxItems = kind switch
        {
            ObjectPickerKind.HeroAbilities => 5,
            ObjectPickerKind.SingleAbility => 1,
            _ => 32
        };

        ApplyLocalization(ownerEntry, field);
        BuildCandidateCatalog();
        PopulateRaceFilter();

        LstAssigned.ItemsSource = _assigned;
        LoadInitialValue(field.ResolvedValue);
        RefreshCatalogList();
    }

    public static string? FormatValueForDisplay(
        W3ObjectField field,
        WarcraftBaseDataService? baseData,
        MapProject? project)
    {
        if (string.IsNullOrWhiteSpace(field.ResolvedValue) || baseData == null)
            return null;
        if (!TryGetObjectPickerKind(field, out _))
            return null;

        var codes = field.ResolvedValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = new List<string>();
        foreach (var code in codes)
        {
            if (code is "_" or "-") continue;
            var custom = project?.Objects.FirstOrDefault(o => o.Rawcode.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (custom != null && !string.IsNullOrWhiteSpace(custom.Name))
            {
                names.Add($"{StripCodes(custom.Name)} [{code}]");
            }
            else if (baseData.AllBaseObjects.TryGetValue(code, out var meta) && !string.IsNullOrWhiteSpace(meta.Name))
            {
                names.Add($"{StripCodes(meta.Name)} [{code}]");
            }
            else
            {
                names.Add(code);
            }
        }
        return names.Count > 0 ? string.Join(", ", names) : string.Empty;
    }

    public static bool TryGetObjectPickerKind(W3ObjectField field, out ObjectPickerKind kind)
    {
        if (TryGetObjectPickerKind(field.FieldId, out kind))
            return true;

        var slkType = (field.SlkTypeName ?? string.Empty).Trim().ToLowerInvariant();
        switch (slkType)
        {
            case "heroabillist":
                kind = ObjectPickerKind.HeroAbilities;
                return true;
            case "abillist":
                kind = ObjectPickerKind.NormalAbilities;
                return true;
            case "abilcode":
                kind = ObjectPickerKind.SingleAbility;
                return true;
            case "upgradelist":
            case "upgradecode":
                kind = ObjectPickerKind.Upgrades;
                return true;
            case "techlist":
                kind = ObjectPickerKind.TechtreeRequirements;
                return true;
            case "unitlist":
            case "unitcode":
                kind = ObjectPickerKind.UnitsAndBuildings;
                return true;
            case "itemlist":
            case "itemcode":
                kind = ObjectPickerKind.Items;
                return true;
            case "bufflist":
            case "effectlist":
                kind = ObjectPickerKind.Buffs;
                return true;
            default:
                kind = ObjectPickerKind.NormalAbilities;
                return false;
        }
    }

    public static bool TryGetObjectPickerKind(string fieldId, out ObjectPickerKind kind)
    {
        switch ((fieldId ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "uhab":
                kind = ObjectPickerKind.HeroAbilities;
                return true;

            case "uabi":
                kind = ObjectPickerKind.NormalAbilities;
                return true;

            case "udaa":
                kind = ObjectPickerKind.SingleAbility;
                return true;

            case "iabi":
                kind = ObjectPickerKind.ItemAbilities;
                return true;

            case "upgr":
            case "ures":
                kind = ObjectPickerKind.Upgrades;
                return true;

            case "ureq":
            case "urq1":
            case "urq2":
            case "ireq":
            case "areq":
            case "greq":
            case "udea":
                kind = ObjectPickerKind.TechtreeRequirements;
                return true;

            case "utra":
            case "ubui":
            case "useu":
            case "uupt":
                kind = ObjectPickerKind.UnitsAndBuildings;
                return true;

            case "usei":
            case "umki":
                kind = ObjectPickerKind.Items;
                return true;

            case "abuf":
            case "aeff":
                kind = ObjectPickerKind.Buffs;
                return true;

            default:
                kind = ObjectPickerKind.NormalAbilities;
                return false;
        }
    }

    private void ApplyLocalization(W3ObjectEntry ownerEntry, W3ObjectField field)
    {
        Title = LocalizationService.L(
            $"Selector de Objetos — {field.DisplayFieldTitle}",
            $"Object Selector — {field.DisplayFieldTitle}");

        TxtHeaderTitle.Text = $"{StripCodes(ownerEntry.Name)} [{ownerEntry.Rawcode}] — {field.DisplayFieldTitle}";
        TxtHeaderSubtitle.Text = _maxItems == 1
            ? LocalizationService.L(
                "Selecciona un único elemento del catálogo derecho (doble clic para asignar).",
                "Select a single item from the right catalog (double-click to assign).")
            : LocalizationService.L(
                $"Doble clic en el catálogo derecho para agregar elementos (máximo {_maxItems}). Usa ↑/↓ para ordenar.",
                $"Double-click in the right catalog to add items (max {_maxItems}). Use ↑/↓ to reorder.");

        LblCatalogHeader.Text = LocalizationService.L("Catálogo Disponible:", "Available Catalog:");
        LblSearch.Text = LocalizationService.L("Buscar:", "Search:");
        BtnMoveUp.Content = LocalizationService.L("↑ Subir", "↑ Up");
        BtnMoveDown.Content = LocalizationService.L("↓ Bajar", "↓ Down");
        BtnRemoveSelected.Content = LocalizationService.L("✕ Quitar", "✕ Remove");
        BtnClearAll.Content = LocalizationService.L("Vaciar", "Clear");
        BtnAddSelected.Content = LocalizationService.L("← Agregar Seleccionado a la Lista", "← Add Selected to List");
        LblRawPreview.Text = LocalizationService.L("Rawcodes (CSV):", "Rawcodes (CSV):");
        BtnCancel.Content = LocalizationService.L("Cancelar", "Cancel");
        BtnOk.Content = LocalizationService.L("Aceptar", "OK");
    }

    private void BuildCandidateCatalog()
    {
        _allCandidates.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool AcceptsCategory(W3ObjectCategory cat) => _kind switch
        {
            ObjectPickerKind.HeroAbilities or ObjectPickerKind.NormalAbilities or ObjectPickerKind.SingleAbility or ObjectPickerKind.ItemAbilities => cat == W3ObjectCategory.Abilities,
            ObjectPickerKind.Upgrades => cat == W3ObjectCategory.Upgrades,
            ObjectPickerKind.TechtreeRequirements => cat is W3ObjectCategory.Units or W3ObjectCategory.Upgrades,
            ObjectPickerKind.UnitsAndBuildings => cat == W3ObjectCategory.Units,
            ObjectPickerKind.Items => cat == W3ObjectCategory.Items,
            ObjectPickerKind.Buffs => cat == W3ObjectCategory.Buffs,
            _ => true
        };

        // 1. Objetos personalizados o modificados en el mapa actual
        if (_project != null)
        {
            foreach (var customObj in _project.Objects)
            {
                if (!AcceptsCategory(customObj.Category))
                    continue;
                if (!seen.Add(customObj.Rawcode))
                    continue;

                _allCandidates.Add(new ObjectPickerCandidate
                {
                    Rawcode = customObj.Rawcode,
                    Name = StripCodes(string.IsNullOrWhiteSpace(customObj.Name) ? customObj.Rawcode : customObj.Name),
                    Race = LocalizationService.L("Personalizado", "Custom"),
                    Category = customObj.Category,
                    IsCustom = customObj.IsCustom,
                    Icon = customObj.IconBitmap ?? _blpIconService.GetIcon(customObj.IconPath, _project)
                });
            }
        }

        // 2. Objetos estándar del MPQ base 1.27b
        foreach (var meta in _baseDataService.AllBaseObjects.Values)
        {
            if (!AcceptsCategory(meta.Category))
                continue;

            if (_kind == ObjectPickerKind.HeroAbilities)
            {
                var isHeroAbil = meta.DefaultFields.TryGetValue("hero", out var hVal) && hVal == "1";
                if (!isHeroAbil) continue;
            }
            else if (_kind == ObjectPickerKind.NormalAbilities)
            {
                var isHeroAbil = meta.DefaultFields.TryGetValue("hero", out var hVal) && hVal == "1";
                if (isHeroAbil) continue;
            }
            else if (_kind == ObjectPickerKind.ItemAbilities)
            {
                var isHeroAbil = meta.DefaultFields.TryGetValue("hero", out var hVal) && hVal == "1";
                if (isHeroAbil) continue;
            }

            if (!seen.Add(meta.Rawcode))
                continue;

            var name = StripCodes(string.IsNullOrWhiteSpace(meta.Name) ? meta.Rawcode : meta.Name);
            if (meta.DefaultFields.TryGetValue("EditorSuffix", out var suffix) && !string.IsNullOrWhiteSpace(suffix) && suffix != "_")
            {
                var cleanSuffix = StripCodes(_baseDataService.ResolveWeString(suffix));
                if (!string.IsNullOrWhiteSpace(cleanSuffix))
                    name = $"{name} {cleanSuffix}";
            }

            _allCandidates.Add(new ObjectPickerCandidate
            {
                Rawcode = meta.Rawcode,
                Name = name,
                Race = NormalizeRaceName(meta.Race),
                Category = meta.Category,
                IsCustom = false,
                Icon = _blpIconService.GetIcon(meta.IconPath, _project)
            });
        }

        _allCandidates.Sort((a, b) =>
        {
            if (a.IsCustom != b.IsCustom)
                return a.IsCustom ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string NormalizeRaceName(string? raw)
    {
        return (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "human" => LocalizationService.L("Humano", "Human"),
            "orc" => LocalizationService.L("Orco", "Orc"),
            "undead" => LocalizationService.L("No-Muerto", "Undead"),
            "nightelf" => LocalizationService.L("Elfo Nocturno", "Night Elf"),
            "naga" => "Naga",
            "creeps" => "Creeps",
            "other" or "commoner" or "unknown" or "" => "Neutral / Otros",
            _ => raw ?? "Neutral / Otros"
        };
    }

    private void PopulateRaceFilter()
    {
        CmbRaceFilter.Items.Clear();
        CmbRaceFilter.Items.Add(LocalizationService.L("Todas las Razas / Grupos", "All Races / Groups"));
        foreach (var race in _allCandidates.Select(c => c.Race).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r))
        {
            CmbRaceFilter.Items.Add(race);
        }
        CmbRaceFilter.SelectedIndex = 0;
    }

    private void LoadInitialValue(string? csv)
    {
        _assigned.Clear();
        var codes = (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var code in codes)
        {
            if (code == "_" || code == "-") continue;
            var match = _allCandidates.FirstOrDefault(c => c.Rawcode.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                _assigned.Add(match);
            }
            else if (_baseDataService.AllBaseObjects.TryGetValue(code, out var baseMeta))
            {
                _assigned.Add(new ObjectPickerCandidate
                {
                    Rawcode = baseMeta.Rawcode,
                    Name = StripCodes(baseMeta.Name),
                    Race = NormalizeRaceName(baseMeta.Race),
                    Category = baseMeta.Category,
                    Icon = _blpIconService.GetIcon(baseMeta.IconPath, _project)
                });
            }
            else
            {
                _assigned.Add(new ObjectPickerCandidate
                {
                    Rawcode = code,
                    Name = code,
                    Race = "Rawcode"
                });
            }
        }

        UpdateAssignedSummary();
    }

    private void RefreshCatalogList()
    {
        if (LstCatalog == null) return;

        IEnumerable<ObjectPickerCandidate> filtered = _allCandidates;
        if (CmbRaceFilter.SelectedIndex > 0 && CmbRaceFilter.SelectedItem is string selectedRace)
        {
            filtered = filtered.Where(c => c.Race.Equals(selectedRace, StringComparison.OrdinalIgnoreCase));
        }

        var q = TxtSearch?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered.Where(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Rawcode.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        LstCatalog.ItemsSource = filtered.ToList();
    }

    private void UpdateAssignedSummary()
    {
        SelectedCsvValue = string.Join(",", _assigned.Select(a => a.Rawcode));
        LblAssignedHeader.Text = LocalizationService.L(
            $"Elementos Asignados ({_assigned.Count} / {_maxItems}):",
            $"Assigned Items ({_assigned.Count} / {_maxItems}):");

        _suppressRawSync = true;
        TxtRawPreview.Text = SelectedCsvValue;
        _suppressRawSync = false;
    }

    private void AddCandidate(ObjectPickerCandidate? candidate)
    {
        if (candidate == null) return;

        if (_maxItems == 1)
        {
            _assigned.Clear();
            _assigned.Add(candidate);
            UpdateAssignedSummary();
            return;
        }

        if (_assigned.Count >= _maxItems)
        {
            MessageBox.Show(
                this,
                LocalizationService.L(
                    $"Este campo permite como máximo {_maxItems} elementos.",
                    $"This field allows at most {_maxItems} items."),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_assigned.Any(a => a.Rawcode.Equals(candidate.Rawcode, StringComparison.OrdinalIgnoreCase)))
            return;

        _assigned.Add(candidate);
        UpdateAssignedSummary();
    }

    private void CmbRaceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCatalogList();
    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshCatalogList();

    private void LstCatalog_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstCatalog.SelectedItem is ObjectPickerCandidate cand)
            AddCandidate(cand);
    }

    private void BtnAddSelected_Click(object sender, RoutedEventArgs e)
    {
        if (LstCatalog.SelectedItem is ObjectPickerCandidate cand)
            AddCandidate(cand);
    }

    private void LstAssigned_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstAssigned.SelectedItem is ObjectPickerCandidate cand)
        {
            _assigned.Remove(cand);
            UpdateAssignedSummary();
        }
    }

    private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (LstAssigned.SelectedItem is ObjectPickerCandidate cand)
        {
            var idx = LstAssigned.SelectedIndex;
            _assigned.Remove(cand);
            if (_assigned.Count > 0)
                LstAssigned.SelectedIndex = Math.Clamp(idx, 0, _assigned.Count - 1);
            UpdateAssignedSummary();
        }
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        var idx = LstAssigned.SelectedIndex;
        if (idx > 0)
        {
            _assigned.Move(idx, idx - 1);
            LstAssigned.SelectedIndex = idx - 1;
            UpdateAssignedSummary();
        }
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        var idx = LstAssigned.SelectedIndex;
        if (idx >= 0 && idx < _assigned.Count - 1)
        {
            _assigned.Move(idx, idx + 1);
            LstAssigned.SelectedIndex = idx + 1;
            UpdateAssignedSummary();
        }
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        _assigned.Clear();
        UpdateAssignedSummary();
    }

    private void TxtRawPreview_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRawSync) return;
        SelectedCsvValue = TxtRawPreview.Text.Trim();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static string StripCodes(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return Regex.Replace(s, @"\|c[0-9a-fA-F]{8}|\|r|\|n", " ").Trim();
    }
}
