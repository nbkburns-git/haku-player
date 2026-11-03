# haku_sr.dll tests (no mpv, no app): colours (BT.709, NV12), the padded-texture edge, and the AI path's colours and speed
# for each tier at 1920x1080 and 1440x1080, with how much of each frame is the model itself.
# Needs native\haku_sr\out\hsr_test.exe (native\haku_sr\build.cmd) and the models in %LOCALAPPDATA%\Haku Upscaler\models.
param([string]$Out = (Join-Path $env:TEMP "haku-tests\engines"))
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) "native\haku_sr\out\hsr_test.exe"
$models = Join-Path $env:LOCALAPPDATA "Haku Upscaler\models"
New-Item -ItemType Directory -Force $Out | Out-Null
$failed = 0
foreach ($tier in @(@('Fast', 'SuperUltraCompact'), @('Balanced', 'UltraCompact'), @('Quality', 'Compact'))) {
    $onnx = Join-Path $models "2x_AnimeJaNai_HD_V3Sharp1_$($tier[1])_fp16.onnx"
    if (-not (Test-Path $onnx)) { Write-Host "SKIP  $($tier[0]): no model"; continue }
    $lines = & $exe $onnx $Out
    $ok = $LASTEXITCODE -eq 0 -and ($lines -match 'all checks passed')
    if (-not $ok) { $failed++ }
    Write-Host ("{0,-5} {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $tier[0]) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    $lines | Where-Object { $_ -match 'ms|padded|FAIL' } | ForEach-Object { "      $_" }
}
exit $failed
