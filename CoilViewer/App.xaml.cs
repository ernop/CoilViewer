using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System;

namespace CoilViewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Logger.LogLaunch(e.Args);

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ViewerConfig.Load(configPath);
        var initialPathInput = e.Args.FirstOrDefault();
        string? resolvedInitialPath = null;
        if (!string.IsNullOrWhiteSpace(initialPathInput))
        {
            resolvedInitialPath = ResolvePath(initialPathInput);
            Logger.LogPathProbe(initialPathInput, resolvedInitialPath);
        }

        DirectoryInstanceGuard? initialGuard = null;
        var initialTarget = resolvedInitialPath ?? initialPathInput;
        var initialDirectory = DirectoryInstanceGuard.ResolveDirectory(initialTarget);
        if (initialDirectory != null)
        {
            if (!DirectoryInstanceGuard.TryAcquire(initialDirectory, out initialGuard))
            {
                DirectoryInstanceGuard.SignalExisting(initialDirectory, initialTarget);
                Shutdown();
                return;
            }
        }

        var window = new MainWindow(config, configPath, resolvedInitialPath ?? initialPathInput, initialGuard);
        window.Show();
    }

    private static string? ResolvePath(string input)
    {
        try
        {
            if (File.Exists(input) || Directory.Exists(input))
            {
                return Path.GetFullPath(input);
            }

            var expanded = Environment.ExpandEnvironmentVariables(input ?? string.Empty);
            if (File.Exists(expanded) || Directory.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }
        }
        catch
        {
            // ignore resolution errors
        }

        return null;
    }
}

