using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio.Controls;

public partial class ObjectEditorView : UserControl
{
    private WarcraftBaseDataService? _baseDataService;
    private BlpIconService? _blpIconService;
    private MapProject? _currentProject;

    private W3ObjectCategory _activeCategory = W3ObjectCategory.Items;
    private W3ObjectEntry? _selectedEntry;
    private List<W3ObjectField> _currentEntryAllFields = new();
    private bool _suppressUiEvents;

    private static BitmapSource? _folderIcon;
    private static BitmapSource FolderIcon => _folderIcon ??= CreateClassicYellowFolderIcon();

    public event Action? RequestOpenStandaloneWindow;
    public event Action<string?>? RequestOpenIconManager;

    private void BtnEditIconInManager_Click(object sender, RoutedEventArgs e)
    {
        RequestOpenIconManager?.Invoke(TxtPropIconPath?.Text?.Trim());
    }

    private void BtnBrowseIconInPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || _blpIconService == null)
            return;

        var win = Window.GetWindow(this);
        var dlg = new IconPickerWindow(
            _blpIconService,
            _currentProject,
            TxtPropIconPath?.Text?.Trim(),
            _selectedEntry.Name,
            path => RequestOpenIconManager?.Invoke(path))
        {
            Owner = win
        };

        if (dlg.ShowDialog() == true && TxtPropIconPath != null)
        {
            TxtPropIconPath.Text = dlg.SelectedIconPath;
            RefreshFieldsDataGrid();
        }
    }

    public ObjectEditorView()
    {
        InitializeComponent();
        BuildCommandCardButtons();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
        ApplyLocalization();
    }

    private void OnLanguageChanged(string lang)
    {
        Dispatcher.Invoke(() =>
        {
            ApplyLocalization();
            if (_selectedEntry != null)
            {
                DisplayObjectEntry(_selectedEntry);
            }
            else
            {
                RefreshFieldsDataGrid();
            }
        });
    }

    public void ApplyLocalization()
    {
        if (ChkRawData != null) ChkRawData.Content = LocalizationService.L("Mostrar Datos Crudos (Ctrl+D)", "Display Values As Raw Data (Ctrl+D)");
        if (ChkOnlyModified != null) ChkOnlyModified.Content = LocalizationService.L("Solo Campos Modificados", "Only Modified Fields");
        if (TxtBtnPopoutWindow != null) TxtBtnPopoutWindow.Text = LocalizationService.L("Abrir Ventana Object Editor", "Open Object Editor Window");
        if (TxtBtnNewObject != null) TxtBtnNewObject.Text = LocalizationService.L("Nuevo Objeto...", "New Object...");
        if (TxtBtnCloneObject != null) TxtBtnCloneObject.Text = LocalizationService.L("Clonar Seleccionado", "Clone Selected");
        if (TxtBtnResetField != null) TxtBtnResetField.Text = LocalizationService.L("Restaurar Campo a Base", "Reset Field to Default");

        if (TxtTabUnits != null) TxtTabUnits.Text = LocalizationService.L("Unidades", "Units");
        if (TxtTabItems != null) TxtTabItems.Text = LocalizationService.L("Objetos", "Items");
        if (TxtTabDestructables != null) TxtTabDestructables.Text = LocalizationService.L("Destructibles", "Destructibles");
        if (TxtTabDoodads != null) TxtTabDoodads.Text = LocalizationService.L("Doodads", "Doodads");
        if (TxtTabAbilities != null) TxtTabAbilities.Text = LocalizationService.L("Habilidades", "Abilities");
        if (TxtTabBuffs != null) TxtTabBuffs.Text = LocalizationService.L("Buffs/Efectos", "Buffs/Effects");
        if (TxtTabUpgrades != null) TxtTabUpgrades.Text = LocalizationService.L("Mejoras", "Upgrades");

        if (LblTreeSearch != null) LblTreeSearch.Text = LocalizationService.L("Buscar:", "Search:");
        if (LblFieldFilter != null) LblFieldFilter.Text = LocalizationService.L("Filtrar propiedad:", "Filter property:");

        if (ColFieldName != null) ColFieldName.Header = LocalizationService.L("Campo", "Field");
        if (ColFieldValue != null) ColFieldValue.Header = LocalizationService.L("Valor", "Value");
        if (ColFieldStatus != null) ColFieldStatus.Header = LocalizationService.L("Estado", "Status");
    }

    public void HidePopoutButton()
    {
        BtnPopoutWindow.Visibility = Visibility.Collapsed;
    }

    public void InitializeServices(WarcraftBaseDataService baseDataService, BlpIconService blpIconService)
    {
        _baseDataService = baseDataService;
        _blpIconService = blpIconService;
    }

    public void LoadProject(MapProject? project)
    {
        _currentProject = project;
        RebuildObjectTree();
    }

    public void SelectCategoryAndSearch(W3ObjectCategory category, string? searchRawcode = null)
    {
        _activeCategory = category;
        _suppressUiEvents = true;
        switch (category)
        {
            case W3ObjectCategory.Units: TabBtnUnits.IsChecked = true; break;
            case W3ObjectCategory.Items: TabBtnItems.IsChecked = true; break;
            case W3ObjectCategory.Destructables: TabBtnDestructables.IsChecked = true; break;
            case W3ObjectCategory.Doodads: TabBtnDoodads.IsChecked = true; break;
            case W3ObjectCategory.Abilities: TabBtnAbilities.IsChecked = true; break;
            case W3ObjectCategory.Buffs: TabBtnBuffs.IsChecked = true; break;
            case W3ObjectCategory.Upgrades: TabBtnUpgrades.IsChecked = true; break;
        }
        if (searchRawcode != null)
            TxtTreeSearch.Text = searchRawcode;
        _suppressUiEvents = false;

        RebuildObjectTree(searchRawcode);
    }

    private void CategoryTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || sender is not RadioButton rb || rb.Tag is not string tagStr)
            return;

        if (Enum.TryParse<W3ObjectCategory>(tagStr, out var cat))
        {
            _activeCategory = cat;
            RebuildObjectTree();
        }
    }

    private void TxtTreeSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressUiEvents)
            RebuildObjectTree();
    }

    public void RebuildObjectTree(string? autoSelectRawcode = null)
    {
        if (_baseDataService == null || _blpIconService == null)
            return;

        var query = TxtTreeSearch.Text.Trim();
        var showRaw = ChkRawData.IsChecked == true;

        var catTitle = _activeCategory switch
        {
            W3ObjectCategory.Units => "Units",
            W3ObjectCategory.Items => "Items",
            W3ObjectCategory.Destructables => "Destructibles",
            W3ObjectCategory.Doodads => "Doodads",
            W3ObjectCategory.Abilities => "Abilities",
            W3ObjectCategory.Buffs => "Buffs/Effects",
            W3ObjectCategory.Upgrades => "Upgrades",
            _ => "Objects"
        };

        // Map's modified original objects (by rawcode) and custom objects
        var mapObjectsForCat = _currentProject?.Objects
            .Where(o => o.Category == _activeCategory)
            .ToList() ?? new List<W3ObjectEntry>();

        var modifiedStandardLookup = mapObjectsForCat
            .Where(o => !o.IsCustom)
            .GroupBy(o => o.Rawcode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var customObjects = mapObjectsForCat.Where(o => o.IsCustom).ToList();

        // 1. Build Standard Objects list (merging Base SLK/Strings with any map overrides on standard rawcodes)
        var standardEntries = new List<W3ObjectEntry>();
        foreach (var baseMeta in _baseDataService.AllBaseObjects.Values.Where(b => b.Category == _activeCategory))
        {
            if (modifiedStandardLookup.TryGetValue(baseMeta.Rawcode, out var mapModified))
            {
                if (string.IsNullOrEmpty(mapModified.SubcategoryFolder) || mapModified.SubcategoryFolder == "General")
                    mapModified.SubcategoryFolder = baseMeta.SubcategoryFolder;
                standardEntries.Add(mapModified);
            }
            else
            {
                standardEntries.Add(new W3ObjectEntry
                {
                    Category = _activeCategory,
                    SubcategoryFolder = baseMeta.SubcategoryFolder,
                    Rawcode = baseMeta.Rawcode,
                    BaseRawcode = baseMeta.Rawcode,
                    OldId = WarcraftBaseDataService.RawcodeToInt(baseMeta.Rawcode),
                    NewId = 0,
                    Name = baseMeta.Name,
                    TooltipBasic = baseMeta.Tooltip,
                    TooltipExtended = baseMeta.Ubertip,
                    Hotkey = baseMeta.Hotkey,
                    IconPath = baseMeta.Art,
                    ButtonPosX = baseMeta.ButtonPosX,
                    ButtonPosY = baseMeta.ButtonPosY,
                    IsStandardUnmodified = true
                });
            }
        }

        // Also include any non-custom map objects whose rawcode wasn't in AllBaseObjects
        foreach (var modStd in modifiedStandardLookup.Values)
        {
            if (!standardEntries.Any(s => s.Rawcode.Equals(modStd.Rawcode, StringComparison.OrdinalIgnoreCase)))
                standardEntries.Add(modStd);
        }

        // Filter by search query if typed
        if (!string.IsNullOrEmpty(query))
        {
            standardEntries = standardEntries.Where(e => MatchesQuery(e, query)).ToList();
            customObjects = customObjects.Where(e => MatchesQuery(e, query)).ToList();
        }

        var rootNodes = new List<W3ObjectTreeNode>();
        W3ObjectEntry? entryToSelect = null;

        // 1. Standard Root Folder (e.g. "Standard Units (842)")
        var stdRoot = new W3ObjectTreeNode
        {
            FolderKey = "Standard",
            Title = $"Standard {catTitle} ({standardEntries.Count})",
            IsFolder = true,
            IsExpanded = true,
            IconSource = FolderIcon
        };

        foreach (var entry in standardEntries)
        {
            var path = GetClassificationPath(entry);
            InsertEntryIntoTree(stdRoot, path, entry, showRaw, ref entryToSelect, autoSelectRawcode);
        }

        PostProcessFolderHierarchy(stdRoot, 0, true, catTitle, query);
        rootNodes.Add(stdRoot);

        // 2. Custom Root Folder (e.g. "Custom Units (0)")
        var customRoot = new W3ObjectTreeNode
        {
            FolderKey = "Custom",
            Title = $"Custom {catTitle} ({customObjects.Count})",
            IsFolder = true,
            IsExpanded = true,
            IconSource = FolderIcon
        };

        foreach (var entry in customObjects)
        {
            var path = GetClassificationPath(entry);
            InsertEntryIntoTree(customRoot, path, entry, showRaw, ref entryToSelect, autoSelectRawcode);
        }

        PostProcessFolderHierarchy(customRoot, 0, true, catTitle, query);
        rootNodes.Add(customRoot);

        TreeObjects.ItemsSource = rootNodes;

        if (entryToSelect != null)
        {
            DisplayObjectEntry(entryToSelect);
        }
    }

    private List<string> GetClassificationPath(W3ObjectEntry entry)
    {
        BaseObjectMetadata? baseMeta = null;
        if (_baseDataService != null)
        {
            if (!string.IsNullOrEmpty(entry.BaseRawcode) && _baseDataService.AllBaseObjects.TryGetValue(entry.BaseRawcode, out var bm))
            {
                baseMeta = bm;
            }
            else if (_baseDataService.AllBaseObjects.TryGetValue(entry.Rawcode, out var bm2))
            {
                baseMeta = bm2;
            }
        }

        string GetVal(string colName, string? fieldId = null)
        {
            if (fieldId != null)
            {
                var f = entry.Fields.FirstOrDefault(x => x.FieldId.Equals(fieldId, StringComparison.OrdinalIgnoreCase));
                if (f != null && !string.IsNullOrWhiteSpace(f.ResolvedValue))
                    return f.ResolvedValue;
            }
            if (baseMeta != null && baseMeta.DefaultFields.TryGetValue(colName, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
            return string.Empty;
        }

        var cat = _activeCategory;

        if (cat == W3ObjectCategory.Units)
        {
            var raceRaw = GetVal("race", "urac");
            var isBldgRaw = GetVal("isbldg", "ubld");
            var campRaw = GetVal("campaign");
            var specRaw = GetVal("special");
            var primary = GetVal("Primary", "upra");
            var sort2 = GetVal("sort2");
            var unitClass = GetVal("unitClass");
            var propernames = GetVal("Propernames", "upro");

            var race = raceRaw.ToLowerInvariant() switch
            {
                "human" => "Human",
                "orc" => "Orc",
                "undead" => "Undead",
                "nightelf" => "Night Elf",
                "creeps" => "Neutral Hostile",
                "naga" => "Naga",
                "other" or "commoner" or "passive" => "Neutral Passive",
                _ => !string.IsNullOrWhiteSpace(entry.SubcategoryFolder) && entry.SubcategoryFolder != "General"
                    ? entry.SubcategoryFolder
                    : "Neutral Passive"
            };

            var mode = campRaw == "1" ? "Campaign" : "Melee";
            var isBldg = isBldgRaw == "1";

            var isHero = !isBldg && (
                (!string.IsNullOrEmpty(primary) && primary != "_" && primary != "-") ||
                sort2 == "uher" ||
                unitClass.Contains("Hero", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(propernames) && propernames != "_" && propernames != "-")
            );

            var isSpec = specRaw == "1";
            var kind = isBldg ? "Buildings" : (isHero ? "Heroes" : (isSpec ? "Special" : "Units"));

            return new List<string> { race, mode, kind };
        }
        else if (cat == W3ObjectCategory.Abilities)
        {
            var itemRaw = GetVal("item", "aite");
            if (itemRaw == "1")
            {
                return new List<string> { "Items" };
            }

            var raceRaw = GetVal("race", "arac");
            var heroRaw = GetVal("hero", "aher");

            var group = raceRaw.ToLowerInvariant() switch
            {
                "human" => "Human",
                "orc" => "Orc",
                "undead" => "Undead",
                "nightelf" => "Night Elf",
                "creeps" => "Neutral Hostile",
                "naga" => "Neutral Passive",
                "other" or "commoner" or "passive" => "Neutral Passive",
                _ => "Special"
            };

            var kind = heroRaw == "1" ? "Heroes" : "Units";
            if (group == "Special")
            {
                return new List<string> { "Special", kind };
            }
            return new List<string> { group, kind };
        }
        else if (cat == W3ObjectCategory.Items)
        {
            var cls = GetVal("class", "icla");
            var itemClass = cls.ToLowerInvariant() switch
            {
                "permanent" => "Permanent",
                "charged" => "Charged",
                "powerup" => "Power Up",
                "artifact" => "Artifact",
                "purchasable" => "Purchasable",
                "campaign" => "Campaign",
                _ => !string.IsNullOrWhiteSpace(entry.SubcategoryFolder) && entry.SubcategoryFolder != "General"
                    ? entry.SubcategoryFolder
                    : "Miscellaneous"
            };
            return new List<string> { itemClass };
        }
        else if (cat == W3ObjectCategory.Buffs)
        {
            var raceRaw = GetVal("race", "frac");
            var race = raceRaw.ToLowerInvariant() switch
            {
                "human" => "Human",
                "orc" => "Orc",
                "undead" => "Undead",
                "nightelf" => "Night Elf",
                _ => "Neutral"
            };
            return new List<string> { race };
        }
        else if (cat == W3ObjectCategory.Upgrades)
        {
            var raceRaw = GetVal("race", "grac");
            var race = raceRaw.ToLowerInvariant() switch
            {
                "human" => "Human",
                "orc" => "Orc",
                "undead" => "Undead",
                "nightelf" => "Night Elf",
                _ => "Neutral"
            };
            return new List<string> { race };
        }
        else if (cat == W3ObjectCategory.Destructables)
        {
            var catCode = GetVal("category", "bcat");
            var catName = catCode.ToUpperInvariant() switch
            {
                "D" => "Trees/Destructibles",
                "B" => "Bridges/Ramps",
                "P" => "Pathing Blockers",
                _ => !string.IsNullOrWhiteSpace(entry.SubcategoryFolder) && entry.SubcategoryFolder != "General"
                    ? entry.SubcategoryFolder
                    : "Trees/Destructibles"
            };
            return new List<string> { catName };
        }
        else if (cat == W3ObjectCategory.Doodads)
        {
            var catCode = GetVal("category", "dcat");
            var catName = catCode.ToUpperInvariant() switch
            {
                "E" => "Environment",
                "S" => "Structures",
                "W" => "Water",
                "C" => "Cinematic",
                "Z" => "Cliffs",
                _ => !string.IsNullOrWhiteSpace(entry.SubcategoryFolder) && entry.SubcategoryFolder != "General"
                    ? entry.SubcategoryFolder
                    : "Props / Other"
            };
            return new List<string> { catName };
        }

        return new List<string> { string.IsNullOrWhiteSpace(entry.SubcategoryFolder) ? "General" : entry.SubcategoryFolder };
    }

    private void InsertEntryIntoTree(
        W3ObjectTreeNode root,
        List<string> path,
        W3ObjectEntry item,
        bool showRaw,
        ref W3ObjectEntry? entryToSelect,
        string? autoSelectRawcode)
    {
        var current = root;
        var folderStack = new List<W3ObjectTreeNode>();

        foreach (var segment in path)
        {
            var childFolder = current.Children.FirstOrDefault(c => c.IsFolder && c.FolderKey == segment);
            if (childFolder == null)
            {
                childFolder = new W3ObjectTreeNode
                {
                    FolderKey = segment,
                    Title = segment,
                    IsFolder = true,
                    IconSource = FolderIcon
                };
                current.Children.Add(childFolder);
            }
            folderStack.Add(childFolder);
            current = childFolder;
        }

        if (_blpIconService != null)
        {
            item.IconBitmap ??= _blpIconService.GetIcon(item.IconPath, _currentProject);
        }
        var cleanName = StripColorCodes(item.Name);
        var title = showRaw
            ? (item.IsCustom ? $"{item.Rawcode}:{item.BaseRawcode} ({cleanName})" : $"{item.Rawcode} ({cleanName})")
            : (!item.IsStandardUnmodified ? $"{cleanName} [{item.Rawcode}] ★" : cleanName);

        var leafNode = new W3ObjectTreeNode
        {
            Title = title,
            IsFolder = false,
            IconSource = item.IconBitmap,
            ObjectEntry = item
        };
        current.Children.Add(leafNode);

        var isAutoSelected = autoSelectRawcode != null &&
            item.Rawcode.Equals(autoSelectRawcode, StringComparison.OrdinalIgnoreCase);

        if (isAutoSelected)
        {
            entryToSelect = item;
            foreach (var f in folderStack)
            {
                f.IsExpanded = true;
            }
        }
        else if (entryToSelect == null)
        {
            entryToSelect = item;
        }
    }

    private void PostProcessFolderHierarchy(
        W3ObjectTreeNode folder,
        int depth,
        bool isRoot,
        string catTitle,
        string query)
    {
        var subfolders = folder.Children.Where(c => c.IsFolder).ToList();
        var leaves = folder.Children.Where(c => !c.IsFolder).ToList();

        foreach (var sf in subfolders)
        {
            PostProcessFolderHierarchy(sf, depth + 1, false, catTitle, query);
        }

        subfolders.Sort((a, b) => CompareFolderKeys(a.FolderKey, b.FolderKey, _activeCategory, depth));

        leaves.Sort((a, b) =>
        {
            var nameA = StripColorCodes(a.ObjectEntry?.Name ?? a.Title);
            var nameB = StripColorCodes(b.ObjectEntry?.Name ?? b.Title);
            return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        });

        folder.Children.Clear();
        foreach (var sf in subfolders) folder.Children.Add(sf);
        foreach (var leaf in leaves) folder.Children.Add(leaf);

        var totalLeaves = leaves.Count + subfolders.Sum(sf => sf.LeafCount);
        folder.LeafCount = totalLeaves;

        if (isRoot)
        {
            var prefix = folder.FolderKey == "Custom" ? "Custom" : "Standard";
            folder.Title = $"{prefix} {catTitle} ({totalLeaves})";
            folder.IsExpanded = true;
        }
        else
        {
            folder.Title = $"{folder.FolderKey} ({totalLeaves})";

            if (!string.IsNullOrEmpty(query))
            {
                folder.IsExpanded = true;
            }
            else if (!folder.IsExpanded)
            {
                if (depth == 1) // e.g. Human, Permanent
                {
                    folder.IsExpanded = folder.FolderKey is "Human" or "Permanent" or "Trees/Destructibles" or "Environment";
                }
                else if (depth == 2) // e.g. Melee, Campaign
                {
                    folder.IsExpanded = folder.FolderKey is "Melee" or "Units" or "Heroes";
                }
                else if (depth == 3) // e.g. Units, Heroes, Buildings, Special
                {
                    folder.IsExpanded = folder.FolderKey == "Units";
                }
                else
                {
                    folder.IsExpanded = false;
                }
            }
        }
    }

    private static int CompareFolderKeys(string a, string b, W3ObjectCategory category, int depth)
    {
        if (category == W3ObjectCategory.Units)
        {
            if (depth == 0)
            {
                var pA = GetRacePriority(a);
                var pB = GetRacePriority(b);
                if (pA != pB) return pA.CompareTo(pB);
            }
            else if (depth == 1)
            {
                var pA = GetModePriority(a);
                var pB = GetModePriority(b);
                if (pA != pB) return pA.CompareTo(pB);
            }
            else if (depth == 2)
            {
                var pA = GetKindPriority(a);
                var pB = GetKindPriority(b);
                if (pA != pB) return pA.CompareTo(pB);
            }
        }
        else if (category == W3ObjectCategory.Abilities)
        {
            if (depth == 0)
            {
                var pA = GetRacePriority(a);
                var pB = GetRacePriority(b);
                if (pA != pB) return pA.CompareTo(pB);
            }
            else if (depth == 1)
            {
                var pA = GetKindPriority(a);
                var pB = GetKindPriority(b);
                if (pA != pB) return pA.CompareTo(pB);
            }
        }
        else if (category == W3ObjectCategory.Items)
        {
            var pA = GetItemClassPriority(a);
            var pB = GetItemClassPriority(b);
            if (pA != pB) return pA.CompareTo(pB);
        }
        else if (category == W3ObjectCategory.Destructables)
        {
            var pA = GetDestructablePriority(a);
            var pB = GetDestructablePriority(b);
            if (pA != pB) return pA.CompareTo(pB);
        }
        else if (category == W3ObjectCategory.Doodads)
        {
            var pA = GetDoodadPriority(a);
            var pB = GetDoodadPriority(b);
            if (pA != pB) return pA.CompareTo(pB);
        }
        else if (category is W3ObjectCategory.Buffs or W3ObjectCategory.Upgrades)
        {
            var pA = GetRacePriority(a);
            var pB = GetRacePriority(b);
            if (pA != pB) return pA.CompareTo(pB);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetRacePriority(string race) => race switch
    {
        "Human" => 1,
        "Orc" => 2,
        "Undead" => 3,
        "Night Elf" => 4,
        "Neutral Hostile" => 5,
        "Neutral Passive" => 6,
        "Naga" => 7,
        "Items" => 8,
        "Special" => 9,
        "Neutral" => 10,
        _ => 50
    };

    private static int GetModePriority(string mode) => mode switch
    {
        "Melee" => 1,
        "Campaign" => 2,
        "Special" => 3,
        _ => 10
    };

    private static int GetKindPriority(string kind) => kind switch
    {
        "Units" => 1,
        "Heroes" => 2,
        "Buildings" => 3,
        "Special" => 4,
        "Items" => 5,
        _ => 10
    };

    private static int GetItemClassPriority(string cls) => cls switch
    {
        "Permanent" => 1,
        "Charged" => 2,
        "Power Up" => 3,
        "Artifact" => 4,
        "Purchasable" => 5,
        "Campaign" => 6,
        "Miscellaneous" => 7,
        _ => 50
    };

    private static int GetDestructablePriority(string cat) => cat switch
    {
        "Trees/Destructibles" => 1,
        "Pathing Blockers" => 2,
        "Bridges/Ramps" => 3,
        _ => 50
    };

    private static int GetDoodadPriority(string cat) => cat switch
    {
        "Environment" => 1,
        "Structures" => 2,
        "Water" => 3,
        "Cinematic" => 4,
        "Cliffs" => 5,
        "Props / Other" => 6,
        _ => 50
    };

    private static bool MatchesQuery(W3ObjectEntry e, string q)
    {
        return e.Rawcode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.BaseRawcode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.TooltipBasic.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.TooltipExtended.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void TreeObjects_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is W3ObjectTreeNode node && !node.IsFolder && node.ObjectEntry != null)
        {
            DisplayObjectEntry(node.ObjectEntry);
        }
        else
        {
            _selectedEntry = null;
            MdxModelViewer.ClearModel();
            if (Border3DModelSection != null)
                Border3DModelSection.Visibility = Visibility.Collapsed;
        }
    }

    private void DisplayObjectEntry(W3ObjectEntry entry)
    {
        if (_baseDataService == null || _blpIconService == null)
            return;

        _selectedEntry = entry;
        entry.IconBitmap ??= _blpIconService.GetIcon(entry.IconPath, _currentProject);

        _suppressUiEvents = true;
        ImgSelectedObjectHeader.Source = entry.IconBitmap;
        ImgTooltipPreviewIcon.Source = entry.IconBitmap;

        var cleanName = StripColorCodes(entry.Name);
        TxtSelectedObjectHeaderName.Text = $"{cleanName} [{entry.Rawcode}]";
        TxtSelectedObjectHeaderSub.Text = entry.IsCustom
            ? $"Objeto Personalizado • Basado en '{entry.BaseRawcode}' • Carpeta: {entry.SubcategoryFolder}"
            : $"Objeto Estándar 1.27b • Rawcode '{entry.Rawcode}' • Carpeta: {entry.SubcategoryFolder}";

        TxtPropIconPath.Text = entry.IconPath;
        TxtTooltipBasic.Text = entry.TooltipBasic;
        TxtTooltipExtended.Text = entry.TooltipExtended;

        Wc3TooltipRenderer.RenderToTextBlock(TxtPreviewBasic, entry.TooltipBasic);
        Wc3TooltipRenderer.RenderToTextBlock(TxtPreviewExtended, entry.TooltipExtended);
        _suppressUiEvents = false;

        _currentEntryAllFields = _baseDataService.BuildCompleteFieldListForEntry(entry);
        foreach (var f in _currentEntryAllFields)
        {
            var prevCallback = f.ValueUpdatedCallback;
            f.ValueUpdatedCallback = updatedField =>
            {
                prevCallback?.Invoke(updatedField);
                if (!entry.Fields.Contains(updatedField))
                    entry.Fields.Add(updatedField);
                EnsureEntryRegisteredInProject(entry);
                RefreshInspectorAfterFieldEdit(entry);
            };
        }

        RefreshFieldsDataGrid();
        UpdateCommandCardVisual(entry);
        UpdateUnitStatsDisplay(entry);
        Update3DModelDisplay(entry);
    }

    private void EnsureEntryRegisteredInProject(W3ObjectEntry entry)
    {
        if (_currentProject == null)
            return;

        entry.IsStandardUnmodified = false;
        entry.IsModified = true;
        if (!_currentProject.Objects.Contains(entry))
        {
            _currentProject.Objects.Add(entry);
        }
    }

    private void RefreshInspectorAfterFieldEdit(W3ObjectEntry entry)
    {
        if (_blpIconService == null) return;
        entry.RefreshSummaryFromFields();
        entry.IconBitmap = _blpIconService.GetIcon(entry.IconPath, _currentProject);

        _suppressUiEvents = true;
        ImgSelectedObjectHeader.Source = entry.IconBitmap;
        ImgTooltipPreviewIcon.Source = entry.IconBitmap;
        TxtSelectedObjectHeaderName.Text = $"{StripColorCodes(entry.Name)} [{entry.Rawcode}]";
        TxtPropIconPath.Text = entry.IconPath;
        TxtTooltipBasic.Text = entry.TooltipBasic;
        TxtTooltipExtended.Text = entry.TooltipExtended;
        Wc3TooltipRenderer.RenderToTextBlock(TxtPreviewBasic, entry.TooltipBasic);
        Wc3TooltipRenderer.RenderToTextBlock(TxtPreviewExtended, entry.TooltipExtended);
        _suppressUiEvents = false;

        UpdateCommandCardVisual(entry);
        UpdateUnitStatsDisplay(entry);
        Update3DModelDisplay(entry);
    }

    private void UpdateUnitStatsDisplay(W3ObjectEntry? entry)
    {
        if (entry == null || _activeCategory != W3ObjectCategory.Units)
        {
            BorderUnitHudStats.Visibility = Visibility.Collapsed;
            return;
        }

        BorderUnitHudStats.Visibility = Visibility.Visible;

        BaseObjectMetadata? baseMeta = null;
        if (_baseDataService != null)
        {
            if (!string.IsNullOrEmpty(entry.BaseRawcode))
                _baseDataService.AllBaseObjects.TryGetValue(entry.BaseRawcode, out baseMeta);
            if (baseMeta == null)
                _baseDataService.AllBaseObjects.TryGetValue(entry.Rawcode, out baseMeta);
        }

        string GetVal(string colName, string? fieldId = null)
        {
            if (fieldId != null)
            {
                var f = entry.Fields.FirstOrDefault(x => x.FieldId.Equals(fieldId, StringComparison.OrdinalIgnoreCase))
                     ?? _currentEntryAllFields?.FirstOrDefault(x => x.FieldId.Equals(fieldId, StringComparison.OrdinalIgnoreCase));
                if (f != null && !string.IsNullOrWhiteSpace(f.ResolvedValue))
                    return f.ResolvedValue;
            }
            if (baseMeta != null && baseMeta.DefaultFields.TryGetValue(colName, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
            return string.Empty;
        }

        int GetInt(string colName, string? fieldId = null, int defVal = 0)
        {
            var str = GetVal(colName, fieldId);
            return int.TryParse(str, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : defVal;
        }

        // Unit Name
        var cleanName = StripColorCodes(entry.Name);
        TxtHudUnitName.Text = string.IsNullOrWhiteSpace(cleanName) ? entry.Rawcode : cleanName;

        // Damage calculation
        var dmgplus1 = GetInt("dmgplus1", "ua1b", 0);
        var dice1 = GetInt("dice1", "ua1d", 0);
        var sides1 = GetInt("sides1", "ua1s", 0);
        var weapsOn = GetInt("weapsOn", "uaen", 1);
        var def = GetInt("def", "udef", 0);

        // Hero Stats
        var primaryRaw = GetVal("Primary", "upra").Trim().ToUpperInvariant();
        var str = GetInt("STR", "ustr", 0);
        var agi = GetInt("AGI", "uagi", 0);
        var intel = GetInt("INT", "uint", 0);

        var isHero = primaryRaw is "STR" or "AGI" or "INT" ||
                     GetVal("sort2") == "uher" ||
                     GetVal("unitClass").Contains("Hero", StringComparison.OrdinalIgnoreCase);

        // Hero damage bonus: 1 point of Primary Attribute = +1 damage
        var primaryBonus = 0;
        if (primaryRaw == "STR") primaryBonus = str;
        else if (primaryRaw == "AGI") primaryBonus = agi;
        else if (primaryRaw == "INT") primaryBonus = intel;

        var totalBaseDmg = dmgplus1 + primaryBonus;
        var minDmg = totalBaseDmg + (dice1 > 0 ? dice1 : 0);
        var maxDmg = totalBaseDmg + (dice1 > 0 && sides1 > 0 ? (dice1 * sides1) : (dice1 > 0 ? dice1 : 0));

        if (weapsOn == 0 || (minDmg == 0 && maxDmg == 0 && dice1 == 0))
        {
            TxtHudDamage.Text = LocalizationService.L("Ninguno", "None");
        }
        else if (minDmg == maxDmg)
        {
            TxtHudDamage.Text = minDmg.ToString();
        }
        else
        {
            TxtHudDamage.Text = $"{minDmg} - {maxDmg}";
        }

        // Armor
        TxtHudArmor.Text = def.ToString();
        if (isHero && agi > 0)
        {
            var agiArmor = agi * 0.3;
            TxtHudArmorBonus.Text = $"+{agiArmor:0.0}";
        }
        else
        {
            TxtHudArmorBonus.Text = string.Empty;
        }

        // Hero Attributes panel
        if (isHero)
        {
            BorderHeroAttributes.Visibility = Visibility.Visible;
            TxtHudUnitSub.Visibility = Visibility.Visible;

            var primaryTitle = primaryRaw switch
            {
                "STR" => LocalizationService.L("Fuerza", "Strength"),
                "AGI" => LocalizationService.L("Agilidad", "Agility"),
                "INT" => LocalizationService.L("Inteligencia", "Intelligence"),
                _ => LocalizationService.L("Héroe", "Hero")
            };
            TxtHudUnitSub.Text = $"{LocalizationService.L("Héroe", "Hero")} ({primaryTitle})";

            TxtHudStr.Text = str.ToString();
            TxtHudAgi.Text = agi.ToString();
            TxtHudInt.Text = intel.ToString();

            var strLbl = LocalizationService.L("Fuerza:", "Strength:");
            var agiLbl = LocalizationService.L("Agilidad:", "Agility:");
            var intLbl = LocalizationService.L("Inteligencia:", "Intelligence:");

            TxtHudStrLabel.Text = primaryRaw == "STR" ? $"★ {strLbl}" : strLbl;
            TxtHudStrLabel.Foreground = primaryRaw == "STR"
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(220, 200, 140));

            TxtHudAgiLabel.Text = primaryRaw == "AGI" ? $"★ {agiLbl}" : agiLbl;
            TxtHudAgiLabel.Foreground = primaryRaw == "AGI"
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(220, 200, 140));

            TxtHudIntLabel.Text = primaryRaw == "INT" ? $"★ {intLbl}" : intLbl;
            TxtHudIntLabel.Foreground = primaryRaw == "INT"
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(220, 200, 140));
        }
        else
        {
            BorderHeroAttributes.Visibility = Visibility.Collapsed;
            TxtHudUnitSub.Visibility = Visibility.Collapsed;
        }

        // Icons and Attack / Armor type labels
        var atkType1 = GetVal("atkType1", "ua1t").Trim().ToLowerInvariant();
        var (attackIcon, attackTypeLabel) = atkType1 switch
        {
            "normal" => (@"ReplaceableTextures\CommandButtons\BTNAttack.blp", LocalizationService.L("Normal", "Normal")),
            "pierce" => (@"ReplaceableTextures\CommandButtons\BTNHumanMissileUpOne.blp", LocalizationService.L("Perforación", "Pierce")),
            "siege" => (@"ReplaceableTextures\CommandButtons\BTNMortarTeam.blp", LocalizationService.L("Asedio", "Siege")),
            "magic" => (@"ReplaceableTextures\CommandButtons\BTNFireBolt.blp", LocalizationService.L("Mágico", "Magic")),
            "chaos" => (@"ReplaceableTextures\CommandButtons\BTNDoom.blp", LocalizationService.L("Caos", "Chaos")),
            "hero" => (@"ReplaceableTextures\CommandButtons\BTNAttack.blp", LocalizationService.L("Héroe", "Hero")),
            "spells" => (@"ReplaceableTextures\CommandButtons\BTNDispelMagic.blp", LocalizationService.L("Hechizos", "Spells")),
            _ => (@"ReplaceableTextures\CommandButtons\BTNAttack.blp", string.IsNullOrEmpty(atkType1) ? "Normal" : atkType1)
        };
        TxtHudAttackType.Text = $"({attackTypeLabel})";

        var defType = GetVal("defType", "udty").Trim().ToLowerInvariant();
        var (armorIcon, armorTypeLabel) = defType switch
        {
            "small" => (@"ReplaceableTextures\CommandButtons\BTNLeatherUpgradeOne.blp", LocalizationService.L("Ligera", "Light")),
            "medium" => (@"ReplaceableTextures\CommandButtons\BTNHumanArmorUpOne.blp", LocalizationService.L("Media", "Medium")),
            "heavy" or "large" => (@"ReplaceableTextures\CommandButtons\BTNHumanArmorUpTwo.blp", LocalizationService.L("Pesada", "Heavy")),
            "fort" => (@"ReplaceableTextures\CommandButtons\BTNCastle.blp", LocalizationService.L("Fortificada", "Fortified")),
            "hero" => (@"ReplaceableTextures\CommandButtons\BTNCirclet.blp", LocalizationService.L("Héroe", "Hero")),
            "divine" => (@"ReplaceableTextures\CommandButtons\BTNDivineIntervention.blp", LocalizationService.L("Divina", "Divine")),
            "none" or "unarmored" => (@"ReplaceableTextures\CommandButtons\BTNGlove.blp", LocalizationService.L("Sin armadura", "Unarmored")),
            _ => (@"ReplaceableTextures\CommandButtons\BTNHumanArmorUpOne.blp", string.IsNullOrEmpty(defType) ? LocalizationService.L("Media", "Medium") : defType)
        };
        TxtHudArmorType.Text = $"({armorTypeLabel})";

        if (_blpIconService != null)
        {
            ImgHudAttack.Source = _blpIconService.GetIcon(attackIcon);
            ImgHudArmor.Source = _blpIconService.GetIcon(armorIcon);
            if (isHero)
            {
                ImgHudStr.Source ??= _blpIconService.GetIcon(@"ReplaceableTextures\CommandButtons\BTNGauntletsOfOgrePower.blp");
                ImgHudAgi.Source ??= _blpIconService.GetIcon(@"ReplaceableTextures\CommandButtons\BTNSlippersOfAgility.blp");
                ImgHudInt.Source ??= _blpIconService.GetIcon(@"ReplaceableTextures\CommandButtons\BTNMantleOfIntelligence.blp");
            }
        }
    }

    private void RefreshFieldsDataGrid()
    {
        if (_baseDataService == null)
            return;

        var showRaw = ChkRawData.IsChecked == true;
        var onlyModified = ChkOnlyModified.IsChecked == true;
        var filter = TxtFieldFilter.Text.Trim();

        IEnumerable<W3ObjectField> list = _currentEntryAllFields;
        foreach (var f in list)
        {
            f.FieldName = _baseDataService.GetFieldDisplayName(f.FieldId, showRaw);
            f.FormattedDisplayOverride = showRaw
                ? null
                : (FlagsChecklistWindow.FormatValueForDisplay(f)
                   ?? ObjectListPickerWindow.FormatValueForDisplay(f, _baseDataService, _currentProject));
            f.NotifyLocalizationChanged();
        }

        if (onlyModified)
            list = list.Where(f => f.IsModified);

        if (!string.IsNullOrEmpty(filter))
        {
            list = list.Where(f =>
                f.FieldId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                f.FieldName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                f.ResolvedValue.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        GridFields.ItemsSource = list.ToList();
    }

    private void TxtFieldFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshFieldsDataGrid();

    private void ChkDisplayOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        RebuildObjectTree(_selectedEntry?.Rawcode);
        RefreshFieldsDataGrid();
    }

    private void GridFields_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GridFields.SelectedItem is W3ObjectField field)
        {
            OpenEditDialogForField(field);
        }
    }

    private void GridFields_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && GridFields.SelectedItem is W3ObjectField field)
        {
            e.Handled = true;
            OpenEditDialogForField(field);
        }
    }

    private void InlineChoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedEntry == null)
            return;
        if (sender is ComboBox cmb && !cmb.IsDropDownOpen && !cmb.IsKeyboardFocusWithin)
            return;

        EnsureEntryRegisteredInProject(_selectedEntry);
    }

    private void OpenEditDialogForField(W3ObjectField field)
    {
        if (_selectedEntry == null)
            return;

        // Campos con opciones desplegables se eligen inline en la misma tabla (sin abrir ventana extra)
        if (field.HasChoices)
        {
            if (GridFields.ItemContainerGenerator.ContainerFromItem(field) is DataGridRow row)
            {
                var combo = FindVisualChild<ComboBox>(row);
                if (combo != null)
                {
                    combo.Focus();
                    combo.IsDropDownOpen = true;
                }
            }
            return;
        }

        var win = Window.GetWindow(this);

        // Campos de Banderas por Casillas (Targets Allowed, Clasificación de Unidad, Colocación, Tilesets)
        if (FlagsChecklistWindow.TryGetFlagFieldKind(field, out var flagKind))
        {
            var dlgFlags = new FlagsChecklistWindow(_selectedEntry, field, flagKind)
            {
                Owner = win
            };

            if (dlgFlags.ShowDialog() == true)
            {
                ApplyEditedFieldValue(field, dlgFlags.SelectedValue);
            }
            return;
        }

        // Campos de Listas de Objetos / Árbol Tecnológico (Habilidades de Héroe/Unidad, Upgrades, Requisitos, Entrenamiento, etc.)
        if (ObjectListPickerWindow.TryGetObjectPickerKind(field, out var objPickerKind) &&
            _baseDataService != null && _blpIconService != null)
        {
            var dlgObjList = new ObjectListPickerWindow(
                _baseDataService,
                _blpIconService,
                _currentProject,
                _selectedEntry,
                field,
                objPickerKind)
            {
                Owner = win
            };

            if (dlgObjList.ShowDialog() == true)
            {
                ApplyEditedFieldValue(field, dlgObjList.SelectedCsvValue);
            }
            return;
        }

        // Campos de Modelo 3D -> Selector con todas las rutas + previsualización 3D
        if (IsModelField(field.FieldId) && _blpIconService != null)
        {
            var dlgModel = new ModelPickerWindow(_blpIconService, _currentProject, field.ResolvedValue, field.FieldName)
            {
                Owner = win
            };

            if (dlgModel.ShowDialog() == true)
            {
                ApplyEditedFieldValue(field, dlgModel.SelectedModelPath);
            }
            return;
        }

        // Campos de Icono -> Selector con todas las rutas + vista previa + verificación DISBTN
        if (IsIconField(field.FieldId) && _blpIconService != null)
        {
            var dlgIcon = new IconPickerWindow(
                _blpIconService,
                _currentProject,
                field.ResolvedValue,
                field.FieldName,
                path => RequestOpenIconManager?.Invoke(path))
            {
                Owner = win
            };

            if (dlgIcon.ShowDialog() == true)
            {
                ApplyEditedFieldValue(field, dlgIcon.SelectedIconPath);
            }
            return;
        }

        var dlg = new EditFieldDialog(_selectedEntry, field)
        {
            Owner = win
        };

        if (dlg.ShowDialog() == true)
        {
            ApplyEditedFieldValue(field, dlg.NewValue);
        }
    }

    private void ApplyEditedFieldValue(W3ObjectField field, string newValue)
    {
        if (_selectedEntry == null)
            return;

        field.ResolvedValue = newValue;
        field.IsModified = true;
        if (!_selectedEntry.Fields.Contains(field))
            _selectedEntry.Fields.Add(field);

        EnsureEntryRegisteredInProject(_selectedEntry);
        RefreshInspectorAfterFieldEdit(_selectedEntry);
        RefreshFieldsDataGrid();
    }

    private static bool IsModelField(string fieldId) => fieldId.ToLowerInvariant() switch
    {
        "umdl" or "bfil" or "dfil" or "ifil" or "ua1m" or "ua2m"
        or "acat" or "atat" or "asat" or "aeat" or "aaea" or "amat"
        or "ftat" or "feft" or "fsat" or "feat" or "fmat" => true,
        _ => false
    };

    private static bool IsIconField(string fieldId) => fieldId.ToLowerInvariant() switch
    {
        "uico" or "ussi" or "iico" or "aart" or "auar" or "arar" or "fart" or "gar1" => true,
        _ => false
    };

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private void GridFields_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_selectedEntry != null && e.Row.Item is W3ObjectField field)
        {
            Dispatcher.BeginInvoke(() =>
            {
                field.IsModified = true;
                EnsureEntryRegisteredInProject(_selectedEntry);
                RefreshInspectorAfterFieldEdit(_selectedEntry);
            });
        }
    }

    private void GridFields_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    // ==================== CARTA DE COMANDO 4x3 INTERACTIVA ====================

    private void BuildCommandCardButtons()
    {
        GridCommandCard.Children.Clear();
        string[,] defaultHotkeys =
        {
            { "Q", "W", "E", "R" },
            { "A", "S", "D", "F" },
            { "Z", "X", "C", "V" }
        };

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var cx = x;
                var cy = y;
                var btn = new Button
                {
                    Margin = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(30, 32, 38)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(95, 95, 95)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(0),
                    Tag = (cx, cy),
                    ToolTip = $"Posición de Botón ({cx}, {cy}) — Clic para asignar aquí [{defaultHotkeys[cy, cx]}]"
                };
                btn.Click += (_, _) => AssignButtonPosition(cx, cy);
                GridCommandCard.Children.Add(btn);
            }
        }
    }

    private void UpdateCommandCardVisual(W3ObjectEntry entry)
    {
        var bx = Math.Clamp(entry.ButtonPosX, 0, 3);
        var by = Math.Clamp(entry.ButtonPosY, 0, 2);
        TxtCardPosInfo.Text = $"Button Pos: ({bx}, {by})";
        TxtCardHotkeyInfo.Text = $"Hotkey: [{(string.IsNullOrEmpty(entry.Hotkey) ? "-" : entry.Hotkey)}]";

        foreach (var child in GridCommandCard.Children)
        {
            if (child is not Button btn || btn.Tag is not ValueTuple<int, int> pos)
                continue;

            if (pos.Item1 == bx && pos.Item2 == by)
            {
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 204, 0));
                btn.BorderThickness = new Thickness(2);
                btn.Content = new Image
                {
                    Source = entry.IconBitmap,
                    Stretch = Stretch.Fill
                };
            }
            else
            {
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(75, 75, 75));
                btn.BorderThickness = new Thickness(1);
                btn.Content = new TextBlock
                {
                    Text = $"{pos.Item1},{pos.Item2}",
                    Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                    FontSize = 9
                };
            }
        }
    }

    private void AssignButtonPosition(int x, int y)
    {
        if (_selectedEntry == null)
            return;

        _selectedEntry.ButtonPosX = x;
        _selectedEntry.ButtonPosY = y;
        EnsureEntryRegisteredInProject(_selectedEntry);

        var xCode = _selectedEntry.Category == W3ObjectCategory.Abilities ? "abpx" : "ubpx";
        var yCode = _selectedEntry.Category == W3ObjectCategory.Abilities ? "abpy" : "ubpy";
        SetOrCreateField(_selectedEntry, xCode, x.ToString());
        SetOrCreateField(_selectedEntry, yCode, y.ToString());

        RefreshFieldsDataGrid();
        UpdateCommandCardVisual(_selectedEntry);
    }

    private void SetOrCreateField(W3ObjectEntry entry, string fieldCode, string newValue)
    {
        var field = _currentEntryAllFields.FirstOrDefault(f => f.FieldId.Equals(fieldCode, StringComparison.OrdinalIgnoreCase) && f.Level <= 1)
                    ?? entry.Fields.FirstOrDefault(f => f.FieldId.Equals(fieldCode, StringComparison.OrdinalIgnoreCase));

        if (field != null)
        {
            field.ResolvedValue = newValue;
            field.IsModified = true;
            if (!entry.Fields.Contains(field))
                entry.Fields.Add(field);
        }
        else
        {
            var created = new W3ObjectField
            {
                FieldId = fieldCode,
                FieldIntId = WarcraftBaseDataService.RawcodeToInt(fieldCode),
                FieldName = _baseDataService?.GetFieldDisplayName(fieldCode) ?? fieldCode,
                Level = 0,
                ResolvedValue = newValue,
                RawValue = newValue,
                IsModified = true
            };
            entry.Fields.Add(created);
            _currentEntryAllFields.Insert(0, created);
        }
    }

    // ==================== EDICIÓN DE TOOLTIPS E ICONOS EN VIVO ====================

    private void TxtPropIconPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents || _selectedEntry == null || _blpIconService == null)
            return;

        _selectedEntry.IconPath = TxtPropIconPath.Text.Trim();
        _selectedEntry.IconBitmap = _blpIconService.GetIcon(_selectedEntry.IconPath, _currentProject);
        ImgSelectedObjectHeader.Source = _selectedEntry.IconBitmap;
        ImgTooltipPreviewIcon.Source = _selectedEntry.IconBitmap;

        var iconField = _selectedEntry.Category switch
        {
            W3ObjectCategory.Items => "iico",
            W3ObjectCategory.Abilities => "aart",
            W3ObjectCategory.Buffs => "fart",
            W3ObjectCategory.Upgrades => "gar1",
            _ => "uico"
        };
        EnsureEntryRegisteredInProject(_selectedEntry);
        SetOrCreateField(_selectedEntry, iconField, _selectedEntry.IconPath);
        UpdateCommandCardVisual(_selectedEntry);
    }

    private void TxtTooltipBasic_TextChanged(object sender, TextChangedEventArgs e)
    {
        Wc3TooltipRenderer.RenderToTextBlock(TxtPreviewBasic, TxtTooltipBasic.Text);
        if (_suppressUiEvents || _selectedEntry == null)
            return;

        _selectedEntry.TooltipBasic = TxtTooltipBasic.Text;
        EnsureEntryRegisteredInProject(_selectedEntry);
        var tipCode = _selectedEntry.Category switch
        {
            W3ObjectCategory.Abilities => "atp1",
            W3ObjectCategory.Buffs => "ftip",
            W3ObjectCategory.Upgrades => "gtp1",
            _ => "utip"
        };
        SetOrCreateField(_selectedEntry, tipCode, TxtTooltipBasic.Text);
    }

    private void TxtTooltipExtended_TextChanged(object sender, TextChangedEventArgs e)
    {
        Wc3TooltipRenderer.RenderToTextBlock(TxtPreviewExtended, TxtTooltipExtended.Text);
        if (_suppressUiEvents || _selectedEntry == null)
            return;

        _selectedEntry.TooltipExtended = TxtTooltipExtended.Text;
        EnsureEntryRegisteredInProject(_selectedEntry);
        var uberCode = _selectedEntry.Category switch
        {
            W3ObjectCategory.Abilities => "aub1",
            W3ObjectCategory.Buffs => "fube",
            W3ObjectCategory.Upgrades => "gub1",
            _ => "utub"
        };
        SetOrCreateField(_selectedEntry, uberCode, TxtTooltipExtended.Text);
    }

    // ==================== CREAR / CLONAR OBJETO Y RESTAURAR CAMPO ====================

    private void BtnNewCustomObject_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject == null || _selectedEntry == null)
            return;

        var prefix = _activeCategory switch
        {
            W3ObjectCategory.Items => 'I',
            W3ObjectCategory.Abilities => 'A',
            W3ObjectCategory.Buffs => 'B',
            W3ObjectCategory.Destructables => 'B',
            W3ObjectCategory.Upgrades => 'R',
            _ => 'h'
        };

        // Find next unused rawcode (e.g. I000..I999 / I00A..I0ZZ)
        var newRawcode = GenerateNextFreeRawcode(prefix);
        var baseRaw = _selectedEntry.IsCustom ? _selectedEntry.BaseRawcode : _selectedEntry.Rawcode;

        var clone = new W3ObjectEntry
        {
            Category = _activeCategory,
            SubcategoryFolder = _selectedEntry.SubcategoryFolder,
            BaseRawcode = baseRaw,
            Rawcode = newRawcode,
            OldId = WarcraftBaseDataService.RawcodeToInt(baseRaw),
            NewId = WarcraftBaseDataService.RawcodeToInt(newRawcode),
            Name = $"{StripColorCodes(_selectedEntry.Name)} (Copia)",
            TooltipBasic = _selectedEntry.TooltipBasic,
            TooltipExtended = _selectedEntry.TooltipExtended,
            Hotkey = _selectedEntry.Hotkey,
            IconPath = _selectedEntry.IconPath,
            ButtonPosX = _selectedEntry.ButtonPosX,
            ButtonPosY = _selectedEntry.ButtonPosY,
            IconBitmap = _selectedEntry.IconBitmap,
            IsModified = true
        };

        foreach (var f in _selectedEntry.Fields)
        {
            clone.Fields.Add(new W3ObjectField
            {
                FieldId = f.FieldId,
                FieldIntId = f.FieldIntId,
                FieldName = f.FieldName,
                Level = f.Level,
                Variation = f.Variation,
                DataPointer = f.DataPointer,
                DataType = f.DataType,
                RawValue = f.RawValue,
                ResolvedValue = f.ResolvedValue,
                IsModified = true
            });
        }

        var nameField = _activeCategory switch
        {
            W3ObjectCategory.Abilities => "anam",
            W3ObjectCategory.Buffs => "fnam",
            W3ObjectCategory.Upgrades => "gnam",
            _ => "unam"
        };
        SetOrCreateField(clone, nameField, clone.Name);

        _currentProject.Objects.Add(clone);
        RebuildObjectTree(newRawcode);
    }

    private string GenerateNextFreeRawcode(char prefix)
    {
        var used = new HashSet<string>(
            (_currentProject?.Objects.Select(o => o.Rawcode) ?? Enumerable.Empty<string>())
            .Concat(_baseDataService?.AllBaseObjects.Keys ?? Enumerable.Empty<string>()),
            StringComparer.OrdinalIgnoreCase);

        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (var i = 1; i < 46656; i++)
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

    private void BtnResetFieldToDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || GridFields.SelectedItem is not W3ObjectField field || !field.IsModified)
            return;

        _selectedEntry.Fields.Remove(field);
        DisplayObjectEntry(_selectedEntry);
    }

    private void BtnPopoutWindow_Click(object sender, RoutedEventArgs e)
    {
        RequestOpenStandaloneWindow?.Invoke();
    }

    private static string StripColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return Regex.Replace(text, @"\|c[0-9a-fA-F]{8}|\|r|\|n", " ").Trim();
    }

    private static BitmapSource CreateClassicYellowFolderIcon()
    {
        const int w = 16, h = 16;
        var px = new byte[w * h * 4];
        for (var y = 2; y < 14; y++)
        {
            for (var x = 1; x < 15; x++)
            {
                if (y < 4 && x > 7) continue; // Folder tab top-right cutout
                var isBorder = x == 1 || x == 14 || y == 2 || y == 13 || (y == 4 && x >= 7);
                var idx = ((y * w) + x) * 4;
                px[idx] = isBorder ? (byte)35 : (byte)95;      // B
                px[idx + 1] = isBorder ? (byte)135 : (byte)215; // G
                px[idx + 2] = isBorder ? (byte)185 : (byte)255; // R
                px[idx + 3] = 255;
            }
        }
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        wb.Freeze();
        return wb;
    }

    private void BtnToggle3DModelView_Click(object sender, RoutedEventArgs e)
    {
        if (Border3DModelContainer.Visibility == Visibility.Visible)
        {
            Border3DModelContainer.Visibility = Visibility.Collapsed;
            BtnToggle3DModelView.Content = "Mostrar Visor 3D";
        }
        else
        {
            Border3DModelContainer.Visibility = Visibility.Visible;
            BtnToggle3DModelView.Content = "Ocultar Visor 3D";
            if (_selectedEntry != null)
            {
                Update3DModelDisplay(_selectedEntry);
            }
        }
    }

    private sealed class ModelVariantOption
    {
        public string DisplayLabel { get; set; } = string.Empty;
        public string MdxPath { get; set; } = string.Empty;
    }

    private bool _suppressVariantChange;

    private void Update3DModelDisplay(W3ObjectEntry entry)
    {
        if (Border3DModelSection == null)
            return;

        var variants = GetAvailableModelsForEntry(entry);
        if (variants.Count == 0 || _blpIconService == null)
        {
            // No tiene modelo o no existe en MPQ/Importaciones: limpiar visor y ocultar apartado 3D
            MdxModelViewer.ClearModel();
            if (CmbModelEffectVariant != null)
            {
                CmbModelEffectVariant.ItemsSource = null;
                CmbModelEffectVariant.Visibility = Visibility.Collapsed;
            }
            Border3DModelSection.Visibility = Visibility.Collapsed;
            return;
        }

        Border3DModelSection.Visibility = Visibility.Visible;

        if (CmbModelEffectVariant != null)
        {
            _suppressVariantChange = true;
            CmbModelEffectVariant.ItemsSource = variants;
            CmbModelEffectVariant.SelectedIndex = 0;
            CmbModelEffectVariant.Visibility = variants.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _suppressVariantChange = false;
        }

        if (Border3DModelContainer != null && Border3DModelContainer.Visibility == Visibility.Visible)
        {
            MdxModelViewer.SetContext(_blpIconService, _currentProject);
            MdxModelViewer.LoadModel(variants[0].MdxPath);
        }
    }

    private void CmbModelEffectVariant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVariantChange || _blpIconService == null)
            return;
        if (CmbModelEffectVariant?.SelectedItem is ModelVariantOption opt &&
            Border3DModelContainer != null &&
            Border3DModelContainer.Visibility == Visibility.Visible)
        {
            MdxModelViewer.SetContext(_blpIconService, _currentProject);
            MdxModelViewer.LoadModel(opt.MdxPath);
        }
    }

    private List<ModelVariantOption> GetAvailableModelsForEntry(W3ObjectEntry entry)
    {
        var results = new List<ModelVariantOption>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        BaseObjectMetadata? baseMeta = null;
        if (_baseDataService != null)
        {
            var raw = string.IsNullOrEmpty(entry.BaseRawcode) ? entry.Rawcode : entry.BaseRawcode;
            baseMeta = _baseDataService.GetBaseObject(raw);
        }

        void TryAddFieldCandidates(string label, string fieldId, params string[] slkKeys)
        {
            string? rawVal = null;
            var f = entry.Fields.FirstOrDefault(x => x.FieldId.Equals(fieldId, StringComparison.OrdinalIgnoreCase));
            if (f != null && !string.IsNullOrWhiteSpace(f.ResolvedValue))
                rawVal = f.ResolvedValue;

            if (string.IsNullOrWhiteSpace(rawVal) && baseMeta != null)
            {
                foreach (var slkKey in slkKeys)
                {
                    var match = baseMeta.DefaultFields.FirstOrDefault(kv =>
                        kv.Key.Equals(slkKey, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(match.Value))
                    {
                        rawVal = match.Value;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(rawVal))
                return;

            var parts = rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var resolved = ResolveExistingModelPath(parts[i]);
                if (!string.IsNullOrEmpty(resolved) && seenPaths.Add(resolved))
                {
                    var suffix = parts.Length > 1 ? $" #{i + 1}" : string.Empty;
                    results.Add(new ModelVariantOption
                    {
                        DisplayLabel = $"{label}{suffix}",
                        MdxPath = resolved
                    });
                }
            }
        }

        switch (entry.Category)
        {
            case W3ObjectCategory.Units:
                TryAddFieldCandidates("Modelo Unidad", "umdl", "file", "model", "fileModel");
                TryAddFieldCandidates("Proyectil 1", "ua1m", "Missileart", "Missileart_1");
                TryAddFieldCandidates("Proyectil 2", "ua2m", "Missileart_2");
                break;

            case W3ObjectCategory.Items:
                TryAddFieldCandidates("Modelo Ítem", "ifil", "file", "model");
                break;

            case W3ObjectCategory.Destructables:
                TryAddFieldCandidates("Destructible", "bfil", "file", "model");
                break;

            case W3ObjectCategory.Doodads:
                TryAddFieldCandidates("Doodad", "dfil", "file", "model");
                break;

            case W3ObjectCategory.Abilities:
                TryAddFieldCandidates("Efecto Objetivo", "atat", "TargetArt", "Targetart");
                TryAddFieldCandidates("Efecto Especial", "asat", "SpecialArt", "Specialart");
                TryAddFieldCandidates("Efecto Área", "aeat", "EffectArt", "Effectart", "Effect");
                TryAddFieldCandidates("Efecto Persistente", "aaea", "AreaeffectArt", "Areaeffectart");
                TryAddFieldCandidates("Proyectil / Misil", "amat", "Missileart", "MissileArt");
                TryAddFieldCandidates("Efecto Lanzador", "acat", "CasterArt", "Casterart");
                break;

            case W3ObjectCategory.Buffs:
                TryAddFieldCandidates("Efecto Objetivo", "ftat", "TargetArt", "Targetart");
                TryAddFieldCandidates("Efecto Visual", "feft", "EffectArt", "Effectart", "Effect");
                TryAddFieldCandidates("Efecto Especial", "fsat", "SpecialArt", "Specialart");
                TryAddFieldCandidates("Efecto Área", "feat", "Areaeffectart");
                TryAddFieldCandidates("Proyectil", "fmat", "Missileart", "MissileArt");
                break;

            default:
                TryAddFieldCandidates("Modelo", "umdl", "file", "model", "fileModel");
                break;
        }

        return results;
    }

    private string ResolveExistingModelPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        var trimmed = rawPath.Trim().Trim('"');
        if (trimmed == "_" || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("_.mdl", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("_.mdx", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var normalized = NormalizeMdxPath(trimmed);
        if (_blpIconService == null)
            return normalized;

        var tileset = _currentProject?.Terrain != null ? _currentProject.Terrain.Tileset.ToString() : null;
        if (_blpIconService.FindRawFileBytes(normalized, _currentProject, tileset) != null)
            return normalized;

        // Muchos destructibles/doodads base omiten el índice de variación (ej: AshenTree -> AshenTree0.mdx)
        if (normalized.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
        {
            var var0 = normalized.Substring(0, normalized.Length - 4) + "0.mdx";
            if (_blpIconService.FindRawFileBytes(var0, _currentProject, tileset) != null)
                return var0;
        }

        return string.Empty;
    }

    public string GetModelPathForEntry(W3ObjectEntry entry)
    {
        var variants = GetAvailableModelsForEntry(entry);
        return variants.Count > 0 ? variants[0].MdxPath : string.Empty;
    }

    private static string NormalizeMdxPath(string path)
    {
        var clean = path.Trim().Trim('"').Replace('/', '\\');
        if (clean.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(0, clean.Length - 4) + ".mdx";
        else if (!clean.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            clean += ".mdx";
        return clean;
    }
}

