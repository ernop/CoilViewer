# NSFW Detection Setup Guide

## Overview

CoilViewer now includes optional NSFW (Not Safe For Work) content detection using ONNX Runtime. This feature uses your GPU (RTX 3090) for fast inference when available.

## Setup Instructions

### 1. Download an NSFW Detection Model

You need to download a pre-trained ONNX model file. Here are some options:

**Option A: Use Hugging Face Models**
- Visit https://huggingface.co/models?search=nsfw+onnx
- Download a compatible ONNX model
- Save it as `Models/nsfw_detector.onnx` in your CoilViewer output directory

**Option B: Convert a PyTorch Model**
- Use models like NudeNet or similar
- Convert to ONNX using `torch.onnx.export()`
- Place the converted `.onnx` file in the Models directory

**Model Requirements:**
- Format: ONNX (.onnx)
- Input shape: [1, 3, 224, 224] (RGB image, normalized)
- Output: [1, 2] probabilities [safe, nsfw] or similar binary classification

### 2. Enable NSFW Detection

Edit `config.json` in your CoilViewer directory:

```json
{
  "EnableNsfwDetection": true,
  "NsfwModelPath": "Models/nsfw_detector.onnx",
  "NsfwThreshold": 0.5
}
```

- `EnableNsfwDetection`: Set to `true` to enable detection
- `NsfwModelPath`: Path to your ONNX model file (relative to executable or absolute)
- `NsfwThreshold`: Confidence threshold (0.0-1.0) for NSFW classification

### 3. GPU Acceleration (Optional)

The service automatically tries to use CUDA GPU acceleration if available. To ensure GPU support:

1. Install CUDA Toolkit (11.x or 12.x recommended)
2. Install cuDNN matching your CUDA version
3. The ONNX Runtime will automatically detect and use your GPU

If GPU is not available, the service falls back to CPU inference.

## Usage

Once enabled, the NSFW detection service:

1. **Initializes at startup** - Loads the model when the application starts
2. **Checks images automatically** - When images are displayed, they are checked in the background
3. **Logs results** - NSFW detections are logged to the application log

## Future Enhancements

The current implementation logs NSFW detections. Future enhancements could include:
- Visual indicators in the UI
- Automatic skipping of NSFW images
- Filtering options in the UI
- Batch scanning of entire directories

## Troubleshooting

**Model not found:**
- Ensure the model file exists at the specified path
- Check that the path in `config.json` is correct
- The service will disable gracefully if the model is missing

**GPU not detected:**
- Verify CUDA installation with `nvidia-smi`
- Check that CUDA version matches ONNX Runtime requirements
- The service will use CPU if GPU is unavailable

**Performance:**
- GPU inference is significantly faster than CPU
- With RTX 3090, expect sub-100ms inference times per image
- Batch processing can be added for even better performance

