using System;
using System.Collections.Generic;
using System.IO;

namespace CoilViewer;

internal sealed class DetectionCache
{
    private readonly Dictionary<string, NsfwDetectionResult?> _nsfwCache = new();
    private readonly Dictionary<string, ObjectDetectionResult?> _objectCache = new();
    private readonly object _lock = new();

    public void SetNsfwResult(string imagePath, NsfwDetectionResult? result)
    {
        lock (_lock)
        {
            _nsfwCache[imagePath] = result;
        }
    }

    public NsfwDetectionResult? GetNsfwResult(string imagePath)
    {
        lock (_lock)
        {
            return _nsfwCache.TryGetValue(imagePath, out var result) ? result : null;
        }
    }

    public void SetObjectResult(string imagePath, ObjectDetectionResult? result)
    {
        lock (_lock)
        {
            _objectCache[imagePath] = result;
        }
    }

    public ObjectDetectionResult? GetObjectResult(string imagePath)
    {
        lock (_lock)
        {
            return _objectCache.TryGetValue(imagePath, out var result) ? result : null;
        }
    }

    public bool HasNsfwResult(string imagePath)
    {
        lock (_lock)
        {
            return _nsfwCache.ContainsKey(imagePath);
        }
    }

    public bool HasObjectResult(string imagePath)
    {
        lock (_lock)
        {
            return _objectCache.ContainsKey(imagePath);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _nsfwCache.Clear();
            _objectCache.Clear();
        }
    }
}

