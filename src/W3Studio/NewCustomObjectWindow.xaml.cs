using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class NewCustomObjectWindow : Window
{
    private readonly W3ObjectCategory _category;
    private readonly string _baseRawcode;
    private readonly HashSet<string> _usedRawcodes;

    public string ObjectName => TxtObjectName.Text.Trim();
    public string CustomRawcode => TxtRawcode.Text.Trim();

    public NewCustomObjectWindow(
        W3ObjectCategory category,
        string baseRawcode,
        string baseDisplayName,
        string suggestedRawcode,
        IEnumerable<string> usedRawcodes)
    {
        _category = category;
        _baseRawcode = baseRawcode;
        _usedRawcodes = new HashSet<string>(usedRawcodes, StringComparer.OrdinalIgnoreCase);

        InitializeComponent();

        bool isHero = category == W3ObjectCategory.Units &&
                      UnitPaletteFilterHelper.IsHeroUnit(category, baseRawcode, baseRawcode);

        TxtBaseInfo.Text = $"Categoría: {category}  •  Base: [{baseRawcode}] {baseDisplayName}";
        TxtObjectName.Text = $"{baseDisplayName} (Custom)";
        TxtRawcode.Text = suggestedRawcode;
        TxtRawcodeHint.Text = isHero
            ? "Héroe WC3 1.27b: el Rawcode debe iniciar con MAYÚSCULA (ej: H000)."
            : "Rawcode único de 4 caracteres ASCII (ej: h000, A000, I000).";

        ValidateLive();
        TxtObjectName.Focus();
        TxtObjectName.SelectAll();
    }

    private void TxtRawcode_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateLive();
    }

    private bool ValidateLive()
    {
        var (ok, err) = ObjectEditorHelper.ValidateCustomRawcode(CustomRawcode, _category, _baseRawcode, _usedRawcodes);
        TxtValidationError.Text = ok ? string.Empty : err;
        BtnCreate.IsEnabled = ok;
        return ok;
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateLive())
            return;
        if (string.IsNullOrWhiteSpace(ObjectName))
        {
            MessageBox.Show(this, "Ingresa un nombre para el nuevo objeto.", "Nuevo Objeto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

