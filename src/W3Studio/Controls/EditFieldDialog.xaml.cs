using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using War3Net.Build.Object;
using W3Studio.Models;

namespace W3Studio.Controls;

public partial class EditFieldDialog : Window
{
    private readonly W3ObjectField _field;
    private readonly W3ObjectEntry _entry;

    public string NewValue { get; private set; } = string.Empty;

    public EditFieldDialog(W3ObjectEntry entry, W3ObjectField field)
    {
        InitializeComponent();
        _entry = entry;
        _field = field;

        Title = $"Editar {field.FieldName} — {entry.Name} [{entry.Rawcode}]";
        TxtFieldHeader.Text = field.DisplayFieldTitle;
        TxtFieldSub.Text = $"Campo: {field.FieldId} • Tipo: {field.DataType} • Objeto: {entry.Name} [{entry.Rawcode}]";

        InitializeEditorMode();
    }

    private void InitializeEditorMode()
    {
        // 1. Si el campo tiene opciones predefinidas (ComboBox)
        if (_field.Choices != null && _field.Choices.Count > 0)
        {
            PnlChoice.Visibility = Visibility.Visible;
            PnlNumeric.Visibility = Visibility.Collapsed;
            PnlString.Visibility = Visibility.Collapsed;

            CmbChoice.ItemsSource = _field.Choices;
            CmbChoice.DisplayMemberPath = nameof(FieldChoiceOption.DisplayText);

            // Seleccionar el valor actual
            var currentVal = _field.ResolvedValue.Trim();
            var matched = _field.Choices.FirstOrDefault(c =>
                c.Value.Equals(currentVal, StringComparison.OrdinalIgnoreCase) ||
                c.DisplayText.Contains(currentVal, StringComparison.OrdinalIgnoreCase));

            CmbChoice.SelectedItem = matched ?? _field.Choices[0];

            Loaded += (_, _) =>
            {
                CmbChoice.Focus();
            };

            CmbChoice.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    BtnOk_Click(s, e);
                }
            };
            return;
        }

        // 2. Si el campo es numérico entero (Int)
        if (_field.DataType == ObjectDataType.Int)
        {
            PnlChoice.Visibility = Visibility.Collapsed;
            PnlNumeric.Visibility = Visibility.Visible;
            PnlString.Visibility = Visibility.Collapsed;

            TxtNumericLabel.Text = "Introduce un valor entero:";
            var min = _field.MinInt ?? int.MinValue;
            var max = _field.MaxInt ?? int.MaxValue;
            TxtNumericRange.Text = (min == int.MinValue && max == int.MaxValue)
                ? "Intervalo: Cualquier número entero de 32 bits"
                : $"Intervalo válido: [{min} .. {max}]";

            TxtNumericValue.Text = _field.ResolvedValue;

            Loaded += (_, _) =>
            {
                TxtNumericValue.Focus();
                TxtNumericValue.SelectAll();
            };

            TxtNumericValue.PreviewKeyDown += NumericPreviewKeyDown;
            return;
        }

        // 3. Si el campo es numérico real (Real / Float)
        if (_field.DataType == ObjectDataType.Real)
        {
            PnlChoice.Visibility = Visibility.Collapsed;
            PnlNumeric.Visibility = Visibility.Visible;
            PnlString.Visibility = Visibility.Collapsed;

            TxtNumericLabel.Text = $"Introduce un valor decimal (hasta {_field.DecimalPlaces} decimales):";
            var min = _field.MinReal ?? -1000000.0;
            var max = _field.MaxReal ?? 1000000.0;
            TxtNumericRange.Text = $"Intervalo válido: [{min.ToString("0.##", CultureInfo.InvariantCulture)} .. {max.ToString("0.##", CultureInfo.InvariantCulture)}] • Máx {_field.DecimalPlaces} decimales";

            TxtNumericValue.Text = _field.ResolvedValue;

            Loaded += (_, _) =>
            {
                TxtNumericValue.Focus();
                TxtNumericValue.SelectAll();
            };

            TxtNumericValue.PreviewKeyDown += NumericPreviewKeyDown;
            return;
        }

        // 4. Modo String / Texto libre
        PnlChoice.Visibility = Visibility.Collapsed;
        PnlNumeric.Visibility = Visibility.Collapsed;
        PnlString.Visibility = Visibility.Visible;
        TxtValue.Text = _field.ResolvedValue;

        Loaded += (_, _) =>
        {
            TxtValue.Focus();
            TxtValue.SelectAll();
        };

        TxtValue.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                e.Handled = true;
                BtnOk_Click(s, e);
            }
        };
    }

    private void NumericPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            BtnOk_Click(sender, e);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            BtnSpinUp_Click(sender, e);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            BtnSpinDown_Click(sender, e);
        }
    }

    private void TxtNumericValue_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ValidateNumericInput();
    }

    private bool ValidateNumericInput()
    {
        if (TxtNumericError == null || BtnOk == null) return false;

        var text = TxtNumericValue.Text.Trim().Replace(',', '.');
        if (string.IsNullOrEmpty(text))
        {
            TxtNumericError.Text = "El valor no puede estar vacío.";
            TxtNumericError.Visibility = Visibility.Visible;
            BtnOk.IsEnabled = false;
            return false;
        }

        if (_field.DataType == ObjectDataType.Int)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
            {
                TxtNumericError.Text = "Introduce un número entero válido (sin letras ni decimales).";
                TxtNumericError.Visibility = Visibility.Visible;
                BtnOk.IsEnabled = false;
                return false;
            }

            var min = _field.MinInt ?? int.MinValue;
            var max = _field.MaxInt ?? int.MaxValue;
            if (intVal < min || intVal > max)
            {
                TxtNumericError.Text = $"El valor debe estar dentro del intervalo [{min} .. {max}].";
                TxtNumericError.Visibility = Visibility.Visible;
                BtnOk.IsEnabled = false;
                return false;
            }
        }
        else if (_field.DataType == ObjectDataType.Real)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var realVal))
            {
                TxtNumericError.Text = "Introduce un número decimal válido.";
                TxtNumericError.Visibility = Visibility.Visible;
                BtnOk.IsEnabled = false;
                return false;
            }

            var min = _field.MinReal ?? -1000000.0;
            var max = _field.MaxReal ?? 1000000.0;
            if (realVal < min || realVal > max)
            {
                TxtNumericError.Text = $"El valor debe estar dentro del intervalo [{min.ToString("0.##", CultureInfo.InvariantCulture)} .. {max.ToString("0.##", CultureInfo.InvariantCulture)}].";
                TxtNumericError.Visibility = Visibility.Visible;
                BtnOk.IsEnabled = false;
                return false;
            }
        }

        TxtNumericError.Visibility = Visibility.Collapsed;
        BtnOk.IsEnabled = true;
        return true;
    }

    private void BtnSpinUp_Click(object sender, RoutedEventArgs e)
    {
        AdjustNumericValue(1);
    }

    private void BtnSpinDown_Click(object sender, RoutedEventArgs e)
    {
        AdjustNumericValue(-1);
    }

    private void AdjustNumericValue(int direction)
    {
        var text = TxtNumericValue.Text.Trim().Replace(',', '.');
        if (_field.DataType == ObjectDataType.Int)
        {
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val);
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            val += direction * step;
            var min = _field.MinInt ?? int.MinValue;
            var max = _field.MaxInt ?? int.MaxValue;
            if (val < min) val = min;
            if (val > max) val = max;
            TxtNumericValue.Text = val.ToString();
        }
        else if (_field.DataType == ObjectDataType.Real)
        {
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val);
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1.0 : 0.1;
            val += direction * step;
            var min = _field.MinReal ?? -1000000.0;
            var max = _field.MaxReal ?? 1000000.0;
            if (val < min) val = min;
            if (val > max) val = max;
            TxtNumericValue.Text = Math.Round(val, _field.DecimalPlaces).ToString($"F{_field.DecimalPlaces}", CultureInfo.InvariantCulture);
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        // 1. Modo Opciones desplegables
        if (_field.Choices != null && _field.Choices.Count > 0)
        {
            if (CmbChoice.SelectedItem is FieldChoiceOption opt)
            {
                NewValue = opt.Value;
            }
            else
            {
                NewValue = CmbChoice.Text;
            }
            DialogResult = true;
            Close();
            return;
        }

        // 2. Modo Numérico
        if (_field.DataType == ObjectDataType.Int || _field.DataType == ObjectDataType.Real)
        {
            if (!ValidateNumericInput())
                return;

            var text = TxtNumericValue.Text.Trim().Replace(',', '.');
            if (_field.DataType == ObjectDataType.Int)
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                {
                    NewValue = intVal.ToString(CultureInfo.InvariantCulture);
                    DialogResult = true;
                    Close();
                    return;
                }
            }
            else if (_field.DataType == ObjectDataType.Real)
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var realVal))
                {
                    realVal = Math.Round(realVal, _field.DecimalPlaces);
                    NewValue = realVal.ToString("0.##", CultureInfo.InvariantCulture);
                    DialogResult = true;
                    Close();
                    return;
                }
            }
            return;
        }

        // 3. Modo Texto libre
        NewValue = TxtValue.Text;
        DialogResult = true;
        Close();
    }
}
