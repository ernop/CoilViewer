using System;
using System.Diagnostics;

namespace CoilViewer;

/// <summary>
/// Process-relative high-resolution timing. Starts in the static ctor so the
/// clock begins as early as managed code allows (before WPF Application init).
/// </summary>
internal static class StartupTimeline
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Sync = new();
    private static long _lastMarkMs;

    public static long ElapsedMs => Clock.ElapsedMilliseconds;

    public static void Mark(string label)
    {
        lock (Sync)
        {
            var now = Clock.ElapsedMilliseconds;
            var delta = now - _lastMarkMs;
            _lastMarkMs = now;
            Logger.Log($"[STARTUP-TIMELINE] +{now}ms (+{delta}ms) {label}");
        }
    }
}
