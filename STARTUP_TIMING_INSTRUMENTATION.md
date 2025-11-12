# Startup Timing Instrumentation

## Overview

I've added comprehensive timing measurements to **every step** of the CoilViewer startup process, from when you click to open the program until the first image is displayed on screen.

## How to View Timing Data

All timing information is logged to `coilviewer-launch.log` in the project root directory. Each timing entry is tagged with a category prefix:

- `[STARTUP]` - Application initialization (App.xaml.cs)
- `[MAINWINDOW]` - MainWindow constructor and initialization
- `[LOADSEQUENCE]` - Image sequence loading
- `[IMAGESEQUENCE]` - Directory enumeration and sorting
- `[DISPLAYCURRENT]` - Image display process
- `[IMAGECACHE]` - Image file loading and decoding

## What Gets Measured

### 1. Application Startup (`App.OnStartup`)
- `base.OnStartup` - WPF framework initialization
- `Logger.LogLaunch` - Logging initialization
- `Config loading` - Reading and parsing config.json
- `InitializeModelsAsync` - Starting async ML model loading (non-blocking)
- `Path resolution` - Resolving command-line arguments
- `DirectoryInstanceGuard setup` - Multi-instance coordination
- `MainWindow constructor` - Creating main window
- `window.Show()` - Displaying the window
- `Window activation and focus` - Bringing window to front
- **TOTAL APP STARTUP TIME** - Overall startup time

### 2. MainWindow Initialization
- `InitializeComponent` - XAML parsing and control creation
- `Field initialization` - Setting up internal fields
- `ApplyConfig` - Applying user configuration
- `UpdateContextMenu` - Building context menu
- `InitializeFilterControls` - Setting up filter UI
- `LoadSequence` - Loading image directory (if initial path provided)
- **TOTAL MAINWINDOW CONSTRUCTOR TIME** - Overall MainWindow creation time

### 3. LoadSequence
- `ResolveDirectory` - Determining target directory
- `EnsureDirectoryGuard` - Instance locking
- `Parse sort options and resolve focus` - Sort configuration
- `_sequence.LoadFromPath` - Enumerating files
- `Create ImageCache` - Initializing cache system
- `UpdateWindowTitle` - Setting window title
- `UpdateContextMenu` - Updating menu state
- `DisplayCurrentAsync` - Starting image display (fire and forget)
- **TOTAL LOADSEQUENCE TIME** - Overall sequence loading time

### 4. ImageSequence.LoadFromPath
- `Get directory info` - Directory metadata
- `EnumerateFiles and filter` - Finding image files
- `Apply sort ordering` - Sorting files
- `Materialize file list` - Converting LINQ to list
- `Copy to _allImages` - Backup for filtering
- `Find initial index` - Locating starting image
- **TOTAL LOADFROMPATH TIME** - Overall enumeration time

### 5. DisplayCurrentAsync
This measures two different paths:

#### Cached Path (instant):
- `TryGetCached (HIT)` - Found in cache
- `Set ImageDisplay.Source (cached)` - Setting UI
- `UpdateFitScale` - Calculating zoom
- `UpdateOverlay` - Updating overlay info
- `UpdateContextMenu` - Refreshing menu
- `UpdateWindowTitle` - Updating title
- `PreloadAround` - Starting preload (fire and forget)
- **TOTAL DISPLAYCURRENT TIME (CACHED)** - Cached display time

#### Load Path (file I/O):
- `TryGetCached (MISS)` - Not in cache
- `ShowMessage` - Showing "Loading..."
- `_cache.GetOrLoadAsync (actual load)` - Loading from disk
- `Set ImageDisplay.Source (loaded)` - Setting UI
- `UpdateFitScale` - Calculating zoom
- `UpdateOverlay` - Updating overlay info
- `UpdateContextMenu` - Refreshing menu
- `UpdateWindowTitle` - Updating title
- `PreloadAround` - Starting preload (fire and forget)
- **TOTAL DISPLAYCURRENT TIME (LOADED)** - Load display time

