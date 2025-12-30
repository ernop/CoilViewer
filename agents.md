# CoilViewer

High-speed fullscreen image browser focused on instant navigation through large folders on Windows with sane usability.

- [Product Requirements](product.md) - Feature list and requirements tracker

## Supported Formats

- PNG (`.png`)
- JPEG (`.jpg`, `.jpeg`, `.jpe`, `.jfif`)
- WebP (`.webp`, requires Windows WebP codec - bundled with modern Windows 10/11)
- GIF (`.gif`, displayed as the first frame)
- BMP / DIB (`.bmp`, `.dib`)
- TIFF (`.tiff`, `.tif`)

The viewer relies on Windows Imaging Component. Any additional codecs registered with WIC (e.g. HEIC/HEIF) will also work if you add their extensions to `config.json`.

## Build

- Requires .NET SDK 8.0 or later.
- Restore and build: `dotnet build CoilViewer/CoilViewer.csproj`
- Optional portable release: `dotnet publish CoilViewer/CoilViewer.csproj -c Release -r win-x64 --self-contained false`
- The published folder contains `CoilViewer.exe`. Copy the entire directory to keep the app portable.

## Usage

- Launch `CoilViewer.exe` directly, double-click an image file, or pass a file/folder path: `CoilViewer.exe "C:\path\image.png"`
- Keyboard:
  - `Right`, `Down`, `Space`: next image
  - `Left`, `Up`, `Backspace`: previous image
  - `Home`/`End`: jump to first/last image
  - `Ctrl+Shift+Arrow`: jump halfway toward start/end of sequence
  - `A`: archive current image to "old" subfolder (moves file and shows next image instantly)
  - `Ctrl+O`: open image to load its folder
  - `Ctrl+R`: reload `config.json`
  - `I`: toggle metadata overlay
  - `/` or `?`: show keyboard shortcuts
  - `=`/`-`: zoom in/out
  - `\`: reset zoom
  - `F11` or double-click: toggle fullscreen
  - `Esc`: exit fullscreen or close the app
- Mouse:
  - Scroll wheel to navigate
  - Double-click toggles fullscreen
  - Drag window when not fullscreen
- Drag & drop a file or folder onto the window to switch folders.
- Right-click anywhere to open the "Sort" context menu. Choose field (file name, created date, modified date, size) and direction (ascending/descending). The choice is saved immediately and re-used on next launch.

The viewer preloads adjacent images in the background for instant navigation and caches them in memory. Large folders (5k+ images) are handled without blocking the UI.

## Configuration

A `config.json` file is created next to the executable on first launch:

```json
{
  "PreloadImageCount": 20,
  "BackgroundColor": "#000000",
  "FitMode": "Uniform",
  "ScalingMode": "HighQuality",
  "ShowOverlay": true,
  "LoopAround": true,
  "SortField": "FileName",
  "SortDirection": "Ascending"
}
```

- `PreloadImageCount`: number of images to preload on each side of the current image. The cache will hold up to (PreloadImageCount * 2 + 1) images total. Default: 20.
- `BackgroundColor`: any WPF color string (e.g. `#111111`, `Black`).
- `FitMode`: `Uniform`, `UniformToFill`, `Fill`, or `None`.
- `ScalingMode`: `HighQuality`, `Fant`, `LowQuality`, or `NearestNeighbor`.
- `ShowOverlay`: toggles filename/resolution HUD.
- `LoopAround`: wrap navigation at folder ends.
- `SortField`: `FileName`, `CreationTime`, `LastWriteTime`, or `FileSize`.
- `SortDirection`: `Ascending` or `Descending`.

Update the file, then press `Ctrl+R` to reload without restarting. Right-click -> Sort also updates these values automatically.

## Set as Default Viewer

For a portable setup:

1. Publish or copy the build output to a permanent directory.
2. Right-click a `.png` (or other image) -> `Open with` -> `Choose another app`.
3. Enable `Always use this app`, click `More apps`, then `Look for another app on this PC`.
4. Browse to `CoilViewer.exe` and select it.

Windows will then launch Coil Viewer for that extension. Repeat for additional image types if desired.

---

## Performance & Optimization

- [Startup Optimization Report](STARTUP_OPTIMIZATION_REPORT.md) - Performance analysis and optimizations
- [Startup Timing Instrumentation](STARTUP_TIMING_INSTRUMENTATION.md) - How startup timing is measured
- [Native AOT Explained](NATIVE_AOT_EXPLAINED.md) - Ahead-of-time compilation details
- [.NET Runtime Version Issue](NET_RUNTIME_VERSION_ISSUE.md) - Runtime compatibility notes

## Machine Learning Features

CoilViewer supports optional AI-powered image filtering using ONNX models.

### Setup Guides

- [NSFW Detection Setup](NSFW_DETECTION_SETUP.md) - How to enable NSFW filtering
- [Model Download Guide](MODEL_DOWNLOAD_GUIDE.md) - Where to get the ML models
- [Model Testing Guide](MODEL_TESTING_GUIDE.md) - How to verify models work correctly

### Technical Details

- [ML Model Interaction](ML_MODEL_INTERACTION.md) - How the app interfaces with ONNX models
- [Object Detection Optimization](OBJECT_DETECTION_OPTIMIZATION.md) - Performance tuning for inference
- [Object Detection Input Size Guide](OBJECT_DETECTION_INPUT_SIZE_GUIDE.md) - Input preprocessing details
- [Object Detection Changes Summary](OBJECT_DETECTION_CHANGES_SUMMARY.md) - Recent changes to detection code
- [Input Size Implementation Summary](INPUT_SIZE_IMPLEMENTATION_SUMMARY.md) - Input sizing implementation details
- [Models Moved Summary](MODELS_MOVED_SUMMARY.md) - Model file location changes

## Development Notes

- [Zoom/Pan Postmortem](ZOOM_PAN_POSTMORTEM.md) - Lessons from implementing zoom/pan
- [Zoom/Pan Postmortem (Extended)](zoom_pan_postmortem_opus_view.md) - Detailed technical analysis
