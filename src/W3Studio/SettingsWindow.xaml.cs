using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class SettingsWindow : Window
{
    private readonly WorkspaceSettings _settings;
    private bool _initializing = true;

    public SettingsWindow(WorkspaceSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        LoadSettingsToUi();
        _initializing = false;
    }

    private void LoadSettingsToUi()
    {
        // Tema actual
        var theme = ThemeService.CurrentTheme;
        switch (theme)
        {
            case AppTheme.Dark: RbThemeDark.IsChecked = true; break;
            case AppTheme.Light: RbThemeLight.IsChecked = true; break;
            case AppTheme.Pink: RbThemePink.IsChecked = true; break;
            case AppTheme.DarkGreen: RbThemeDarkGreen.IsChecked = true; break;
            case AppTheme.DarkBlue: RbThemeDarkBlue.IsChecked = true; break;
            default: RbThemeSystem.IsChecked = true; break;
        }

        // Rutas y Warcraft
        TxtWar3Path.Text = _settings.Warcraft3Dir;
        ChkWindowed.IsChecked = _settings.WindowedMode;
        TxtExtraArgs.Text = _settings.AdditionalLaunchArgs;

        // Preferencias del Editor
        ChkAutoSaveBeforeTest.IsChecked = _settings.AutoSaveBeforeTest;
        ChkShowGridByDefault.IsChecked = _settings.ShowGridByDefault;

        UpdateComponentStatus();
    }

    private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SecTheme == null || SecWarcraft == null || SecEditor == null) return;

        SecTheme.Visibility = LstCategories.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SecWarcraft.Visibility = LstCategories.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SecEditor.Visibility = LstCategories.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RbTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        if (sender is RadioButton { Tag: string themeTag })
        {
            var theme = ThemeService.ParseTheme(themeTag);
            ThemeService.ApplyTheme(theme, _settings, saveSettings: true);
        }
    }

    private void BtnBrowseWar3_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecciona war3.exe o la carpeta de Warcraft III 1.27b",
            Filter = "Warcraft III Executable (war3.exe; Frozen Throne.exe)|war3.exe;Frozen Throne.exe|Todos los archivos (*.*)|*.*",
            InitialDirectory = Directory.Exists(_settings.Warcraft3Dir) ? _settings.Warcraft3Dir : @"C:\"
        };

        if (dlg.ShowDialog(this) == true)
        {
            var folder = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(folder))
            {
                TxtWar3Path.Text = folder;
            }
        }
    }

    private void TxtWar3Path_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateComponentStatus();
    }

    private void UpdateComponentStatus()
    {
        if (TxtStatusWar3Exe == null) return;

        var dir = TxtWar3Path.Text.Trim();
        var existsDir = Directory.Exists(dir);

        var exe1 = Path.Combine(dir, "war3.exe");
        var exe2 = Path.Combine(dir, "Frozen Throne.exe");
        var hasExe = existsDir && (File.Exists(exe1) || File.Exists(exe2));

        var hasWar3Mpq = existsDir && File.Exists(Path.Combine(dir, "war3.mpq"));
        var hasWar3xMpq = existsDir && File.Exists(Path.Combine(dir, "War3x.mpq"));
        var hasPatchMpq = existsDir && File.Exists(Path.Combine(dir, "War3Patch.mpq"));

        SetStatusText(TxtStatusWar3Exe, "Ejecutable (war3.exe / Frozen Throne.exe)", hasExe);
        SetStatusText(TxtStatusWar3Mpq, "war3.mpq (Datos base)", hasWar3Mpq);
        SetStatusText(TxtStatusWar3xMpq, "War3x.mpq (Expansion The Frozen Throne)", hasWar3xMpq);
        SetStatusText(TxtStatusWar3PatchMpq, "War3Patch.mpq (Parche 1.27b)", hasPatchMpq);
    }

    private static void SetStatusText(TextBlock tb, string name, bool ok)
    {
        tb.Text = ok ? $"✓ {name}: Encontrado" : $"✗ {name}: No encontrado";
        tb.Foreground = ok ? new SolidColorBrush(Color.FromRgb(46, 160, 67)) : new SolidColorBrush(Color.FromRgb(217, 37, 37));
        tb.FontWeight = ok ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void SaveValuesToSettings()
    {
        _settings.Warcraft3Dir = TxtWar3Path.Text.Trim();
        _settings.WindowedMode = ChkWindowed.IsChecked == true;
        _settings.AdditionalLaunchArgs = TxtExtraArgs.Text.Trim();
        _settings.AutoSaveBeforeTest = ChkAutoSaveBeforeTest.IsChecked == true;
        _settings.ShowGridByDefault = ChkShowGridByDefault.IsChecked == true;
        _settings.AppThemeName = ThemeService.CurrentTheme.ToString();
        _settings.Save();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        SaveValuesToSettings();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        SaveValuesToSettings();
        DialogResult = true;
        Close();
    }
}

