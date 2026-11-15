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
        // Idioma actual
        var lang = LocalizationService.NormalizeLanguage(_settings.AppLanguage);
        CmbAppLanguage.SelectedIndex = lang == "en" ? 1 : 0;

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
        ChkOpenModulesSeparateWindows.IsChecked = _settings.OpenModulesInSeparateWindows;
        ChkShowInternalImports.IsChecked = _settings.ShowInternalMapFilesInImports;
        ChkShowCompiledTriggerScript.IsChecked = _settings.ShowCompiledScriptInTriggers;

        ApplyWindowLocalization();
        UpdateComponentStatus();
    }

    private void CmbAppLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        if (CmbAppLanguage.SelectedItem is ComboBoxItem { Tag: string langTag })
        {
            var normalized = LocalizationService.NormalizeLanguage(langTag);
            _settings.AppLanguage = normalized;
            LocalizationService.SetLanguage(normalized, notify: true);
            _settings.Save();
            ApplyWindowLocalization();
            UpdateComponentStatus();
        }
    }

    private void ApplyWindowLocalization()
    {
        Title = LocalizationService.L("Configuración — W3 Studio", "Settings — W3 Studio");
        if (CatItemThemes != null) CatItemThemes.Content = LocalizationService.L("🎨 Temas e Idioma", "🎨 Themes & Language");
        if (CatItemWar3 != null) CatItemWar3.Content = LocalizationService.L("🎮 Warcraft III y Pruebas", "🎮 Warcraft III & Testing");
        if (CatItemEditor != null) CatItemEditor.Content = LocalizationService.L("🛠️ Editor y Preferencias", "🛠️ Editor & Preferences");

        if (LblLanguageTitle != null) LblLanguageTitle.Text = LocalizationService.L("Idioma del Editor:", "Editor Language:");
        if (LblLanguageDesc != null) LblLanguageDesc.Text = LocalizationService.L(
            "Cambia el idioma de los campos del Editor de Objetos, menús y herramientas.",
            "Switch the language of Object Editor fields, menus, and tools.");
        if (LblThemeTitle != null) LblThemeTitle.Text = LocalizationService.L("Selecciona el tema de la aplicación:", "Select Application Theme:");
        if (LblThemeDesc != null) LblThemeDesc.Text = LocalizationService.L(
            "El tema se aplicará en tiempo real en la ventana principal, el editor de objetos y todas las herramientas.",
            "The theme is applied in real time across the main window, Object Editor, and all tools.");
        if (ChkAutoSaveBeforeTest != null) ChkAutoSaveBeforeTest.Content = LocalizationService.L(
            "Guardar mapa automáticamente antes de Probar (F9)",
            "Auto-save map before Test Map (F9)");
        if (ChkShowGridByDefault != null) ChkShowGridByDefault.Content = LocalizationService.L(
            "Mostrar cuadrícula de terreno por defecto",
            "Show terrain grid by default");
        if (ChkOpenModulesSeparateWindows != null) ChkOpenModulesSeparateWindows.Content = LocalizationService.L(
            "Ventana: abrir módulos en ventanas independientes (desmarcar para abrirlos en el área del viewport)",
            "Windowed: open modules in separate windows (uncheck to open inside the viewport area)");
        if (LblEditorBehaviorTitle != null) LblEditorBehaviorTitle.Text = LocalizationService.L(
            "Comportamiento del Editor:",
            "Editor Behavior:");
        if (LblAdvancedViewTitle != null) LblAdvancedViewTitle.Text = LocalizationService.L(
            "Modo Inspector Avanzado (Archivos Internos del Mapa):",
            "Advanced Inspector Mode (Internal Map Files):");
        if (LblAdvancedViewWarning != null) LblAdvancedViewWarning.Text = LocalizationService.L(
            "⚠ Por defecto se ocultan los archivos internos del sistema de Warcraft III (war3map.w3e, war3map.wtg, war3map.shd, etc.) para evitar modificaciones accidentales que corrompan el mapa.",
            "⚠ Internal Warcraft III system files (war3map.w3e, war3map.wtg, war3map.shd, etc.) are hidden by default to prevent accidental edits that could corrupt the map.");
        if (LblAdvImportsCat != null) LblAdvImportsCat.Text = LocalizationService.L(
            "📦 Gestor de Importaciones (F12)",
            "📦 Import Manager (F12)");
        if (ChkShowInternalImports != null) ChkShowInternalImports.Content = LocalizationService.L(
            "Mostrar archivos binarios internos del mapa (war3map.w3e, .w3i, .wtg, .doo, .shd, etc.)",
            "Show internal binary map files (war3map.w3e, .w3i, .wtg, .doo, .shd, etc.)");
        if (LblAdvTriggersCat != null) LblAdvTriggersCat.Text = LocalizationService.L(
            "⚡ Editor de Triggers (F4)",
            "⚡ Trigger Editor (F4)");
        if (ChkShowCompiledTriggerScript != null) ChkShowCompiledTriggerScript.Content = LocalizationService.L(
            "Mostrar nodo de script compilado completo (war3map.j) en el árbol de Triggers",
            "Show full compiled script node (war3map.j) in the Trigger tree");
        if (ChkWindowed != null) ChkWindowed.Content = LocalizationService.L(
            "Ejecutar Warcraft III en Modo Ventana (-window)",
            "Run Warcraft III in Windowed Mode (-window)");
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

        SetStatusText(TxtStatusWar3Exe, LocalizationService.L("Ejecutable (war3.exe / Frozen Throne.exe)", "Executable (war3.exe / Frozen Throne.exe)"), hasExe);
        SetStatusText(TxtStatusWar3Mpq, LocalizationService.L("war3.mpq (Datos base)", "war3.mpq (Base data)"), hasWar3Mpq);
        SetStatusText(TxtStatusWar3xMpq, LocalizationService.L("War3x.mpq (Expansión The Frozen Throne)", "War3x.mpq (The Frozen Throne expansion)"), hasWar3xMpq);
        SetStatusText(TxtStatusWar3PatchMpq, LocalizationService.L("War3Patch.mpq (Parche 1.27b)", "War3Patch.mpq (Patch 1.27b)"), hasPatchMpq);
    }

    private static void SetStatusText(TextBlock tb, string name, bool ok)
    {
        var foundStr = LocalizationService.L("Encontrado", "Found");
        var notFoundStr = LocalizationService.L("No encontrado", "Not found");
        tb.Text = ok ? $"✓ {name}: {foundStr}" : $"✗ {name}: {notFoundStr}";
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
        _settings.OpenModulesInSeparateWindows = ChkOpenModulesSeparateWindows.IsChecked == true;
        _settings.ShowInternalMapFilesInImports = ChkShowInternalImports.IsChecked == true;
        _settings.ShowCompiledScriptInTriggers = ChkShowCompiledTriggerScript.IsChecked == true;
        _settings.AppThemeName = ThemeService.CurrentTheme.ToString();
        if (CmbAppLanguage.SelectedItem is ComboBoxItem { Tag: string langTag })
        {
            _settings.AppLanguage = LocalizationService.NormalizeLanguage(langTag);
            LocalizationService.SetLanguage(_settings.AppLanguage, notify: true);
        }
        _settings.Save();
        WorkspaceSettings.NotifyAdvancedViewSettingsChanged();
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

