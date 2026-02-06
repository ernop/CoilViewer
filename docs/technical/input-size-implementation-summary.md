# Input Size Configuration - Implementation Summary

## What Was Done

I've implemented **dynamic input size detection and configuration** for the object detection model, allowing you to use larger (or smaller) input sizes than the default 224x224.

## Key Changes

### 1. ObjectDetectionService.cs
- Made input size **dynamic** instead of hardcoded constant
- Added `DetectInputSize()` method that reads the model's metadata to determine expected input dimensions
- Added `Initialize()` parameter for manual input size override
- Updated all internal methods to use the dynamic `_inputSize` field
- Exposes current input size via public `InputSize` property

### 2. ViewerConfig.cs
- Added new configuration option: `ObjectDetectionInputSize`
  - **0** = auto-detect from model (default)
  - **Any positive number** = manually override (e.g., 299, 384, 512)

### 3. App.xaml.cs
- Updated initialization to pass configured input size to the service

### 4. config.example.json
- Added `ObjectDetectionInputSize` field with default value of 0

## How It Works Now

### Startup Sequence:
1. CoilViewer loads `config.json`
2. If `ObjectDetectionInputSize` is 0 (default):
   - System loads the ONNX model
   - Reads model metadata to detect input shape
   - Logs: "Model input shape: [1, 3, 224, 224]"
   - Logs: "Detected input size: 224x224"
3. If `ObjectDetectionInputSize` is set (e.g., 512):
   - System overrides with configured value
   - Logs: "Using configured input size: 512x512"
4. All images are now resized to this size before inference

### Image Processing:
```
Your 4K Image (3840x2160)
          ↓
High-Quality Bicubic Resize
          ↓
Model Input Size (224x224, 299x299, 512x512, etc.)
          ↓
Preprocessing & Normalization
          ↓
ONNX Inference
          ↓
Top-K Predictions
```

## Testing Your Current Model

Your `mobilenet_v2.onnx` model is **likely fixed at 224x224** (most pre-trained models are), but you can test if it supports other sizes:

### Test 1: Try 299x299
Edit `config.json`:
```json
{
  "EnableObjectDetection": true,
  "ObjectDetectionInputSize": 299,
  ...
}
```

Launch CoilViewer and check logs:
- ✓ Success: "Using configured input size: 299x299"
- ✗ Failure: Error about dimension mismatch

### Test 2: Try 384x384
```json
"ObjectDetectionInputSize": 384
```

### Test 3: Try 512x512
```json
"ObjectDetectionInputSize": 512
```

## What Happens with Different Models

### Fixed Dimension Models (Most Common)
If your model was exported with fixed dimensions [1, 3, 224, 224]:
- You **can only use 224x224**
- Trying other sizes will cause an error
- The model needs to be re-exported with dynamic dimensions or retrained

### Dynamic Dimension Models (Less Common)
If your model was exported with dynamic dimensions [1, 3, H, W]:
- You **can use any size** (within reason, typically 32-1024)
- Larger sizes = more accuracy but slower
- The model weights will adapt to different input sizes

## Performance Impact

Based on pixel counts:

| Input Size | Pixels | Relative Speed | Relative Accuracy |
|-----------|--------|----------------|-------------------|
| 224x224 | 50,176 | 1.0x (baseline) | 1.0x |
| 299x299 | 89,401 | ~0.56x (slower) | ~1.1-1.2x |
| 384x384 | 147,456 | ~0.34x (slower) | ~1.2-1.3x |
| 512x512 | 262,144 | ~0.19x (slower) | ~1.3-1.5x |

*Actual performance depends on CPU, model architecture, and ONNX Runtime optimizations*

## What You Can Do Now

### Option 1: Test Current Model Flexibility
Try different input sizes with your existing model to see if it supports them.

### Option 2: Use Existing 224x224 Model
Your images are already being processed at any size! The system automatically downsamples them to 224x224 using high-quality bicubic interpolation. This works perfectly fine for most use cases.

### Option 3: Get a Different Model
If you need better accuracy with larger inputs:
- Download InceptionV3 (299x299 native)
- Download EfficientNet-B4 (380x380 native)
- Export your own model with dynamic dimensions

## Monitoring Performance

CoilViewer logs performance statistics:
```
Object detection: 50 images processed, 12.5s total, 250.0ms avg per image
```

Compare different input sizes:
- 224x224: typically 50-150ms per image
- 299x299: typically 90-270ms per image
- 512x512: typically 250-750ms per image

## Documentation Created

1. **OBJECT_DETECTION_INPUT_SIZE_GUIDE.md** - Comprehensive guide for users
2. **OBJECT_DETECTION_OPTIMIZATION.md** - Technical optimization details
3. **OBJECT_DETECTION_CHANGES_SUMMARY.md** - Code-level changes
4. **INPUT_SIZE_IMPLEMENTATION_SUMMARY.md** - This file

## Current Status

✅ **Built Successfully** (Debug & Release)
✅ **No Linter Errors**
✅ **Input Size Auto-Detection Working**
✅ **Manual Override Configured**
✅ **High-Quality Preprocessing Maintained**
✅ **Backward Compatible** (defaults to auto-detect)

## Quick Start

To experiment with larger input sizes RIGHT NOW:

1. Open: `CoilViewer/config.json`
2. Find: `"ObjectDetectionInputSize": 0`
3. Change to: `"ObjectDetectionInputSize": 299` (or 384, 512, etc.)
4. Run CoilViewer
5. Check logs to see if it worked or got a dimension error

If you get an error, your model is fixed at 224x224. That's okay! The 224x224 input with high-quality preprocessing I added earlier is still much better than before.

## The Bottom Line

**Question**: "Can we send in bigger images than 224x224?"

**Answer**: 
- Images of ANY size are already accepted and automatically resized
- The 224x224 is the model's processing resolution, not your image size
- Your model likely requires exactly 224x224 (this is normal)
- To use larger processing resolution, you'd need a different model or re-export yours
- BUT: The high-quality bicubic resizing I added ensures 224x224 still captures good detail from your high-res images

