# Comprehensive CoilViewer Startup Performance Test
# Tests startup time on 10 different large images

$testImages = Get-ChildItem -Path "D:\dl" -Recurse -File -Include *.jpg,*.jpeg,*.png,*.bmp,*.gif,*.webp -ErrorAction SilentlyContinue | 
    Where-Object { $_.Length -ge 1MB -and $_.Length -le 1.5MB } | 
    Select-Object -First 10

if ($testImages.Count -eq 0) {
    Write-Host "No suitable test images found in D:\dl"
    exit 1
}

$results = @()
$testNumber = 0

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "CoilViewer Startup Performance Test" -ForegroundColor Cyan
Write-Host "Testing $($testImages.Count) images" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

foreach ($image in $testImages) {
    $testNumber++
    $fileName = $image.Name
    $sizeMB = [math]::Round($image.Length / 1MB, 2)
    
    Write-Host "[$testNumber/$($testImages.Count)] Testing: $fileName ($sizeMB MB)" -ForegroundColor Yellow
    
    # Clear the log
    Clear-Content -Path "coilviewer-launch.log" -ErrorAction SilentlyContinue
    
    # Run CoilViewer
    $process = Start-Process -FilePath "CoilViewer\bin\Debug\net8.0-windows\CoilViewer.exe" -ArgumentList "`"$($image.FullName)`"" -PassThru
    
    # Wait for it to initialize
    Start-Sleep -Seconds 3
    
    # Kill it
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    
    # Wait for process to fully exit
    Start-Sleep -Milliseconds 500
    
    # Extract timing data from log
    $logContent = Get-Content "coilviewer-launch.log" -ErrorAction SilentlyContinue
    
    if ($logContent) {
        # Extract key timings
        $totalStartup = ($logContent | Select-String "TOTAL APP STARTUP TIME: (\d+)ms" | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        $initComponent = ($logContent | Select-String "\[MAINWINDOW\] InitializeComponent: (\d+)ms" | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        $windowShow = ($logContent | Select-String "\[STARTUP\] window\.Show\(\): (\d+)ms" | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        $configLoad = ($logContent | Select-String "\[STARTUP\] Config loading: (\d+)ms" | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        $dirGuard = ($logContent | Select-String "\[STARTUP\] DirectoryInstanceGuard setup: (\d+)ms" | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        $loadSequence = ($logContent | Select-String "\[LOADSEQUENCE\] ========== TOTAL LOADSEQUENCE TIME: (\d+)ms" | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        $displayCurrent = ($logContent | Select-String "\[DISPLAYCURRENT\] ========== TOTAL DISPLAYCURRENT TIME \(LOADED\): (\d+)ms" | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        $bitmapDecode = ($logContent | Select-String "\[IMAGECACHE\] BitmapDecoder\.Create: (\d+)ms" | Select-Object -First 1 | ForEach-Object { $_.Matches.Groups[1].Value }) -as [int]
        
        $result = [PSCustomObject]@{
            TestNumber = $testNumber
            FileName = $fileName
            SizeMB = $sizeMB
            TotalStartup = $totalStartup
            InitializeComponent = $initComponent
            WindowShow = $windowShow
            ConfigLoad = $configLoad
            DirGuard = $dirGuard
            LoadSequence = $loadSequence
            DisplayCurrent = $displayCurrent
            BitmapDecode = $bitmapDecode
        }
        
        $results += $result
        
        Write-Host "  Total Startup: ${totalStartup}ms" -ForegroundColor Green
        Write-Host "  - InitializeComponent: ${initComponent}ms" -ForegroundColor Gray
        Write-Host "  - window.Show(): ${windowShow}ms" -ForegroundColor Gray
        Write-Host "  - BitmapDecoder: ${bitmapDecode}ms" -ForegroundColor Gray
        Write-Host ""
    } else {
        Write-Host "  ERROR: No log data captured" -ForegroundColor Red
        Write-Host ""
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "AGGREGATE RESULTS" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Calculate statistics
$totalStartups = $results | Where-Object { $_.TotalStartup -ne $null } | Select-Object -ExpandProperty TotalStartup
$avgTotal = [math]::Round(($totalStartups | Measure-Object -Average).Average, 1)
$minTotal = ($totalStartups | Measure-Object -Minimum).Minimum
$maxTotal = ($totalStartups | Measure-Object -Maximum).Maximum
$medianTotal = ($totalStartups | Sort-Object)[[math]::Floor($totalStartups.Count / 2)]

Write-Host "Total Startup Time:" -ForegroundColor Yellow
Write-Host "  Average: ${avgTotal}ms" -ForegroundColor White
Write-Host "  Median:  ${medianTotal}ms" -ForegroundColor White
Write-Host "  Min:     ${minTotal}ms" -ForegroundColor Green
Write-Host "  Max:     ${maxTotal}ms" -ForegroundColor Red
Write-Host ""

# Component averages
$avgInit = [math]::Round(($results.InitializeComponent | Measure-Object -Average).Average, 1)
$avgShow = [math]::Round(($results.WindowShow | Measure-Object -Average).Average, 1)
$avgConfig = [math]::Round(($results.ConfigLoad | Measure-Object -Average).Average, 1)
$avgGuard = [math]::Round(($results.DirGuard | Measure-Object -Average).Average, 1)
$avgSeq = [math]::Round(($results.LoadSequence | Measure-Object -Average).Average, 1)
$avgDecode = [math]::Round(($results.BitmapDecode | Measure-Object -Average).Average, 1)

Write-Host "Component Averages:" -ForegroundColor Yellow
Write-Host "  InitializeComponent: ${avgInit}ms (XAML parsing)" -ForegroundColor White
Write-Host "  window.Show():       ${avgShow}ms (first render)" -ForegroundColor White
Write-Host "  Config loading:      ${avgConfig}ms" -ForegroundColor White
Write-Host "  DirectoryGuard:      ${avgGuard}ms" -ForegroundColor White
Write-Host "  LoadSequence:        ${avgSeq}ms" -ForegroundColor White
Write-Host "  BitmapDecoder:       ${avgDecode}ms (per image)" -ForegroundColor White
Write-Host ""

# Detailed table
Write-Host "Detailed Results:" -ForegroundColor Yellow
$results | Format-Table -Property TestNumber, @{Label='File Size';Expression={"$($_.SizeMB)MB"}}, @{Label='Total';Expression={"$($_.TotalStartup)ms"}}, @{Label='InitComp';Expression={"$($_.InitializeComponent)ms"}}, @{Label='Show';Expression={"$($_.WindowShow)ms"}}, @{Label='Decode';Expression={"$($_.BitmapDecode)ms"}} -AutoSize

# Export detailed results
$results | Export-Csv -Path "startup-test-results.csv" -NoTypeInformation
Write-Host "Detailed results exported to: startup-test-results.csv" -ForegroundColor Cyan
Write-Host ""

