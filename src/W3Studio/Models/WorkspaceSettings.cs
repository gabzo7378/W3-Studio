using System;
using System.IO;
using System.Text.Json;

namespace W3Studio.Models;

public class WorkspaceSettings
{
    public string Warcraft3Dir { get; set; } = @"F:\WARCRAFT\Warcraft III 1.27";
    public string DefaultMapsDir { get; set; } = @"F:\WARCRAFT\Warcraft III 1.27\Maps";
    public string LastOpenedMapPath { get; set; } = string.Empty;
    public bool WindowedMode { get; set; } = true;
    public string AppThemeName { get; set; } = "System";
    public string AppLanguage { get; set; } = "es";
    public bool AutoSaveBeforeTest { get; set; } = true;
    public bool ShowGridByDefault { get; set; } = false;
    public bool ShowInternalMapFilesInImports { get; set; } = false;
    public bool ShowCompiledScriptInTriggers { get; set; } = false;
    public string AdditionalLaunchArgs { get; set; } = string.Empty;

    public static event Action? AdvancedViewSettingsChanged;
    public static void NotifyAdvancedViewSettingsChanged() => AdvancedViewSettingsChanged?.Invoke();

    public string TestCommandArgs
    {
        get
        {
            var args = WindowedMode ? "-window" : string.Empty;
            if (!string.IsNullOrWhiteSpace(AdditionalLaunchArgs))
                args = $"{args} {AdditionalLaunchArgs}".Trim();
            return args;
        }
    }

    public string War3ExePath => Path.Combine(Warcraft3Dir, "war3.exe");
    public string War3MpqPath => Path.Combine(Warcraft3Dir, "war3.mpq");
    public string War3xMpqPath => Path.Combine(Warcraft3Dir, "War3x.mpq");
    public string War3PatchMpqPath => Path.Combine(Warcraft3Dir, "War3Patch.mpq");

    // Self-contained bundled compiler tools inside W3Studio\Tools (Zero external JNGP dependency!)
    public string BundledToolsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");
    public string CliJassHelperPath => Path.Combine(BundledToolsDir, "clijasshelper.exe");
    public string PjassPath => Path.Combine(BundledToolsDir, "pjass.exe");
    public string CommonJPath => Path.Combine(BundledToolsDir, "common.j");
    public string BlizzardJPath => Path.Combine(BundledToolsDir, "blizzard.j");

    private static string SettingsFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "w3studio.settings.json");

    public static WorkspaceSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<WorkspaceSettings>(json);
                if (loaded != null)
                    return loaded;
            }
        }
        catch
        {
            // Fallback to default settings
        }
        return new WorkspaceSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
