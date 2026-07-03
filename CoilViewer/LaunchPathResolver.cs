using System;
using System.IO;

namespace CoilViewer;

internal static class LaunchPathResolver
{
    public static string? Resolve(string input)
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
