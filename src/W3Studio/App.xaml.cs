using System;
using System.IO;
using System.Windows;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.WriteAllText(logPath, e.Exception.ToString());
            MessageBox.Show($"Error inesperado:\n{e.Exception.Message}", "W3 Studio Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_domain.log");
            File.WriteAllText(logPath, e.ExceptionObject?.ToString() ?? "Unknown");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = WorkspaceSettings.Load();
        ThemeService.Initialize(settings);
    }
}
