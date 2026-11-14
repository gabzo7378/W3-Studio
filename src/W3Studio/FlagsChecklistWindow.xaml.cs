using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public enum FlagFieldKind
{
    TargetsAllowed,
    UnitClassification,
    PlacementPathing,
    Tilesets
}

public class FlagCheckItem : ObservableObject
{
    private bool _isChecked;

    public string Code { get; set; } = string.Empty;
    public string LabelEs { get; set; } = string.Empty;
    public string LabelEn { get; set; } = string.Empty;

    public string DisplayLabel => LocalizationService.L(LabelEs, LabelEn);
    public string RawTagDisplay => $"[{Code}]";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
            }
        }
    }
}

public partial class FlagsChecklistWindow : Window
{
    private readonly List<FlagCheckItem> _allFlags = new();
    private bool _suppressEvents;

    public string SelectedValue { get; private set; } = string.Empty;

    public FlagsChecklistWindow(
        W3ObjectEntry entry,
        W3ObjectField field,
        FlagFieldKind kind)
    {
        InitializeComponent();

        Title = LocalizationService.L(
            $"Seleccionar Opciones — {field.DisplayFieldTitle}",
            $"Select Options — {field.DisplayFieldTitle}");

        TxtHeaderTitle.Text = $"{entry.Name} [{entry.Rawcode}] — {field.DisplayFieldTitle}";
        TxtHeaderSubtitle.Text = LocalizationService.L(
            "Marca las casillas deseadas (idéntico al selector oficial de Warcraft III 1.27b).",
            "Check the desired flags (identical to the official Warcraft III 1.27b selector).");

        LblFilter.Text = LocalizationService.L("Filtrar:", "Filter:");
        BtnSelectAll.Content = LocalizationService.L("Marcar Todo", "Check All");
        BtnClearAll.Content = LocalizationService.L("Desmarcar Todo", "Uncheck All");
        LblPreview.Text = LocalizationService.L("Valor resultante:", "Resulting value:");
        BtnCancel.Content = LocalizationService.L("Cancelar", "Cancel");
        BtnOk.Content = LocalizationService.L("Aceptar", "OK");

        var catalog = GetCatalogForKind(kind);
        var currentTokens = (field.ResolvedValue ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, es, en) in catalog)
        {
            _allFlags.Add(new FlagCheckItem
            {
                Code = code,
                LabelEs = es,
                LabelEn = en,
                IsChecked = currentTokens.Contains(code)
            });
        }

        // Si el campo tenía algún token personalizado adicional, lo preservamos en la lista
        foreach (var customToken in currentTokens)
        {
            if (!_allFlags.Any(f => f.Code.Equals(customToken, StringComparison.OrdinalIgnoreCase)))
            {
                _allFlags.Add(new FlagCheckItem
                {
                    Code = customToken,
                    LabelEs = customToken,
                    LabelEn = customToken,
                    IsChecked = true
                });
            }
        }

