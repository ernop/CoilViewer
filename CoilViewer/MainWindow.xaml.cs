using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Drawing.Imaging;
using Microsoft.Win32;

using Point = System.Windows.Point;
using Vector = System.Windows.Vector;
using DataObject = System.Windows.DataObject;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Clipboard = System.Windows.Clipboard;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using MediaColor = System.Windows.Media.Color;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
namespace CoilViewer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const double ZoomStep = 1.25;
    private const double MinZoom = 1.0;
    private const double MaxZoom = 8.0;
    private const double PanWheelFactor = 0.5;
    private const double PanSmoothAcceleration = 3200.0;
    private const double PanSmoothDeceleration = 4200.0;
    private const double PanSmoothMaxSpeed = 1600.0;
    private const double PanSmoothInitialVelocity = 900.0;
    private const double PanSmoothTapDistance = 36.0;
    private const double PanSmoothEpsilon = 0.05;

    private const string PngDataFormat = "PNG";
    private const int ClipboardRetryCount = 5;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(120);
    private ViewerConfig _config;
    private readonly string _configPath;
    private readonly ImageSequence _sequence = new();
    private ImageCache? _cache;
    private readonly DetectionCache _detectionCache = new();
    private DirectoryInstanceGuard? _directoryGuard;
    private bool _isFullscreen;
    private bool _overlayVisible;
    private bool _shortcutsVisible;
    private bool _filterPanelVisible;
    private bool _filterControlsInitialized;
    private string? _pendingInitialPath;
    private double _fitScale = MinZoom;
    private double _zoomScale = MinZoom;
    private BitmapSource? _currentBitmap;
    private bool _isPanning;
    private Point _panStart;
    private Point _panOrigin;
    private Vector _panOffset;
    private readonly HashSet<Key> _smoothPanKeys = new();
    private Vector _smoothPanVelocity;
    private bool _isSmoothPanAnimating;
    private TimeSpan _smoothPanLastTick = TimeSpan.Zero;
    private readonly Stack<ArchiveStep> _archiveHistory = new();
    private readonly DispatcherTimer _statusTimer;
    private FileSystemWatcher? _folderWatcher;
    private readonly DispatcherTimer _folderRescanTimer;
    private readonly DispatcherTimer _overlayHintTimer;
    private DateTime _overlayHintUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan StatusDisplayDuration = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan OverlayHintDuration = TimeSpan.FromSeconds(5);
    private const int MaxArchiveHistorySize = 200;
    private const string QuadraticHintTag = "QuadraticHint";
    private bool _isQuadraticHintVisible;
    private readonly record struct ArchiveAction(string OriginalPath, string ArchivedPath);

    internal MainWindow(ViewerConfig config, string configPath, string? initialPath, DirectoryInstanceGuard? initialGuard)
    {
        var totalTimer = Stopwatch.StartNew();
        var stepTimer = Stopwatch.StartNew();

        InitializeComponent();
        Logger.Log($"[MAINWINDOW] Build {BuildVersion.VersionString} (counter={BuildVersion.Counter})");
        Logger.Log($"[MAINWINDOW] InitializeComponent: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        _config = config;
        _configPath = configPath;
        _directoryGuard = initialGuard;
        _directoryGuard?.AttachWindow(this);
        _directoryGuard?.SetRequestHandler(OnExternalOpenRequest);
        _statusTimer = new DispatcherTimer { Interval = StatusDisplayDuration };
        _statusTimer.Tick += OnStatusTimerTick;
        _folderRescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _folderRescanTimer.Tick += OnFolderRescanTick;
        _overlayHintTimer = new DispatcherTimer { Interval = OverlayHintDuration };
        _overlayHintTimer.Tick += OnOverlayHintTimerTick;
        Logger.Log($"[MAINWINDOW] Field initialization: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        ApplyConfig();
        Logger.Log($"[MAINWINDOW] ApplyConfig: {stepTimer.ElapsedMilliseconds}ms");
        
        stepTimer.Restart();
        UpdateContextMenu();
        Logger.Log($"[MAINWINDOW] UpdateContextMenu: {stepTimer.ElapsedMilliseconds}ms");

        Loaded += (_, _) => Focus();

        // Defer filter UI wiring and directory load until after the first paint so
        // window.Show() is not blocked by enumeration or filter panel setup.
        _pendingInitialPath = initialPath;
        ContentRendered += OnStartupContentRendered;

        totalTimer.Stop();
        Logger.Log($"[MAINWINDOW] ========== TOTAL MAINWINDOW CONSTRUCTOR TIME: {totalTimer.ElapsedMilliseconds}ms (LoadSequence deferred until first frame) ==========");
    }

    private void OnStartupContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnStartupContentRendered;

        var stepTimer = Stopwatch.StartNew();
        if (!_filterControlsInitialized)
        {
            InitializeFilterControls();
            _filterControlsInitialized = true;
            Logger.Log($"[MAINWINDOW] InitializeFilterControls (deferred): {stepTimer.ElapsedMilliseconds}ms");
        }

        stepTimer.Restart();
        if (!string.IsNullOrWhiteSpace(_pendingInitialPath))
        {
            var path = _pendingInitialPath;
            _pendingInitialPath = null;
            LoadSequence(path);
            Logger.Log($"[MAINWINDOW] LoadSequence (deferred): {stepTimer.ElapsedMilliseconds}ms");
        }
        else
        {
            ShowMessage("Press Ctrl+O to open an image.");
            Logger.Log($"[MAINWINDOW] ShowMessage (no initial path, deferred): {stepTimer.ElapsedMilliseconds}ms");
        }
    }

    private void ApplyConfig()
    {
        if (ColorConverter.ConvertFromString(_config.BackgroundColor) is MediaColor color)
        {
            Backdrop.Background = new SolidColorBrush(color);
        }

        ImageDisplay.Stretch = ParseStretch(_config.FitMode);
        RenderOptions.SetBitmapScalingMode(ImageDisplay, ParseScalingMode(_config.ScalingMode));
        SetOverlayVisibility(_config.ShowOverlay, animate: false);
    }

    private static Stretch ParseStretch(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => Stretch.None,
        "fill" => Stretch.Fill,
        "uniformtofill" => Stretch.UniformToFill,
        _ => Stretch.Uniform
    };

    private static BitmapScalingMode ParseScalingMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "lowquality" => BitmapScalingMode.LowQuality,
        "nearestneighbor" => BitmapScalingMode.NearestNeighbor,
        "fant" => BitmapScalingMode.Fant,
        "highquality" => BitmapScalingMode.HighQuality,
        _ => BitmapScalingMode.HighQuality
    };

    private static double PixelsToDip(int pixels, double dpi)
    {
        if (pixels <= 0)
        {
            return 0;
        }

        var effectiveDpi = dpi <= 0 ? 96.0 : dpi;
        return pixels * 96.0 / effectiveDpi;
    }

    private async Task DisplayCurrentAsync()
    {
        var totalTimer = Stopwatch.StartNew();
        var stepTimer = Stopwatch.StartNew();
        var isInitialLoad = _currentBitmap == null; // Track if this is the first image load

        if (_cache == null || !_sequence.HasImages)
        {
            Logger.Log($"[DISPLAYCURRENT] Early return (no cache or images): {stepTimer.ElapsedMilliseconds}ms");
            return;
        }

        stepTimer.Restart();
        SetShortcutsVisibility(false);
        Logger.Log($"[DISPLAYCURRENT] SetShortcutsVisibility: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        var index = _sequence.CurrentIndex;
        var path = _sequence.CurrentPath;
        Logger.Log($"[DISPLAYCURRENT] Get current index and path: {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        if (_cache.TryGetCached(index, out var cached) && cached != null)
        {
            Logger.Log($"[DISPLAYCURRENT] TryGetCached (HIT): {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            _currentBitmap = cached;
            ImageDisplay.Source = cached;
            HideMessage();
            Logger.Log($"[DISPLAYCURRENT] Set ImageDisplay.Source (cached): {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateFitScale(cached, forceReset: true);
            Logger.Log($"[DISPLAYCURRENT] UpdateFitScale: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateOverlay(cached, path, index);
            Logger.Log($"[DISPLAYCURRENT] UpdateOverlay: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateContextMenu();
            Logger.Log($"[DISPLAYCURRENT] UpdateContextMenu: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateWindowTitle();
            Logger.Log($"[DISPLAYCURRENT] UpdateWindowTitle: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            SchedulePreloadAround(index, defer: isInitialLoad);
            Logger.Log($"[DISPLAYCURRENT] PreloadAround scheduled (defer={isInitialLoad}): {stepTimer.ElapsedMilliseconds}ms");
            
            totalTimer.Stop();
            Logger.Log($"[DISPLAYCURRENT] ========== TOTAL DISPLAYCURRENT TIME (CACHED): {totalTimer.ElapsedMilliseconds}ms ==========");
            
            if (isInitialLoad)
            {
                StartupTimeline.Mark("First image displayed (cache hit)");
                Logger.Log($"[STARTUP] TIME TO FIRST IMAGE: {StartupTimeline.ElapsedMs}ms since Main");
                ShowStatus($"Image loaded from cache in {totalTimer.ElapsedMilliseconds}ms");
                ScheduleOverlayHint();
            }
            return;
        }

        Logger.Log($"[DISPLAYCURRENT] TryGetCached (MISS): {stepTimer.ElapsedMilliseconds}ms");

        stepTimer.Restart();
        ShowMessage("Loading...");
        Logger.Log($"[DISPLAYCURRENT] ShowMessage: {stepTimer.ElapsedMilliseconds}ms");

        try
        {
            stepTimer.Restart();
            var bitmap = await _cache.GetOrLoadAsync(index);
            Logger.Log($"[DISPLAYCURRENT] _cache.GetOrLoadAsync (actual load): {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            _currentBitmap = bitmap;
            ImageDisplay.Source = bitmap;
            HideMessage();
            Logger.Log($"[DISPLAYCURRENT] Set ImageDisplay.Source (loaded): {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateFitScale(bitmap, forceReset: true);
            Logger.Log($"[DISPLAYCURRENT] UpdateFitScale: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateOverlay(bitmap, path, index);
            Logger.Log($"[DISPLAYCURRENT] UpdateOverlay: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateContextMenu();
            Logger.Log($"[DISPLAYCURRENT] UpdateContextMenu: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateWindowTitle();
            Logger.Log($"[DISPLAYCURRENT] UpdateWindowTitle: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            SchedulePreloadAround(index, defer: isInitialLoad);
            Logger.Log($"[DISPLAYCURRENT] PreloadAround scheduled (defer={isInitialLoad}): {stepTimer.ElapsedMilliseconds}ms");
            
            totalTimer.Stop();
            Logger.Log($"[DISPLAYCURRENT] ========== TOTAL DISPLAYCURRENT TIME (LOADED): {totalTimer.ElapsedMilliseconds}ms ==========");
            
            if (isInitialLoad)
            {
                StartupTimeline.Mark("First image displayed");
                Logger.Log($"[STARTUP] TIME TO FIRST IMAGE: {StartupTimeline.ElapsedMs}ms since Main");
                ShowStatus($"Image loaded in {totalTimer.ElapsedMilliseconds}ms");
                ScheduleOverlayHint();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to load index={index}, path='{path}'", ex);
            ShowMessage($"Failed to load image '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private void SchedulePreloadAround(int index, bool defer)
    {
        if (_cache == null)
        {
            return;
        }

        if (!defer)
        {
            _cache.PreloadAround(index);
            return;
        }

        // On cold open, let the current image finish decoding before fanning out
        // PreloadImageCount neighbors (default 41 concurrent reads).
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _cache?.PreloadAround(index);
        }));
    }

    private void UpdateOverlay(BitmapSource bitmap, string path, int index)
    {
        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = path;
        }

        fileName = NormalizeFileNameDisplay(fileName);

        OverlayDetails.Text = string.Empty;
        OverlayDetails.ToolTip = null;

        long fileSize = 0;
        string? sortValue = null;

        try
        {
            var info = new FileInfo(path);
            fileSize = info.Length;
            sortValue = GetSortValue(info, _sequence.SortField);
        }
        catch
        {
            // fall back to defaults
        }

        var position = index + 1;
        var overlayMeta = new List<string>
        {
            $"{bitmap.PixelWidth}×{bitmap.PixelHeight}",
            FormatFileSize(fileSize),
            $"{position}/{_sequence.Count}"
        };
        OverlayTitle.Text = string.Join(" • ", overlayMeta);
        if (_config.ShowOverlay && DateTime.UtcNow < _overlayHintUntilUtc)
        {
            OverlayTitle.Text += " • Hit I to toggle";
        }
        UpdateWindowTitle();

        OverlaySort.Visibility = Visibility.Collapsed;
        OverlaySort.Text = string.Empty;

        // Clear previous detection results when switching images
        ClearDetectionResults();

        // Check NSFW content if enabled (for caching)
        if (_config.EnableNsfwDetection && App.NsfwService?.IsAvailable == true)
        {
            _ = CheckNsfwContentAsync(path);
        }

        // Check object content if enabled (for caching)
        if (_config.EnableObjectDetection && App.ObjectService?.IsAvailable == true)
        {
            _ = CheckObjectContentAsync(path);
        }
        
        // Auto-run and display detections if filter panel is open
        if (_filterPanelVisible)
        {
            RunDetectionsForCurrentImage();
        }
    }

    private async Task CheckNsfwContentAsync(string imagePath)
    {
        if (App.NsfwService == null || !App.NsfwService.IsAvailable)
        {
            return;
        }

        // Skip if already cached
        if (_detectionCache.HasNsfwResult(imagePath))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                var result = App.NsfwService.CheckImage(imagePath);
                _detectionCache.SetNsfwResult(imagePath, result);
                
                if (result != null && result.IsNsfw && result.Confidence >= _config.NsfwThreshold)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Logger.Log($"NSFW content detected in '{imagePath}' (confidence: {result.Confidence:P2})");
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to check NSFW content for '{imagePath}'", ex);
        }
    }

    private async Task CheckObjectContentAsync(string imagePath)
    {
        if (App.ObjectService == null || !App.ObjectService.IsAvailable)
        {
            return;
        }

        // Skip if already cached
        if (_detectionCache.HasObjectResult(imagePath))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                var result = App.ObjectService.DetectObjects(imagePath, topK: 10);
                _detectionCache.SetObjectResult(imagePath, result);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to check object content for '{imagePath}'", ex);
        }
    }

    private void ApplyFilters()
    {
        var nsfwFilter = Enum.TryParse<NsfwFilterMode>(_config.NsfwFilterMode, out var nsfw) ? nsfw : NsfwFilterMode.All;
        var objectFilter = Enum.TryParse<ObjectFilterMode>(_config.ObjectFilterMode, out var obj) ? obj : ObjectFilterMode.ShowAll;
        
        _sequence.ApplyFilters(_detectionCache, nsfwFilter, objectFilter, _config.ObjectFilterText, _config.ObjectFilterThreshold);
        _ = DisplayCurrentAsync();
    }

    private void UpdateWindowTitle()
    {
        string title = $"Coil Viewer v{BuildVersion.VersionString}";
        var directory = _sequence.DirectoryPath;
        if (!string.IsNullOrEmpty(directory))
        {
            title = $"Coil Viewer v{BuildVersion.VersionString} — {directory}";
        }

        if (_sequence.HasImages)
        {
            title += $" ({_sequence.CurrentIndex + 1}/{_sequence.Count})";
            
            // Add filename to title
            string fileName = Path.GetFileName(_sequence.CurrentPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                fileName = NormalizeFileNameDisplay(fileName);
                title += $" — {fileName}";
            }
        }

        var sortLabel = $"Sort: {GetFieldLabel(_sequence.SortField)} {GetDirectionLabel(_sequence.SortDirection)}";
        if (_sequence.HasImages && _sequence.SortField != SortField.FileName)
        {
            var sortValue = GetCurrentSortValue();
            if (!string.IsNullOrEmpty(sortValue))
            {
                sortLabel += $" ({sortValue})";
            }
        }

        Title = $"{title} — {sortLabel}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:0.##} GB",
            >= MB => $"{bytes / (double)MB:0.##} MB",
            >= KB => $"{bytes / (double)KB:0.##} KB",
            _ => $"{bytes} B"
        };
    }

    private static string NormalizeFileNameDisplay(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return fileName;
        }

        if (fileName.EndsWith(extension + extension, StringComparison.OrdinalIgnoreCase))
        {
            return fileName.Substring(0, fileName.Length - extension.Length);
        }

        return fileName;
    }

    private void ShowMessage(string text)
    {
        MessageBlock.Text = text;
        MessageBlock.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageBlock.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string text, bool persistent = false)
    {
        StatusText.Inlines.Clear();
        StatusText.Text = text;
        StatusText.Tag = null;
        StatusBar.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _isQuadraticHintVisible = false;

        if (!persistent)
        {
            _statusTimer.Start();
        }
    }

    private void ShowQuadraticJumpStatus(int previousIndex, int newIndex)
    {
        StatusText.Text = string.Empty;
        StatusText.Inlines.Clear();
        StatusText.Tag = null;
        _isQuadraticHintVisible = false;
        StatusText.Inlines.Add(new Run("Half jump: "));
        StatusText.Inlines.Add(new Run($"{previousIndex + 1}") { FontWeight = FontWeights.Bold });
        StatusText.Inlines.Add(new Run(" -> "));
        StatusText.Inlines.Add(new Run($"{newIndex + 1}") { FontWeight = FontWeights.Bold });
        StatusText.Inlines.Add(new Run($" of {_sequence.Count}"));
        StatusBar.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        _statusTimer.Stop();
        StatusBar.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
        StatusText.Inlines.Clear();
        StatusText.Tag = null;
        _isQuadraticHintVisible = false;
    }

    private void ShowQuadraticModeHint()
    {
        if (_isQuadraticHintVisible)
        {
            return;
        }

        string message;
        if (_sequence.HasImages && _sequence.Count > 0)
        {
            var current = _sequence.CurrentIndex + 1;
            var total = _sequence.Count;
            message = $"Quadratic mode ready: {current}/{total}. Press Left/Up to jump halfway toward the start, Right/Down toward the end. Release Ctrl+Shift to exit.";
        }
        else
        {
            message = "Quadratic mode ready: open an image to jump halfway toward the start or end.";
        }

        ShowStatus(message, persistent: true);
        StatusText.Tag = QuadraticHintTag;
        _isQuadraticHintVisible = true;
    }

    private void HideQuadraticModeHint()
    {
        if (!_isQuadraticHintVisible)
        {
            return;
        }

        _isQuadraticHintVisible = false;

        if (Equals(StatusText.Tag, QuadraticHintTag))
        {
            StatusText.Tag = null;
            HideStatus();
        }
    }

    private void ClearQuadraticHintState()
    {
        if (_isQuadraticHintVisible)
        {
            _isQuadraticHintVisible = false;
        }

        if (Equals(StatusText.Tag, QuadraticHintTag))
        {
            StatusText.Tag = null;
        }
    }

    private void UpdateQuadraticHintForKeyDown(Key key)
    {
        if (IsQuadraticMoveModifiersActive())
        {
            if (!_isQuadraticHintVisible && IsQuadraticModifierKey(key))
            {
                ShowQuadraticModeHint();
            }
        }
        else if (_isQuadraticHintVisible && IsQuadraticModifierKey(key))
        {
            HideQuadraticModeHint();
        }
    }

    private bool IsInputControlFocused()
    {
        var focusedElement = Keyboard.FocusedElement;
        return focusedElement is System.Windows.Controls.TextBox || focusedElement is System.Windows.Controls.ComboBox;
    }

    private static bool IsInputControl(object? element)
    {
        if (element == null)
        {
            return false;
        }
        
        // Check if the element itself is an input control
        if (element is System.Windows.Controls.TextBox || 
            element is System.Windows.Controls.ComboBox ||
            element is System.Windows.Controls.PasswordBox)
        {
            return true;
        }
        
        // Check if the element is inside a ComboBox (when dropdown is open)
        if (element is System.Windows.FrameworkElement fe)
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(fe);
            while (parent != null)
            {
                if (parent is System.Windows.Controls.ComboBox || 
                    parent is System.Windows.Controls.TextBox)
                {
                    return true;
                }
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
        }
        
        return false;
    }

    private void UpdateQuadraticHintForKeyUp()
    {
        if (IsQuadraticMoveModifiersActive())
        {
            if (!_isQuadraticHintVisible)
            {
                ShowQuadraticModeHint();
            }
        }
        else
        {
            HideQuadraticModeHint();
        }
    }

    private bool IsQuadraticMoveModifiersActive()
    {
        return Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
    }

    private static bool IsQuadraticModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift;

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        HideStatus();
    }

    private void MoveNext()
    {
        if (!_sequence.HasImages)
        {
            return;
        }

        var previousIndex = _sequence.CurrentIndex;
        var previousPath = _sequence.CurrentPath;
        var sequenceRefreshed = RefreshSequenceIfAtNavigationBoundary(forward: true);
        var index = _sequence.MoveNext(_config.LoopAround);
        if (index >= 0 && ShouldDisplayAfterNavigation(previousIndex, previousPath, sequenceRefreshed))
        {
            _ = DisplayCurrentAsync();
        }
    }

    private void MovePrevious()
    {
        if (!_sequence.HasImages)
        {
            return;
        }

        var previousIndex = _sequence.CurrentIndex;
        var previousPath = _sequence.CurrentPath;
        var sequenceRefreshed = RefreshSequenceIfAtNavigationBoundary(forward: false);
        var index = _sequence.MovePrevious(_config.LoopAround);
        if (index >= 0 && ShouldDisplayAfterNavigation(previousIndex, previousPath, sequenceRefreshed))
        {
            _ = DisplayCurrentAsync();
        }
    }

    private bool RefreshSequenceIfAtNavigationBoundary(bool forward)
    {
        if (!_sequence.HasImages)
        {
            return false;
        }

        var isAtBoundary = forward
            ? _sequence.CurrentIndex + 1 >= _sequence.Count
            : _sequence.CurrentIndex <= 0;
        if (!isAtBoundary)
        {
            return false;
        }

        var nsfwFilter = Enum.TryParse<NsfwFilterMode>(_config.NsfwFilterMode, out var nsfw)
            ? nsfw
            : NsfwFilterMode.All;
        var objectFilter = Enum.TryParse<ObjectFilterMode>(_config.ObjectFilterMode, out var obj)
            ? obj
            : ObjectFilterMode.ShowAll;

        try
        {
            var refreshed = _sequence.RefreshFromDirectory(
                _detectionCache,
                nsfwFilter,
                objectFilter,
                _config.ObjectFilterText,
                _config.ObjectFilterThreshold);
            if (refreshed)
            {
                _cache = new ImageCache(_sequence, _config);
                UpdateContextMenu();
                UpdateWindowTitle();
            }

            return refreshed;
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to refresh image sequence at navigation boundary", ex);
            return false;
        }
    }

    private bool ShouldDisplayAfterNavigation(int previousIndex, string previousPath, bool sequenceRefreshed)
    {
        if (!_sequence.HasImages)
        {
            return false;
        }

        return sequenceRefreshed
            || _sequence.CurrentIndex != previousIndex
            || !string.Equals(_sequence.CurrentPath, previousPath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task MoveCurrentImageToOldAsync()
    {
        if (!_sequence.HasImages)
        {
            ShowMessage("No image to move.");
            return;
        }

        var currentPath = _sequence.CurrentPath;
        var currentIndex = _sequence.CurrentIndex;
        var hasNext = currentIndex + 1 < _sequence.Count;

        // Calculate target path before removing from sequence
        string targetPath;
        try
        {
            var fileInfo = new FileInfo(currentPath);
            var directory = fileInfo.Directory ?? throw new InvalidOperationException("Unable to determine image directory.");
            var targetDirectory = Path.Combine(directory.FullName, "old");
            Directory.CreateDirectory(targetDirectory);

            var originalExtension = Path.GetExtension(currentPath);
            var originalBaseName = Path.GetFileNameWithoutExtension(currentPath);
            targetPath = Path.Combine(targetDirectory, Path.GetFileName(currentPath));
            if (File.Exists(targetPath))
            {
                var counter = 1;
                do
                {
                    targetPath = Path.Combine(targetDirectory, $"{originalBaseName}_{counter}{originalExtension}");
                    counter++;
                } while (File.Exists(targetPath));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to prepare move for image '{currentPath}'", ex);
            ShowMessage($"Failed to move image '{Path.GetFileName(currentPath)}': {ex.Message}");
            return;
        }

        // Immediately move to next image and display it (if available)
        if (hasNext)
        {
            _sequence.MoveNext(loop: false);
        }

        // Remove the archived image from sequence
        var removed = _sequence.RemoveByPath(currentPath);

        // Update cache after removal
        if (removed)
        {
            _cache = new ImageCache(_sequence, _config);
            ResetZoom();
            UpdateContextMenu();
            await DisplayCurrentAsync();
        }
        else
        {
            _cache = null;
            _currentBitmap = null;
            ImageDisplay.Source = null;
            OverlayTitle.Text = string.Empty;
            OverlayDetails.Text = string.Empty;
            OverlaySort.Text = string.Empty;
            SetOverlayVisibility(false, animate: false);
            UpdateContextMenu();
            _fitScale = MinZoom;
            SetZoom(_fitScale);
            ShowMessage("No images remain.");
            UpdateWindowTitle();
        }

        // Do the actual file move in the background (fire and forget)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Run(() => File.Move(currentPath, targetPath));
                _archiveHistory.Push(new ArchiveStep(currentPath, targetPath));
                TrimArchiveHistory();
                Logger.Log($"Moved image '{currentPath}' to '{targetPath}'");
                Dispatcher.Invoke(() => ShowStatus($"Moved to '{targetPath}'."));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to move image '{currentPath}'", ex);
                Dispatcher.Invoke(() => ShowMessage($"Failed to move image '{Path.GetFileName(currentPath)}': {ex.Message}"));
            }
        });
    }

    private void UndoLastArchive()
    {
        if (_archiveHistory.Count == 0)
        {
            ShowStatus("Nothing to undo.");
            return;
        }

        var action = _archiveHistory.Pop();

        try
        {
            if (!File.Exists(action.ArchivedPath))
            {
                ShowStatus($"Cannot undo: '{action.ArchivedPath}' is missing.");
                return;
            }

            var destinationDirectory = Path.GetDirectoryName(action.OriginalPath);
            if (string.IsNullOrEmpty(destinationDirectory))
            {
                ShowStatus("Cannot undo: destination is invalid.");
                return;
            }

            Directory.CreateDirectory(destinationDirectory);

            if (File.Exists(action.OriginalPath))
            {
                ShowStatus($"Cannot undo: '{action.OriginalPath}' already exists.");
                _archiveHistory.Push(action);
                return;
            }

            File.Move(action.ArchivedPath, action.OriginalPath);
            Logger.Log($"Restored image '{action.OriginalPath}' from '{action.ArchivedPath}'");
            LoadSequence(action.OriginalPath, action.OriginalPath);
            ShowStatus($"Restored to '{action.OriginalPath}'.");
        }
        catch (Exception ex)
        {
            _archiveHistory.Push(action);
            Logger.LogError($"Failed to undo archive for '{action.OriginalPath}' from '{action.ArchivedPath}'", ex);
            ShowStatus("Failed to undo archive.");
        }
    }

    private void TrimArchiveHistory()
    {
        if (_archiveHistory.Count <= MaxArchiveHistorySize)
        {
            return;
        }

        // Stack has no RemoveAt; rebuild keeping only the most recent entries.
        var items = _archiveHistory.ToArray();
        _archiveHistory.Clear();
        for (int i = MaxArchiveHistorySize - 1; i >= 0; i--)
        {
            _archiveHistory.Push(items[i]);
        }
    }

    private void JumpToFirst()
    {
        if (_sequence.JumpToFirst())
        {
            _ = DisplayCurrentAsync();
        }
    }

    private void JumpToLast()
    {
        if (_sequence.JumpToLast())
        {
            _ = DisplayCurrentAsync();
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
            _isFullscreen = false;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
    }

    private void ReloadConfig()
    {
        _config = ViewerConfig.Load(_configPath);
        ApplyConfig();
        UpdateContextMenu();
        if (_sequence.HasImages)
        {
            LoadSequence(_sequence.CurrentPath, _sequence.CurrentPath);
        }
        else
        {
            HideMessage();
        }
    }

    private void PromptOpenImage()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.bmp;*.dib;*.gif;*.tiff;*.tif;*.webp;*.svg|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadSequence(dialog.FileName);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Escape key ALWAYS closes the program, even when typing in input fields
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Don't process other shortcuts if user is typing in an input control
        if (IsInputControlFocused() || IsInputControl(e.Source) || IsInputControl(e.OriginalSource))
        {
            return;
        }

        UpdateQuadraticHintForKeyDown(e.Key);

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.Right:
                case Key.Down:
                {
                    var previousIndex = _sequence.CurrentIndex;
                    if (_sequence.JumpHalfTowardsEnd())
                    {
                        Logger.Log($"Quadratic move forward: key={e.Key}, from={previousIndex}, to={_sequence.CurrentIndex}, total={_sequence.Count}");
                        _ = DisplayCurrentAsync();
                        ClearQuadraticHintState();
                        ShowQuadraticJumpStatus(previousIndex, _sequence.CurrentIndex);
                    }

                    e.Handled = true;
                    return;
                }
                case Key.Left:
                case Key.Up:
                {
                    var previousIndex = _sequence.CurrentIndex;
                    if (_sequence.JumpHalfTowardsStart())
                    {
                        Logger.Log($"Quadratic move backward: key={e.Key}, from={previousIndex}, to={_sequence.CurrentIndex}, total={_sequence.Count}");
                        _ = DisplayCurrentAsync();
                        ClearQuadraticHintState();
                        ShowQuadraticJumpStatus(previousIndex, _sequence.CurrentIndex);
                    }

                    e.Handled = true;
                    return;
                }
            }
        }

        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
            case Key.Left:
            case Key.Up:
                if (ZoomPanMath.GetArrowKeyAction(IsZoomed(), quadraticModifierPressed: false) == ArrowKeyAction.Pan)
                {
                    BeginSmoothPan(e.Key);
                    e.Handled = true;
                }
                else
                {
                    if (e.Key == Key.Right || e.Key == Key.Down)
                    {
                        MoveNext();
                    }
                    else
                    {
                        MovePrevious();
                    }

                    e.Handled = true;
                }

                break;
            case Key.Space:
                MoveNext();
                e.Handled = true;
                break;
            case Key.Back:
                MovePrevious();
                e.Handled = true;
                break;
            case Key.Home:
                JumpToFirst();
                e.Handled = true;
                break;
            case Key.End:
                JumpToLast();
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomIn();
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomOut();
                e.Handled = true;
                break;
            case Key.Oem5:
                ResetZoom();
                e.Handled = true;
                break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.None:
                _ = MoveCurrentImageToOldAsync();
                e.Handled = true;
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                UndoLastArchive();
                e.Handled = true;
                break;
            case Key.U when Keyboard.Modifiers == ModifierKeys.None:
                UndoLastArchive();
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                CopyCurrentImageToClipboard();
                e.Handled = true;
                break;
            case Key.R when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                ReloadConfig();
                e.Handled = true;
                break;
            case Key.O when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                PromptOpenImage();
                e.Handled = true;
                break;
            case Key.OemComma when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
            case Key.S when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                OnSettingsClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.I:
                ToggleOverlay();
                e.Handled = true;
                break;
            case Key.F:
                ToggleFilterPanel();
                e.Handled = true;
                break;
            case Key.Oem2:
            case Key.Divide:
                ToggleShortcuts();
                e.Handled = true;
                break;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        UpdateQuadraticHintForKeyUp();

        if (!IsPanKey(e.Key))
        {
            return;
        }

        _smoothPanKeys.Remove(e.Key);

        if (!IsZoomed())
        {
            StopSmoothPanAnimation(resetVelocity: true);
            return;
        }

        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var action = ZoomPanMath.GetMouseWheelAction(
            e.Delta,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            IsZoomed());

        switch (action)
        {
            case MouseWheelAction.ZoomIn:
                var zoomInPosition = e.GetPosition(ImageScrollViewer);
                ZoomIn(zoomInPosition);
                e.Handled = true;
                return;
            case MouseWheelAction.ZoomOut:
                var zoomOutPosition = e.GetPosition(ImageScrollViewer);
                ZoomOut(zoomOutPosition);
                e.Handled = true;
                return;
            case MouseWheelAction.PanVertical:
                Pan(0, -e.Delta * PanWheelFactor * GetPanSpeedMultiplier());
                e.Handled = true;
                return;
            case MouseWheelAction.NavigatePrevious:
                MovePrevious();
                e.Handled = true;
                return;
            case MouseWheelAction.NavigateNext:
                MoveNext();
                e.Handled = true;
                return;
            case MouseWheelAction.None:
                return;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            return;
        }

        if (!_isFullscreen && !IsZoomed())
        {
            DragMove();
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            LoadSequence(files[0]);
        }
    }

    private void LoadSequence(string path, string? preferredImage = null)
    {
        var totalTimer = Stopwatch.StartNew();
        var stepTimer = Stopwatch.StartNew();

        var resolvedDirectory = DirectoryInstanceGuard.ResolveDirectory(path);
        var requestTarget = preferredImage ?? path;
        Logger.Log($"[LOADSEQUENCE] ResolveDirectory: {stepTimer.ElapsedMilliseconds}ms");

        try
        {
            stepTimer.Restart();
            if (resolvedDirectory != null && !EnsureDirectoryGuard(resolvedDirectory, requestTarget))
            {
                ShowMessage($"Directory is already open in another Coil Viewer window: {resolvedDirectory}");
                return;
            }
            Logger.Log($"[LOADSEQUENCE] EnsureDirectoryGuard: {stepTimer.ElapsedMilliseconds}ms");

            stepTimer.Restart();
            var field = SortOptions.ParseField(_config.SortField);
            var direction = SortOptions.ParseDirection(_config.SortDirection);

            string? focus = preferredImage;
            if (focus == null && File.Exists(path))
            {
                focus = Path.GetFullPath(path);
            }

            Logger.Log($"LoadSequence path='{path}', focus='{focus}', field={field}, direction={direction}");
            Logger.Log($"[LOADSEQUENCE] Parse sort options and resolve focus: {stepTimer.ElapsedMilliseconds}ms");

            stepTimer.Restart();
            _sequence.LoadFromPath(path, field, direction, focus);
            Logger.Log($"[LOADSEQUENCE] _sequence.LoadFromPath: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            _detectionCache.Clear();
            _cache = new ImageCache(_sequence, _config);
            Logger.Log($"[LOADSEQUENCE] Create ImageCache: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateWindowTitle();
            Logger.Log($"[LOADSEQUENCE] UpdateWindowTitle: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            UpdateContextMenu();
            Logger.Log($"[LOADSEQUENCE] UpdateContextMenu: {stepTimer.ElapsedMilliseconds}ms");
            
            stepTimer.Restart();
            StartFolderWatcher(_sequence.DirectoryPath!);
            Logger.Log($"[LOADSEQUENCE] StartFolderWatcher: {stepTimer.ElapsedMilliseconds}ms");

            stepTimer.Restart();
            _ = DisplayCurrentAsync();
            Logger.Log($"[LOADSEQUENCE] DisplayCurrentAsync (fire and forget): {stepTimer.ElapsedMilliseconds}ms");
            
            totalTimer.Stop();
            Logger.Log($"[LOADSEQUENCE] ========== TOTAL LOADSEQUENCE TIME: {totalTimer.ElapsedMilliseconds}ms ==========");
        }
        catch (Exception ex)
        {
            Logger.LogError($"LoadSequence failed for '{path}'", ex);
            if (resolvedDirectory != null)
            {
                ReleaseDirectoryGuard(resolvedDirectory);
            }
            ShowMessage(ex.Message);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopFolderWatcher();
        _directoryGuard?.SetRequestHandler(null);
        _directoryGuard?.Dispose();
        _directoryGuard = null;
    _statusTimer.Tick -= OnStatusTimerTick;
    _statusTimer.Stop();
    _overlayHintTimer.Tick -= OnOverlayHintTimerTick;
    _overlayHintTimer.Stop();
    }

    private void StartFolderWatcher(string directory)
    {
        StopFolderWatcher();

        try
        {
            _folderWatcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _folderWatcher.Created += OnFolderChanged;
            _folderWatcher.Deleted += OnFolderChanged;
            _folderWatcher.Renamed += OnFolderChanged;
            Logger.Log($"[FOLDERWATCH] Watching '{directory}'");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to watch folder '{directory}'", ex);
        }
    }

    private void StopFolderWatcher()
    {
        _folderRescanTimer.Stop();

        if (_folderWatcher == null)
        {
            return;
        }

        _folderWatcher.EnableRaisingEvents = false;
        _folderWatcher.Created -= OnFolderChanged;
        _folderWatcher.Deleted -= OnFolderChanged;
        _folderWatcher.Renamed -= OnFolderChanged;
        _folderWatcher.Dispose();
        _folderWatcher = null;
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedImagePath(e.FullPath))
        {
            return;
        }

        ScheduleFolderRescan();
    }

    private static bool IsSupportedImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dib", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleFolderRescan()
    {
        _folderRescanTimer.Stop();
        _folderRescanTimer.Start();
    }

    private void OnFolderRescanTick(object? sender, EventArgs e)
    {
        _folderRescanTimer.Stop();

        if (!_sequence.RefreshFromDirectory(out var addedCount, out _))
        {
            return;
        }

        ApplyFolderRefresh(addedCount);
    }

    private void ApplyFolderRefresh(int addedCount)
    {
        var currentPath = _sequence.HasImages ? _sequence.CurrentPath : null;
        _cache = new ImageCache(_sequence, _config);
        UpdateContextMenu();
        UpdateWindowTitle();

        if (_sequence.Count == 0)
        {
            _currentBitmap = null;
            ImageDisplay.Source = null;
            OverlayTitle.Text = string.Empty;
            OverlayDetails.Text = string.Empty;
            OverlaySort.Text = string.Empty;
            SetOverlayVisibility(false, animate: false);
            _fitScale = MinZoom;
            SetZoom(_fitScale);
            ShowMessage("No images remain.");
            return;
        }

        if (currentPath != null
            && _currentBitmap != null
            && string.Equals(_sequence.CurrentPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            UpdateOverlay(_currentBitmap, currentPath, _sequence.CurrentIndex);
            SchedulePreloadAround(_sequence.CurrentIndex, defer: false);
            if (addedCount > 0)
            {
                ShowStatus(addedCount == 1 ? "1 new image" : $"{addedCount} new images");
            }

            return;
        }

        _ = DisplayCurrentAsync();
    }

    private bool EnsureDirectoryGuard(string directory, string requestPath)
    {
        if (_directoryGuard != null && string.Equals(_directoryGuard.Directory, directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!DirectoryInstanceGuard.TryAcquire(directory, out var newGuard))
        {
            DirectoryInstanceGuard.SignalExisting(directory, requestPath);
            return false;
        }

        _directoryGuard?.SetRequestHandler(null);
        _directoryGuard?.Dispose();
        _directoryGuard = newGuard!;
        _directoryGuard.AttachWindow(this);
        _directoryGuard.SetRequestHandler(OnExternalOpenRequest);
        return true;
    }

    private void ReleaseDirectoryGuard(string directory)
    {
        if (_directoryGuard == null)
        {
            return;
        }

        if (string.Equals(_directoryGuard.Directory, directory, StringComparison.OrdinalIgnoreCase))
        {
            _directoryGuard.SetRequestHandler(null);
            _directoryGuard.Dispose();
            _directoryGuard = null;
        }
    }

    private void OnExternalOpenRequest(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        LoadSequence(targetPath);
    }

    private void UpdateContextMenu()
    {
        var field = _sequence.SortField;
        var direction = _sequence.SortDirection;

        var directionLabel = GetDirectionLabel(direction);
        var sortValue = _sequence.HasImages ? GetCurrentSortValue() : null;
        var sortHeader = sortValue is not null
            ? $"Sort: {GetFieldLabel(field)} {directionLabel} ({sortValue})"
            : $"Sort: {GetFieldLabel(field)} {directionLabel}";
        MenuInfoSort.Header = sortHeader;

        SortFileNameAscMenuItem.IsChecked = field == SortField.FileName && direction == SortDirection.Ascending;
        SortFileNameDescMenuItem.IsChecked = field == SortField.FileName && direction == SortDirection.Descending;
        SortCreationAscMenuItem.IsChecked = field == SortField.CreationTime && direction == SortDirection.Ascending;
        SortCreationDescMenuItem.IsChecked = field == SortField.CreationTime && direction == SortDirection.Descending;
        SortModifiedAscMenuItem.IsChecked = field == SortField.LastWriteTime && direction == SortDirection.Ascending;
        SortModifiedDescMenuItem.IsChecked = field == SortField.LastWriteTime && direction == SortDirection.Descending;
        SortSizeAscMenuItem.IsChecked = field == SortField.FileSize && direction == SortDirection.Ascending;
        SortSizeDescMenuItem.IsChecked = field == SortField.FileSize && direction == SortDirection.Descending;

        if (_sequence.HasImages)
        {
            var currentPath = _sequence.CurrentPath;
            MenuCopyFullPathMenuItem.Header = currentPath;
            MenuCopyFullPathMenuItem.IsEnabled = true;
            var fileName = Path.GetFileName(_sequence.CurrentPath);
            MenuInfoCurrentItem.Header = $"Image: {_sequence.CurrentIndex + 1}/{_sequence.Count} ({fileName})";
        }
        else
        {
            MenuCopyFullPathMenuItem.Header = "Copy full path";
            MenuCopyFullPathMenuItem.IsEnabled = false;
            MenuInfoCurrentItem.Header = "Image: --/--";
        }
    }

    private string? GetCurrentSortValue()
    {
        try
        {
            var info = new FileInfo(_sequence.CurrentPath);
            return GetSortValue(info, _sequence.SortField);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetSortValue(FileInfo info, SortField field) => field switch
    {
        SortField.CreationTime => info.CreationTime.ToString("g", CultureInfo.CurrentCulture),
        SortField.LastWriteTime => info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture),
        SortField.FileSize => FormatFileSize(info.Length),
        _ => info.Name
    };

    private static string GetFieldLabel(SortField field) => field switch
    {
        SortField.CreationTime => "Date created",
        SortField.LastWriteTime => "Date modified",
        SortField.FileSize => "File size",
        _ => "File name"
    };

    private static string GetDirectionLabel(SortDirection direction) => direction == SortDirection.Ascending ? "ASC" : "DESC";

    private void ApplySort(SortField field, SortDirection direction)
    {
        _config.SortField = field.ToConfigValue();
        _config.SortDirection = direction.ToConfigValue();
        _config.Save(_configPath);
        UpdateContextMenu();

        if (_sequence.HasImages)
        {
            var current = _sequence.CurrentPath;
            LoadSequence(current, current);
        }
    }

    private void OnSortOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag)
        {
            return;
        }

        var parts = tag.Split('|');
        if (parts.Length != 2)
        {
            return;
        }

        var field = SortOptions.ParseField(parts[0]);
        var direction = SortOptions.ParseDirection(parts[1]);
        ApplySort(field, direction);
    }

    private void OnCopyFullPathClick(object sender, RoutedEventArgs e)
    {
        if (!_sequence.HasImages)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_sequence.CurrentPath);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to copy full path to clipboard.", ex);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_config, _configPath);
        if (settingsWindow.ShowDialog() == true)
        {
            ReloadConfig();
        }
    }

    private void CopyCurrentImageToClipboard()
    {
        if (!_sequence.HasImages || _currentBitmap == null)
        {
            ShowStatus("No image to copy.");
            return;
        }

        var currentPath = _sequence.CurrentPath;
        Logger.Log($"Clipboard copy requested for '{currentPath}'.");

        try
        {
            var source = GetFrozenBitmapSource(_currentBitmap);
            using var package = CreateClipboardPackage(source, currentPath);

            if (TrySetClipboard(package.DataObject, out var error))
            {
                Logger.Log($"Clipboard copy succeeded for '{currentPath}'.");
                ShowStatus("Image copied to clipboard.");
            }
            else
            {
                var message = $"Failed to copy image to clipboard after {ClipboardRetryCount} attempts.";
                if (error != null)
                {
                    Logger.LogError($"{message} Path='{currentPath}'.", error);
                }
                ShowStatus("Failed to copy image.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to copy image to clipboard.", ex);
            ShowStatus("Failed to copy image.");
        }
    }

    private static BitmapSource GetFrozenBitmapSource(BitmapSource source)
    {
        if (source.IsFrozen && (source.Dispatcher == null || source.Dispatcher.CheckAccess()))
        {
            return source;
        }

        var dispatcher = source.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => CloneFrozen(source));
        }

        return CloneFrozen(source);
    }

    private static BitmapSource CloneFrozen(BitmapSource source)
    {
        var clone = new WriteableBitmap(source);

        if (!clone.IsFrozen && clone.CanFreeze)
        {
            clone.Freeze();
        }

        return clone;
    }

    private static ClipboardPackage CreateClipboardPackage(BitmapSource source, string? filePath)
    {
        var dataObject = new DataObject();
        dataObject.SetImage(source);

        var disposables = new List<IDisposable>();

        var gdiBitmap = CreateGdiBitmap(source);
        dataObject.SetData(DataFormats.Bitmap, gdiBitmap, autoConvert: true);
        disposables.Add(gdiBitmap);

        var dibStream = EncodeToDeviceIndependentBitmap(source);
        if (dibStream != null)
        {
            dataObject.SetData(DataFormats.Dib, dibStream, autoConvert: false);
            disposables.Add(dibStream);
        }

        var pngStream = EncodeToPng(source);
        if (pngStream != null)
        {
            dataObject.SetData(PngDataFormat, pngStream, autoConvert: false);
            disposables.Add(pngStream);
        }

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var files = new StringCollection { filePath };
            dataObject.SetFileDropList(files);
        }

        return new ClipboardPackage(dataObject, disposables);
    }

    private static System.Drawing.Bitmap CreateGdiBitmap(BitmapSource source)
    {
        var bitmap = new System.Drawing.Bitmap(source.PixelWidth, source.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, bitmap.PixelFormat);

        try
        {
            source.CopyPixels(Int32Rect.Empty, data.Scan0, data.Height * data.Stride, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static MemoryStream? EncodeToDeviceIndependentBitmap(BitmapSource source)
    {
        var independentBitmap = CreateIndependentBitmap(source);
        if (independentBitmap == null)
        {
            return null;
        }

        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(independentBitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bytes = stream.ToArray();

        const int bitmapFileHeaderSize = 14;
        if (bytes.Length <= bitmapFileHeaderSize)
        {
            return null;
        }

        var dibBytes = new byte[bytes.Length - bitmapFileHeaderSize];
        Buffer.BlockCopy(bytes, bitmapFileHeaderSize, dibBytes, 0, dibBytes.Length);
        return new MemoryStream(dibBytes, writable: false);
    }

    private static MemoryStream? EncodeToPng(BitmapSource source)
    {
        var independentBitmap = CreateIndependentBitmap(source);
        if (independentBitmap == null)
        {
            return null;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(independentBitmap));

        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    private static BitmapSource? CreateIndependentBitmap(BitmapSource source)
    {
        try
        {
            var pixelFormat = source.Format;
            var width = source.PixelWidth;
            var height = source.PixelHeight;

            // Indexed pixel formats (e.g. Indexed8 from GIF/palettized PNG) require a
            // palette when constructing a WriteableBitmap.  Convert them to Bgra32 to
            // avoid the "Must specify a palette" exception and to produce a universally
            // compatible bitmap for clipboard / encoding operations.
            if (pixelFormat == PixelFormats.Indexed1 ||
                pixelFormat == PixelFormats.Indexed2 ||
                pixelFormat == PixelFormats.Indexed4 ||
                pixelFormat == PixelFormats.Indexed8 ||
                pixelFormat == PixelFormats.BlackWhite ||
                pixelFormat == PixelFormats.Gray2 ||
                pixelFormat == PixelFormats.Gray4)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                source = converted;
                pixelFormat = PixelFormats.Bgra32;
            }

            var stride = (width * pixelFormat.BitsPerPixel + 7) / 8;
            var pixels = new byte[height * stride];

            source.CopyPixels(pixels, stride, 0);

            var bitmap = new WriteableBitmap(width, height, source.DpiX, source.DpiY, pixelFormat, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to create independent bitmap for encoding.", ex);
            return null;
        }
    }

    private static bool TrySetClipboard(DataObject dataObject, out Exception? error)
    {
        error = null;

        for (int attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(dataObject, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;

                if (attempt == ClipboardRetryCount)
                {
                    break;
                }

                Thread.Sleep(ClipboardRetryDelay);
            }
        }

        return false;
    }

    private sealed class ClipboardPackage : IDisposable
    {
        public ClipboardPackage(DataObject dataObject, IEnumerable<IDisposable> resources)
        {
            DataObject = dataObject;
            _resources = new List<IDisposable>(resources.Where(r => r != null));
        }

        public DataObject DataObject { get; }

        private readonly List<IDisposable> _resources;

        public void Dispose()
        {
            foreach (var resource in _resources)
            {
                try
                {
                    resource.Dispose();
                }
                catch
                {
                    // ignore disposal errors
                }
            }
        }
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        UpdateContextMenu();
    }

    private void ToggleOverlay()
    {
        SetOverlayVisibility(!_overlayVisible, animate: true);
        if (_overlayVisible)
        {
            ScheduleOverlayHint();
        }
    }

    private void ScheduleOverlayHint()
    {
        if (!_config.ShowOverlay)
        {
            return;
        }

        _overlayHintUntilUtc = DateTime.UtcNow.Add(OverlayHintDuration);
        _overlayHintTimer.Stop();
        _overlayHintTimer.Start();

        if (_currentBitmap != null && _sequence.HasImages)
        {
            UpdateOverlay(_currentBitmap, _sequence.CurrentPath, _sequence.CurrentIndex);
        }
    }

    private void OnOverlayHintTimerTick(object? sender, EventArgs e)
    {
        _overlayHintTimer.Stop();
        _overlayHintUntilUtc = DateTime.MinValue;

        if (_currentBitmap != null && _sequence.HasImages)
        {
            UpdateOverlay(_currentBitmap, _sequence.CurrentPath, _sequence.CurrentIndex);
        }
    }

    private void ToggleFilterPanel()
    {
        SetFilterPanelVisibility(!_filterPanelVisible, animate: true);
    }

    private void SetFilterPanelVisibility(bool visible, bool animate)
    {
        if (visible && !_filterControlsInitialized)
        {
            InitializeFilterControls();
            _filterControlsInitialized = true;
        }

        _filterPanelVisible = visible;
        FilterPanel.BeginAnimation(UIElement.OpacityProperty, null);

        if (!visible)
        {
            if (animate)
            {
                var fadeOut = new DoubleAnimation(FilterPanel.Opacity, 0.0, TimeSpan.FromMilliseconds(150));
                fadeOut.Completed += (_, _) => FilterPanel.Visibility = Visibility.Collapsed;
                FilterPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                FilterPanel.Opacity = 0;
                FilterPanel.Visibility = Visibility.Collapsed;
            }
            return;
        }

        FilterPanel.Visibility = Visibility.Visible;
        if (animate)
        {
            var fadeIn = new DoubleAnimation(FilterPanel.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
            FilterPanel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
        else
        {
            FilterPanel.Opacity = 1.0;
        }
        
        // Auto-run detections when filter panel opens
        RunDetectionsForCurrentImage();
    }

    private void OnNsfwFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_config == null || string.IsNullOrEmpty(_configPath))
        {
            return;
        }

        if (NsfwFilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _config.NsfwFilterMode = tag;
            _config.Save(_configPath);
        }
    }

    private void OnObjectFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_config == null || string.IsNullOrEmpty(_configPath))
        {
            return;
        }

        if (ObjectFilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _config.ObjectFilterMode = tag;
            _config.Save(_configPath);
        }
    }

    private void OnObjectFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_config == null || string.IsNullOrEmpty(_configPath))
        {
            return;
        }

        if (sender is System.Windows.Controls.TextBox textBox)
        {
            _config.ObjectFilterText = textBox.Text;
            _config.Save(_configPath);
        }
    }

    private void OnApplyFiltersClick(object sender, RoutedEventArgs e)
    {
        ApplyFilters();
        ShowStatus($"Applied filters: {_sequence.Count} images shown");
    }

    private void InitializeFilterControls()
    {
        // Initialize NSFW filter
        var nsfwFilterIndex = _config.NsfwFilterMode switch
        {
            "NoNsfw" => 1,
            "NsfwOnly" => 2,
            _ => 0
        };
        NsfwFilterCombo.SelectedIndex = nsfwFilterIndex;

        // Initialize object filter
        var objectFilterIndex = _config.ObjectFilterMode switch
        {
            "ShowOnly" => 1,
            "Exclude" => 2,
            _ => 0
        };
        ObjectFilterCombo.SelectedIndex = objectFilterIndex;
        ObjectFilterText.Text = _config.ObjectFilterText;
    }

    private void SetOverlayVisibility(bool visible, bool animate)
    {
        _overlayVisible = visible;
        Overlay.BeginAnimation(UIElement.OpacityProperty, null);

        if (!visible)
        {
            if (animate)
            {
                var fadeOut = new DoubleAnimation(Overlay.Opacity, 0.0, TimeSpan.FromMilliseconds(150));
                fadeOut.Completed += (_, _) => Overlay.Visibility = Visibility.Collapsed;
                Overlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                Overlay.Opacity = 0;
                Overlay.Visibility = Visibility.Collapsed;
            }

            return;
        }

        Overlay.Visibility = Visibility.Visible;
        if (animate)
        {
            var fadeIn = new DoubleAnimation(Overlay.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
            Overlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
        else
        {
            Overlay.Opacity = 1.0;
        }
    }

    private void SetShortcutsVisibility(bool visible, bool animate = true)
    {
        if (_shortcutsVisible == visible && ((visible && ShortcutsOverlay.Visibility == Visibility.Visible) ||
            (!visible && ShortcutsOverlay.Visibility != Visibility.Visible)))
        {
            return;
        }

        _shortcutsVisible = visible;
        ShortcutsOverlay.BeginAnimation(UIElement.OpacityProperty, null);

        if (!visible)
        {
            if (ShortcutsOverlay.Visibility != Visibility.Visible)
            {
                ShortcutsOverlay.Visibility = Visibility.Collapsed;
                ShortcutsOverlay.Opacity = 0;
                return;
            }

            if (animate)
            {
                var fadeOut = new DoubleAnimation(ShortcutsOverlay.Opacity, 0.0, TimeSpan.FromMilliseconds(150));
                fadeOut.Completed += (_, _) =>
                {
                    ShortcutsOverlay.Visibility = Visibility.Collapsed;
                    ShortcutsOverlay.Opacity = 0;
                };
                ShortcutsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                ShortcutsOverlay.Opacity = 0;
                ShortcutsOverlay.Visibility = Visibility.Collapsed;
            }

            return;
        }

        ShortcutsOverlay.Visibility = Visibility.Visible;
        if (animate)
        {
            var fadeIn = new DoubleAnimation(ShortcutsOverlay.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
            ShortcutsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
        else
        {
            ShortcutsOverlay.Opacity = 1.0;
        }
    }

    private void ToggleShortcuts(bool forceHide = false)
    {
        if (forceHide)
        {
            SetShortcutsVisibility(false);
            return;
        }

        SetShortcutsVisibility(!_shortcutsVisible);
    }

    private bool IsZoomed() => ZoomPanMath.IsZoomed(_zoomScale, _fitScale);

    private void ZoomIn(Point? anchor = null)
    {
        if (!TryGetViewportSize(out _, out _) && anchor.HasValue)
        {
            anchor = null;
        }

        SetZoom(_zoomScale * ZoomStep, anchor);
    }

    private void ZoomOut(Point? anchor = null)
    {
        if (!TryGetViewportSize(out _, out _) && anchor.HasValue)
        {
            anchor = null;
        }

        SetZoom(_zoomScale / ZoomStep, anchor);
    }

    private void ResetZoom()
    {
        StopPanning();
        StopSmoothPanAnimation(resetVelocity: true);
        SetZoom(_fitScale);
    }

    private void SetZoom(double scale, Point? anchor = null)
    {
        var fit = _fitScale;
        var clamped = ZoomPanMath.ClampZoomScale(scale, fit, MaxZoom);

        Logger.Log($"SetZoom: requested={scale:F4}, fit={fit:F4}, clamped={clamped:F4}, _fitScale={_fitScale:F4}, _zoomScale={_zoomScale:F4}");

        var previousScale = _zoomScale;
        if (double.IsNaN(previousScale) || previousScale <= 0)
        {
            previousScale = fit;
        }

        // Capture current scroll state before scaling.
        var viewportOk = TryGetViewportSize(out var viewportW, out var viewportH);
        var anchorPoint = anchor;
        if (!viewportOk)
        {
            anchorPoint = null;
        }

        var anchorX = anchorPoint?.X ?? (viewportW * 0.5);
        var anchorY = anchorPoint?.Y ?? (viewportH * 0.5);

        var oldOffsetX = ImageScrollViewer.HorizontalOffset;
        var oldOffsetY = ImageScrollViewer.VerticalOffset;

        _zoomScale = clamped;

        // Apply zoom via layout transform.
        ImageScaleTransform.ScaleX = clamped;
        ImageScaleTransform.ScaleY = clamped;

        // Force layout so ScrollViewer recomputes extents/scrollable range.
        ImageScrollViewer.UpdateLayout();

        // Preserve the same anchor content point under the cursor/center.
        // Delegate to ZoomPanMath which computes content sizes from the bitmap's
        // DIP dimensions rather than from ScrollViewer.ExtentWidth/Height.
        // WPF's ScrollViewer pads Extent to at least the viewport size when
        // content is smaller, which corrupts the centering-gap calculation.
        if (previousScale > 0 && _currentBitmap != null)
        {
            var imageDipW = PixelsToDip(_currentBitmap.PixelWidth, _currentBitmap.DpiX);
            var imageDipH = PixelsToDip(_currentBitmap.PixelHeight, _currentBitmap.DpiY);

            Logger.Log($"SetZoom detail: prevScale={previousScale:F6}, newScale={clamped:F6}, " +
                $"viewport=({viewportW:F1},{viewportH:F1}), oldOffset=({oldOffsetX:F2},{oldOffsetY:F2}), " +
                $"imageDip=({imageDipW:F1},{imageDipH:F1}), anchor=({anchorX:F1},{anchorY:F1}), " +
                $"extentWH=({ImageScrollViewer.ExtentWidth:F1},{ImageScrollViewer.ExtentHeight:F1}), " +
                $"scrollableWH=({ImageScrollViewer.ScrollableWidth:F1},{ImageScrollViewer.ScrollableHeight:F1})");

            var (newOffsetX, newOffsetY) = ZoomPanMath.ComputeScrollOffsetsAfterZoom(
                anchorX, anchorY,
                viewportW, viewportH,
                oldOffsetX, oldOffsetY,
                previousScale, clamped,
                imageDipW, imageDipH);

            Logger.Log($"SetZoom result: newOffset=({newOffsetX:F2},{newOffsetY:F2})");

            ImageScrollViewer.ScrollToHorizontalOffset(newOffsetX);
            ImageScrollViewer.ScrollToVerticalOffset(newOffsetY);

            // Verify the offsets were applied (deferred to after layout settles).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Logger.Log($"SetZoom verify: actualOffset=({ImageScrollViewer.HorizontalOffset:F2},{ImageScrollViewer.VerticalOffset:F2}), " +
                    $"scrollableWH=({ImageScrollViewer.ScrollableWidth:F1},{ImageScrollViewer.ScrollableHeight:F1})");
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            // Update centered pan cache using ScrollViewer's own scrollable range
            // (ApplyPan reads ScrollableWidth/Height, so _panOffset must match).
            var scrollableW = ImageScrollViewer.ScrollableWidth;
            var scrollableH = ImageScrollViewer.ScrollableHeight;
            if (double.IsNaN(scrollableW) || scrollableW < 0) scrollableW = 0;
            if (double.IsNaN(scrollableH) || scrollableH < 0) scrollableH = 0;

            _panOffset = new Vector(newOffsetX - scrollableW * 0.5, newOffsetY - scrollableH * 0.5);
        }
        else
        {
            ClampPan();
        }

        if (!IsZoomed())
        {
            StopSmoothPanAnimation(resetVelocity: true);
        }
    }

    private void UpdateFitScale(BitmapSource bitmap, bool forceReset)
    {
        if (bitmap == null)
        {
            return;
        }

        if (!TryGetViewportSize(out double viewportWidth, out double viewportHeight))
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateFitScale(bitmap, forceReset)), DispatcherPriority.Loaded);
            return;
        }

        double imageWidth = PixelsToDip(bitmap.PixelWidth, bitmap.DpiX);
        double imageHeight = PixelsToDip(bitmap.PixelHeight, bitmap.DpiY);

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return;
        }

        double computed = ZoomPanMath.ComputeFitScale(viewportWidth, viewportHeight, imageWidth, imageHeight);

        bool wasZoomed = IsZoomed();
        _fitScale = computed;
        // No separate fit transform - SetZoom handles the combined scale

        if (forceReset || !wasZoomed)
        {
            StopPanning();
            SetZoom(_fitScale);
        }
        else
        {
            SetZoom(Math.Max(_zoomScale, _fitScale));
        }
    }

    private double GetPanSpeedMultiplier()
    {
        // Scale pan speed with zoom level (relative to fit)
        if (_fitScale <= 0)
        {
            return 1.0;
        }
        var relativeZoom = _zoomScale / _fitScale;
        return Math.Max(1.0, relativeZoom);
    }

    private void BeginSmoothPan(Key key)
    {
        if (!IsPanKey(key))
        {
            return;
        }

        var added = _smoothPanKeys.Add(key);
        var speedMultiplier = GetPanSpeedMultiplier();

        if (added)
        {
            var direction = GetPanDirection(key);

            if (direction.X != 0)
            {
                var target = direction.X * PanSmoothInitialVelocity * speedMultiplier;
                _smoothPanVelocity.X = BlendInitialVelocity(_smoothPanVelocity.X, target);
            }

            if (direction.Y != 0)
            {
                var target = direction.Y * PanSmoothInitialVelocity * speedMultiplier;
                _smoothPanVelocity.Y = BlendInitialVelocity(_smoothPanVelocity.Y, target);
            }

            if (direction.X != 0 || direction.Y != 0)
            {
                Pan(direction.X * PanSmoothTapDistance * speedMultiplier, direction.Y * PanSmoothTapDistance * speedMultiplier);
            }
        }

        if (!_isSmoothPanAnimating)
        {
            CompositionTarget.Rendering += OnSmoothPanRendering;
            _isSmoothPanAnimating = true;
            _smoothPanLastTick = TimeSpan.Zero;
        }
    }

    private void OnSmoothPanRendering(object? sender, EventArgs e)
    {
        if (!IsZoomed())
        {
            StopSmoothPanAnimation(resetVelocity: true);
            return;
        }

        if (e is not RenderingEventArgs args)
        {
            return;
        }

        if (_smoothPanLastTick == TimeSpan.Zero)
        {
            _smoothPanLastTick = args.RenderingTime;
            return;
        }

        double deltaSeconds = (args.RenderingTime - _smoothPanLastTick).TotalSeconds;
        _smoothPanLastTick = args.RenderingTime;

        if (deltaSeconds <= 0 || deltaSeconds > 0.25)
        {
            deltaSeconds = 1.0 / 60.0;
        }

        var speedMultiplier = GetPanSpeedMultiplier();
        var accelerationMagnitude = PanSmoothAcceleration * speedMultiplier;
        var decelerationMagnitude = PanSmoothDeceleration * speedMultiplier;
        var maxSpeed = PanSmoothMaxSpeed * speedMultiplier;

        double accelerationX = 0;
        if (_smoothPanKeys.Contains(Key.Right))
        {
            accelerationX += accelerationMagnitude;
        }
        if (_smoothPanKeys.Contains(Key.Left))
        {
            accelerationX -= accelerationMagnitude;
        }

        double accelerationY = 0;
        if (_smoothPanKeys.Contains(Key.Down))
        {
            accelerationY += accelerationMagnitude;
        }
        if (_smoothPanKeys.Contains(Key.Up))
        {
            accelerationY -= accelerationMagnitude;
        }

        var velocity = _smoothPanVelocity;

        if (Math.Abs(accelerationX) < double.Epsilon)
        {
            velocity.X = MoveTowards(velocity.X, 0, decelerationMagnitude * deltaSeconds);
        }
        else
        {
            velocity.X += accelerationX * deltaSeconds;
            velocity.X = Math.Clamp(velocity.X, -maxSpeed, maxSpeed);
        }

        if (Math.Abs(accelerationY) < double.Epsilon)
        {
            velocity.Y = MoveTowards(velocity.Y, 0, decelerationMagnitude * deltaSeconds);
        }
        else
        {
            velocity.Y += accelerationY * deltaSeconds;
            velocity.Y = Math.Clamp(velocity.Y, -maxSpeed, maxSpeed);
        }

        if (Math.Abs(velocity.X) < PanSmoothEpsilon)
        {
            velocity.X = 0;
        }

        if (Math.Abs(velocity.Y) < PanSmoothEpsilon)
        {
            velocity.Y = 0;
        }

        _smoothPanVelocity = velocity;

        if (_smoothPanKeys.Count == 0 && velocity.X == 0 && velocity.Y == 0)
        {
            StopSmoothPanAnimation(resetVelocity: true);
            return;
        }

        var deltaX = velocity.X * deltaSeconds;
        var deltaY = velocity.Y * deltaSeconds;

        if (Math.Abs(deltaX) > double.Epsilon || Math.Abs(deltaY) > double.Epsilon)
        {
            Pan(deltaX, deltaY);
        }
    }

    private void StopSmoothPanAnimation(bool resetVelocity = false)
    {
        if (_isSmoothPanAnimating)
        {
            CompositionTarget.Rendering -= OnSmoothPanRendering;
            _isSmoothPanAnimating = false;
        }

        _smoothPanLastTick = TimeSpan.Zero;

        if (resetVelocity)
        {
            _smoothPanVelocity = new Vector();
            _smoothPanKeys.Clear();
        }
    }

    private static bool IsPanKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down;

    private static Vector GetPanDirection(Key key) => key switch
    {
        Key.Left => new Vector(-1, 0),
        Key.Right => new Vector(1, 0),
        Key.Up => new Vector(0, -1),
        Key.Down => new Vector(0, 1),
        _ => new Vector()
    };

    private static double BlendInitialVelocity(double current, double target)
    {
        if (Math.Abs(current) < PanSmoothEpsilon)
        {
            return target;
        }

        if (Math.Sign(current) != Math.Sign(target))
        {
            return target;
        }

        var magnitude = Math.Max(Math.Abs(current), Math.Abs(target));
        return Math.Sign(target) * magnitude;
    }

    private static double MoveTowards(double current, double target, double maxDelta)
    {
        double delta = target - current;
        if (Math.Abs(delta) <= maxDelta)
        {
            return target;
        }

        return current + Math.Sign(delta) * maxDelta;
    }

    private void SetPan(double panX, double panY)
    {
        ApplyPan(panX, panY);
    }

    private void ClampPan()
    {
        ApplyPan(_panOffset.X, _panOffset.Y);
    }

    private void ApplyPan(double panX, double panY)
    {
        if (!IsZoomed())
        {
            panX = 0;
            panY = 0;
        }

        if (TryGetPanLimits(out var maxX, out var maxY))
        {
            var requestedX = panX;
            var requestedY = panY;
            panX = Math.Clamp(panX, -maxX, maxX);
            panY = Math.Clamp(panY, -maxY, maxY);

            if (panX != requestedX || panY != requestedY)
            {
                if (TryGetContentSize(out var contentW, out var contentH) &&
                    TryGetViewportSize(out var viewportW, out var viewportH))
                {
                    Logger.Log($"PAN clamp: requested=({requestedX:F2},{requestedY:F2}) -> clamped=({panX:F2},{panY:F2}) max=({maxX:F2},{maxY:F2}) content=({contentW:F2},{contentH:F2}) viewport=({viewportW:F2},{viewportH:F2}) zoomScale={_zoomScale:F4} fitScale={_fitScale:F4}");
                }
                else
                {
                    Logger.Log($"PAN clamp: requested=({requestedX:F2},{requestedY:F2}) -> clamped=({panX:F2},{panY:F2}) max=({maxX:F2},{maxY:F2}) zoomScale={_zoomScale:F4} fitScale={_fitScale:F4}");
                }
            }
        }
        else
        {
            panX = 0;
            panY = 0;
        }

        _panOffset = new Vector(panX, panY);

        // Apply pan by scrolling (centered pan coordinates).
        // centeredPan = scrollOffset - scrollable/2
        var scrollableW = ImageScrollViewer.ScrollableWidth;
        var scrollableH = ImageScrollViewer.ScrollableHeight;

        if (double.IsNaN(scrollableW) || scrollableW < 0)
        {
            scrollableW = 0;
        }

        if (double.IsNaN(scrollableH) || scrollableH < 0)
        {
            scrollableH = 0;
        }

        var targetHorizontal = Math.Clamp(panX + scrollableW * 0.5, 0, scrollableW);
        var targetVertical = Math.Clamp(panY + scrollableH * 0.5, 0, scrollableH);

        ImageScrollViewer.ScrollToHorizontalOffset(targetHorizontal);
        ImageScrollViewer.ScrollToVerticalOffset(targetVertical);
    }

    private bool TryGetPanLimits(out double maxX, out double maxY)
    {
        maxX = 0;
        maxY = 0;

        if (_currentBitmap == null)
        {
            return false;
        }

        // Use ScrollViewer's computed scrollable extents.
        // Convert to centered pan limits (half of scrollable range).
        var scrollableW = ImageScrollViewer.ScrollableWidth;
        var scrollableH = ImageScrollViewer.ScrollableHeight;

        if (double.IsNaN(scrollableW) || scrollableW < 0)
        {
            scrollableW = 0;
        }

        if (double.IsNaN(scrollableH) || scrollableH < 0)
        {
            scrollableH = 0;
        }

        maxX = scrollableW * 0.5;
        maxY = scrollableH * 0.5;
        return true;
    }

    private bool TryGetViewportSize(out double width, out double height)
    {
        var vw = ImageScrollViewer.ViewportWidth;
        var vh = ImageScrollViewer.ViewportHeight;
        var aw = ImageScrollViewer.ActualWidth;
        var ah = ImageScrollViewer.ActualHeight;

        // In some layout states Viewport* may be 0 briefly; use the larger of viewport/actual.
        width = Math.Max(vw, aw);
        height = Math.Max(vh, ah);

        if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
        {
            width = 0;
            height = 0;
            return false;
        }

        return true;
    }

    private bool TryGetContentSize(out double width, out double height)
    {
        // Prefer ScrollViewer extent (post-layout), fall back to computed value.
        width = ImageScrollViewer.ExtentWidth;
        height = ImageScrollViewer.ExtentHeight;

        if (!double.IsNaN(width) && width > 0 && !double.IsNaN(height) && height > 0)
        {
            return true;
        }

        width = 0;
        height = 0;

        if (_currentBitmap == null)
        {
            return false;
        }

        double bitmapWidthDip = PixelsToDip(_currentBitmap.PixelWidth, _currentBitmap.DpiX);
        double bitmapHeightDip = PixelsToDip(_currentBitmap.PixelHeight, _currentBitmap.DpiY);

        var scale = _zoomScale;
        if (double.IsNaN(scale) || scale <= 0)
        {
            scale = _fitScale > 0 ? _fitScale : 1.0;
        }

        width = bitmapWidthDip * scale;
        height = bitmapHeightDip * scale;
        return true;
    }

    private void Pan(double deltaX, double deltaY)
    {
        if (!IsZoomed())
        {
            return;
        }

        SetPan(_panOffset.X + deltaX, _panOffset.Y + deltaY);
    }

    private void PanTo(double horizontal, double vertical)
    {
        SetPan(horizontal, vertical);
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsZoomed() || e.ClickCount > 1)
        {
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(ImageScrollViewer);
        _panOrigin = new Point(_panOffset.X, _panOffset.Y);
        ImageScrollViewer.CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var current = e.GetPosition(ImageScrollViewer);
        var deltaX = _panStart.X - current.X;
        var deltaY = _panStart.Y - current.Y;
        PanTo(_panOrigin.X + deltaX, _panOrigin.Y + deltaY);
        e.Handled = true;
    }

    private void OnImageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopPanning();
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        ImageScrollViewer.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private void OnImageScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_currentBitmap != null)
        {
            UpdateFitScale(_currentBitmap, forceReset: !IsZoomed());
        }
    }

    private void OnCheckNsfwClick(object sender, RoutedEventArgs e)
    {
        if (!_sequence.HasImages || App.NsfwService == null || !App.NsfwService.IsAvailable)
        {
            ShowStatus("NSFW detection not available.");
            return;
        }

        var currentPath = _sequence.CurrentPath;
        if (string.IsNullOrEmpty(currentPath))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = App.NsfwService.CheckImage(currentPath);
            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            Dispatcher.Invoke(() => DisplayNsfwResult(result, elapsedMs));
        });
    }

    private void OnRunObjectDetectionClick(object sender, RoutedEventArgs e)
    {
        if (!_sequence.HasImages || App.ObjectService == null || !App.ObjectService.IsAvailable)
        {
            ShowStatus("Object detection not available.");
            return;
        }

        var currentPath = _sequence.CurrentPath;
        if (string.IsNullOrEmpty(currentPath))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = App.ObjectService.DetectObjects(currentPath, topK: 25);
            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            Dispatcher.Invoke(() => DisplayObjectResult(result, elapsedMs));
        });
    }

    private void OnSearchObjectClick(object sender, RoutedEventArgs e)
    {
        var searchTerm = ObjectSearchText.Text?.Trim();
        if (string.IsNullOrEmpty(searchTerm))
        {
            ShowStatus("Please enter a search term.");
            return;
        }

        if (!_sequence.HasImages || App.ObjectService == null || !App.ObjectService.IsAvailable)
        {
            ShowStatus("Object detection not available.");
            return;
        }

        var currentPath = _sequence.CurrentPath;
        if (string.IsNullOrEmpty(currentPath))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = App.ObjectService.DetectObjects(currentPath, topK: 50);
            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var matchingObjects = result?.Predictions
                .Where(p => p.ClassName.ToLowerInvariant().Contains(searchTerm.ToLowerInvariant()))
                .ToList();

            Dispatcher.Invoke(() =>
            {
                if (matchingObjects != null && matchingObjects.Any())
                {
                    DisplayObjectResult(new ObjectDetectionResult { Predictions = matchingObjects }, elapsedMs);
                    ShowStatus($"Found {matchingObjects.Count} match(es) for '{searchTerm}' in {elapsedMs}ms.");
                }
                else
                {
                    DetectionResultsPanel.Visibility = Visibility.Visible;
                    ObjectResultsPanel.Visibility = Visibility.Visible;
                    ObjectTagsList.ItemsSource = null;
                    ShowStatus($"No matches found for '{searchTerm}' ({elapsedMs}ms).");
                }
            });
        });
    }

    private void ClearDetectionResults()
    {
        DetectionResultsPanel.Visibility = Visibility.Collapsed;
        NsfwResultsPanel.Visibility = Visibility.Collapsed;
        ObjectResultsPanel.Visibility = Visibility.Collapsed;
        ObjectTagsList.ItemsSource = null;
    }
    
    private void RunDetectionsForCurrentImage()
    {
        if (!_sequence.HasImages)
        {
            return;
        }
        
        var currentPath = _sequence.CurrentPath;
        if (string.IsNullOrEmpty(currentPath))
        {
            return;
        }
        
        // Run NSFW detection if available
        if (App.NsfwService?.IsAvailable == true)
        {
            _ = Task.Run(() =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = App.NsfwService.CheckImage(currentPath);
                stopwatch.Stop();
                var elapsedMs = stopwatch.ElapsedMilliseconds;
                Dispatcher.Invoke(() => DisplayNsfwResult(result, elapsedMs));
            });
        }
        
        // Run object detection if available
        if (App.ObjectService?.IsAvailable == true)
        {
            _ = Task.Run(() =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = App.ObjectService.DetectObjects(currentPath, topK: 10);
                stopwatch.Stop();
                var elapsedMs = stopwatch.ElapsedMilliseconds;
                Dispatcher.Invoke(() => DisplayObjectResult(result, elapsedMs));
            });
        }
    }

    private void DisplayNsfwResult(NsfwDetectionResult? result, long elapsedMs = 0)
    {
        if (result == null)
        {
            ShowStatus("No NSFW detection result.");
            return;
        }

        DetectionResultsPanel.Visibility = Visibility.Visible;
        NsfwResultsPanel.Visibility = Visibility.Visible;

        if (result.IsNsfw)
        {
            NsfwResultText.Text = "NSFW";
            NsfwResultBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 68, 68)); // Red
        }
        else
        {
            NsfwResultText.Text = "SAFE";
            NsfwResultBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 255, 68)); // Green
        }

        var timingText = elapsedMs > 0 ? $" ({elapsedMs}ms)" : "";
        NsfwConfidenceText.Text = $"Confidence: {result.Confidence * 100:F1}%{timingText}";
        
        ShowStatus($"NSFW check complete: {(result.IsNsfw ? "NSFW" : "SAFE")} ({result.Confidence * 100:F1}% confidence) - {elapsedMs}ms");
    }

    private void DisplayObjectResult(ObjectDetectionResult? result, long elapsedMs = 0)
    {
        if (result == null || result.Predictions.Count == 0)
        {
            var timingText = elapsedMs > 0 ? $" ({elapsedMs}ms)" : "";
            ShowStatus($"No objects detected{timingText}.");
            return;
        }

        // Filter predictions by minimum confidence threshold (15% seems reasonable for real detections)
        const float MinConfidenceThreshold = 0.15f;
        var filteredPredictions = result.Predictions
            .Where(p => p.Confidence >= MinConfidenceThreshold)
            .ToList();

        if (filteredPredictions.Count == 0)
        {
            var timingText = elapsedMs > 0 ? $" ({elapsedMs}ms)" : "";
            ShowStatus($"No objects detected with sufficient confidence{timingText}.");
            DetectionResultsPanel.Visibility = Visibility.Visible;
            ObjectResultsPanel.Visibility = Visibility.Visible;
            ObjectTagsList.ItemsSource = null;
            return;
        }

        DetectionResultsPanel.Visibility = Visibility.Visible;
        ObjectResultsPanel.Visibility = Visibility.Visible;

        var tags = filteredPredictions.Select((p, index) => new ObjectTag
        {
            Rank = index + 1,
            Name = p.ClassName,
            Confidence = p.Confidence,
            ConfidencePercent = $"{p.Confidence * 100:F1}%",
            RawConfidence = $"{p.Confidence:F4}"
        }).ToList();

        ObjectTagsList.ItemsSource = tags;
        
        var topPrediction = filteredPredictions.FirstOrDefault();
        var timingSuffix = elapsedMs > 0 ? $" - {elapsedMs}ms" : "";
        if (topPrediction != null)
        {
            ShowStatus($"Top: {topPrediction.ClassName} ({topPrediction.Confidence * 100:F1}%) - {filteredPredictions.Count} object(s) detected{timingSuffix}");
        }
        else
        {
            ShowStatus($"Detected {filteredPredictions.Count} object(s){timingSuffix}.");
        }
    }

    private sealed class ObjectTag
    {
        public int Rank { get; set; }
        public string Name { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public string ConfidencePercent { get; set; } = string.Empty;
        public string RawConfidence { get; set; } = string.Empty;
    }

    private readonly struct ArchiveStep
    {
        internal string OriginalPath { get; }
        internal string ArchivedPath { get; }

        internal ArchiveStep(string originalPath, string archivedPath)
        {
            OriginalPath = originalPath;
            ArchivedPath = archivedPath;
        }
    }

    // ===== Self-test helpers (no UI interaction) =====
    // These are intentionally internal and side-effectful to enable automated verification
    // of zoom/pan behavior without manual testing.

    internal void DebugLoadBitmapForSelfTest(BitmapSource bitmap)
    {
        _currentBitmap = bitmap;
        ImageDisplay.Source = bitmap;
        HideMessage();
        UpdateFitScale(bitmap, forceReset: true);
        // Ensure transforms are applied.
        UpdateLayout();
    }

    internal double DebugGetFitScale() => _fitScale;

    internal Rect DebugGetViewportRect()
    {
        _ = TryGetViewportSize(out var w, out var h);
        return new Rect(0, 0, w, h);
    }

    internal Vector DebugGetPanOffset() => _panOffset;

    internal Rect DebugGetImageBoundsInViewport()
    {
        // Bounds of the Image element in the coordinate space of the viewport container.
        var rect = new Rect(0, 0, ImageDisplay.ActualWidth, ImageDisplay.ActualHeight);
        try
        {
            var t = ImageDisplay.TransformToAncestor(ImageScrollViewer);
            return t.TransformBounds(rect);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    internal void DebugSetZoomForSelfTest(double scale)
    {
        SetZoom(scale, anchor: null);
        UpdateLayout();
    }

    internal void DebugSetPanForSelfTest(double panX, double panY)
    {
        ApplyPan(panX, panY);
        UpdateLayout();
    }
}