using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CoilViewer;

internal sealed class ImageCache
{
    private readonly ImageSequence _sequence;
    private readonly ViewerConfig _config;
    private readonly Dictionary<int, BitmapSource> _cache = new();
    private readonly Dictionary<int, Task<BitmapSource>> _pending = new();
    private readonly object _lock = new();

    public ImageCache(ImageSequence sequence, ViewerConfig config)
    {
        _sequence = sequence;
        _config = config;
    }

    public Task<BitmapSource> GetOrLoadAsync(int index)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(index, out var cached))
            {
                return Task.FromResult(cached);
            }

            if (_pending.TryGetValue(index, out var task))
            {
                return task;
            }

            var loadTask = Task.Run(() => LoadBitmap(index));
            _pending[index] = loadTask;
            loadTask.ContinueWith(t =>
            {
                lock (_lock)
                {
                    _pending.Remove(index);
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        _cache[index] = t.Result;
                        Trim(index);
                    }
                }
            }, TaskScheduler.Default);

            return loadTask;
        }
    }

    public bool TryGetCached(int index, out BitmapSource? bitmap)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(index, out bitmap);
        }
    }

    public void PreloadAround(int centerIndex)
    {
        if (_sequence.Count == 0)
        {
            return;
        }

        var indices = new List<int>();
        for (int offset = -_config.PreloadImageCount; offset <= _config.PreloadImageCount; offset++)
        {
            var idx = WrapIndex(centerIndex + offset);
            if (idx >= 0)
            {
                indices.Add(idx);
            }
        }

        foreach (var idx in indices)
        {
            _ = GetOrLoadAsync(idx);
        }
    }

    private BitmapSource LoadBitmap(int index)
    {
        var totalTimer = Stopwatch.StartNew();
        var stepTimer = Stopwatch.StartNew();

        var path = _sequence[index];
        Logger.Log($"[IMAGECACHE] Get path from sequence: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Logger.Log($"[IMAGECACHE] Open FileStream for '{Path.GetFileName(path)}': {stepTimer.ElapsedMilliseconds}ms");
        
        stepTimer.Restart();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        Logger.Log($"[IMAGECACHE] BitmapDecoder.Create: {stepTimer.ElapsedMilliseconds}ms");
        
        stepTimer.Restart();
        var frame = decoder.Frames[0];
        Logger.Log($"[IMAGECACHE] Get decoder.Frames[0]: {stepTimer.ElapsedMilliseconds}ms");
        
        stepTimer.Restart();
        frame.Freeze();
        Logger.Log($"[IMAGECACHE] frame.Freeze(): {stepTimer.ElapsedMilliseconds}ms");
        
        totalTimer.Stop();
        Logger.Log($"[IMAGECACHE] ========== TOTAL LOADBITMAP TIME for '{Path.GetFileName(path)}': {totalTimer.ElapsedMilliseconds}ms ==========");
        
        return frame;
    }

    private void Trim(int centerIndex)
    {
        var maxCacheSize = (_config.PreloadImageCount * 2) + 1;
        if (_cache.Count <= maxCacheSize)
        {
            return;
        }

        var valid = new HashSet<int>();
        for (int offset = -_config.PreloadImageCount; offset <= _config.PreloadImageCount; offset++)
        {
            var idx = WrapIndex(centerIndex + offset);
            if (idx >= 0)
            {
                valid.Add(idx);
            }
        }

        var toRemove = new List<int>();
        foreach (var key in _cache.Keys)
        {
            if (!valid.Contains(key))
            {
                toRemove.Add(key);
            }
        }

        foreach (var key in toRemove)
        {
            _cache.Remove(key);
        }
    }

    private int WrapIndex(int index)
    {
        var count = _sequence.Count;
        if (count == 0)
        {
            return -1;
        }

        if (_config.LoopAround)
        {
            var mod = index % count;
            if (mod < 0)
            {
                mod += count;
            }

            return mod;
        }

        if (index < 0 || index >= count)
        {
            return -1;
        }

        return index;
    }
}
