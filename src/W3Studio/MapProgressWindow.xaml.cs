using System;
using System.Windows;

namespace W3Studio;

public partial class MapProgressWindow : Window
{
    public MapProgressWindow(string dialogTitle, string subjectName, string initialStep = "Iniciando...")
    {
        InitializeComponent();
        TxtDialogTitle.Text = dialogTitle;
        TxtMapName.Text = subjectName;
        TxtStepDetail.Text = initialStep;
        TxtPercent.Text = "0%";
        PrgOperation.Value = 0;
    }

    public void UpdateProgress(int percent, string stepText)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyProgressUi(clamped, stepText)));
            return;
        }

        ApplyProgressUi(clamped, stepText);
    }

    private void ApplyProgressUi(int percent, string stepText)
    {
        PrgOperation.IsIndeterminate = false;
        PrgOperation.Value = percent;
        TxtPercent.Text = $"{percent}%";
        if (!string.IsNullOrWhiteSpace(stepText))
            TxtStepDetail.Text = stepText;
    }
}

