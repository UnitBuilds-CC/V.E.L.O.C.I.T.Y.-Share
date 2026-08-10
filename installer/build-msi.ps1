#!/usr/bin/env pwsh
# build-msi.ps1 — Build VelocityShare MSI installer
# Usage: .\build-msi.ps1 [-Version 1.0.0] [-Configuration Release]

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path $PSScriptRoot -Parent
$installerDir = $PSScriptRoot
$publishDir = Join-Path $rootDir "publish\VelocityShare.Server"
$firewallExt = "$env:USERPROFILE\.nuget\packages\wixtoolset.firewall.wixext\5.0.2\wixext5\WixToolset.Firewall.wixext.dll"

Write-Host "=== VelocityShare MSI Builder ===" -ForegroundColor Cyan

# Step 1: Publish the server application
if (-not $SkipPublish) {
    Write-Host "`n[1/4] Publishing server (self-contained, win-x64)..." -ForegroundColor Yellow
    dotnet publish (Join-Path $rootDir "VelocityShare.Server\VelocityShare.Server.csproj") `
        -c $Configuration -r win-x64 --self-contained -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    Write-Host "  Published to $publishDir" -ForegroundColor Green
} else {
    Write-Host "`n[1/4] Skipping publish (using existing output)" -ForegroundColor DarkGray
}

# Step 2: Generate harvested file list
Write-Host "`n[2/4] Generating file manifest..." -ForegroundColor Yellow
& powershell -ExecutionPolicy Bypass -File (Join-Path $installerDir "generate-harvest.ps1") `
    -PublishDir $publishDir -OutputFile (Join-Path $installerDir "HarvestedFiles.wxs")
if ($LASTEXITCODE -ne 0) { throw "Harvest generation failed" }
Write-Host "  Generated HarvestedFiles.wxs" -ForegroundColor Green

# Step 3: Restore WiX extensions
Write-Host "`n[3/4] Checking WiX extensions..." -ForegroundColor Yellow
if (-not (Test-Path $firewallExt)) {
    Write-Host "  Restoring NuGet packages for WiX extensions..." -ForegroundColor Yellow
    dotnet restore (Join-Path $installerDir "VelocityShare.wixproj")
}
Write-Host "  Extensions ready" -ForegroundColor Green

# Step 4: Build MSI
Write-Host "`n[4/4] Building MSI package..." -ForegroundColor Yellow
$outputMsi = Join-Path $installerDir "VelocityShare.msi"
wix build `
    (Join-Path $installerDir "Package.wxs") `
    (Join-Path $installerDir "HarvestedFiles.wxs") `
    -arch x64 `
    -o $outputMsi `
    -ext $firewallExt `
    -dcl high `
    -d "var.Version=$Version"

if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$msiSize = [math]::Round((Get-Item $outputMsi).Length / 1MB, 2)
Write-Host "`n=== Build Complete ===" -ForegroundColor Cyan
Write-Host "  Output: $outputMsi" -ForegroundColor Green
Write-Host "  Size:   ${msiSize} MB" -ForegroundColor Green
