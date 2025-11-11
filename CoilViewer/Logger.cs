using System;
using System.IO;

namespace CoilViewer;

internal static class Logger
{
    private static readonly object Sync = new();
    private static readonly string RootDirectory = ResolveRootDirectory();
    private static readonly string LaunchLogPath = Path.Combine(RootDirectory, "coilviewer-launch.log");
    private static readonly string ErrorLogPath = Path.Combine(RootDirectory, "coilviewer-errors.log");

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

    private static void LogInternal(string path, string message)
    {
        try
        {
            var line = $"{DateTime.Now:O} {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // ignore logging failures
        }
    }
}
