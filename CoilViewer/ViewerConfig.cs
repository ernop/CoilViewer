using System;
using System.IO;
using System.Text.Json;

namespace CoilViewer;

public sealed class ViewerConfig
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
        if (PreloadImageCount < 0)
        {
            PreloadImageCount = 0;
        }

        SortField = SortOptions.ParseField(SortField).ToConfigValue();
        SortDirection = SortOptions.ParseDirection(SortDirection).ToConfigValue();

        // Normalize NSFW model path
        if (!string.IsNullOrWhiteSpace(NsfwModelPath) && !Path.IsPathRooted(NsfwModelPath))
        {
            NsfwModelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, NsfwModelPath));
        }

        // Normalize object model paths
        if (!string.IsNullOrWhiteSpace(ObjectModelPath) && !Path.IsPathRooted(ObjectModelPath))
        {
            ObjectModelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ObjectModelPath));
        }

        if (!string.IsNullOrWhiteSpace(ObjectLabelsPath) && !Path.IsPathRooted(ObjectLabelsPath))
        {
            ObjectLabelsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ObjectLabelsPath));
        }
    }

    public static ViewerConfig CreateDefault() => new()
    {
        PreloadImageCount = 20,
        BackgroundColor = "#000000",
        FitMode = "Uniform",
        ScalingMode = "HighQuality",
        ShowOverlay = true,
        LoopAround = true,
        SortField = CoilViewer.SortField.FileName.ToConfigValue(),
        SortDirection = CoilViewer.SortDirection.Ascending.ToConfigValue(),
        EnableNsfwDetection = false,
        NsfwModelPath = "..\\..\\Models\\nsfw_detector.onnx",
        NsfwThreshold = 0.5f,
        NsfwFilterMode = "All",
        EnableObjectDetection = false,
        ObjectModelPath = "..\\..\\Models\\mobilenet_v2.onnx",
        ObjectLabelsPath = "..\\..\\Models\\imagenet_labels.txt",
        ObjectDetectionInputSize = 0,
        ObjectFilterMode = "ShowAll",
        ObjectFilterText = string.Empty,
        ObjectFilterThreshold = 0.1f
    };

    public int PreloadImageCount { get; set; }
    
    // Backward compatibility properties (deprecated)
    [System.Obsolete("Use PreloadImageCount instead")]
    public int PreloadRadius
    {
        get => PreloadImageCount;
        set => PreloadImageCount = value;
    }
    
    [System.Obsolete("No longer used - cache size is automatically calculated from PreloadImageCount")]
    public int MaxCachedImages { get; set; }
    
    public string BackgroundColor { get; set; } = "#000000";
    public string FitMode { get; set; } = "Uniform";
    public string ScalingMode { get; set; } = "HighQuality";
    public bool ShowOverlay { get; set; } = true;
    public bool LoopAround { get; set; } = true;
    public string SortField { get; set; } = CoilViewer.SortField.FileName.ToConfigValue();
    public string SortDirection { get; set; } = CoilViewer.SortDirection.Ascending.ToConfigValue();
    public bool EnableNsfwDetection { get; set; } = false;
    public string NsfwModelPath { get; set; } = string.Empty;
    public float NsfwThreshold { get; set; } = 0.5f;
    public string NsfwFilterMode { get; set; } = "All";
    public bool EnableObjectDetection { get; set; } = false;
    public string ObjectModelPath { get; set; } = string.Empty;
    public string ObjectLabelsPath { get; set; } = string.Empty;
    public int ObjectDetectionInputSize { get; set; } = 0; // 0 = auto-detect, or specify size (e.g. 224, 299, 384, 512)
    public string ObjectFilterMode { get; set; } = "ShowAll";
    public string ObjectFilterText { get; set; } = string.Empty;
    public float ObjectFilterThreshold { get; set; } = 0.3f;
}
