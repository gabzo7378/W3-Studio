using System;
using System.IO;
using System.Linq;
using W3Studio.Models;
using W3Studio.Services;
using Xunit;

namespace W3Studio.Tests;

public class WorkspaceSettingsAndLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceSettingsAndLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "W3StudioTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Ignore cleanup lock
        }
    }

    [Fact]
    public void SaveAndLoad_RoundtripsWindowLayoutAndLayerPreferences()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var original = new WorkspaceSettings
        {
            Warcraft3Dir = @"D:\Games\Warcraft III",
            WindowLeft = 120,
            WindowTop = 48,
            WindowWidth = 1480,
            WindowHeight = 910,
            WindowMaximized = true,
            ShowUnits = false,
            ShowDoodads = true,
            ShowRegions = true,
            ShowGridByDefault = true,
            ShowPathing = true,
            ShowCameraBounds = true,
            ShowWeather = false,
            LightingPreset = "Dusk",
            ShowToolPalette = false
        };
        original.AddRecentMap(@"C:\Maps\Alpha.w3x");
        original.AddRecentMap(@"C:\Maps\Beta.w3m");

        original.Save(path);
        var loaded = WorkspaceSettings.Load(path);

        Assert.Equal(@"D:\Games\Warcraft III", loaded.Warcraft3Dir);
        Assert.Equal(120, loaded.WindowLeft);
        Assert.Equal(48, loaded.WindowTop);
        Assert.Equal(1480, loaded.WindowWidth);
        Assert.Equal(910, loaded.WindowHeight);
        Assert.True(loaded.WindowMaximized);
        Assert.False(loaded.ShowUnits);
        Assert.True(loaded.ShowRegions);
        Assert.True(loaded.ShowPathing);
        Assert.True(loaded.ShowCameraBounds);
        Assert.False(loaded.ShowWeather);
        Assert.Equal("Dusk", loaded.LightingPreset);
        Assert.False(loaded.ShowToolPalette);
        Assert.Equal(new[] { @"C:\Maps\Beta.w3m", @"C:\Maps\Alpha.w3x" }, loaded.RecentMaps);
    }

    [Fact]
    public void AddRecentMap_DeduplicatesCaseInsensitivelyAndCapsAtTen()
    {
        var s = new WorkspaceSettings();
        for (int i = 1; i <= 12; i++)
        {
            s.AddRecentMap($@"F:\Maps\Map_{i:D2}.w3x");
        }

        Assert.Equal(WorkspaceSettings.MaxRecentMapsCount, s.RecentMaps.Count);
        Assert.Equal(@"F:\Maps\Map_12.w3x", s.RecentMaps[0]);
        Assert.DoesNotContain(@"F:\Maps\Map_01.w3x", s.RecentMaps);
        Assert.DoesNotContain(@"F:\Maps\Map_02.w3x", s.RecentMaps);

        // Re-adding an existing path moves it back to index 0 without duplicating
        s.AddRecentMap(@"f:\maps\map_05.w3x");
        Assert.Equal(WorkspaceSettings.MaxRecentMapsCount, s.RecentMaps.Count);
        Assert.Equal(@"f:\maps\map_05.w3x", s.RecentMaps[0]);
        Assert.Single(s.RecentMaps, p => p.EndsWith("Map_05.w3x", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsSafeDefaults()
    {
        var path = Path.Combine(_tempDir, "broken.json");
        File.WriteAllText(path, "{ invalid json !!!");

        var loaded = WorkspaceSettings.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("Noon", loaded.LightingPreset);
        Assert.True(loaded.ShowUnits);
        Assert.Empty(loaded.RecentMaps);
    }

    [Fact]
    public void AppLogger_WritesFormattedEntryToCustomLogPath()
    {
        var logFile = Path.Combine(_tempDir, "logs", "unit_test.log");
        AppLogger.Write("INFO", "UnitTest", "Hello world log", customLogPath: logFile);
        AppLogger.Write("ERROR", "UnitTest", "Simulated failure", new InvalidOperationException("Boom"), customLogPath: logFile);

        Assert.True(File.Exists(logFile));
        var content = File.ReadAllText(logFile);
        Assert.Contains("[INFO] [UnitTest] Hello world log", content);
        Assert.Contains("[ERROR] [UnitTest] Simulated failure", content);
        Assert.Contains("InvalidOperationException: Boom", content);
    }

    [Fact]
    public void OpenModulesInSeparateWindows_DefaultsToTrueAndRoundtripsFalse()
    {
        var defaults = new WorkspaceSettings();
        Assert.True(defaults.OpenModulesInSeparateWindows);

        var path = Path.Combine(_tempDir, "window_mode_settings.json");
        defaults.OpenModulesInSeparateWindows = false;
        defaults.Save(path);

        var loaded = WorkspaceSettings.Load(path);
        Assert.False(loaded.OpenModulesInSeparateWindows);
    }
}