### 6. ImageCache.LoadBitmap (disk I/O)
- `Get path from sequence` - Path lookup
- `Open FileStream` - Opening file handle
- `BitmapDecoder.Create` - Creating decoder
- `Get decoder.Frames[0]` - Accessing first frame
- `frame.Freeze()` - Making thread-safe
- **TOTAL LOADBITMAP TIME** - Overall file load time

## Viewing the Results

After running CoilViewer, open `coilviewer-launch.log` and look for the timing entries. Example output:

```
[STARTUP] base.OnStartup: 2ms
[STARTUP] Logger.LogLaunch: 1ms
[STARTUP] Config loading: 3ms
[STARTUP] InitializeModelsAsync (fire and forget): 0ms
[STARTUP] Path resolution: 5ms
[STARTUP] DirectoryInstanceGuard setup: 1ms
[MAINWINDOW] InitializeComponent: 45ms
[MAINWINDOW] Field initialization: 0ms
[MAINWINDOW] ApplyConfig: 1ms
[MAINWINDOW] UpdateContextMenu: 0ms
[MAINWINDOW] InitializeFilterControls: 0ms
[IMAGESEQUENCE] Get directory info: 0ms
[IMAGESEQUENCE] EnumerateFiles and filter: 2ms
[IMAGESEQUENCE] Apply sort ordering: 0ms
[IMAGESEQUENCE] Materialize file list (150 files): 8ms
[IMAGESEQUENCE] Copy to _allImages: 0ms
[IMAGESEQUENCE] Find initial index: 0ms
[IMAGESEQUENCE] ========== TOTAL LOADFROMPATH TIME: 10ms ==========
[LOADSEQUENCE] _sequence.LoadFromPath: 10ms
[LOADSEQUENCE] Create ImageCache: 0ms
[DISPLAYCURRENT] TryGetCached (MISS): 0ms
[IMAGECACHE] Get path from sequence: 0ms
[IMAGECACHE] Open FileStream for 'image001.jpg': 1ms
[IMAGECACHE] BitmapDecoder.Create: 8ms
[IMAGECACHE] Get decoder.Frames[0]: 0ms
[IMAGECACHE] frame.Freeze(): 0ms
[IMAGECACHE] ========== TOTAL LOADBITMAP TIME for 'image001.jpg': 9ms ==========
[DISPLAYCURRENT] _cache.GetOrLoadAsync (actual load): 10ms
[DISPLAYCURRENT] Set ImageDisplay.Source (loaded): 1ms
[DISPLAYCURRENT] UpdateFitScale: 0ms
[DISPLAYCURRENT] UpdateOverlay: 1ms
[DISPLAYCURRENT] ========== TOTAL DISPLAYCURRENT TIME (LOADED): 15ms ==========
[MAINWINDOW] LoadSequence: 25ms
[MAINWINDOW] ========== TOTAL MAINWINDOW CONSTRUCTOR TIME: 71ms ==========
[STARTUP] MainWindow constructor: 71ms
[STARTUP] window.Show(): 5ms
[STARTUP] Window activation and focus: 1ms
[STARTUP] ========== TOTAL APP STARTUP TIME: 89ms ==========
```

## Understanding the Results

The timing breakdown helps you understand:

1. **Startup overhead** - How long WPF initialization takes
2. **File enumeration** - Time spent listing and sorting files
3. **Image loading** - Actual disk I/O and decoding time
4. **UI updates** - Time spent updating the interface
5. **Bottlenecks** - Which operations take the most time

### Common Bottlenecks

- **InitializeComponent** (30-60ms) - XAML parsing, largest fixed cost
- **Materialize file list** - Scales with number of files in directory
- **BitmapDecoder.Create** - Varies by image size/format
- **UpdateFitScale** - Can be slow for very large images

### What Was Fixed

Previously, ML model initialization was **blocking** the startup (1000-3000ms). This is now asynchronous and shows as `0ms` in the log because it doesn't block the UI thread anymore.

## Interpreting "fire and forget"

When you see `(fire and forget): 0ms`, this means the operation was started asynchronously and doesn't block - the 0ms only measures the time to **start** the operation, not to complete it.

## Next Steps

If startup is still slow, examine the log to find the slowest operations:
- Look for operations taking > 50ms
- Check if file enumeration is slow (many files?)
- Verify image decoding time (large images?)
- Consider SSD vs HDD (file I/O)


