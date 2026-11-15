using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using W3Studio.Services;

namespace W3Studio;

public partial class NewMapWindow : Window
{
    private readonly WarcraftBaseDataService _baseDataService;
    public string CreatedMapPath { get; private set; } = string.Empty;

    public NewMapWindow(WarcraftBaseDataService baseDataService, string? war3Dir)
    {
        InitializeComponent();
        _baseDataService = baseDataService;

        string mapsDir = !string.IsNullOrWhiteSpace(war3Dir) && Directory.Exists(Path.Combine(war3Dir, "Maps"))
            ? Path.Combine(war3Dir, "Maps")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Warcraft III", "Maps");

        TxtOutputPath.Text = Path.Combine(mapsDir, "NuevoMapaW3Studio.w3m");
    }

    private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Guardar Nuevo Mapa de Warcraft III",
            Filter = "Warcraft III Map (*.w3m;*.w3x)|*.w3m;*.w3x|Todos los archivos (*.*)|*.*",
            DefaultExt = ".w3m",
            FileName = Path.GetFileName(TxtOutputPath.Text)
        };

        var dir = Path.GetDirectoryName(TxtOutputPath.Text);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            dlg.InitialDirectory = dir;

        if (dlg.ShowDialog() == true)
            TxtOutputPath.Text = dlg.FileName;
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        string path = TxtOutputPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Indica la ruta donde guardar el mapa.", "Nuevo Mapa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int cells = 96;
        if (CmbMapSize.SelectedItem is ComboBoxItem sizeItem && int.TryParse(sizeItem.Tag?.ToString(), out int parsedSize))
            cells = parsedSize;

        char tileset = 'L';
        if (CmbTileset.SelectedItem is ComboBoxItem tsItem && tsItem.Tag is string tsTag && tsTag.Length > 0)
            tileset = tsTag[0];

        try
        {
            var opts = new NewMapCreationOptions
            {
                FilePath = path,
                MapName = string.IsNullOrWhiteSpace(TxtMapName.Text) ? "Nuevo Mapa" : TxtMapName.Text.Trim(),
                Author = string.IsNullOrWhiteSpace(TxtAuthor.Text) ? "W3 Studio" : TxtAuthor.Text.Trim(),
                CellsWidth = cells,
                CellsHeight = cells,
                TilesetChar = tileset,
                InitialDeepWater = RadDeepWater.IsChecked == true,
                BaseCliffLayer = 2
            };

            MapShadowAndMinimapService.CreateBlankMap(opts, _baseDataService);
            CreatedMapPath = path;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error creando el nuevo mapa:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

