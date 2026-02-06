# CoilViewer

High-speed fullscreen image browser focused on instant navigation through large folders on Windows.

## Quick Start

1. Install [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) or later.
2. Build: `dotnet build CoilViewer/CoilViewer.csproj`
3. Run: `dotnet run --project CoilViewer/CoilViewer.csproj -- "C:\path\to\image.png"`
   Or launch `CoilViewer.exe` from the build output and open a folder.

## Features

- Instant navigation through thousands of images with background preloading
- Keyboard-driven: arrows, space, home/end, Ctrl+Shift+Arrow for half-jumps
- Smooth zoom and pan (mouse wheel, drag, `=`/`-`/`\` keys)
- Archive images to an "old" subfolder with `A`, undo with `Ctrl+Z`
- Sort by name, date, size via right-click context menu
- Fullscreen toggle with F11 or double-click
- Drag-and-drop to switch folders
- Optional AI-powered filtering (NSFW detection, object detection via ONNX models)
- Metadata overlay with `I`, keyboard shortcuts with `/`

## Supported Formats

PNG, JPEG, WebP, GIF (first frame), BMP/DIB, TIFF, SVG -- plus any format with a registered WIC codec.

## Documentation

Full documentation is in [agents.md](agents.md). Additional docs are organized under `docs/`:

- **[docs/guides/](docs/guides/)** -- Setup guides for ML models and features
- **[docs/technical/](docs/technical/)** -- Architecture, performance, and implementation details
- **[docs/postmortems/](docs/postmortems/)** -- Debugging session write-ups

## Configuration

A `config.json` is created next to the executable on first launch. Press `Ctrl+R` to reload it live. See [agents.md](agents.md#configuration) for all options.

## Building a Portable Release

```
dotnet publish CoilViewer/CoilViewer.csproj -c Release -r win-x64 --self-contained false
```

Copy the published folder to keep the app portable.
