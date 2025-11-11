using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
using System.Windows.Threading;
using System.Drawing.Imaging;
using Microsoft.Win32;

using Point = System.Windows.Point;
using Vector = System.Windows.Vector;
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
    private DirectoryInstanceGuard? _directoryGuard;
    private bool _isFullscreen;
    private bool _overlayVisible;
    private bool _shortcutsVisible;
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
    private static readonly TimeSpan StatusDisplayDuration = TimeSpan.FromSeconds(2.5);
    private readonly record struct ArchiveAction(string OriginalPath, string ArchivedPath);

    internal MainWindow(ViewerConfig config, string configPath, string? initialPath, DirectoryInstanceGuard? initialGuard)
    {
        InitializeComponent();

        _config = config;
        _configPath = configPath;
        _directoryGuard = initialGuard;
        _directoryGuard?.AttachWindow(this);
        _directoryGuard?.SetRequestHandler(OnExternalOpenRequest);
        _statusTimer = new DispatcherTimer { Interval = StatusDisplayDuration };
        _statusTimer.Tick += OnStatusTimerTick;

        ApplyConfig();
        UpdateContextMenu();

        Loaded += (_, _) => Focus();

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            LoadSequence(initialPath);
        }
        else
        {
            ShowMessage("Press Ctrl+O to open an image.");
        }
    }

    private void ApplyConfig()
    {
        if (ColorConverter.ConvertFromString(_config.BackgroundColor) is Color color)
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
        if (_cache == null || !_sequence.HasImages)
        {
            return;
        }

        SetShortcutsVisibility(false);

        var index = _sequence.CurrentIndex;
        var path = _sequence.CurrentPath;

        if (_cache.TryGetCached(index, out var cached) && cached != null)
        {
            _currentBitmap = cached;
            ImageDisplay.Source = cached;
            HideMessage();
            UpdateFitScale(cached, forceReset: true);
            UpdateOverlay(cached, path, index);
            UpdateContextMenu();
            UpdateWindowTitle();
            _cache.PreloadAround(index);
            return;
        }

        ShowMessage("Loading...");

        try
        {
            var bitmap = await _cache.GetOrLoadAsync(index);
            _currentBitmap = bitmap;
            ImageDisplay.Source = bitmap;
            HideMessage();
            UpdateFitScale(bitmap, forceReset: true);
            UpdateOverlay(bitmap, path, index);
            UpdateContextMenu();
            UpdateWindowTitle();
            _cache.PreloadAround(index);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to load index={index}, path='{path}'", ex);
            ShowMessage($"Failed to load image: {ex.Message}");
        }
    }

    private void UpdateOverlay(BitmapSource bitmap, string path, int index)
    {
        string fileName = Path.GetFileName(path);
        OverlayTitle.Text = fileName;

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
        OverlayDetails.Text = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {FormatFileSize(fileSize)} • {position}/{_sequence.Count}";
        UpdateWindowTitle();

        if (!string.IsNullOrEmpty(sortValue))
        {
            OverlaySort.Visibility = Visibility.Visible;
            OverlaySort.Text = $"{GetFieldLabel(_sequence.SortField)}: {sortValue}";
        }
        else
        {
            OverlaySort.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateWindowTitle()
    {
        string title = "Coil Viewer";
        var directory = _sequence.DirectoryPath;
        if (!string.IsNullOrEmpty(directory))
        {
            title = $"Coil Viewer — {directory}";
        }

        if (_sequence.HasImages)
        {
            title += $" ({_sequence.CurrentIndex + 1}/{_sequence.Count})";
        }

        Title = title;
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

    private void ShowMessage(string text)
    {
        MessageBlock.Text = text;
        MessageBlock.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageBlock.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusBar.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        _statusTimer.Stop();
        StatusBar.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
    }

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

        var index = _sequence.MoveNext(_config.LoopAround);
        if (index >= 0)
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

        var index = _sequence.MovePrevious(_config.LoopAround);
        if (index >= 0)
        {
            _ = DisplayCurrentAsync();
        }
    }

    private async Task MoveCurrentImageToOldAsync()
    {
        if (!_sequence.HasImages)
        {
            ShowMessage("No image to move.");
            return;
        }

        var currentPath = _sequence.CurrentPath;

        try
        {
            var fileInfo = new FileInfo(currentPath);
            var directory = fileInfo.Directory ?? throw new InvalidOperationException("Unable to determine image directory.");
            var targetDirectory = Path.Combine(directory.FullName, "old");
            Directory.CreateDirectory(targetDirectory);

            var targetPath = Path.Combine(targetDirectory, fileInfo.Name);
            if (File.Exists(targetPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(fileInfo.Name);
                var extension = fileInfo.Extension;
                var counter = 1;
                do
                {
                    targetPath = Path.Combine(targetDirectory, $"{baseName}_{counter}{extension}");
                    counter++;
                } while (File.Exists(targetPath));
            }

            File.Move(currentPath, targetPath);
            _archiveHistory.Push(new ArchiveStep(currentPath, targetPath));
            Logger.Log($"Moved image '{currentPath}' to '{targetPath}'");

            if (_sequence.RemoveCurrent())
            {
                _cache = new ImageCache(_sequence, _config);
                ResetZoom();
                UpdateContextMenu();
                await DisplayCurrentAsync();
                HideMessage();
                ShowStatus($"Moved to '{targetPath}'.");
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
                ImageFitTransform.ScaleX = _fitScale;
                ImageFitTransform.ScaleY = _fitScale;
                SetZoom(_fitScale);
                ShowStatus($"Moved to '{targetPath}'.");
                ShowMessage("No images remain.");
                UpdateWindowTitle();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to move image '{currentPath}'", ex);
            ShowMessage($"Failed to move image: {ex.Message}");
        }
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
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.bmp;*.dib;*.gif;*.tiff;*.tif;*.webp|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadSequence(dialog.FileName);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _shortcutsVisible)
        {
            ToggleShortcuts(forceHide: true);
            e.Handled = true;
            return;
        }

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
                if (IsZoomed())
                {
                    Logger.Log($"Arrow key pan requested: key={e.Key}, zoomScale={_zoomScale:0.###}, fitScale={_fitScale:0.###}, pan=({_panOffset.X:0.##},{_panOffset.Y:0.##})");
                    BeginSmoothPan(e.Key);
                    e.Handled = true;
                }
                else
                {
                    if (e.Key == Key.Right || e.Key == Key.Down)
                    {
                        Logger.Log($"Arrow key next image: key={e.Key}");
                        MoveNext();
                    }
                    else
                    {
                        Logger.Log($"Arrow key previous image: key={e.Key}");
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
            case Key.I:
                ToggleOverlay();
                e.Handled = true;
                break;
            case Key.Oem2:
            case Key.Divide:
                ToggleShortcuts();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullscreen)
                {
                    ToggleFullscreen();
                    e.Handled = true;
                }
                else
                {
                    Close();
                }

                break;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
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
        if (e.Delta > 0)
        {
            MovePrevious();
            e.Handled = true;
            return;
        }

        if (e.Delta < 0)
        {
            MoveNext();
            e.Handled = true;
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
        var resolvedDirectory = DirectoryInstanceGuard.ResolveDirectory(path);
        var requestTarget = preferredImage ?? path;

        try
        {
            if (resolvedDirectory != null && !EnsureDirectoryGuard(resolvedDirectory, requestTarget))
            {
                ShowMessage($"Directory is already open in another Coil Viewer window: {resolvedDirectory}");
                return;
            }

            var field = SortOptions.ParseField(_config.SortField);
            var direction = SortOptions.ParseDirection(_config.SortDirection);

            string? focus = preferredImage;
            if (focus == null && File.Exists(path))
            {
                focus = Path.GetFullPath(path);
            }

            Logger.Log($"LoadSequence path='{path}', focus='{focus}', field={field}, direction={direction}");

            _sequence.LoadFromPath(path, field, direction, focus);
            _cache = new ImageCache(_sequence, _config);
            UpdateWindowTitle();
            UpdateContextMenu();
            _ = DisplayCurrentAsync();
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
        _directoryGuard?.SetRequestHandler(null);
        _directoryGuard?.Dispose();
        _directoryGuard = null;
    _statusTimer.Tick -= OnStatusTimerTick;
    _statusTimer.Stop();
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
        if (_shortcutsVisible == visible)
        {
            if (visible && ShortcutsOverlay.Visibility == Visibility.Visible)
            {
                return;
            }

            if (!visible && ShortcutsOverlay.Visibility != Visibility.Visible)
            {
                return;
            }
        }

        _shortcutsVisible = visible;
        ShortcutsOverlay.BeginAnimation(UIElement.OpacityProperty, null);

        if (visible)
        {
            ShortcutsOverlay.Visibility = Visibility.Visible;

            if (animate)
            {
                var fadeIn = new DoubleAnimation(ShortcutsOverlay.Opacity, 1.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ShortcutsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
            else
            {
                ShortcutsOverlay.Opacity = 1.0;
            }

            return;
        }

        if (!animate || ShortcutsOverlay.Visibility != Visibility.Visible)
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            ShortcutsOverlay.Opacity = 0.0;
            return;
        }

        var fadeOut = new DoubleAnimation(ShortcutsOverlay.Opacity, 0.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
        };
        ShortcutsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void ToggleShortcuts(bool forceHide = false)
    {
        var targetVisibility = forceHide ? false : !_shortcutsVisible;
        SetShortcutsVisibility(targetVisibility);
    }

    private bool IsZoomed() => _zoomScale > _fitScale + 0.001;

    private void ZoomIn()
    {
        SetZoom(_zoomScale * ZoomStep);
    }

    private void ZoomOut()
    {
        SetZoom(_zoomScale / ZoomStep);
    }

    private void ResetZoom()
    {
        StopPanning();
        StopSmoothPanAnimation(resetVelocity: true);
        SetZoom(_fitScale);
    }

    private void SetZoom(double scale)
    {
        var fit = Math.Max(_fitScale, 0.01);
        var clamped = Math.Clamp(scale, fit, MaxZoom);
        _zoomScale = clamped;

        var relative = clamped / fit;
        ImageZoomTransform.ScaleX = relative;
        ImageZoomTransform.ScaleY = relative;

        ClampPan();

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

        double viewportWidth = ImageScrollViewer.ViewportWidth;
        double viewportHeight = ImageScrollViewer.ViewportHeight;

        if (viewportWidth <= 0 || viewportHeight <= 0 || double.IsNaN(viewportWidth) || double.IsNaN(viewportHeight))
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

        double scaleX = viewportWidth / imageWidth;
        double scaleY = viewportHeight / imageHeight;
        double computed = Math.Min(1.0, Math.Min(scaleX, scaleY));

        if (double.IsNaN(computed) || double.IsInfinity(computed) || computed <= 0)
        {
            computed = MinZoom;
        }

        bool wasZoomed = _zoomScale > _fitScale + 0.001;
        _fitScale = Math.Max(computed, 0.01);
        ImageFitTransform.ScaleX = _fitScale;
        ImageFitTransform.ScaleY = _fitScale;

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
        var scale = ImageZoomTransform.ScaleX;
        if (double.IsNaN(scale) || scale <= 0)
        {
            return 1.0;
        }

        return Math.Max(1.0, scale);
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
            panX = Math.Clamp(panX, -maxX, maxX);
            panY = Math.Clamp(panY, -maxY, maxY);
        }
        else
        {
            panX = 0;
            panY = 0;
        }

        _panOffset = new Vector(panX, panY);
        ImagePanTransform.X = -panX;
        ImagePanTransform.Y = -panY;
    }

    private bool TryGetPanLimits(out double maxX, out double maxY)
    {
        maxX = 0;
        maxY = 0;

        if (_currentBitmap == null)
        {
            return false;
        }

        double viewportWidth = ImageScrollViewer.ViewportWidth;
        double viewportHeight = ImageScrollViewer.ViewportHeight;

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        double baseWidth = PixelsToDip(_currentBitmap.PixelWidth, _currentBitmap.DpiX) * _fitScale;
        double baseHeight = PixelsToDip(_currentBitmap.PixelHeight, _currentBitmap.DpiY) * _fitScale;
        double zoomFactor = ImageZoomTransform.ScaleX;

        double contentWidth = baseWidth * zoomFactor;
        double contentHeight = baseHeight * zoomFactor;

        maxX = Math.Max(0, (contentWidth - viewportWidth) / 2);
        maxY = Math.Max(0, (contentHeight - viewportHeight) / 2);
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
}