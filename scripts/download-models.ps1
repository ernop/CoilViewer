# Download Models Script for CoilViewer
# Downloads NSFW detection and ImageNet classification models

$ErrorActionPreference = "Stop"

$baseDir = Split-Path -Parent $PSScriptRoot
$modelsDir = Join-Path $baseDir "Models"

Write-Host "CoilViewer Model Download Script" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""

# Create Models directory
if (-not (Test-Path $modelsDir)) {
    New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null
    Write-Host "Created directory: $modelsDir" -ForegroundColor Green
}

# Function to download file
function Download-File {
    param(
        [string]$Url,
        [string]$OutputPath,
        [string]$Description
    )
    
    Write-Host "Downloading $Description..." -ForegroundColor Yellow
    Write-Host "  URL: $Url" -ForegroundColor Gray
    Write-Host "  To: $OutputPath" -ForegroundColor Gray
    
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $OutputPath -UseBasicParsing
        Write-Host "  [OK] Downloaded successfully" -ForegroundColor Green
        
        $fileInfo = Get-Item $OutputPath
        Write-Host "  Size: $([math]::Round($fileInfo.Length / 1MB, 2)) MB" -ForegroundColor Gray
        return $true
    }
    catch {
        Write-Host "  [FAIL] Failed: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

Write-Host ""
Write-Host "Available Models:" -ForegroundColor Cyan
Write-Host ""

# 1. MobileNet V2 for ImageNet Classification (ONNX Model Zoo)
$mobilenetUrl = "https://github.com/onnx/models/raw/main/validated/vision/classification/mobilenet/model/mobilenetv2-12.onnx"
$mobilenetPath = Join-Path $modelsDir "mobilenet_v2.onnx"

if (-not (Test-Path $mobilenetPath)) {
    Download-File -Url $mobilenetUrl -OutputPath $mobilenetPath -Description "MobileNet V2 (ImageNet Classification)" | Out-Null
} else {
    Write-Host "MobileNet V2 already exists, skipping..." -ForegroundColor Yellow
}

# 2. ImageNet Labels
# Try multiple sources for ImageNet labels
$imagenetLabelsUrls = @(
    "https://raw.githubusercontent.com/pytorch/hub/master/imagenet_classes.txt",
    "https://gist.githubusercontent.com/yrevar/942d3a0ac09ec9e5eb3a/raw/238f720ff059c1f82f368259d1ca4ffa5dd8f9f5/imagenet1000_clsidx_to_labels.txt"
)
$imagenetLabelsPath = Join-Path $modelsDir "imagenet_labels.txt"

if (-not (Test-Path $imagenetLabelsPath)) {
    $downloaded = $false
    foreach ($url in $imagenetLabelsUrls) {
        if (Download-File -Url $url -OutputPath $imagenetLabelsPath -Description "ImageNet Class Labels") {
            $downloaded = $true
            break
        }
    }
    if (-not $downloaded) {
        Write-Host "  Creating ImageNet labels file manually..." -ForegroundColor Yellow
        # Create a basic ImageNet labels file (first 100 classes as fallback)
        $labels = @()
        for ($i = 0; $i -lt 1000; $i++) {
            $labels += "class_$i"
        }
        $labels | Out-File -FilePath $imagenetLabelsPath -Encoding UTF8
        Write-Host "  Created basic labels file (will use class names from model if available)" -ForegroundColor Yellow
    }
} else {
    Write-Host "ImageNet labels already exist, skipping..." -ForegroundColor Yellow
}

# 3. NSFW Detection Model
# Note: We'll use a publicly available NSFW model
# One option is to use a converted OpenNSFW model
Write-Host ""
Write-Host "NSFW Detection Model:" -ForegroundColor Cyan
Write-Host "  Note: NSFW models are not available in the official ONNX Model Zoo." -ForegroundColor Yellow
Write-Host "  You can:" -ForegroundColor Yellow
Write-Host "  1. Convert OpenNSFW (Yahoo) model to ONNX" -ForegroundColor White
Write-Host "  2. Use a pre-converted model from Hugging Face or other sources" -ForegroundColor White
Write-Host "  3. Train your own model" -ForegroundColor White
Write-Host ""
Write-Host "  For testing, you can download a pre-converted model from:" -ForegroundColor Yellow
Write-Host "  https://huggingface.co/models?search=nsfw+onnx" -ForegroundColor Cyan
Write-Host ""

$nsfwPath = Join-Path $modelsDir "nsfw_detector.onnx"
if (-not (Test-Path $nsfwPath)) {
    Write-Host "NSFW model not found. Place it manually at:" -ForegroundColor Yellow
    Write-Host "  $nsfwPath" -ForegroundColor White
} else {
    Write-Host "NSFW model found at: $nsfwPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "Download Summary:" -ForegroundColor Cyan
Write-Host "=================" -ForegroundColor Cyan

$downloaded = @()
if (Test-Path $mobilenetPath) { $downloaded += "MobileNet V2" }
if (Test-Path $imagenetLabelsPath) { $downloaded += "ImageNet Labels" }
if (Test-Path $nsfwPath) { $downloaded += "NSFW Detector" }

if ($downloaded.Count -gt 0) {
    Write-Host "[OK] Downloaded models:" -ForegroundColor Green
    foreach ($item in $downloaded) {
        Write-Host "  - $item" -ForegroundColor White
    }
} else {
    Write-Host "No models downloaded." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "1. If NSFW model is missing, download it manually and place in Models folder" -ForegroundColor White
Write-Host "2. Update config.json to enable detection:" -ForegroundColor White
Write-Host "   - Set EnableObjectDetection: true" -ForegroundColor Gray
Write-Host "   - Set EnableNsfwDetection: true (if model available)" -ForegroundColor Gray
Write-Host "3. Launch CoilViewer and press F to open filter panel" -ForegroundColor White
Write-Host ""

