using System;
using System.IO;

namespace CoilViewer;

internal static class ImageSequenceSelfTest
{
    public static bool Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CoilViewer_ImageSequenceSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sequence = new ImageSequence();
            var detectionCache = new DetectionCache();

            var first = Path.Combine(root, "001.png");
            var second = Path.Combine(root, "002.png");
            File.WriteAllBytes(first, Array.Empty<byte>());
            File.WriteAllBytes(second, Array.Empty<byte>());

            sequence.LoadFromPath(root, SortField.FileName, SortDirection.Ascending);
            if (sequence.Count != 2 || !sequence.JumpToLast())
            {
                return false;
            }

            var third = Path.Combine(root, "003.png");
            File.WriteAllBytes(third, Array.Empty<byte>());

            var refreshed = sequence.RefreshFromDirectory(
                detectionCache,
                NsfwFilterMode.All,
                ObjectFilterMode.ShowAll,
                string.Empty,
                objectFilterThreshold: 0);
            if (!refreshed || sequence.Count != 3 || !string.Equals(sequence.CurrentPath, second, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            sequence.MoveNext(loop: false);
            if (!string.Equals(sequence.CurrentPath, third, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var zero = Path.Combine(root, "000.png");
            File.WriteAllBytes(zero, Array.Empty<byte>());

            sequence.JumpToFirst();
            refreshed = sequence.RefreshFromDirectory(
                detectionCache,
                NsfwFilterMode.All,
                ObjectFilterMode.ShowAll,
                string.Empty,
                objectFilterThreshold: 0);
            if (!refreshed || sequence.Count != 4 || !string.Equals(sequence.CurrentPath, first, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            sequence.MovePrevious(loop: false);
            return string.Equals(sequence.CurrentPath, zero, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogError("ImageSequenceSelfTest failed", ex);
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to clean image sequence self-test directory '{root}'", ex);
            }
        }
    }
}
