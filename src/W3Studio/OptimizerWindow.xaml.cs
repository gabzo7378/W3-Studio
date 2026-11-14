using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class OptimizerWindow : Window
{
    private readonly WorkspaceSettings _settings;
    private MapProject? _activeProject;
    private string? _lastOutputFilePath;
    private bool _isRunning;

    public OptimizerWindow(WorkspaceSettings settings, MapProject? activeProject)
    {
        InitializeComponent();
        _settings = settings;
        SetProject(activeProject);
    }

    public void SetProject(MapProject? project)
    {
        _activeProject = project;
        BtnUseActiveMap.IsEnabled = project != null && !string.IsNullOrEmpty(project.FilePath);

        if (project != null && !string.IsNullOrEmpty(project.FilePath))
        {
            ApplySourcePath(project.FilePath);
            TxtSourceBadge.Text = $"Mapa abierto: {Path.GetFileName(project.FilePath)}";
        }
        else if (string.IsNullOrWhiteSpace(TxtSourcePath.Text))
        {
            TxtSourceBadge.Text = "Sin mapa seleccionado";
        }
    }

    private void ApplySourcePath(string sourcePath)
    {
        TxtSourcePath.Text = sourcePath;
        TxtOutputPath.Text = BuildDefaultOutputPath(sourcePath);
        TxtSourceBadge.Text = $"Origen: {Path.GetFileName(sourcePath)}";
        _ = RefreshAnalysisAsync(sourcePath);
    }

    private static string BuildDefaultOutputPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext))
            ext = ".w3x";
        return Path.Combine(dir, $"{name}_optimized{ext}");
    }

    private async Task RefreshAnalysisAsync(string sourcePath)
    {
        try
        {
            var projectSnapshot = _activeProject != null &&
                                  string.Equals(_activeProject.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase)
                ? _activeProject
                : null;

            var breakdown = await Task.Run(() =>
            {
                if (File.Exists(sourcePath))
                    return MapOptimizerService.AnalyzeMapFile(sourcePath);
                if (projectSnapshot != null)
                    return MapOptimizerService.AnalyzeProjectInMemory(projectSnapshot);
                return new MapSizeBreakdown();
            });

            LblTotalSource.Text = MapOptimizerService.FormatBytes(breakdown.TotalSourceBytes);
            LblFileCount.Text = breakdown.FileCount.ToString();
            LblJassSize.Text = MapOptimizerService.FormatBytes(breakdown.JassBytes);
            LblEditorSize.Text = MapOptimizerService.FormatBytes(breakdown.EditorOnlyBytes);
            LblObjectSize.Text = MapOptimizerService.FormatBytes(breakdown.ObjectDataBytes);
            LblTerrainSize.Text = MapOptimizerService.FormatBytes(breakdown.TerrainAndDooBytes);
            LblImportsSize.Text = MapOptimizerService.FormatBytes(breakdown.ImportsBytes);
            LblOtherSize.Text = MapOptimizerService.FormatBytes(breakdown.OtherBytes);
        }
        catch (Exception ex)
        {
            TxtLog.Text = $"[Análisis] Error leyendo mapa: {ex.Message}";
        }
    }

    private void BtnUseActiveMap_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject != null && !string.IsNullOrEmpty(_activeProject.FilePath))
        {
            ApplySourcePath(_activeProject.FilePath);
        }
        else
        {
            MessageBox.Show("No hay un mapa guardado/abierto actualmente en W3 Studio.", "Optimizador", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnBrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Seleccionar Mapa de Warcraft III para Optimizar",
            Filter = "Mapas de Warcraft III (*.w3x;*.w3m)|*.w3x;*.w3m|Todos los archivos (*.*)|*.*"
        };

        if (!string.IsNullOrEmpty(TxtSourcePath.Text))
        {
            var dir = Path.GetDirectoryName(TxtSourcePath.Text);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() == true)
        {
            ApplySourcePath(dlg.FileName);
        }
    }

    private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Guardar Mapa Optimizado Como",
            Filter = "Mapa Frozen Throne (*.w3x)|*.w3x|Mapa Reign of Chaos (*.w3m)|*.w3m|Todos los archivos (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(TxtOutputPath.Text)
                ? "map_optimized.w3x"
                : Path.GetFileName(TxtOutputPath.Text)
        };

        if (!string.IsNullOrWhiteSpace(TxtOutputPath.Text))
        {
            var dir = Path.GetDirectoryName(TxtOutputPath.Text);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() == true)
        {
            TxtOutputPath.Text = dlg.FileName;
        }
    }

    private void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtSourcePath.Text))
            return;
        _ = RefreshAnalysisAsync(TxtSourcePath.Text);
    }

    private void BtnPresetRecommended_Click(object sender, RoutedEventArgs e)
    {
        ChkRemoveJassComments.IsChecked = true;
        ChkCompactJassWhitespace.IsChecked = true;
        ChkInlineTrigStrings.IsChecked = true;
        ChkRemoveDebugLines.IsChecked = true;
        ChkCompressJassNames.IsChecked = true;
        ChkValidateWithPjass.IsChecked = true;

        ChkRemoveEditorTriggerFiles.IsChecked = true;
        ChkCleanUnusedWtsStrings.IsChecked = true;
        ChkCleanObjectDataStrings.IsChecked = true;
        ChkRemoveEditorSoundsFile.IsChecked = false;
        ChkRemoveEditorCamerasFile.IsChecked = false;
        ChkRemoveMapPreviewImage.IsChecked = false;

        ChkRecompressAllMpqFiles.IsChecked = true;
        ChkRemoveListfileAndAttributes.IsChecked = false;
        CmbMpqBlockSize.SelectedIndex = 1; // 4KB
    }

    private void BtnPresetMaxProtect_Click(object sender, RoutedEventArgs e)
    {
        ChkRemoveJassComments.IsChecked = true;
        ChkCompactJassWhitespace.IsChecked = true;
        ChkInlineTrigStrings.IsChecked = true;
        ChkRemoveDebugLines.IsChecked = true;
        ChkCompressJassNames.IsChecked = true;
        ChkValidateWithPjass.IsChecked = true;

        ChkRemoveEditorTriggerFiles.IsChecked = true;
        ChkCleanUnusedWtsStrings.IsChecked = true;
        ChkCleanObjectDataStrings.IsChecked = true;
        ChkRemoveEditorSoundsFile.IsChecked = true;
        ChkRemoveEditorCamerasFile.IsChecked = true;
        ChkRemoveMapPreviewImage.IsChecked = false;

        ChkRecompressAllMpqFiles.IsChecked = true;
        ChkRemoveListfileAndAttributes.IsChecked = true;
        CmbMpqBlockSize.SelectedIndex = 2; // 8KB
    }

    private void BtnPresetSafeClean_Click(object sender, RoutedEventArgs e)
    {
        ChkRemoveJassComments.IsChecked = true;
        ChkCompactJassWhitespace.IsChecked = true;
        ChkInlineTrigStrings.IsChecked = true;
        ChkRemoveDebugLines.IsChecked = true;
        ChkCompressJassNames.IsChecked = false;
        ChkValidateWithPjass.IsChecked = true;

        ChkRemoveEditorTriggerFiles.IsChecked = false;
        ChkCleanUnusedWtsStrings.IsChecked = true;
        ChkCleanObjectDataStrings.IsChecked = false;
        ChkRemoveEditorSoundsFile.IsChecked = false;
        ChkRemoveEditorCamerasFile.IsChecked = false;
        ChkRemoveMapPreviewImage.IsChecked = false;

        ChkRecompressAllMpqFiles.IsChecked = true;
        ChkRemoveListfileAndAttributes.IsChecked = false;
        CmbMpqBlockSize.SelectedIndex = 1; // 4KB
    }

    private MapOptimizerOptions CollectOptions()
    {
        ushort blockSize = 3;
        if (CmbMpqBlockSize.SelectedItem is ComboBoxItem item && ushort.TryParse(item.Tag?.ToString(), out var parsed))
            blockSize = parsed;

        return new MapOptimizerOptions
        {
            RemoveJassComments = ChkRemoveJassComments.IsChecked == true,
            CompactJassWhitespace = ChkCompactJassWhitespace.IsChecked == true,
            InlineTrigStrings = ChkInlineTrigStrings.IsChecked == true,
            RemoveDebugLines = ChkRemoveDebugLines.IsChecked == true,
            CompressJassNames = ChkCompressJassNames.IsChecked == true,
            ValidateWithPjass = ChkValidateWithPjass.IsChecked == true,

            RemoveEditorTriggerFiles = ChkRemoveEditorTriggerFiles.IsChecked == true,
            CleanUnusedWtsStrings = ChkCleanUnusedWtsStrings.IsChecked == true,
            CleanObjectDataStrings = ChkCleanObjectDataStrings.IsChecked == true,
            RemoveEditorSoundsFile = ChkRemoveEditorSoundsFile.IsChecked == true,
            RemoveEditorCamerasFile = ChkRemoveEditorCamerasFile.IsChecked == true,
            RemoveMapPreviewImage = ChkRemoveMapPreviewImage.IsChecked == true,

            RecompressAllMpqFiles = ChkRecompressAllMpqFiles.IsChecked == true,
            RemoveListfileAndAttributes = ChkRemoveListfileAndAttributes.IsChecked == true,
            MpqBlockSize = blockSize
        };
    }

    private async void BtnRunOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
            return;

        var sourcePath = TxtSourcePath.Text.Trim();
        var outputPath = TxtOutputPath.Text.Trim();

        if (string.IsNullOrEmpty(sourcePath))
        {
            MessageBox.Show("Selecciona primero un archivo de mapa (.w3x / .w3m).", "Optimizador", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = BuildDefaultOutputPath(sourcePath);
            TxtOutputPath.Text = outputPath;
        }

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            var confirm = MessageBox.Show(
                "El archivo origen y destino son exactamente iguales.\nSi eliminas .wtg/.wct o el (listfile), NO podrás volver a abrirlo en World Editor.\n\n¿Seguro que deseas sobrescribir el mapa original?",
                "Confirmar Sobrescritura",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var options = CollectOptions();
        var liveProject = _activeProject != null &&
                          string.Equals(_activeProject.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase)
            ? _activeProject
            : null;

        _isRunning = true;
        BtnRunOptimize.IsEnabled = false;
        BtnRunOptimize.Content = "⏳ Optimizando...";
        TxtLog.Text = $"Iniciando optimización de: {sourcePath}\r\nDestino: {outputPath}\r\nProcesando...\r\n";

        try
        {
            var result = await Task.Run(() =>
                MapOptimizerService.OptimizeMapFile(sourcePath, outputPath, options, _settings, liveProject));

            TxtLog.Text = string.Join(Environment.NewLine, result.LogLines);
            TxtLog.ScrollToEnd();

            if (result.Success)
            {
                _lastOutputFilePath = result.OutputPath;
                BtnOpenOutputFolder.IsEnabled = true;

                LblBeforeAfter.Text = $"{MapOptimizerService.FormatBytes(result.OriginalFileBytes)} ➔ {MapOptimizerService.FormatBytes(result.OptimizedFileBytes)}";
                LblSavedStats.Text = $"{MapOptimizerService.FormatBytes(result.SavedBytes)} ({result.SavedPercent:F1}%)";
                LblScriptStats.Text = $"{result.ObfuscatedIdentifiers} renombres | PJASS: {(options.ValidateWithPjass ? (result.PjassPassed ? "OK" : "Fallback") : "Omitido")}";

                MessageBox.Show(
                    $"¡Mapa optimizado con éxito!\n\nOriginal: {MapOptimizerService.FormatBytes(result.OriginalFileBytes)}\nOptimizado: {MapOptimizerService.FormatBytes(result.OptimizedFileBytes)}\nAhorro: {MapOptimizerService.FormatBytes(result.SavedBytes)} ({result.SavedPercent:F1}%)\nArchivos MPQ: {result.FilesBefore} ➔ {result.FilesAfter}",
                    "WC3 Map Optimizer 5.0",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                LblScriptStats.Text = "Error";
                MessageBox.Show(
                    $"No se pudo completar la optimización:\n{result.ErrorMessage}",
                    "Error en Optimizador",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            TxtLog.Text += Environment.NewLine + $"[ERROR CRÍTICO] {ex.Message}";
            MessageBox.Show($"Error inesperado: {ex.Message}", "Error en Optimizador", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRunning = false;
            BtnRunOptimize.IsEnabled = true;
            BtnRunOptimize.Content = "⚡ Optimizar Mapa";
        }
    }

    private void BtnOpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastOutputFilePath))
            return;

        try
        {
            if (File.Exists(_lastOutputFilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutputFilePath}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(_lastOutputFilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    {
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir el explorador: {ex.Message}", "Optimizador", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

