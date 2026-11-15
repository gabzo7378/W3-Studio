using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace W3Studio.Models;

public class WorkspaceSettings
{
    public const int MaxRecentMapsCount = 10;

    public string Warcraft3Dir { get; set; } = @"F:\WARCRAFT\Warcraft III 1.27";
    public string DefaultMapsDir { get; set; } = @"F:\WARCRAFT\Warcraft III 1.27\Maps";
    public string LastOpenedMapPath { get; set; } = string.Empty;
    public List<string> RecentMaps { get; set; } = new();
    public bool WindowedMode { get; set; } = true;
    public string AppThemeName { get; set; } = "System";
    public string AppLanguage { get; set; } = "es";
    public bool AutoSaveBeforeTest { get; set; } = true;
    public bool ShowGridByDefault { get; set; } = false;
    public bool ShowUnits { get; set; } = true;
    public bool ShowDoodads { get; set; } = true;
    public bool ShowRegions { get; set; } = false;
    public bool ShowPathing { get; set; } = false;
    public bool ShowCameraBounds { get; set; } = false;
    public bool ShowWeather { get; set; } = true;
    public string LightingPreset { get; set; } = "Noon";
    public bool ShowToolPalette { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 840;
    public bool WindowMaximized { get; set; } = false;
    public bool ShowInternalMapFilesInImports { get; set; } = false;
    public bool ShowCompiledScriptInTriggers { get; set; } = false;
    public string AdditionalLaunchArgs { get; set; } = string.Empty;
    public bool OpenModulesInSeparateWindows { get; set; } = true;
    public bool IgnoreEntityPathing { get; set; } = false;

    public void AddRecentMap(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        RecentMaps ??= new List<string>();
        var normalized = filePath.Trim();
        RecentMaps.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        RecentMaps.Insert(0, normalized);
        if (RecentMaps.Count > MaxRecentMapsCount)
            RecentMaps.RemoveRange(MaxRecentMapsCount, RecentMaps.Count - MaxRecentMapsCount);
    }

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
    public string War3xLocalMpqPath => Path.Combine(Warcraft3Dir, "War3xlocal.mpq");
    public string War3LocalMpqPath => Path.Combine(Warcraft3Dir, "War3Local.mpq");
    public string War3PatchMpqPath => Path.Combine(Warcraft3Dir, "War3Patch.mpq");

    // Self-contained bundled compiler tools inside W3Studio\Tools (Zero external JNGP dependency!)
    public string BundledToolsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");
    public string CliJassHelperPath => Path.Combine(BundledToolsDir, "clijasshelper.exe");
    public string PjassPath => Path.Combine(BundledToolsDir, "pjass.exe");
    public string CommonJPath => Path.Combine(BundledToolsDir, "common.j");
    public string BlizzardJPath => Path.Combine(BundledToolsDir, "blizzard.j");

    private static string SettingsFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "w3studio.settings.json");

    public static WorkspaceSettings Load(string? customPath = null)
    {
        var path = string.IsNullOrWhiteSpace(customPath) ? SettingsFilePath : customPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<WorkspaceSettings>(json);
                if (loaded != null)
                {
                    loaded.RecentMaps ??= new List<string>();
                    return loaded;
                }
            }
        }
        catch
        {
            // Fallback to default settings
        }
        return new WorkspaceSettings();
    }

    public void Save(string? customPath = null)
    {
        var path = string.IsNullOrWhiteSpace(customPath) ? SettingsFilePath : customPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
