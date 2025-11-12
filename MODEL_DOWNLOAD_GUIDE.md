# Model Download Guide

## Quick Summary

- **Object Detection Model**: ✅ Already downloaded (`mobilenet_v2.onnx` and `imagenet_labels.txt`)
- **NSFW Detection Model**: ❌ Needs manual download

## Object Detection (Already Complete)

The object detection model (MobileNet V2) and ImageNet labels are already in the `Models/` directory. Object detection is ready to use!

## NSFW Detection (Manual Download Required)

The NSFW model cannot be downloaded automatically. Follow these steps:

### Option 1: Download from Hugging Face (Recommended)

1. Visit: https://huggingface.co/models?search=nsfw+onnx
2. Find a compatible model (e.g., search for "nsfw" and "onnx")
3. Click on a model (e.g., `Falconsai/nsfw_image_detection`)
4. Click the "Files and versions" tab
5. Look for a `.onnx` file
6. Click the download button next to the file
7. Save it as: `D:\proj\CoilViewer\Models\nsfw_detector.onnx`

### Option 2: Use a Pre-converted Model

Some models that work well:
- **Falconsai/nsfw_image_detection** on Hugging Face
- Search Hugging Face for "nsfw onnx" to find other options

### Model Requirements

- Format: ONNX (.onnx)
- Input: [1, 3, 224, 224] RGB image (normalized)
- Output: [1, 2] probabilities [safe, nsfw] or similar binary classification

## After Downloading

Once you've downloaded the NSFW model:

1. Place it in: `D:\proj\CoilViewer\Models\nsfw_detector.onnx`
2. The config is already set to enable both features by default
3. Launch CoilViewer and press `F` to open the filter panel
4. Click "Check NSFW" to test

## Running the Download Script

To re-run the download script:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/download-models.ps1
```

This will:
- Download MobileNet V2 (if missing)
- Download ImageNet labels (if missing)
- Attempt to download NSFW model (may require manual download)

