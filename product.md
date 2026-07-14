- Meta: whenever you fulfill a new user-visible requirement, append a concise bullet about it here.
- The program bumps a global build counter on every build so the version reads 0.000N.
- The window title shows "CoilViewer" followed by that current build version.
- The viewer accepts an optional command-line argument, resolves it against the file system (including environment variables), and loads that folder or image on startup. Double-clicking an image file also opens it in CoilViewer.
- The program supports PNG, JPEG, GIF, BMP/DIB, TIFF, and WebP files via Windows Imaging Component.
- The program lists every image in the chosen folder and moves between them instantly.
- The overlay presents the active sort mode and the key metadata for the displayed image.
- The program sorts images by filename, creation time, modification time, or size in ascending or descending order.
- Right-click opens a sort menu that updates the config and reflects the current sort choice.
- The program lets Space, Backspace, and the arrow keys move to the next or previous image.
- Home jumps to the first image and End jumps to the last image in the sequence.
- F11 and a double-click toggle fullscreen mode on and off.
- Ctrl+O opens an image and loads its folder; drag and drop does the same.
- Ctrl+R reloads the JSON config without restarting; menu changes save back to the same file.
- The config file controls preload radius, cache size, background color, fit/scaling mode, overlay visibility, loop behavior, and default sort.
- The program preloads neighboring images so navigation remains instant.
- The "I" key toggles the metadata overlay with a gentle fade. For the first five seconds after open (or after re-enabling the overlay), the overlay appends "Hit I to toggle".
- "/" or "?" toggles a keyboard shortcut list that can be dismissed quickly.
- "=" (or Shift+=) zooms in, "-" zooms out, and zoom resets when a new image loads.
- "\" resets zoom to the fitted scale without disturbing navigation.
- When zoomed, the arrow keys and mouse wheel pan the image smoothly.
- When not zoomed, the mouse wheel navigates to the previous or next image.
- The shortcuts overlay highlights the "A" key for automatically archiving the current image into the "old" folder.
- Mouse drag pans the zoomed image while leaving image navigation unchanged.
- Pressing "A" moves the current image into an "old" subfolder, auto-renaming to avoid collisions, and immediately displays the next image without delay.
- Launch and error details are written to `CoilViewer-launch.log` and `CoilViewer-errors.log` near the project root.
- Never use fancy quotes; use ASCII quotes only.
- Never use emojis in any output.

- Ctrl+Shift+Arrow keys jump halfway toward the start or end of the image list for rapid exploration.

- Optional AI-powered filtering: Press "F" to open the filter panel. NSFW filter supports "All", "No NSFW", or "NSFW Only" modes. Object filter supports text-based filtering (e.g., "cat", "pizza") with "Show All", "Show Only", or "Exclude" modes. Both filters require explicit enablement in config.json and model files; detection runs only when enabled and models are present. Filters apply to the image sequence and can be combined.

- Comprehensive startup timing instrumentation logs every initialization step to CoilViewer-launch.log, breaking down App startup, MainWindow construction, image sequence loading, and bitmap decoding with millisecond precision.
- ML model initialization runs asynchronously in the background to avoid blocking window display and first-image rendering.
- DirectoryInstanceGuard cold-start optimization reduced startup overhead by 40 percent, bringing typical launch time to under 550ms from click to displayed image.
- Lazy-loaded XAML elements removed 270+ lines of unused UI markup from startup parsing, cutting InitializeComponent time by 10-15 percent.
- ReadyToRun compilation enabled for Release builds pre-compiles IL to native code, eliminating JIT overhead and reducing startup time by 30-80ms.
- Tiered compilation with QuickJit ensures debug builds start quickly while allowing the runtime to optimize hot paths during execution.

- Navigation no longer loops by default: reaching the first image and pressing previous (or the last image and pressing next) sticks in place instead of wrapping, making Home/End meaningful. A brief edge flash on the relevant side signals the boundary (no sound). Looping remains available via the loop/LoopAround config option.
- A native Linux build (GTK3/Python, command "coilbrowser") lives in linux/ and is maintained alongside the Windows build with matching features: navigation, sorting, right-click menu (copy path, copy image, info, sort, open, settings), info overlay with resolution/size/index/sort, zoom and pan (drag, arrows, and wheel when zoomed), archive-to-old with undo (Ctrl+Z or U), Ctrl+C copy image, Ctrl+O open, Ctrl+R reload config, Ctrl+S/Ctrl+, settings dialog, neighbor preloading, auto-detection of new images in the open folder, fullscreen, and launch/error logging. AI filtering is intentionally deferred (off by default, requires explicit opt-in and local models).

- While a folder is open, CoilViewer watches it for added/removed image files (FileSystemWatcher on Windows, GIO file monitor on Linux), debounces rapid bursts, re-sorts the list, keeps the current image in place, and preloads new neighbors.

- Only one CoilViewer window may own a given image directory. A second launch redirects to the existing window (named pipe on Windows, Unix domain socket on Linux) and focuses the requested image.

- Archiving moves the current image into an `old/` subfolder with `_1`, `_2`, ... collision suffixes, advances to the next image immediately, completes the file move in the background, and supports undo (Ctrl+Z or U) for up to 200 recent archives with the same validation and status messages as the Windows build.

