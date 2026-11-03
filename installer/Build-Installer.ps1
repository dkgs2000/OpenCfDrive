# Builds the 64-bit installers into .\artifacts (or -OutputDir):
#   OpenCfDrive-<version>-x64.msi        the Windows Installer package
#   OpenCfDrive-<version>-x64-setup.exe  a bootstrapper that installs the MSI
#
# The app (OpenCfDrive.exe) and the service (OpenCfDrive.Service.exe) are each
# published self-contained (no .NET install needed) as a single file, then
# packaged with WiX v5, which the build restores from NuGet.
# Both install per user into %LOCALAPPDATA%\Programs\OpenCfDrive, with no
# administrator rights.
#
# Usage:
#   .\installer\Build-Installer.ps1 -Version 1.2.3 [-Repository owner/name]
#
# -Repository is the GitHub repository whose releases the app updates itself
# from (the release workflow passes its own). Without it, the app does not
# check for updates.

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [ValidatePattern('^$|^[\w.-]+/[\w.-]+$')]
    [string]$Repository = "",
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

foreach ($project in "OpenCfDrive.Service", "OpenCfDrive.App") {
    Write-Host "Publishing $project $Version (win-x64, self-contained, single file)..." -ForegroundColor Cyan
    Invoke-Dotnet @(
        "publish", (Join-Path $repoRoot "$project\$project.csproj"),
        "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        # Each exe carries its own .NET; compressed, the two take about 100 MB
        # on disk instead of about 240, for a fraction of a second at start.
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None", "-p:DebugSymbols=false",
        "-p:Version=$Version", "-p:UpdateRepository=$Repository", "-o", $publishDir, "-nologo"
    )
}

# Not incremental: WiX's up-to-date check goes by the files the last build
# bound, so after building another version it skips compiling and then
# finds no installer under the new name.
Write-Host "Building the MSI..." -ForegroundColor Cyan
Invoke-Dotnet @(
    "build", (Join-Path $PSScriptRoot "msi\OpenCfDrive.Msi.wixproj"), "--no-incremental",
    "-c", "Release", "-p:Version=$Version", "-p:PublishDir=$publishDir\", "-o", $OutputDir, "-nologo"
)
$msi = Join-Path $OutputDir "OpenCfDrive-$Version-x64.msi"
if (-not (Test-Path $msi)) { throw "The MSI was not built: $msi" }

Write-Host "Building the .exe installer..." -ForegroundColor Cyan
Invoke-Dotnet @(
    "build", (Join-Path $PSScriptRoot "bundle\OpenCfDrive.Bundle.wixproj"), "--no-incremental",
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
