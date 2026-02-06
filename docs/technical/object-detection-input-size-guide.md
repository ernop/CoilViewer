# Object Detection Input Size Configuration Guide

## Understanding Input Size

The "input size" refers to the resolution (width x height) that images are resized to before being fed into the object detection model. By default, MobileNetV2 uses **224x224 pixels**.

## Why Larger Input Sizes?

**Larger input sizes can improve detection accuracy** because:
- More detail is preserved from the original image
- Small objects are less likely to be lost during downsampling
- Finer features are retained for better classification

**Trade-offs:**
- **Processing time increases**: A 512x512 image has ~5x more pixels than 224x224
- **Memory usage increases**: Larger tensors require more RAM
- **Model compatibility**: The ONNX model file may have fixed dimensions

## Current Model: MobileNetV2

Your `mobilenet_v2.onnx` model will report its expected input dimensions when initialized. You'll see a log message like:

```
Model input shape: [1, 3, 224, 224]
Detected input size: 224x224
```

## Testing Your Model's Flexibility

Your current MobileNetV2 model was likely exported with **fixed dimensions (224x224)**. However, some models support dynamic/flexible input sizes.

### To test if your model supports larger inputs:

1. Open `config.json` in the CoilViewer directory
2. Add or modify the `ObjectDetectionInputSize` field:

```json
{
  "EnableObjectDetection": true,
  "ObjectModelPath": "..\\..\\Models\\mobilenet_v2.onnx",
  "ObjectLabelsPath": "..\\..\\Models\\imagenet_labels.txt",
  "ObjectDetectionInputSize": 299,
  ...
}
```

3. Launch CoilViewer and check the logs:
   - If it works: You'll see "Using configured input size: 299x299"
   - If it fails: You'll see an error about dimension mismatch

## Common Input Sizes for ImageNet Models

| Size | Model Examples | Relative Performance |
|------|----------------|---------------------|
| 224x224 | MobileNetV1, MobileNetV2, ResNet50 | Baseline |
| 299x299 | InceptionV3, Xception | ~1.8x slower |
| 384x384 | EfficientNetB4, ViT-Base | ~3x slower |
| 512x512 | EfficientNetB6 | ~5.3x slower |

## Configuration Options

### Option 1: Auto-Detect (Default)
```json
"ObjectDetectionInputSize": 0
```
The system will read the model's metadata and use its expected dimensions.

### Option 2: Manual Override
```json
"ObjectDetectionInputSize": 384
```
Force a specific input size. **Warning**: This may crash if your model doesn't support it!

## Getting Models That Support Larger Inputs

If you want to use larger input sizes with better performance:

### 1. InceptionV3 (299x299)
- Better accuracy than MobileNetV2
- Native 299x299 input
- Download ONNX model from ONNX Model Zoo or export from PyTorch/TensorFlow

### 2. EfficientNet family
- State-of-the-art accuracy/efficiency trade-off
- EfficientNet-B0: 224x224
- EfficientNet-B1: 240x240
- EfficientNet-B2: 260x260
- EfficientNet-B3: 300x300
- EfficientNet-B4: 380x380

### 3. Dynamic Input Models
Some models can be exported with dynamic dimensions:
```python
# PyTorch example
torch.onnx.export(
    model, 
    dummy_input,
    "mobilenet_dynamic.onnx",
    dynamic_axes={'input': {2: 'height', 3: 'width'}}
)
```

## Real-World Example

Let's say you have a 4K image (3840x2160) and want to detect objects:

### With 224x224 input:
- Image is downsampled from 3840x2160 → 224x224
- ~97% of pixels are discarded
- Small objects may disappear entirely
- Processing time: ~50-100ms per image

### With 512x512 input:
- Image is downsampled from 3840x2160 → 512x512
- ~93% of pixels are discarded (but less than 224x224)
- Small objects better preserved
- Processing time: ~250-500ms per image

## Recommendations

### For Speed (real-time browsing):
```json
"ObjectDetectionInputSize": 224
```

### For Balanced Performance:
```json
"ObjectDetectionInputSize": 299
```

### For Maximum Accuracy (don't care about speed):
```json
"ObjectDetectionInputSize": 512
```
**Note**: Only works if you have a model that supports this size!

## Checking the Logs

After changing the input size, launch CoilViewer and check `coilviewer-launch.log`:

```
Object detection initialized with CPU...
Model input shape: [1, 3, 224, 224]
Using configured input size: 384x384
Object detection service initialized successfully
```

Or if there's an error:
```
Failed to initialize object detection service: Input tensor shape mismatch
Expected [1, 3, 224, 224] but got [1, 3, 384, 384]
```

## Next Steps

1. **Try the current model** with `ObjectDetectionInputSize: 299` to see if it's flexible
2. **Monitor performance**: Check the average ms per image in the logs
3. **If you need larger inputs**: Consider downloading a different model architecture
4. **Remember**: Higher resolution ≠ always better if the model wasn't trained for it

## Technical Details

The system now:
- ✓ Auto-detects model input dimensions from ONNX metadata
- ✓ Allows manual override via config
- ✓ Uses high-quality bicubic interpolation for resizing
- ✓ Logs detected/configured input size for verification
- ✓ Maintains proper ImageNet normalization regardless of input size

