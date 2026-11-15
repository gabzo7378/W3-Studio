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
            AppLogger.Error("App.Dispatcher", "Unhandled UI exception", e.Exception);
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.WriteAllText(logPath, e.Exception.ToString());
            MessageBox.Show($"Error inesperado:\n{e.Exception.Message}", "W3 Studio Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLogger.Error("App.Domain", $"Unhandled domain exception: {e.ExceptionObject}", e.ExceptionObject as Exception);
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
