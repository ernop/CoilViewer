using System;
using System.IO;

namespace CoilViewer;

internal static class Logger
{
    private static readonly object Sync = new();
    private static readonly string RootDirectory = ResolveRootDirectory();
    private static readonly string LaunchLogPath = Path.Combine(RootDirectory, "CoilViewer-launch.log");
    private static readonly string ErrorLogPath = Path.Combine(RootDirectory, "CoilViewer-errors.log");

    // Log rotation: when a log file exceeds this size it is renamed to .old and a
    // fresh file is started.  This prevents multi-megabyte log files from accumulating.
    private const long MaxLogSizeBytes = 2 * 1024 * 1024; // 2 MB

    private static string ResolveRootDirectory()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    public static void LogLaunch(string[] args)
    {
        LogInternal(LaunchLogPath, $"Launch args ({args.Length}): {string.Join(" | ", args)}");
    }

    public static void LogPathProbe(string input, string? resolvedPath)
    {
        var exists = resolvedPath != null && (File.Exists(resolvedPath) || Directory.Exists(resolvedPath));
        LogInternal(LaunchLogPath, $"Probe input='{input}', resolved='{resolvedPath}', exists={exists}");
    }

    public static void Log(string message)
    {
        LogInternal(LaunchLogPath, message);
    }

    public static void LogError(string message, Exception ex)
    {
        LogInternal(ErrorLogPath, $"{message}{Environment.NewLine}{ex}");
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxLogSizeBytes)
            {
                return;
            }

            var oldPath = path + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
            File.Move(path, oldPath);
        }
        catch
        {
            // Best-effort rotation; do not let this prevent logging.
        }
    }

    private static void LogInternal(string path, string message)
    {
        try
        {
            var line = $"{DateTime.Now:O} {message}{Environment.NewLine}";
            lock (Sync)
            {
                RotateIfNeeded(path);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // ignore logging failures
        }
    }
}
