using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using War3Net.Build.Common;
using War3Net.Build.Info;
using W3Studio.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public sealed class PlayerSlotVm
{
    public int Id { get; set; }
    public int PlayerNumber => Id + 1;
    public string ColorLabel { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PlayerController Controller { get; set; }
    public PlayerRace Race { get; set; }
    public bool FixedStartLocation { get; set; }
    public float StartX { get; set; }
    public float StartY { get; set; }
}

public sealed class ForceVm : ObservableObject
{
    private string _name = "Force";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public ForceFlags Flags { get; set; } = ForceFlags.Allied | ForceFlags.AlliedVictory | ForceFlags.ShareVision;
    public bool[] MemberSlots { get; } = new bool[24];
}

public partial class ScenarioWindow : Window
{
    private static readonly string[] PlayerColorLabels =
    {
        "1 - Rojo", "2 - Azul", "3 - Teal", "4 - Púrpura",
        "5 - Amarillo", "6 - Naranja", "7 - Verde", "8 - Rosa",
        "9 - Gris", "10 - Azul Claro", "11 - Verde Osc.", "12 - Marrón"
    };

    private readonly MapProject _project;
    private readonly BlpIconService _blpService;
    private readonly Action _onApplied;
    private readonly ObservableCollection<PlayerSlotVm> _players = new();
    private readonly ObservableCollection<ForceVm> _forces = new();
    private bool _suppressForceUi;

    public ScenarioWindow(MapProject project, BlpIconService blpService, Action onApplied, int initialTabIndex = 0)
    {
        InitializeComponent();
        _project = project;
        _blpService = blpService;
        _onApplied = onApplied;

        if (initialTabIndex >= 0 && initialTabIndex < TabsScenario.Items.Count)
            TabsScenario.SelectedIndex = initialTabIndex;

        PopulateComboBoxes();
        LoadFromProject();
    }

    private void PopulateComboBoxes()
    {
        CmbTileset.ItemsSource = Enum.GetValues<Tileset>();
        CmbWeather.ItemsSource = Enum.GetValues<WeatherType>();
        CmbFogStyle.ItemsSource = Enum.GetValues<FogStyle>();
        ColPlayerController.ItemsSource = Enum.GetValues<PlayerController>();
        ColPlayerRace.ItemsSource = Enum.GetValues<PlayerRace>();
    }

    private MapInfo EnsureMapInfo()
    {
        if (_project.MapInfo != null)
            return _project.MapInfo;

        var info = new MapInfo(MapInfoFormatVersion.v25)
        {
            MapName = _project.MapName,
            MapAuthor = _project.MapAuthor,
            MapDescription = _project.MapDescription,
            RecommendedPlayers = string.IsNullOrWhiteSpace(_project.SuggestedPlayers) ? "Any" : _project.SuggestedPlayers,
            Tileset = Tileset.LordaeronSummer,
            FogStyle = FogStyle.Linear,
            GlobalWeather = WeatherType.None,
            MapFlags = MapFlags.MaskedAreasArePartiallyVisible | MapFlags.ShowWaterWavesOnCliffShores | MapFlags.ShowWaterWavesOnRollingShores | MapFlags.UseItemClassificationSystem
        };
        _project.MapInfo = info;
        return info;
    }

    private void LoadFromProject()
    {
        var info = EnsureMapInfo();

        TxtMapName.Text = _project.ResolveTrigStr(info.MapName);
        TxtMapAuthor.Text = _project.ResolveTrigStr(info.MapAuthor);
        TxtSuggestedPlayers.Text = _project.ResolveTrigStr(info.RecommendedPlayers);
        TxtMapDescription.Text = _project.ResolveTrigStr(info.MapDescription);

        TxtLoadingPath.Text = info.LoadingScreenPath ?? string.Empty;
        TxtLoadingTitle.Text = _project.ResolveTrigStr(info.LoadingScreenTitle);
        TxtLoadingSubtitle.Text = _project.ResolveTrigStr(info.LoadingScreenSubtitle);
        TxtLoadingText.Text = _project.ResolveTrigStr(info.LoadingScreenText);

        CmbTileset.SelectedItem = info.Tileset;
        ChkMaskPartiallyVisible.IsChecked = info.MapFlags.HasFlag(MapFlags.MaskedAreasArePartiallyVisible);
        ChkHideMinimap.IsChecked = info.MapFlags.HasFlag(MapFlags.HideMinimapInPreviewScreens);
        ChkUseCustomForces.IsChecked = info.MapFlags.HasFlag(MapFlags.UseCustomForces);
        ChkFixedPlayerSettings.IsChecked = info.MapFlags.HasFlag(MapFlags.FixedPlayerSettingsForCustomForces);
        ChkWavesCliff.IsChecked = info.MapFlags.HasFlag(MapFlags.ShowWaterWavesOnCliffShores);
        ChkWavesRolling.IsChecked = info.MapFlags.HasFlag(MapFlags.ShowWaterWavesOnRollingShores);
        ChkUseItemClassification.IsChecked = info.MapFlags.HasFlag(MapFlags.UseItemClassificationSystem);
        ChkUseCustomTechtree.IsChecked = info.MapFlags.HasFlag(MapFlags.UseCustomTechtree);
        ChkUseCustomAbilities.IsChecked = info.MapFlags.HasFlag(MapFlags.UseCustomAbilities);
        ChkUseCustomUpgrades.IsChecked = info.MapFlags.HasFlag(MapFlags.UseCustomUpgrades);

        CmbWeather.SelectedItem = info.GlobalWeather;
        ChkHasTerrainFog.IsChecked = info.MapFlags.HasFlag(MapFlags.HasTerrainFog);
        CmbFogStyle.SelectedItem = info.FogStyle;
        TxtFogStart.Text = info.FogStartZ.ToString("0.##", CultureInfo.InvariantCulture);
        TxtFogEnd.Text = info.FogEndZ.ToString("0.##", CultureInfo.InvariantCulture);
        TxtFogDensity.Text = info.FogDensity.ToString("0.###", CultureInfo.InvariantCulture);
        TxtFogColorRgb.Text = $"{info.FogColor.R}, {info.FogColor.G}, {info.FogColor.B}";
        TxtWaterColorRgba.Text = $"{info.WaterTintingColor.R}, {info.WaterTintingColor.G}, {info.WaterTintingColor.B}, {info.WaterTintingColor.A}";

        // Load Players 0..11
        _players.Clear();
        var existingPlayers = info.Players.ToDictionary(p => p.Id);
        for (int i = 0; i < 12; i++)
        {
            if (existingPlayers.TryGetValue(i, out var p))
            {
                _players.Add(new PlayerSlotVm
                {
                    Id = i,
                    ColorLabel = PlayerColorLabels[i],
                    Name = _project.ResolveTrigStr(p.Name),
                    Controller = p.Controller,
                    Race = p.Race,
                    FixedStartLocation = p.Flags.HasFlag(PlayerFlags.FixedStartPosition),
                    StartX = p.StartPosition.X,
                    StartY = p.StartPosition.Y
                });
            }
            else
            {
                var sloc = _project.PlacedUnits.FirstOrDefault(u => u.IsStartLocation && u.PlayerId == i);
                _players.Add(new PlayerSlotVm
                {
                    Id = i,
                    ColorLabel = PlayerColorLabels[i],
                    Name = $"Player {i + 1}",
                    Controller = PlayerController.None,
                    Race = PlayerRace.Human,
                    FixedStartLocation = false,
                    StartX = sloc?.X ?? 0f,
                    StartY = sloc?.Y ?? 0f
                });
            }
        }
        GridPlayers.ItemsSource = _players;

        // Load Forces
        _forces.Clear();
        foreach (var f in info.Forces)
        {
            var vm = new ForceVm
            {
                Name = _project.ResolveTrigStr(f.Name),
                Flags = f.Flags
            };
            for (int pid = 0; pid < 24; pid++)
                vm.MemberSlots[pid] = f.Players[pid];
            _forces.Add(vm);
        }

        if (_forces.Count == 0)
        {
            var defForce = new ForceVm { Name = "Force 1" };
            foreach (var p in _players.Where(p => p.Controller != PlayerController.None))
                defForce.MemberSlots[p.Id] = true;
            _forces.Add(defForce);
        }

        LstForces.ItemsSource = _forces;
        LstForces.SelectedIndex = 0;
    }

    private void LstForces_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstForces.SelectedItem is not ForceVm force)
        {
            PnlForceEditor.IsEnabled = false;
            return;
        }

        _suppressForceUi = true;
        PnlForceEditor.IsEnabled = true;
        TxtForceName.Text = force.Name;
        ChkForceAllied.IsChecked = force.Flags.HasFlag(ForceFlags.Allied);
        ChkForceAlliedVictory.IsChecked = force.Flags.HasFlag(ForceFlags.AlliedVictory);
        ChkForceShareVision.IsChecked = force.Flags.HasFlag(ForceFlags.ShareVision);
        ChkForceShareControl.IsChecked = force.Flags.HasFlag(ForceFlags.ShareUnitControl);
        ChkForceShareAdvControl.IsChecked = force.Flags.HasFlag(ForceFlags.ShareAdvancedUnitControl);

        WpForcePlayers.Children.Clear();
        for (int i = 0; i < 12; i++)
        {
            int pid = i;
            var p = _players[pid];
            var chk = new CheckBox
            {
                Content = $"{PlayerColorLabels[pid]} ({p.Name})",
                IsChecked = force.MemberSlots[pid],
                Width = 210,
                Margin = new Thickness(0, 3, 8, 3),
                IsEnabled = p.Controller != PlayerController.None
            };
            chk.Checked += (_, _) => AssignPlayerToForce(pid, force);
            chk.Unchecked += (_, _) => force.MemberSlots[pid] = false;
            WpForcePlayers.Children.Add(chk);
        }

        _suppressForceUi = false;
    }

    private void AssignPlayerToForce(int pid, ForceVm targetForce)
    {
        if (_suppressForceUi) return;
        foreach (var f in _forces)
            f.MemberSlots[pid] = ReferenceEquals(f, targetForce);
    }

    private void TxtForceName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressForceUi || LstForces.SelectedItem is not ForceVm force) return;
        force.Name = TxtForceName.Text;
    }

    private void ForceFlag_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressForceUi || LstForces.SelectedItem is not ForceVm force) return;
        ForceFlags flags = 0;
        if (ChkForceAllied.IsChecked == true) flags |= ForceFlags.Allied;
        if (ChkForceAlliedVictory.IsChecked == true) flags |= ForceFlags.AlliedVictory;
        if (ChkForceShareVision.IsChecked == true) flags |= ForceFlags.ShareVision;
        if (ChkForceShareControl.IsChecked == true) flags |= ForceFlags.ShareUnitControl;
        if (ChkForceShareAdvControl.IsChecked == true) flags |= ForceFlags.ShareAdvancedUnitControl;
        force.Flags = flags;
    }

    private void BtnAddForce_Click(object sender, RoutedEventArgs e)
    {
        var f = new ForceVm { Name = $"Force {_forces.Count + 1}" };
        _forces.Add(f);
        LstForces.SelectedItem = f;
    }

    private void BtnRemoveForce_Click(object sender, RoutedEventArgs e)
    {
        if (LstForces.SelectedItem is not ForceVm f || _forces.Count <= 1)
            return;
        _forces.Remove(f);
        if (_forces.Count > 0) LstForces.SelectedIndex = 0;
    }

    private void BtnPickLoadingModel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ModelPickerWindow(_blpService, _project, TxtLoadingPath.Text, "Modelo Pantalla de Carga")
        {
            Owner = this
        };
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedModelPath))
        {
            TxtLoadingPath.Text = picker.SelectedModelPath;
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var info = EnsureMapInfo();

        info.MapName = TxtMapName.Text.Trim();
        info.MapAuthor = TxtMapAuthor.Text.Trim();
        info.RecommendedPlayers = TxtSuggestedPlayers.Text.Trim();
        info.MapDescription = TxtMapDescription.Text;

        info.LoadingScreenPath = TxtLoadingPath.Text.Trim();
        info.LoadingScreenTitle = TxtLoadingTitle.Text.Trim();
        info.LoadingScreenSubtitle = TxtLoadingSubtitle.Text.Trim();
        info.LoadingScreenText = TxtLoadingText.Text;

        if (CmbTileset.SelectedItem is Tileset ts)
            info.Tileset = ts;

        var flags = info.MapFlags;
        SetFlag(ref flags, MapFlags.MaskedAreasArePartiallyVisible, ChkMaskPartiallyVisible.IsChecked == true);
        SetFlag(ref flags, MapFlags.HideMinimapInPreviewScreens, ChkHideMinimap.IsChecked == true);
        SetFlag(ref flags, MapFlags.UseCustomForces, ChkUseCustomForces.IsChecked == true);
        SetFlag(ref flags, MapFlags.FixedPlayerSettingsForCustomForces, ChkFixedPlayerSettings.IsChecked == true);
        SetFlag(ref flags, MapFlags.ShowWaterWavesOnCliffShores, ChkWavesCliff.IsChecked == true);
        SetFlag(ref flags, MapFlags.ShowWaterWavesOnRollingShores, ChkWavesRolling.IsChecked == true);
        SetFlag(ref flags, MapFlags.UseItemClassificationSystem, ChkUseItemClassification.IsChecked == true);
        SetFlag(ref flags, MapFlags.UseCustomTechtree, ChkUseCustomTechtree.IsChecked == true);
        SetFlag(ref flags, MapFlags.UseCustomAbilities, ChkUseCustomAbilities.IsChecked == true);
        SetFlag(ref flags, MapFlags.UseCustomUpgrades, ChkUseCustomUpgrades.IsChecked == true);
        SetFlag(ref flags, MapFlags.HasTerrainFog, ChkHasTerrainFog.IsChecked == true);
        info.MapFlags = flags;

        if (CmbWeather.SelectedItem is WeatherType wt)
            info.GlobalWeather = wt;
        if (CmbFogStyle.SelectedItem is FogStyle fs)
            info.FogStyle = fs;

        if (float.TryParse(TxtFogStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fStart))
            info.FogStartZ = fStart;
        if (float.TryParse(TxtFogEnd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fEnd))
            info.FogEndZ = fEnd;
        if (float.TryParse(TxtFogDensity.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fDens))
            info.FogDensity = fDens;

        info.FogColor = ParseColor3Or4(TxtFogColorRgb.Text, info.FogColor);
        info.WaterTintingColor = ParseColor3Or4(TxtWaterColorRgba.Text, info.WaterTintingColor);

        // Save Players back to MapInfo
        info.Players.Clear();
        foreach (var pv in _players.Where(p => p.Controller != PlayerController.None))
        {
            var pFlags = (PlayerFlags)0;
            if (pv.FixedStartLocation) pFlags |= PlayerFlags.FixedStartPosition;
            if (pv.Race == PlayerRace.Selectable) pFlags |= PlayerFlags.RaceSelectable;

            info.Players.Add(new PlayerData
            {
                Id = pv.Id,
                Name = pv.Name,
                Controller = pv.Controller,
                Race = pv.Race,
                Flags = pFlags,
                StartPosition = new Vector2(pv.StartX, pv.StartY),
                AllyLowPriorityFlags = new Bitmask32(0),
                AllyHighPriorityFlags = new Bitmask32(0),
                EnemyLowPriorityFlags = new Bitmask32(0),
                EnemyHighPriorityFlags = new Bitmask32(0)
            });
        }

        // Save Forces back to MapInfo
        info.Forces.Clear();
        foreach (var fv in _forces)
        {
            info.Forces.Add(new ForceData
            {
                Name = fv.Name,
                Flags = fv.Flags,
                Players = new Bitmask32(fv.MemberSlots)
            });
        }

        _project.MapName = info.MapName;
        _project.MapAuthor = info.MapAuthor;
        _project.MapDescription = info.MapDescription;
        _project.SuggestedPlayers = info.RecommendedPlayers;
        _project.HasScenarioEdits = true;

        _onApplied();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static void SetFlag(ref MapFlags flags, MapFlags flag, bool enabled)
    {
        if (enabled) flags |= flag;
        else flags &= ~flag;
    }

    private static System.Drawing.Color ParseColor3Or4(string text, System.Drawing.Color fallback)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 &&
            int.TryParse(parts[0], out int r) &&
            int.TryParse(parts[1], out int g) &&
            int.TryParse(parts[2], out int b))
        {
            int a = parts.Length >= 4 && int.TryParse(parts[3], out int parsedA) ? parsedA : fallback.A;
            return System.Drawing.Color.FromArgb(
                Math.Clamp(a, 0, 255),
                Math.Clamp(r, 0, 255),
                Math.Clamp(g, 0, 255),
                Math.Clamp(b, 0, 255));
        }
        return fallback;
    }
}
