using System;
using System.IO;
using System.Text.Json;

namespace CoilViewer;

internal sealed class ViewerConfig
{
    public static ViewerConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var config = CreateDefault();
            config.Save(path);
            return config;
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<ViewerConfig>(json);
            config ??= CreateDefault();
            config.Normalize();
            return config;
        }
        catch (Exception)
        {
            var fallback = CreateDefault();
            fallback.Normalize();
            return fallback;
        }
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void Normalize()
    {
        if (PreloadRadius < 0)
        {
            PreloadRadius = 0;
        }

        var minimumCache = Math.Max(1, (PreloadRadius * 2) + 1);
        if (MaxCachedImages < minimumCache)
        {
            MaxCachedImages = minimumCache;
        }

        SortField = SortOptions.ParseField(SortField).ToConfigValue();
        SortDirection = SortOptions.ParseDirection(SortDirection).ToConfigValue();
    }

    public static ViewerConfig CreateDefault() => new()
    {
        PreloadRadius = 4,
        MaxCachedImages = 9,
        BackgroundColor = "#000000",
        FitMode = "Uniform",
        ScalingMode = "HighQuality",
        ShowOverlay = true,
        LoopAround = true,
        SortField = CoilViewer.SortField.FileName.ToConfigValue(),
        SortDirection = CoilViewer.SortDirection.Ascending.ToConfigValue()
    };

    public int PreloadRadius { get; set; }
    public int MaxCachedImages { get; set; }
    public string BackgroundColor { get; set; } = "#000000";
    public string FitMode { get; set; } = "Uniform";
    public string ScalingMode { get; set; } = "HighQuality";
    public bool ShowOverlay { get; set; } = true;
    public bool LoopAround { get; set; } = true;
    public string SortField { get; set; } = CoilViewer.SortField.FileName.ToConfigValue();
    public string SortDirection { get; set; } = CoilViewer.SortDirection.Ascending.ToConfigValue();
}
