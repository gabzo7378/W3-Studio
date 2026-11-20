using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Services;

namespace W3Studio;

public partial class AutoFillLevelsWindow : Window
{
    private readonly int _maxLevels;
    private readonly bool _isInteger;

    public List<string> GeneratedValues { get; private set; } = new();

    public AutoFillLevelsWindow(string fieldDisplayName, int maxLevels, string initialBaseValue, bool isInteger)
    {
        _maxLevels = Math.Max(1, maxLevels);
        _isInteger = isInteger;

        InitializeComponent();

        TxtHeaderInfo.Text = $"{fieldDisplayName}  ({maxLevels} niveles)";
        if (double.TryParse(initialBaseValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            TxtBaseValue.Text = parsed.ToString("0.###", CultureInfo.InvariantCulture);

        UpdatePreview();
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            UpdatePreview();
    }

    private AutoFillProgressionMode GetSelectedMode()
    {
        var tag = (CmbMode?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "Geometric" => AutoFillProgressionMode.Geometric,
            "Wc3Formula" => AutoFillProgressionMode.Wc3Formula,
            "Constant" => AutoFillProgressionMode.Constant,
            _ => AutoFillProgressionMode.Linear
        };
    }

    private bool UpdatePreview()
    {
        if (LstPreview == null) return false;
        LstPreview.Items.Clear();

        if (!double.TryParse(TxtBaseValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var baseVal) ||
            !double.TryParse(TxtLevelFactor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) ||
            !double.TryParse(TxtPrevFactor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var prevFactor))
        {
            LstPreview.Items.Add("Ingresa valores numéricos válidos.");
            return false;
        }

        var mode = GetSelectedMode();
        GeneratedValues = ObjectEditorHelper.ComputeAutoFillLevelValues(baseVal, factor, prevFactor, _maxLevels, _isInteger, mode);
        for (int i = 0; i < GeneratedValues.Count; i++)
        {
            LstPreview.Items.Add($"Nivel {i + 1,2}:  {GeneratedValues[i]}");
        }
        return true;
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdatePreview() || GeneratedValues.Count == 0)
            return;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

