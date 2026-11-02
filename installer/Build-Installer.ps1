# Builds the 64-bit installers into .\artifacts (or -OutputDir):
#   OpenCfDrive-<version>-x64.msi        the Windows Installer package
#   OpenCfDrive-<version>-x64-setup.exe  a bootstrapper that installs the MSI
#
# The service is published self-contained (no .NET install needed) as a
# single file, then packaged with WiX v5, which the build restores from NuGet.
# Both install per user into %LOCALAPPDATA%\Programs\OpenCfDrive, with no
# administrator rights.
#
# Usage:
#   .\installer\Build-Installer.ps1 -Version 1.2.3

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$OutputDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $repoRoot "artifacts" }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$publishDir = Join-Path $OutputDir "publish"

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE)." }
}

Remove-Item -LiteralPath $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "Publishing OpenCfDrive.Service $Version (win-x64, self-contained, single file)..." -ForegroundColor Cyan
Invoke-Dotnet @(
    "publish", (Join-Path $repoRoot "OpenCfDrive.Service\OpenCfDrive.Service.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None", "-p:DebugSymbols=false",
    "-p:Version=$Version", "-o", $publishDir, "-nologo"
)

Write-Host "Building the MSI..." -ForegroundColor Cyan
Invoke-Dotnet @(
    "build", (Join-Path $PSScriptRoot "msi\OpenCfDrive.Msi.wixproj"),
    "-c", "Release", "-p:Version=$Version", "-p:PublishDir=$publishDir\", "-o", $OutputDir, "-nologo"
)
$msi = Join-Path $OutputDir "OpenCfDrive-$Version-x64.msi"
if (-not (Test-Path $msi)) { throw "The MSI was not built: $msi" }

Write-Host "Building the .exe installer..." -ForegroundColor Cyan
Invoke-Dotnet @(
    "build", (Join-Path $PSScriptRoot "bundle\OpenCfDrive.Bundle.wixproj"),
    "-c", "Release", "-p:Version=$Version", "-p:MsiPath=$msi", "-o", $OutputDir, "-nologo"
)
$exe = Join-Path $OutputDir "OpenCfDrive-$Version-x64-setup.exe"
if (-not (Test-Path $exe)) { throw "The .exe installer was not built: $exe" }

# Only the two installers are the result; drop the publish folder and the
# build's intermediate files (.wixpdb, cab).
Remove-Item -LiteralPath $publishDir -Recurse -Force
Get-ChildItem -LiteralPath $OutputDir -File | Where-Object { $_.FullName -notin @($msi, $exe) } | Remove-Item -Force

Write-Host "Built:" -ForegroundColor Green
Get-ChildItem -LiteralPath $OutputDir -File | ForEach-Object { Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
