# Models Moved and Performance Tracking Added

## Changes Made

### 1. Models Location
- **Moved from**: `CoilViewer/bin/Debug/net8.0-windows/Models/` (and Release)
- **Moved to**: `Models/` (project root)
- **Reason**: Models in bin/obj get deleted on "clean" - now they persist

### 2. .gitignore Updated
- Added `Models/*.onnx` and `Models/*.txt` to ignore model files
- Added `!Models/.gitkeep` to keep the directory in git
- Models are user-specific and large, so not committed

### 3. Configuration Updated
- **Default paths** in `ViewerConfig.cs` now point to `..\\..\\Models\\`
- **Path normalization** resolves relative paths to absolute paths
- **Example config** created: `config.example.json`

### 4. Performance Tracking Added
Both detection services now track:
- **Total images checked**: Count of processed images
- **Total time**: Cumulative milliseconds
- **Average time per image**: Automatically calculated
- **Auto-logging**: Stats logged every 10 images

### 5. Download Script Updated
- `scripts/download-models.ps1` now downloads to `Models/` folder
- No longer needs separate Debug/Release paths

## Performance Logging

When detection is enabled, you'll see logs like:
```
Object detection: 10 images processed, 0.8s total, 80.0ms avg per image
Object detection: 20 images processed, 1.5s total, 75.0ms avg per image
NSFW detection: 10 images processed, 0.9s total, 90.0ms avg per image
```

## Expected Performance

**With RTX 3090 GPU** (when GPU package installed):
- Object Detection: ~50-100ms per image
- NSFW Detection: ~50-100ms per image

**With CPU**:
- Object Detection: ~200-500ms per image
- NSFW Detection: ~200-500ms per image

**First image** may be slower due to model warm-up.

## Testing

1. **Models are ready**: `Models/mobilenet_v2.onnx` and `Models/imagenet_labels.txt`
2. **Enable in config.json**:
   ```json
   "EnableObjectDetection": true
   ```
3. **Launch CoilViewer** and browse images
4. **Check logs** for performance stats every 10 images
5. **Press F** to open filter panel and test filtering

## Files Updated

- `.gitignore` - Added Models folder exclusions
- `CoilViewer/ViewerConfig.cs` - Updated default paths
- `CoilViewer/NsfwDetectionService.cs` - Added performance tracking
- `CoilViewer/ObjectDetectionService.cs` - Added performance tracking
- `scripts/download-models.ps1` - Updated to use Models/ folder
- `config.example.json` - Created example configuration

