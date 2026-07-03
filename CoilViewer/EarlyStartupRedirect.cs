using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CoilViewer;

/// <summary>
/// Runs before Main / WPF Application startup. Duplicate launches for an
/// already-open directory exit here without paying the full WPF boot cost.
/// </summary>
internal static class EarlyStartupRedirect
{
    [ModuleInitializer]
    internal static void RunBeforeMain()
    {
        StartupTimeline.Mark("ModuleInitializer entered");

        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var pathArg = args.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(pathArg))
            {
                return;
            }

            StartupTimeline.Mark("Early redirect path resolution begin");
            var resolved = LaunchPathResolver.Resolve(pathArg);
            var target = resolved ?? pathArg;
            var directory = DirectoryInstanceGuard.ResolveDirectory(target);
            if (directory == null)
            {
                return;
            }

            if (!DirectoryInstanceGuard.TryRedirectToExistingInstance(directory, target))
            {
                StartupTimeline.Mark("Early redirect check passed (new instance)");
                return;
            }

            StartupTimeline.Mark("Early redirect complete (WPF skipped)");
            Logger.Log($"[STARTUP] ========== EARLY REDIRECT EXIT: {StartupTimeline.ElapsedMs}ms (existing instance for '{directory}') ==========");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            // Redirect is best-effort; never prevent a normal launch.
            Logger.LogError("Early startup redirect failed; continuing with full launch.", ex);
        }
    }
}
