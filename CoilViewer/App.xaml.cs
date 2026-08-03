using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System;

namespace CoilViewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static NsfwDetectionService? _nsfwService;
    private static ObjectDetectionService? _objectService;

    public static NsfwDetectionService? NsfwService => _nsfwService;
    public static ObjectDetectionService? ObjectService => _objectService;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTimeline.Mark("App.OnStartup entered");
        var totalTimer = Stopwatch.StartNew();
        var stepTimer = Stopwatch.StartNew();

        // Log .NET runtime information immediately after startup to diagnose version issues
        LogRuntimeEnvironmentInfo();
        
        base.OnStartup(e);
        Logger.Log($"[STARTUP] base.OnStartup: {stepTimer.ElapsedMilliseconds}ms");
        
        stepTimer.Restart();
        Logger.LogLaunch(e.Args);
        Logger.Log($"[STARTUP] Logger.LogLaunch: {stepTimer.ElapsedMilliseconds}ms");

        // Self-test mode: run deterministic zoom/pan checks without showing UI.
        // Usage: CoilViewer.exe --selftest
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            stepTimer.Restart();
            var zoomPanOk = ZoomPanSelfTest.Run();
            var imageSequenceOk = ImageSequenceSelfTest.Run();
            var ok = zoomPanOk && imageSequenceOk;
            Logger.Log($"[STARTUP] Self-tests zoomPanOk={zoomPanOk}, imageSequenceOk={imageSequenceOk}: {stepTimer.ElapsedMilliseconds}ms");
            Shutdown(ok ? 0 : 1);
            return;
        }

        stepTimer.Restart();
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ViewerConfig.Load(configPath);
        Logger.Log($"[STARTUP] Config loading: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        // Initialize ML models asynchronously in the background to avoid blocking UI startup
        InitializeModelsAsync(config);
        Logger.Log($"[STARTUP] InitializeModelsAsync (fire and forget): {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        var initialPathInput = e.Args.FirstOrDefault();
        string? resolvedInitialPath = null;
        if (!string.IsNullOrWhiteSpace(initialPathInput))
        {
            resolvedInitialPath = LaunchPathResolver.Resolve(initialPathInput);
            Logger.LogPathProbe(initialPathInput, resolvedInitialPath);
        }
        Logger.Log($"[STARTUP] Path resolution: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
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
        Logger.Log($"[STARTUP] DirectoryInstanceGuard setup: {stepTimer.ElapsedMilliseconds}ms");

        try
        {
            stepTimer.Restart();
            var window = new MainWindow(config, configPath, resolvedInitialPath ?? initialPathInput, initialGuard);
            Logger.Log($"[STARTUP] MainWindow constructor: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            window.Show();
            Logger.Log($"[STARTUP] window.Show(): {stepTimer.ElapsedMilliseconds}ms");
            StartupTimeline.Mark("Window shown");
            Logger.Log($"[STARTUP] TIME TO WINDOW VISIBLE: {StartupTimeline.ElapsedMs}ms since Main");

            window.ContentRendered += (_, _) =>
            {
                StartupTimeline.Mark("First content rendered");
                Logger.Log($"[STARTUP] TIME TO FIRST FRAME: {StartupTimeline.ElapsedMs}ms since Main");
            };
            
            stepTimer.Restart();
            window.Activate();
            
            // Ensure window is brought to front
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
            Logger.Log($"[STARTUP] Window activation and focus: {stepTimer.ElapsedMilliseconds}ms");
            
            totalTimer.Stop();
            Logger.Log($"[STARTUP] ========== TOTAL APP STARTUP TIME: {totalTimer.ElapsedMilliseconds}ms (timeline {StartupTimeline.ElapsedMs}ms since Main) ==========");
            Logger.Log("[STARTUP] Note: TIME TO FIRST IMAGE is logged separately when DisplayCurrentAsync completes.");
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to create or show MainWindow", ex);
            System.Windows.MessageBox.Show($"Failed to start CoilViewer: {ex.Message}", "CoilViewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _nsfwService?.Dispose();
        _objectService?.Dispose();
        base.OnExit(e);
    }

    private static async void InitializeModelsAsync(ViewerConfig config)
    {
        // Run model initialization on background thread to avoid blocking startup
        await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Initialize NSFW detection service if enabled
                if (config.EnableNsfwDetection && !string.IsNullOrWhiteSpace(config.NsfwModelPath))
                {
                    var service = new NsfwDetectionService();
                    service.Initialize(config.NsfwModelPath);
                    _nsfwService = service;
                    Logger.Log("NSFW detection service initialized in background.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to initialize NSFW detection service", ex);
            }

            try
            {
                // Initialize object detection service if enabled
                if (config.EnableObjectDetection && !string.IsNullOrWhiteSpace(config.ObjectModelPath))
                {
                    var service = new ObjectDetectionService();
                    service.Initialize(config.ObjectModelPath, config.ObjectLabelsPath, config.ObjectDetectionInputSize);
                    _objectService = service;
                    Logger.Log("Object detection service initialized in background.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to initialize object detection service", ex);
            }
        });
    }

    private static void LogRuntimeEnvironmentInfo()
    {
        try
        {
            var runtimeVersion = Environment.Version.ToString();
            var frameworkDescription = RuntimeInformation.FrameworkDescription;
            var osDescription = RuntimeInformation.OSDescription;
            var processArchitecture = RuntimeInformation.ProcessArchitecture;
            var osArchitecture = RuntimeInformation.OSArchitecture;
            var baseDirectory = AppContext.BaseDirectory;
            var runtimeConfigPath = Path.Combine(baseDirectory, "CoilViewer.runtimeconfig.json");
            var runtimeConfigExists = File.Exists(runtimeConfigPath);
            
            Logger.Log($"[RUNTIME-ENV] Framework: {frameworkDescription}");
            Logger.Log($"[RUNTIME-ENV] Environment.Version: {runtimeVersion}");
            Logger.Log($"[RUNTIME-ENV] Process Architecture: {processArchitecture}");
            Logger.Log($"[RUNTIME-ENV] OS: {osDescription} ({osArchitecture})");
            Logger.Log($"[RUNTIME-ENV] BaseDirectory: {baseDirectory}");
            Logger.Log($"[RUNTIME-ENV] RuntimeConfig exists: {runtimeConfigExists}");
            
            if (runtimeConfigExists)
            {
                try
                {
                    var runtimeConfigContent = File.ReadAllText(runtimeConfigPath);
                    Logger.Log($"[RUNTIME-ENV] RuntimeConfig.json contents: {runtimeConfigContent}");
                }
                catch (Exception ex)
                {
                    Logger.LogError("Failed to read runtimeconfig.json", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to log runtime environment info", ex);
        }
    }
}

