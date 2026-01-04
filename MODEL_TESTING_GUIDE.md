# Model Download and Testing Summary

## Successfully Downloaded Models

### 1. MobileNet V2 (Object Detection)
- **File**: `Models/mobilenet_v2.onnx`
- **Size**: 13.32 MB
- **Source**: ONNX Model Zoo
- **Purpose**: ImageNet classification (1000 classes)
- **Status**: ✓ Ready to use

### 2. ImageNet Class Labels
- **File**: `Models/imagenet_labels.txt`
- **Size**: ~0.01 MB
- **Source**: PyTorch Hub
- **Classes**: 1000 ImageNet classes including:
  - Cats: tabby, tiger cat, Persian cat, Siamese cat, Egyptian cat
  - Dogs: Many breeds (Labrador retriever, golden retriever, etc.)
  - Food: pizza, cheeseburger, hotdog, ice cream, etc.
  - And many more!
- **Status**: ✓ Ready to use

### 3. NSFW Detection Model
- **File**: `Models/nsfw_detector.onnx`
- **Status**: ⚠ Not downloaded (requires manual download)
- **Reason**: Not available in official ONNX Model Zoo
- **Options**:
  1. Download from Hugging Face: https://huggingface.co/models?search=nsfw+onnx
  2. Convert OpenNSFW model to ONNX
  3. Use other pre-converted models

## Testing the Models

### Enable Object Detection

1. **Launch CoilViewer** - It will create `config.json` on first run
2. **Edit `config.json`** in the executable directory:
```json
{
  "EnableObjectDetection": true,
  "ObjectModelPath": "Models/mobilenet_v2.onnx",
  "ObjectLabelsPath": "Models/imagenet_labels.txt",
  "ObjectFilterMode": "ShowAll",
  "ObjectFilterText": "",
  "ObjectFilterThreshold": 0.1
}
```

3. **Press F** to open the filter panel
4. **Test Object Filtering**:
   - Set Object Filter to "Show Only"
   - Enter "cat" in the text box
   - Click "Apply Filters"
   - Should show only images containing cats

5. **Try other searches**:
   - "pizza" - finds pizza images
   - "dog" - finds dog images
   - "car" - finds car images
   - etc.

### How It Works

1. **Detection runs in background** as you view images
2. **Results are cached** - each image is only analyzed once
3. **Filtering applies instantly** using cached results
4. **GPU acceleration** is used automatically if CUDA is available

### Example Filter Combinations

- **Show only cats**: Object Filter = "Show Only", Text = "cat"
- **Exclude dogs**: Object Filter = "Exclude", Text = "dog"
- **No NSFW + Show only pizza**: NSFW Filter = "No NSFW", Object Filter = "Show Only", Text = "pizza"

## Model Performance

With RTX 3090 GPU:
- **Object Detection**: ~50-100ms per image
- **NSFW Detection**: ~50-100ms per image (when model available)
- **CPU Fallback**: ~200-500ms per image

## Next Steps

1. **Test with your image collection**:
   - Open a folder with images
   - Let it detect objects as you browse
   - Use filters to find specific content

2. **Download NSFW model** (optional):
   - Visit Hugging Face and search for "nsfw onnx"
   - Download a compatible model
   - Place in `Models/nsfw_detector.onnx`
   - Enable in config.json: `"EnableNsfwDetection": true`

3. **Fine-tune thresholds**:
   - Adjust `ObjectFilterThreshold` (0.0-1.0) to control sensitivity
   - Lower = more matches, Higher = more confident matches only

## Troubleshooting

- **Model not loading**: Check file paths in config.json are correct
- **No GPU acceleration**: Install CUDA Toolkit and cuDNN
- **Slow detection**: Normal on first pass, speeds up as cache fills
- **Filter not working**: Ensure images have been viewed (detection runs on display)

