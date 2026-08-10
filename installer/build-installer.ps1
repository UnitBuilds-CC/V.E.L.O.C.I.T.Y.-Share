#!/usr/bin/env pwsh
# build-installer.ps1 — Build VelocityShare installer (Inno Setup EXE and/or WiX MSI)
# Usage: .\build-installer.ps1 [-Version 1.0.0] [-Format exe|msi|both]

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [ValidateSet("exe", "msi", "both")]
    [string]$Format = "exe",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path $PSScriptRoot -Parent
$installerDir = $PSScriptRoot
$publishDir = Join-Path $rootDir "publish\VelocityShare.Server"
$iscc = "C:\Program Files\Inno Setup 7\ISCC.exe"
$firewallExt = "$env:USERPROFILE\.nuget\packages\wixtoolset.firewall.wixext\5.0.2\wixext5\WixToolset.Firewall.wixext.dll"

Write-Host "=== VelocityShare Installer Builder ===" -ForegroundColor Cyan
Write-Host "  Format: $Format | Version: $Version" -ForegroundColor DarkGray

# Step 1: Publish the server application
if (-not $SkipPublish) {
    Write-Host "`n[1/3] Publishing server (self-contained, win-x64)..." -ForegroundColor Yellow
    dotnet publish (Join-Path $rootDir "VelocityShare.Server\VelocityShare.Server.csproj") `
        -c $Configuration -r win-x64 --self-contained -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    $fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
    Write-Host "  Published $fileCount files to $publishDir" -ForegroundColor Green
} else {
    Write-Host "`n[1/3] Skipping publish (using existing output)" -ForegroundColor DarkGray
}

# Build Inno Setup EXE
if ($Format -eq "exe" -or $Format -eq "both") {
    Write-Host "`n[2/3] Building Inno Setup EXE..." -ForegroundColor Yellow
    if (-not (Test-Path $iscc)) { throw "Inno Setup 7 not found at $iscc" }

    # Update version in .iss file
    $issPath = Join-Path $installerDir "VelocityShare.iss"
    $issContent = Get-Content $issPath -Raw
    $issContent = $issContent -replace '#define MyAppVersion "[^"]*"', "#define MyAppVersion `"$Version`""
    $issContent = $issContent -replace 'OutputBaseFilename=[^\r\n]*', "OutputBaseFilename=VelocityShare-$Version-Setup"
    Set-Content $issPath $issContent -NoNewline

    & $iscc $issPath
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup build failed" }

    $exePath = Join-Path $installerDir "Output\VelocityShare-$Version-Setup.exe"
    $exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host "  Built: $exePath (${exeSize} MB)" -ForegroundColor Green
}

# Build WiX MSI
if ($Format -eq "msi" -or $Format -eq "both") {
    Write-Host "`n[3/3] Building WiX MSI..." -ForegroundColor Yellow

    # Generate harvested file list
    & powershell -ExecutionPolicy Bypass -File (Join-Path $installerDir "generate-harvest.ps1") `
        -PublishDir $publishDir -OutputFile (Join-Path $installerDir "HarvestedFiles.wxs")
    if ($LASTEXITCODE -ne 0) { throw "Harvest generation failed" }

    if (-not (Test-Path $firewallExt)) {
        dotnet restore (Join-Path $installerDir "VelocityShare.wixproj")
    }

    $msiPath = Join-Path $installerDir "VelocityShare.msi"
    wix build `
        (Join-Path $installerDir "Package.wxs") `
        (Join-Path $installerDir "HarvestedFiles.wxs") `
        -arch x64 -o $msiPath -ext $firewallExt -dcl high
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

    $msiSize = [math]::Round((Get-Item $msiPath).Length / 1MB, 2)
    Write-Host "  Built: $msiPath (${msiSize} MB)" -ForegroundColor Green
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Cyan
