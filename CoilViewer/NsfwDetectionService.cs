using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CoilViewer;

public sealed class NsfwDetectionService : IDisposable
{
    private InferenceSession? _session;
    private readonly object _lock = new();
    private bool _isInitialized;
    private const int InputSize = 224; // Standard input size for many NSFW models
    private int _totalImagesChecked = 0;
    private long _totalMilliseconds = 0;

    public bool IsAvailable => _isInitialized && _session != null;
    
    public int TotalImagesChecked => _totalImagesChecked;
    public double AverageMillisecondsPerImage => _totalImagesChecked > 0 ? (double)_totalMilliseconds / _totalImagesChecked : 0;

    public void Initialize(string modelPath)
    {
        if (_isInitialized)
        {
            return;
        }

        lock (_lock)
        {
            if (_isInitialized)
            {
                return;
            }

            try
            {
                if (!File.Exists(modelPath))
                {
                    Logger.Log($"NSFW model not found at '{modelPath}'. NSFW detection will be disabled.");
                    _isInitialized = true; // Mark as initialized to prevent retries
                    return;
                }

                var options = new SessionOptions();
                
                // Try to use GPU if available (CUDA)
                // Note: CUDA provider requires Microsoft.ML.OnnxRuntime.Gpu package
                // For now, using CPU provider. GPU can be enabled by installing the GPU package.
                try
                {
                    // Uncomment when GPU package is installed:
                    // options.AppendExecutionProvider_Cuda();
                    Logger.Log("NSFW detection initialized with CPU (install Microsoft.ML.OnnxRuntime.Gpu for GPU support).");
                }
                catch
                {
                    Logger.Log("NSFW detection initialized with CPU.");
                }

                _session = new InferenceSession(modelPath, options);
                _isInitialized = true;
                Logger.Log($"NSFW detection service initialized successfully with model: {modelPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize NSFW detection service: {ex.Message}", ex);
                _session?.Dispose();
                _session = null;
                _isInitialized = true; // Mark as initialized to prevent retries
            }
        }
    }

    public NsfwDetectionResult? CheckImage(string imagePath)
    {
        if (!IsAvailable || _session == null)
        {
            Logger.Log($"NSFW detection skipped for '{imagePath}' (service unavailable).");
            return null;
        }

        try
        {
            using var bitmap = LoadImageAsBitmap(imagePath);
            var result = CheckImage(bitmap);

            if (result != null)
            {
                Logger.Log($"NSFW detection for '{imagePath}': is_nsfw={result.IsNsfw}, nsfw_prob={result.NsfwProbability:F3}, safe_prob={result.SafeProbability:F3}, confidence={result.Confidence:F3}");
            }
            else
            {
                Logger.Log($"NSFW detection for '{imagePath}' returned no result.");
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to check image '{imagePath}' for NSFW content", ex);
            return null;
        }
    }

    public NsfwDetectionResult? CheckImage(Bitmap bitmap)
    {
        if (!IsAvailable || _session == null)
        {
            return null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Preprocess image: resize to model input size and normalize
            using var resized = new Bitmap(bitmap, InputSize, InputSize);
            var input = PreprocessImage(resized);

            // Get input name from model metadata
            var inputName = _session.InputMetadata.Keys.First();
            var inputTensor = new DenseTensor<float>(input, new[] { 1, 3, InputSize, InputSize });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            // Run inference
            using var results = _session.Run(inputs);
            var output = results.First().Value as DenseTensor<float>;

            if (output == null)
            {
                return null;
            }

            // Interpret results
            // Most NSFW models output probabilities: [safe, nsfw] or [neutral, drawing, hentai, porn, sexy]
            // We'll handle both cases
            var probabilities = output.ToArray();
            
            if (probabilities.Length >= 2)
            {
                // Binary classification: [safe, nsfw]
                var nsfwProbability = probabilities.Length == 2 ? probabilities[1] : probabilities.Skip(1).Sum();
                
                stopwatch.Stop();
                lock (_lock)
                {
                    _totalImagesChecked++;
                    _totalMilliseconds += stopwatch.ElapsedMilliseconds;
                    
                    // Log performance stats every 10 images
                    if (_totalImagesChecked % 10 == 0)
                    {
                        var avgMs = AverageMillisecondsPerImage;
                        var totalSeconds = _totalMilliseconds / 1000.0;
                        Logger.Log($"NSFW detection: {_totalImagesChecked} images processed, {totalSeconds:F1}s total, {avgMs:F1}ms avg per image");
                    }
                }
                
                return new NsfwDetectionResult
                {
                    IsNsfw = nsfwProbability > 0.5f,
                    Confidence = nsfwProbability,
                    SafeProbability = probabilities[0],
                    NsfwProbability = nsfwProbability
                };
            }

            stopwatch.Stop();
            lock (_lock)
            {
                _totalImagesChecked++;
                _totalMilliseconds += stopwatch.ElapsedMilliseconds;
            }

            return null;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError("Failed to check image for NSFW content", ex);
            return null;
        }
    }

    private static Bitmap LoadImageAsBitmap(string imagePath)
    {
        // Use WPF's BitmapDecoder to load the image - supports all formats including WebP
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        
        // Convert to a format suitable for System.Drawing.Bitmap
        var bitmap = new Bitmap(frame.PixelWidth, frame.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            bitmap.PixelFormat);
        
        try
        {
            frame.CopyPixels(System.Windows.Int32Rect.Empty, bitmapData.Scan0, 
                bitmapData.Height * bitmapData.Stride, bitmapData.Stride);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
        
        return bitmap;
    }

    private static float[] PreprocessImage(Bitmap bitmap)
    {
        // Convert to RGB and normalize to [0, 1] range
        // Most models expect: (pixel / 255.0 - mean) / std
        // For simplicity, we'll use standard ImageNet normalization
        const float meanR = 0.485f;
        const float meanG = 0.456f;
        const float meanB = 0.406f;
        const float stdR = 0.229f;
        const float stdG = 0.224f;
        const float stdB = 0.225f;

        var data = new float[3 * InputSize * InputSize];
        var index = 0;

        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                
                // Normalize RGB values
                data[index] = (pixel.R / 255.0f - meanR) / stdR; // R
                data[index + InputSize * InputSize] = (pixel.G / 255.0f - meanG) / stdG; // G
                data[index + (2 * InputSize * InputSize)] = (pixel.B / 255.0f - meanB) / stdB; // B
                index++;
            }
        }

        return data;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _session?.Dispose();
            _session = null;
            _isInitialized = false;
        }
    }
}

public sealed class NsfwDetectionResult
{
    public bool IsNsfw { get; set; }
    public float Confidence { get; set; }
    public float SafeProbability { get; set; }
    public float NsfwProbability { get; set; }
}

