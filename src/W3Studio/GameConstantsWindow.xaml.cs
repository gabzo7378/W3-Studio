using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class GameConstantsWindow : Window
{
    private readonly MapProject _project;
    private readonly BlpIconService _blpService;
    private readonly Action _onApplied;
    private List<IniKeyEntry> _miscEntries = new();
    private List<IniKeyEntry> _skinEntries = new();
    private bool _isLoading = true;

    public GameConstantsWindow(MapProject project, BlpIconService blpService, Action onApplied, int initialTabIndex = 0)
    {
        InitializeComponent();
        _project = project;
        _blpService = blpService;
        _onApplied = onApplied;

        if (initialTabIndex >= 0 && initialTabIndex < TabsAdvanced.Items.Count)
            TabsAdvanced.SelectedIndex = initialTabIndex;

        LoadEntries();
    }

    private void LoadEntries()
    {
        _isLoading = true;
        _miscEntries = GameConstantsService.LoadGameplayConstants(_project, _blpService);
        _skinEntries = GameConstantsService.LoadGameInterface(_project, _blpService);

        var miscCats = new List<string> { "Todas" };
        miscCats.AddRange(_miscEntries.Select(e => e.Category).Distinct().OrderBy(c => c));
        CmbMiscCategory.ItemsSource = miscCats;
        CmbMiscCategory.SelectedIndex = 0;

        var skinCats = new List<string> { "Todos" };
        skinCats.AddRange(_skinEntries.Select(e => e.Category).Distinct().OrderBy(c => c));
        CmbSkinCategory.ItemsSource = skinCats;
        CmbSkinCategory.SelectedIndex = 0;

        _isLoading = false;
        RefreshMiscGrid();
        RefreshSkinGrid();
    }

    private void MiscFilter_Changed(object sender, EventArgs e)
    {
        if (!_isLoading) RefreshMiscGrid();
    }

    private void SkinFilter_Changed(object sender, EventArgs e)
    {
        if (!_isLoading) RefreshSkinGrid();
    }

    private void RefreshMiscGrid()
    {
        var cat = CmbMiscCategory.SelectedItem as string;
        var q = TxtMiscSearch.Text.Trim();
        bool modOnly = ChkMiscModifiedOnly.IsChecked == true;

        var filtered = _miscEntries.Where(e =>
            (!modOnly || e.IsModified) &&
            (string.IsNullOrEmpty(cat) || cat == "Todas" || e.Category == cat) &&
            (string.IsNullOrEmpty(q) ||
             e.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             e.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             e.CurrentValue.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        GridMisc.ItemsSource = filtered;
        TxtMiscSummary.Text = $"{_miscEntries.Count(e => e.IsModified)} constante(s) modificada(s) de {_miscEntries.Count} totales";
    }

    private void RefreshSkinGrid()
    {
        var cat = CmbSkinCategory.SelectedItem as string;
        var q = TxtSkinSearch.Text.Trim();
        bool modOnly = ChkSkinModifiedOnly.IsChecked == true;

        var filtered = _skinEntries.Where(e =>
            (!modOnly || e.IsModified) &&
            (string.IsNullOrEmpty(cat) || cat == "Todos" || e.Category == cat) &&
            (string.IsNullOrEmpty(q) ||
             e.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             e.Section.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             e.CurrentValue.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        GridSkin.ItemsSource = filtered;
        TxtSkinSummary.Text = $"{_skinEntries.Count(e => e.IsModified)} entrada(s) modificada(s) de {_skinEntries.Count} totales";
    }

    private void BtnResetMiscRow_Click(object sender, RoutedEventArgs e)
    {
        if (GridMisc.SelectedItem is IniKeyEntry entry)
        {
            entry.CurrentValue = entry.DefaultValue;
            GridMisc.Items.Refresh();
            TxtMiscSummary.Text = $"{_miscEntries.Count(x => x.IsModified)} constante(s) modificada(s) de {_miscEntries.Count} totales";
        }
    }

    private void BtnResetSkinRow_Click(object sender, RoutedEventArgs e)
    {
        if (GridSkin.SelectedItem is IniKeyEntry entry)
        {
            entry.CurrentValue = entry.DefaultValue;
            GridSkin.Items.Refresh();
            TxtSkinSummary.Text = $"{_skinEntries.Count(x => x.IsModified)} entrada(s) modificada(s) de {_skinEntries.Count} totales";
        }
    }

    private void BtnPickSkinIcon_Click(object sender, RoutedEventArgs e)
    {
        if (GridSkin.SelectedItem is not IniKeyEntry entry)
        {
            MessageBox.Show("Selecciona primero una fila en la tabla de Interfaz de Juego.", "Game Interface", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new IconPickerWindow(_blpService, _project, entry.CurrentValue, entry.DisplayName) { Owner = this };
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedIconPath))
        {
            entry.CurrentValue = picker.SelectedIconPath;
            GridSkin.Items.Refresh();
            TxtSkinSummary.Text = $"{_skinEntries.Count(x => x.IsModified)} entrada(s) modificada(s) de {_skinEntries.Count} totales";
        }
    }

    private void BtnPickSkinModel_Click(object sender, RoutedEventArgs e)
    {
        if (GridSkin.SelectedItem is not IniKeyEntry entry)
        {
            MessageBox.Show("Selecciona primero una fila en la tabla de Interfaz de Juego.", "Game Interface", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new ModelPickerWindow(_blpService, _project, entry.CurrentValue, entry.DisplayName) { Owner = this };
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedModelPath))
        {
            entry.CurrentValue = picker.SelectedModelPath;
            GridSkin.Items.Refresh();
            TxtSkinSummary.Text = $"{_skinEntries.Count(x => x.IsModified)} entrada(s) modificada(s) de {_skinEntries.Count} totales";
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        GridMisc.CommitEdit();
        GridSkin.CommitEdit();

        GameConstantsService.SaveGameplayConstants(_project, _miscEntries);
        GameConstantsService.SaveGameInterface(_project, _skinEntries);

        _onApplied();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
