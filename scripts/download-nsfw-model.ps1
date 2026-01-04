# NSFW Model Download Script
# This script downloads a pre-trained NSFW detection ONNX model

$ErrorActionPreference = "Stop"

$modelDir = Join-Path $PSScriptRoot "..\CoilViewer\bin\Debug\net8.0-windows\Models"
$modelPath = Join-Path $modelDir "nsfw_detector.onnx"

Write-Host "NSFW Model Download Script" -ForegroundColor Cyan
Write-Host "=========================" -ForegroundColor Cyan
Write-Host ""

# Create Models directory if it doesn't exist
if (-not (Test-Path $modelDir)) {
    New-Item -ItemType Directory -Path $modelDir -Force | Out-Null
    Write-Host "Created directory: $modelDir" -ForegroundColor Green
}

# Check if model already exists
if (Test-Path $modelPath) {
    Write-Host "Model already exists at: $modelPath" -ForegroundColor Yellow
    Write-Host "Delete it first if you want to re-download." -ForegroundColor Yellow
    exit 0
}

Write-Host "To download an NSFW detection model:" -ForegroundColor Yellow
Write-Host ""
Write-Host "Option 1: Use a pre-converted ONNX model from Hugging Face" -ForegroundColor Cyan
Write-Host "  - Visit: https://huggingface.co/models?search=nsfw+onnx" -ForegroundColor White
Write-Host "  - Download a compatible model and save it as: $modelPath" -ForegroundColor White
Write-Host ""
Write-Host "Option 2: Convert a PyTorch model to ONNX" -ForegroundColor Cyan
Write-Host "  - Use a model like NudeNet or similar" -ForegroundColor White
Write-Host "  - Convert using torch.onnx.export()" -ForegroundColor White
Write-Host ""
Write-Host "Option 3: Use Yahoo Open NSFW model converted to ONNX" -ForegroundColor Cyan
Write-Host "  - Original: https://github.com/yahoo/open_nsfw" -ForegroundColor White
Write-Host "  - Look for ONNX conversions online" -ForegroundColor White
Write-Host ""
Write-Host "Model Requirements:" -ForegroundColor Yellow
Write-Host "  - Format: ONNX (.onnx)" -ForegroundColor White
Write-Host "  - Input: [1, 3, 224, 224] RGB image (normalized)" -ForegroundColor White
Write-Host "  - Output: [1, 2] probabilities [safe, nsfw] or similar" -ForegroundColor White
Write-Host ""
Write-Host "After downloading, update config.json:" -ForegroundColor Yellow
Write-Host "  - Set EnableNsfwDetection: true" -ForegroundColor White
Write-Host "  - Set NsfwModelPath to the model file path" -ForegroundColor White
Write-Host ""