        RefreshFilteredList();
        UpdatePreview();
    }

    public static string? FormatValueForDisplay(W3ObjectField field)
    {
        if (string.IsNullOrWhiteSpace(field.ResolvedValue))
            return null;
        if (!TryGetFlagFieldKind(field, out var kind))
            return null;

        var catalog = GetCatalogForKind(kind);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, es, en) in catalog)
        {
            map[code] = LocalizationService.L(es, en);
        }

        var tokens = field.ResolvedValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var translated = tokens.Select(t => map.TryGetValue(t, out var lbl) ? lbl : t);
        return string.Join(", ", translated);
    }

    public static bool TryGetFlagFieldKind(W3ObjectField field, out FlagFieldKind kind)
    {
        if (TryGetFlagFieldKind(field.FieldId, out kind))
            return true;

        var slkType = (field.SlkTypeName ?? string.Empty).Trim().ToLowerInvariant();
        switch (slkType)
        {
            case "targetlist":
                kind = FlagFieldKind.TargetsAllowed;
                return true;
            case "unitclass":
                kind = FlagFieldKind.UnitClassification;
                return true;
            case "pathinglist":
                kind = FlagFieldKind.PlacementPathing;
                return true;
            case "tilesetlist":
                kind = FlagFieldKind.Tilesets;
                return true;
            default:
                kind = FlagFieldKind.TargetsAllowed;
                return false;
        }
    }

    public static bool TryGetFlagFieldKind(string fieldId, out FlagFieldKind kind)
    {
        switch ((fieldId ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "ua1g":
            case "ua2g":
            case "ua1p":
            case "ua2p":
            case "utar":
            case "atar":
            case "btar":
                kind = FlagFieldKind.TargetsAllowed;
                return true;

            case "utyp":
                kind = FlagFieldKind.UnitClassification;
                return true;

            case "upap":
                kind = FlagFieldKind.PlacementPathing;
                return true;

            case "util":
            case "btil":
            case "dtil":
                kind = FlagFieldKind.Tilesets;
                return true;

            default:
                kind = FlagFieldKind.TargetsAllowed;
                return false;
        }
    }

    private static List<(string Code, string Es, string En)> GetCatalogForKind(FlagFieldKind kind)
    {
        return kind switch
        {
            FlagFieldKind.TargetsAllowed => new()
            {
                ("air", "Aéreo", "Air"),
                ("ground", "Terrestre", "Ground"),
                ("structure", "Estructura", "Structure"),
                ("ward", "Guardián (Ward)", "Ward"),
                ("item", "Objeto (Item)", "Item"),
                ("tree", "Árbol", "Tree"),
                ("wall", "Muro", "Wall"),
                ("debris", "Escombros (Debris)", "Debris"),
                ("bridge", "Puente", "Bridge"),
                ("decoration", "Decoración", "Decoration"),
                ("terrain", "Terreno", "Terrain"),
                ("self", "Sí mismo (Self)", "Self"),
                ("notself", "No sí mismo (Not Self)", "Not Self"),
                ("player", "Unidades del Jugador", "Player Units"),
                ("allies", "Aliados", "Allied"),
                ("friend", "Amigo (Friend)", "Friend"),
                ("neutral", "Neutral", "Neutral"),
                ("enemy", "Enemigo (Enemy)", "Enemy"),
                ("vulnerable", "Vulnerable", "Vulnerable"),
                ("invulnerable", "Invulnerable", "Invulnerable"),
                ("hero", "Héroe", "Hero"),
                ("nonhero", "No-Héroe", "Non-Hero"),
                ("alive", "Vivo", "Alive"),
                ("dead", "Muerto", "Dead"),
                ("organic", "Orgánico", "Organic"),
                ("mechanical", "Mecánico", "Mechanical"),
                ("ancient", "Antiguo (Ancient)", "Ancient"),
                ("nonancient", "No-Antiguo (Non-Ancient)", "Non-Ancient"),
                ("sapper", "Zapador / Suicida", "Sapper (Suicidal)"),
                ("nonsapper", "No-Zapador", "Non-Sapper"),
                ("none", "Ninguno", "None")
            },
            FlagFieldKind.UnitClassification => new()
            {
                ("giant", "Gigante", "Giant"),
                ("undead", "Muerto viviente (Undead)", "Undead"),
                ("summoned", "Invocado", "Summoned"),
                ("mechanical", "Mecánico", "Mechanical"),
                ("peon", "Trabajador (Tipo Peón)", "Peon-type Worker"),
                ("sapper", "Suicida (Sapper)", "Suicidal"),
                ("townhall", "Ayuntamiento (Town Hall)", "Town Hall"),
                ("tree", "Árbol", "Tree"),
                ("ward", "Guardián (Ward)", "Ward"),
                ("ancient", "Antiguo (Ancient)", "Ancient"),
                ("standon", "Caminable encima", "Walkable"),
                ("neutral", "Neutral", "Neutral"),
                ("tauren", "Tauren", "Tauren")
            },
            FlagFieldKind.PlacementPathing => new()
            {
                ("unwalkable", "No caminable (Terreno)", "Unwalkable"),
                ("unflyable", "No volable (Aire)", "Unflyable"),
                ("unbuildable", "No construible", "Unbuildable"),
                ("blighted", "Requiere Tierra Corrupta (Blight)", "Blighted"),
                ("unblighted", "Sin Tierra Corrupta", "Unblighted"),
                ("unfloatable", "No navegable (Agua)", "Unfloatable"),
                ("unamph", "No anfibio", "Non-Amphibious")
            },
            FlagFieldKind.Tilesets => new()
            {
                ("*", "Todos los Conjuntos de Terreno (*)", "All Tilesets (*)"),
                ("A", "Vallefresno (Ashenvale)", "Ashenvale"),
                ("B", "Los Baldíos (Barrens)", "Barrens"),
                ("C", "Frondavil (Felwood)", "Felwood"),
                ("D", "Mazmorra (Dungeon)", "Dungeon"),
                ("F", "Lordaeron Otoño", "Lordaeron Fall"),
                ("G", "Subterráneo (Underground)", "Underground"),
                ("I", "Glaciar Corona de Hielo", "Icecrown Glacier"),
                ("J", "Ruinas de Dalaran", "Dalaran Ruins"),
                ("K", "Ciudadela Negra (Black Citadel)", "Black Citadel"),
                ("L", "Lordaeron Verano", "Lordaeron Summer"),
                ("N", "Rasganorte (Northrend)", "Northrend"),
                ("O", "Terrallende (Outland)", "Outland"),
                ("Q", "Aldea Otoño", "Village Fall"),
                ("V", "Aldea (Village)", "Village"),
                ("W", "Lordaeron Invierno", "Lordaeron Winter"),
                ("X", "Dalaran", "Dalaran"),
                ("Y", " Paisaje Urbano (Cityscape)", "Cityscape"),
                ("Z", "Ruinas Sumergidas (Sunken Ruins)", "Sunken Ruins")
            },
            _ => new()
        };
    }

    private void RefreshFilteredList()
    {
        var q = TxtSearch?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(q))
        {
            ItemsFlags.ItemsSource = _allFlags.ToList();
        }
        else
        {
            ItemsFlags.ItemsSource = _allFlags
                .Where(f => f.DisplayLabel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            f.Code.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void UpdatePreview()
    {
        var checkedItems = _allFlags.Where(f => f.IsChecked).Select(f => f.Code).ToList();
        SelectedValue = string.Join(",", checkedItems);
        if (TxtResultPreview != null)
            TxtResultPreview.Text = SelectedValue;
        if (TxtSelectedCount != null)
        {
            TxtSelectedCount.Text = LocalizationService.L(
                $"{checkedItems.Count} opción(es) seleccionada(s)",
                $"{checkedItems.Count} option(s) selected");
        }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilteredList();

    private void FlagCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        UpdatePreview();
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        foreach (var f in _allFlags) f.IsChecked = true;
        _suppressEvents = false;
        RefreshFilteredList();
        UpdatePreview();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        foreach (var f in _allFlags) f.IsChecked = false;
        _suppressEvents = false;
        RefreshFilteredList();
        UpdatePreview();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        DialogResult = true;
        Close();
    }
}
