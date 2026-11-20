using System;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using W3Studio.Models;

namespace W3Studio;

public partial class ImportHeightmapWindow : Window
{
    public string SelectedImagePath => TxtImagePath.Text.Trim();
    public float MinZ { get; private set; } = -128f;
    public float MaxZ { get; private set; } = 512f;
    public bool SmoothPass => ChkSmooth.IsChecked == true;

    public ImportHeightmapWindow(W3TerrainData terrain)
    {
        InitializeComponent();
        TxtMapDimInfo.Text = $"Grilla del mapa: {terrain.GridWidth} × {terrain.GridHeight} vértices (Norte arriba).";
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Seleccionar imagen de Heightmap",
            Filter = "Imágenes (PNG/JPG/BMP/TIF)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Todos los archivos|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            TxtImagePath.Text = dlg.FileName;
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedImagePath))
        {
            MessageBox.Show(this, "Selecciona primero una imagen de heightmap.", "Importar Heightmap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!float.TryParse(TxtMinZ.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float minZ) ||
            !float.TryParse(TxtMaxZ.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float maxZ))
        {
            MessageBox.Show(this, "Ingresa valores numéricos válidos para Min Z y Max Z.", "Importar Heightmap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MinZ = minZ;
        MaxZ = maxZ;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

