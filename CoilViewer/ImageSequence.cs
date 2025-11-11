using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CoilViewer;

internal sealed class ImageSequence
{
    private static readonly string[] SupportedExtensions =
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tiff", ".tif", ".webp"
    };

    private readonly List<string> _images = new();

    public IReadOnlyList<string> Images => _images;

    public bool HasImages => _images.Count > 0;

    public int Count => _images.Count;

    public string? DirectoryPath { get; private set; }

    public SortField SortField { get; private set; } = SortField.FileName;

    public SortDirection SortDirection { get; private set; } = SortDirection.Ascending;

    public void LoadFromPath(string path, SortField sortField, SortDirection sortDirection, string? preferredImage = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be provided", nameof(path));
        }

        var attributes = File.GetAttributes(path);
        var directory = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path)
            : new FileInfo(path).Directory ?? throw new InvalidOperationException("Unable to determine directory");

        DirectoryPath = directory.FullName;
        SortField = sortField;
        SortDirection = sortDirection;

        _images.Clear();
        var files = directory
            .EnumerateFiles()
            .Where(f => IsSupportedExtension(f.Extension));

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

        _images.AddRange(files.Select(f => f.FullName));

        if (_images.Count == 0)
        {
            throw new InvalidOperationException($"No supported images found in '{directory.FullName}'.");
        }

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

    private static bool IsSupportedExtension(string extension)
    {
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
