using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CoilViewer;

internal sealed class ImageSequence
{
    private static readonly string[] SupportedExtensions =
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tiff", ".tif", ".webp", ".svg"
    };

    private static bool IsSupportedExtension(string extension)
    {
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private readonly List<string> _allImages = new();
    private readonly List<string> _images = new();

    public IReadOnlyList<string> Images => _images;

    public bool HasImages => _images.Count > 0;

    public int Count => _images.Count;

    public string? DirectoryPath { get; private set; }

    public SortField SortField { get; private set; } = SortField.FileName;

    public SortDirection SortDirection { get; private set; } = SortDirection.Ascending;

    public void LoadFromPath(string path, SortField sortField, SortDirection sortDirection, string? preferredImage = null)
    {
        var totalTimer = Stopwatch.StartNew();
        var stepTimer = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be provided", nameof(path));
        }

        var attributes = File.GetAttributes(path);
        var directory = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path)
            : new FileInfo(path).Directory ?? throw new InvalidOperationException("Unable to determine directory");
        Logger.Log($"[IMAGESEQUENCE] Get directory info: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        DirectoryPath = directory.FullName;
        SortField = sortField;
        SortDirection = sortDirection;

        _images.Clear();
        var files = directory
            .EnumerateFiles()
            .Where(f => IsSupportedExtension(f.Extension));
        Logger.Log($"[IMAGESEQUENCE] EnumerateFiles and filter: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        files = SortField switch
        {
            SortField.CreationTime => SortDirection == SortDirection.Ascending
                ? files.OrderBy(f => f.CreationTimeUtc)
                : files.OrderByDescending(f => f.CreationTimeUtc),
            SortField.LastWriteTime => SortDirection == SortDirection.Ascending
                ? files.OrderBy(f => f.LastWriteTimeUtc)
                : files.OrderByDescending(f => f.LastWriteTimeUtc),
            SortField.FileSize => SortDirection == SortDirection.Ascending
                ? files.OrderBy(f => f.Length)
                : files.OrderByDescending(f => f.Length),
            _ => SortDirection == SortDirection.Ascending
                ? files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
        };
        Logger.Log($"[IMAGESEQUENCE] Apply sort ordering: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        _images.AddRange(files.Select(f => f.FullName));
        Logger.Log($"[IMAGESEQUENCE] Materialize file list ({_images.Count} files): {stepTimer.ElapsedMilliseconds}ms");
        
        stepTimer.Restart();
        _allImages.Clear();
        _allImages.AddRange(_images);
        Logger.Log($"[IMAGESEQUENCE] Copy to _allImages: {stepTimer.ElapsedMilliseconds}ms");

        if (_images.Count == 0)
        {
            throw new InvalidOperationException($"No supported images found in '{directory.FullName}'.");
        }

        stepTimer.Restart();
        string? initialPath = preferredImage;
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            var fileInfo = new FileInfo(path);
            initialPath ??= fileInfo.FullName;
        }

        if (!string.IsNullOrEmpty(initialPath))
        {
            CurrentIndex = _images.FindIndex(p => string.Equals(p, initialPath, StringComparison.OrdinalIgnoreCase));
            if (CurrentIndex < 0)
            {
                CurrentIndex = 0;
            }
        }
        else
        {
            CurrentIndex = 0;
        }
        Logger.Log($"[IMAGESEQUENCE] Find initial index: {stepTimer.ElapsedMilliseconds}ms");
        
        totalTimer.Stop();
        Logger.Log($"[IMAGESEQUENCE] ========== TOTAL LOADFROMPATH TIME: {totalTimer.ElapsedMilliseconds}ms ==========");
    }

    public int CurrentIndex { get; private set; }

    public string CurrentPath => _images[CurrentIndex];

    public string this[int index] => _images[index];

    public int MoveNext(bool loop)
    {
        if (_images.Count == 0)
        {
            return -1;
        }

        if (CurrentIndex + 1 >= _images.Count)
        {
            if (!loop)
            {
                return CurrentIndex;
            }

            CurrentIndex = 0;
            return CurrentIndex;
        }

        CurrentIndex++;
        return CurrentIndex;
    }

    public int MovePrevious(bool loop)
    {
        if (_images.Count == 0)
        {
            return -1;
        }

        if (CurrentIndex - 1 < 0)
        {
            if (!loop)
            {
                return CurrentIndex;
            }

            CurrentIndex = _images.Count - 1;
            return CurrentIndex;
        }

        CurrentIndex--;
        return CurrentIndex;
    }

    public bool RemoveCurrent()
    {
        if (_images.Count == 0)
        {
            return false;
        }

        _images.RemoveAt(CurrentIndex);

        if (_images.Count == 0)
        {
            CurrentIndex = 0;
            return false;
        }

        if (CurrentIndex >= _images.Count)
        {
            CurrentIndex = _images.Count - 1;
        }

        return true;
    }

    public bool RemoveByPath(string path)
    {
        if (_images.Count == 0)
        {
            return false;
        }

        var index = _images.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _images.RemoveAt(index);

        if (_images.Count == 0)
        {
            CurrentIndex = 0;
            return false;
        }

        // Adjust CurrentIndex if the removed item was before or at the current position
        if (index <= CurrentIndex)
        {
            if (CurrentIndex > 0)
            {
                CurrentIndex--;
            }
            else
            {
                CurrentIndex = 0;
            }
        }

        if (CurrentIndex >= _images.Count)
        {
            CurrentIndex = _images.Count - 1;
        }

        return true;
    }

    public bool JumpToFirst()
    {
        if (_images.Count == 0)
        {
            return false;
        }

        CurrentIndex = 0;
        return true;
    }

    public bool JumpToLast()
    {
        if (_images.Count == 0)
        {
            return false;
        }

        CurrentIndex = _images.Count - 1;
        return true;
    }

    public bool JumpHalfTowardsEnd()
    {
        if (_images.Count == 0)
        {
            return false;
        }

        var remaining = (_images.Count - 1) - CurrentIndex;
        if (remaining <= 0)
        {
            return false;
        }

        var step = (int)Math.Ceiling(remaining / 2.0);
        step = Math.Max(step, 1);
        CurrentIndex = Math.Min(CurrentIndex + step, _images.Count - 1);
        return true;
    }

    public bool JumpHalfTowardsStart()
    {
        if (_images.Count == 0)
        {
            return false;
        }

        var remaining = CurrentIndex;
        if (remaining <= 0)
        {
            return false;
        }

        var step = (int)Math.Ceiling(remaining / 2.0);
        step = Math.Max(step, 1);
        CurrentIndex = Math.Max(CurrentIndex - step, 0);
        return true;
    }

    public void ApplyFilters(
        DetectionCache detectionCache,
        NsfwFilterMode nsfwFilter,
        ObjectFilterMode objectFilter,
        string objectFilterText,
        float objectFilterThreshold)
    {
        // Save current path before filtering
        string? currentPath = null;
        if (_images.Count > 0 && CurrentIndex >= 0 && CurrentIndex < _images.Count)
        {
            currentPath = _images[CurrentIndex];
        }

        _images.Clear();
        
        foreach (var imagePath in _allImages)
        {
            bool include = true;

            // Apply NSFW filter
            if (nsfwFilter != NsfwFilterMode.All)
            {
                var nsfwResult = detectionCache.GetNsfwResult(imagePath);
                bool isNsfw = nsfwResult?.IsNsfw == true;

                if (nsfwFilter == NsfwFilterMode.NoNsfw && isNsfw)
                {
                    include = false;
                }
                else if (nsfwFilter == NsfwFilterMode.NsfwOnly && !isNsfw)
                {
                    include = false;
                }
            }

            // Apply object filter
            if (include && objectFilter != ObjectFilterMode.ShowAll && !string.IsNullOrWhiteSpace(objectFilterText))
            {
                var objectResult = detectionCache.GetObjectResult(imagePath);
                bool containsObject = objectResult?.Predictions.Any(p =>
                    p.Confidence >= objectFilterThreshold &&
                    p.ClassName.ToLowerInvariant().Contains(objectFilterText.ToLowerInvariant())) == true;

                if (objectFilter == ObjectFilterMode.ShowOnly && !containsObject)
                {
                    include = false;
                }
                else if (objectFilter == ObjectFilterMode.Exclude && containsObject)
                {
                    include = false;
                }
            }

            if (include)
            {
                _images.Add(imagePath);
            }
        }

        // Adjust current index if needed
        if (_images.Count == 0)
        {
            CurrentIndex = 0;
        }
        else if (currentPath != null)
        {
            var newIndex = _images.IndexOf(currentPath);
            if (newIndex >= 0)
            {
                CurrentIndex = newIndex;
            }
            else
            {
                CurrentIndex = Math.Min(CurrentIndex, _images.Count - 1);
            }
        }
        else if (CurrentIndex >= _images.Count)
        {
            CurrentIndex = _images.Count - 1;
        }
    }

    public int AllImagesCount => _allImages.Count;
}
