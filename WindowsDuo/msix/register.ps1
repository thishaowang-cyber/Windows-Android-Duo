# Register the Windows Duo package so the process gains package identity, which is
# what Windows.Graphics.Capture requires.
#
# No certificate and no Windows SDK are needed: the package is registered from
# loose files (manifest + Assets in the build output folder, with WindowsDuo.exe
# and its DLLs already sitting beside them), and Developer Mode waives the
# signature requirement.
#
# Verified with Developer Mode OFF: the manifest itself is accepted by the
# deployment stack (it parses, resolves identity and indexes fine) and the only
# failure is the signing policy — HRESULT 0x80073CFF, "requires a Windows
# certificate or a related system policy ... source is Unsigned". Turning
# Developer Mode on is therefore the single remaining prerequisite.

$ErrorActionPreference = 'Stop'

$outDir = Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows10.0.22621.0'
$outDir = [System.IO.Path]::GetFullPath($outDir)
$manifest = Join-Path $outDir 'AppxManifest.xml'

Write-Host "package folder : $outDir"

if (-not (Test-Path $manifest)) {
    Write-Host "AppxManifest.xml not found - build the project first:" -ForegroundColor Red
    Write-Host "  dotnet build -c Release"
    exit 1
}

# 1. Developer Mode
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$dev = (Get-ItemProperty -Path $key -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($dev -ne 1) {
    Write-Host ""
    Write-Host "Developer Mode is OFF." -ForegroundColor Yellow
    Write-Host "Sparse package registration from loose files requires it. Enable with, in an elevated PowerShell:"
    Write-Host ""
    Write-Host "  New-Item -Path '$key' -Force | Out-Null"
    Write-Host "  Set-ItemProperty -Path '$key' -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord"
    Write-Host ""
    Write-Host "(or Settings > System > For developers > Developer Mode)"
    exit 2
}
Write-Host "developer mode : on"

# 2. Register / re-register
$existing = Get-AppxPackage -Name 'WindowsDuo.Sparse' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "removing previous registration..."
    Remove-AppxPackage -Package $existing.PackageFullName
}

Add-AppxPackage -Register $manifest
$pkg = Get-AppxPackage -Name 'WindowsDuo.Sparse'
Write-Host "registered     : $($pkg.PackageFullName)" -ForegroundColor Green
Write-Host ""
Write-Host "Next: run the WGC probe to see whether capture is now permitted:"
Write-Host "  & '$outDir\WindowsDuo.exe' --wgc-probe --seconds 3"
