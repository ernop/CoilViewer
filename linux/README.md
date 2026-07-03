# CoilViewer - Linux build (CoilBrowser)

A native GTK3 / Python (PyGObject) image viewer that mirrors the Windows WPF
CoilViewer feature set. The command and config keep the historical name
`coilbrowser` so existing file associations keep working.

## Install

```bash
bash linux/install-coilbrowser-linux.sh
```

This symlinks `~/.local/bin/coilbrowser` to `linux/coilbrowser-linux.py` (so
edits here are live immediately), installs a `.desktop` entry, and registers
the viewer as the default handler for common image MIME types.

## Requirements

- Python 3
- PyGObject (`gi`) with GTK 3.0, Gdk, GdkPixbuf
- `python3-cairo`

## Run

```bash
coilbrowser /path/to/folder-or-image
```

## Config

`~/.config/coilbrowser/config.json`

| Key | Meaning | Default |
| --- | --- | --- |
| `background` | window background color | `#000000` |
| `show_overlay` | show the info overlay | `true` |
| `loop` | wrap around at ends | `true` |
| `sort_field` | `name` / `ctime` / `mtime` / `size` | `name` |
| `sort_direction` | `ascending` / `descending` | `ascending` |
| `preload_count` | neighbor images to preload each side | `20` |
| `fit_mode` | `Uniform` (fit) / `UniformToFill` (crop) | `Uniform` |
| `scaling_mode` | `HighQuality` / `Fast` | `HighQuality` |

## Feature parity with the Windows (WPF) build

| Feature | Windows | Linux |
| --- | --- | --- |
| Open folder/image from CLI (env-var + `~` expansion) | yes | yes |
| Formats PNG/JPEG/GIF/BMP/TIFF/WebP (+SVG) | yes | yes |
| Instant next/previous, Home/End | yes | yes |
| Space / Backspace / arrows navigation | yes | yes |
| Ctrl+Shift+Arrow half jump | yes | yes |
| Sort by name/created/modified/size, asc/desc | yes | yes |
| Right-click menu (copy path, copy image, info, sort, open, settings) | yes | yes |
| Info overlay: resolution, size, index/count, sort | yes | yes |
| Toggle overlay (`I`), help (`/` or `?`) | yes | yes |
| Zoom in/out (`+`/`-`), reset (`\`) | yes | yes |
| Pan with mouse drag and arrows/wheel when zoomed | yes | yes |
| Wheel navigates when not zoomed | yes | yes |
| Archive to `old/` (`A`) with auto-rename (`_1`, `_2`, ...) | yes | yes |
| Undo archive (`Ctrl+Z` / `U`) with 200-step history | yes | yes |
| One window per folder (duplicate launch redirects) | yes | yes |
| Copy image to clipboard (`Ctrl+C`) | yes | yes |
| Copy full path (menu) | yes | yes |
| Open dialog (`Ctrl+O`) + drag-and-drop | yes | yes |
| Reload config (`Ctrl+R`) | yes | yes |
| Settings dialog (`Ctrl+S` / `Ctrl+,`) | yes | yes |
| Neighbor preloading for instant navigation | yes | yes |
| Auto-detect new/removed images in open folder | yes | yes |
| Fullscreen (`F11` / double-click) | yes | yes |
| Launch / error logging | yes | yes (`~/.local/share/coilbrowser/`) |
| Optional AI NSFW / object filtering (`F`) | yes | deferred (off by default; needs explicit opt-in + models) |

The AI filtering panel is intentionally not yet ported: per project policy ML
features stay disabled by default and must never auto-download or auto-engage.
It is the only remaining gap and can be added behind explicit config + local
model files.
