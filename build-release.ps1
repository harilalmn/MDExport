#Requires -Version 5.1
<#
Builds MDExport in Release mode and assembles a Release/ folder
containing the MSI installer and README, ready to ship.

Usage:
    pwsh ./build-release.ps1
    powershell -ExecutionPolicy Bypass -File ./build-release.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root         = $PSScriptRoot
$appProj      = Join-Path $root 'MDExport\MDExport.csproj'
$installerProj= Join-Path $root 'MDExport.Installer\MDExport.Installer.wixproj'
$msiOut       = Join-Path $root 'MDExport.Installer\bin\x64\Release'
$releaseDir   = Join-Path $root 'Release'
$readme       = Join-Path $root 'README.md'

Write-Host "==> Publishing MDExport (Release / win-x64)" -ForegroundColor Cyan
dotnet publish $appProj -c Release -r win-x64 --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> Building MSI installer" -ForegroundColor Cyan
dotnet build $installerProj -c Release
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

Write-Host "==> Assembling Release folder" -ForegroundColor Cyan
if (Test-Path $releaseDir) {
    Remove-Item -Recurse -Force $releaseDir
}
New-Item -ItemType Directory -Path $releaseDir | Out-Null

$msi = Get-ChildItem -Path $msiOut -Filter '*.msi' | Select-Object -First 1
if (-not $msi) { throw "no MSI found in $msiOut" }
Copy-Item $msi.FullName $releaseDir
Copy-Item $readme $releaseDir

Write-Host ""
Write-Host "Release contents:" -ForegroundColor Green
Get-ChildItem $releaseDir | Format-Table Name, Length -AutoSize
Write-Host "Done. Output: $releaseDir" -ForegroundColor Green
