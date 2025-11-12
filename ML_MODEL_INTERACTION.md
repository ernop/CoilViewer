# ML Model Interaction Overview

## Architecture

CoilViewer uses ONNX Runtime for local ML inference. Two detection services are available:

1. **NSFW Detection Service** (`NsfwDetectionService.cs`)
   - Binary classification: safe vs. NSFW content
   - Uses ONNX models (e.g., NSFW detector models)
   - Input: 224x224 RGB image, normalized
   - Output: [safe_probability, nsfw_probability]

2. **Object Detection Service** (`ObjectDetectionService.cs`)
   - Multi-class classification: ImageNet-style object recognition
   - Uses ONNX models (e.g., MobileNet, ResNet)
   - Input: 224x224 RGB image, ImageNet-normalized
   - Output: Top-K class predictions with confidence scores

## Opt-In Design

**Default State: DISABLED**
- `EnableNsfwDetection` defaults to `false`
- `EnableObjectDetection` defaults to `false`
- Services do not initialize unless explicitly enabled

**Initialization Requirements:**
1. User must set `EnableNsfwDetection: true` or `EnableObjectDetection: true` in `config.json`
2. Model file must exist at the specified path
3. If model is missing, service logs a message and disables gracefully (no crash)

**No Automatic Downloads:**
- Models are never downloaded automatically
- User must manually obtain and place model files
- No network requests are made without explicit user action

## Detection Flow

1. **Startup**: Services initialize only if enabled AND model file exists
2. **Image Display**: When an image is shown, detection runs asynchronously in background
3. **Caching**: Results are cached in `DetectionCache` to avoid re-detection
4. **Filtering**: Filter panel (F key) applies cached results to filter image sequence

## GPU Acceleration

- Services attempt CUDA GPU acceleration if available
- Falls back to CPU automatically if GPU unavailable
- No user configuration required for GPU detection

## User Control

- Filter panel (F key) provides UI for:
  - NSFW filter: All / No NSFW / NSFW Only
  - Object filter: Show All / Show Only [text] / Exclude [text]
- Filters apply only to already-detected images (uses cache)
- Detection runs passively in background as images are viewed
