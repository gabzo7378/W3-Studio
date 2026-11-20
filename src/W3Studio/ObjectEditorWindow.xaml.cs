using System.Windows;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class ObjectEditorWindow : Window
{
    public event System.Action<string?>? RequestOpenIconManager;
    public event System.Action<W3ObjectEntry?>? ObjectDataChanged;

    public ObjectEditorWindow(
        WarcraftBaseDataService baseDataService,
        BlpIconService blpIconService,
        MapProject? project,
        W3ObjectCategory initialCategory = W3ObjectCategory.Units,
        string? searchRawcode = null)
    {
        InitializeComponent();
        ObjEditorControl.HidePopoutButton();
        ObjEditorControl.RequestOpenIconManager += path => RequestOpenIconManager?.Invoke(path);
        ObjEditorControl.ObjectDataChanged += entry => ObjectDataChanged?.Invoke(entry);
        ObjEditorControl.InitializeServices(baseDataService, blpIconService);
        ObjEditorControl.LoadProject(project);
        ObjEditorControl.SelectCategoryAndSearch(initialCategory, searchRawcode);
    }

    public void SelectCategoryAndSearch(W3ObjectCategory category, string? searchRawcode = null)
    {
        ObjEditorControl.SelectCategoryAndSearch(category, searchRawcode);
    }

    public void LoadProject(MapProject? project)
    {
        ObjEditorControl.LoadProject(project);
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e) => Close();
    private void MenuNewObject_Click(object sender, RoutedEventArgs e) => ObjEditorControl.OpenNewCustomObjectDialog();
    private void MenuCatUnits_Click(object sender, RoutedEventArgs e) => ObjEditorControl.SelectCategoryAndSearch(W3ObjectCategory.Units);
    private void MenuCatItems_Click(object sender, RoutedEventArgs e) => ObjEditorControl.SelectCategoryAndSearch(W3ObjectCategory.Items);
    private void MenuCatDestructables_Click(object sender, RoutedEventArgs e) => ObjEditorControl.SelectCategoryAndSearch(W3ObjectCategory.Destructables);
    private void MenuCatDoodads_Click(object sender, RoutedEventArgs e) => ObjEditorControl.SelectCategoryAndSearch(W3ObjectCategory.Doodads);
    private void MenuCatAbilities_Click(object sender, RoutedEventArgs e) => ObjEditorControl.SelectCategoryAndSearch(W3ObjectCategory.Abilities);
    private void MenuCatBuffs_Click(object sender, RoutedEventArgs e) => ObjEditorControl.SelectCategoryAndSearch(W3ObjectCategory.Buffs);
    private void MenuCatUpgrades_Click(object sender, RoutedEventArgs e) => ObjEditorControl.SelectCategoryAndSearch(W3ObjectCategory.Upgrades);
}

