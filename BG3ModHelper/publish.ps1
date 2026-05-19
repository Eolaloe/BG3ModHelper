# BG3MM_UpdateHelper Release Build Script
#
# Usage:
#   .\publish.ps1                    # Framework-dependent single exe (~5MB)
#                                    #   - User needs .NET 8 Desktop Runtime installed
#                                    #
#   .\publish.ps1 -SelfContained     # Self-contained single exe (~150MB)
#                                    #   - No .NET install needed on user PC
#                                    #   - Recommended for public release
#
# Output:
#   publish\                                   <- raw publish output
#   _<timestamp>_<mode>_BG3MM_UpdateHelper.zip <- release-ready zip

param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$projectDir   = "BG3MM_UpdateHelper"
$publishDir   = "publish"
$timestamp    = Get-Date -Format "yyMMddTHHmm"
$mode         = if ($SelfContained) { "selfcontained" } else { "fxdep" }
$zipName      = "_${timestamp}_${mode}_BG3MM_UpdateHelper.zip"
$configuration = "Release"

Write-Host "==> Cleaning previous output..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipName)    { Remove-Item $zipName -Force }

Write-Host "==> Running dotnet publish ($mode)..."
$publishArgs = @(
    "publish", $projectDir,
    "-c", $configuration,
    "-r", "win-x64",
    "-o", $publishDir,
    "-p:PublishSingleFile=true"
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true", "-p:IncludeNativeLibrariesForSelfExtract=true")
} else {
    $publishArgs += @("--self-contained", "false")
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed"
    exit 1
}

Write-Host "==> Removing developer-only artifacts..."
$excludePatterns = @(
    "*.pdb",
    "*.xml",
    "*.csproj.user",
    "*.deps.json.bak"
)
foreach ($pattern in $excludePatterns) {
    Get-ChildItem $publishDir -Recurse -Include $pattern -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Write-Host "==> Creating zip: $zipName"
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipName -Force

$zipSize = (Get-Item $zipName).Length / 1MB
Write-Host ""
Write-Host "============================================"
Write-Host " Build complete"
Write-Host " Mode:  $mode"
Write-Host " File:  $zipName"
Write-Host " Size:  $([math]::Round($zipSize, 2)) MB"
Write-Host "============================================"

if (-not $SelfContained) {
    Write-Host ""
    Write-Host "Note: Users must install .NET 8 Desktop Runtime to run this build."
    Write-Host "      For public release without dependency, use: .\publish.ps1 -SelfContained"
}
