# OpenCfDrive Install Script
# Builds and publishes OpenCfDrive.Service, then creates the sync root directory.
#
# Prerequisites:
#   - Windows 10 version 2004 (build 19041) or later, Cloud Files API present
#   - .NET 10 SDK (dotnet on PATH)
#
# Usage (no elevation needed):
#   .\scripts\drive\Install-OpenCfDrive.ps1
#
# After installing, run:
#   1. .\scripts\drive\Setup-OpenCfDrive-Environment.ps1   (optional demo backend: Docker WebDAV + rclone RC)
#   2. .\scripts\drive\Start-OpenCfDrive.ps1              (launches the service)
#   3. Open Explorer to the sync root

param(
    # Repository root (this script lives in scripts\drive).
    [string]$OpenCfDriveRoot = (Split-Path (Split-Path $PSScriptRoot)),
    [string]$SyncRoot        = "$env:USERPROFILE\OpenCfDrive",
    [string]$Remote          = "e2e:",
    [string]$RcUrl           = "http://127.0.0.1:5572",
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Native tools (dotnet) write progress/notices to stderr; under
# $ErrorActionPreference='Stop' Windows PowerShell turns that into a terminating
# NativeCommandError, so relax it for the call and judge by $LASTEXITCODE.
function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) {
        Write-Host (($output | Out-String).Trim()) -ForegroundColor DarkGray
    }
    return $exitCode
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
$slnx = Join-Path $OpenCfDriveRoot "OpenCfDrive.slnx"
$serviceProj = Join-Path $OpenCfDriveRoot "OpenCfDrive.Service\OpenCfDrive.Service.csproj"
$publishDir = Join-Path $OpenCfDriveRoot "publish"

Write-Host "=== OpenCfDrive Installer ===" -ForegroundColor Cyan
Write-Host "Sync root : $SyncRoot"
Write-Host "Remote    : $Remote"
Write-Host "Rclone RC : $RcUrl"
Write-Host ""

# --- 1. Build ---------------------------------------------------------------
if (-not $SkipBuild) {
    if (-not (Test-Path $dotnet)) {
        throw ".NET 10 SDK not found (dotnet is not on PATH)."
    }

    Write-Host "Building OpenCfDrive..." -ForegroundColor Yellow
    $buildExit = Invoke-Native $dotnet @("build", $slnx, "-nologo", "-c", "Release")
    if ($buildExit -ne 0) {
        throw "Build failed (exit $buildExit)"
    }

    Write-Host "Publishing service to $publishDir..." -ForegroundColor Yellow
    $publishExit = Invoke-Native $dotnet @("publish", $serviceProj, "-c", "Release", "-o", $publishDir, "--self-contained", "false", "-nologo")
    if ($publishExit -ne 0) {
        throw "Publish failed (exit $publishExit)"
    }
}

# --- 2. Create sync root directory ------------------------------------------
Write-Host "Creating sync root $SyncRoot..." -ForegroundColor Yellow
if (-not (Test-Path $SyncRoot)) {
    New-Item -ItemType Directory -Path $SyncRoot -Force | Out-Null
}

Write-Host "Installed." -ForegroundColor Green
Write-Host ""
Write-Host "State database location: %LOCALAPPDATA%\OpenCfDrive\state\opencfdrive-<hash>.db" -ForegroundColor DarkGray
Write-Host "(Engine state never lives inside the sync root; that folder is user data.)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Start Docker and run: .\scripts\drive\Setup-OpenCfDrive-Environment.ps1"
Write-Host "  2. Run: .\scripts\drive\Start-OpenCfDrive.ps1 -SyncRoot `"$SyncRoot`""
Write-Host "  3. Open Explorer to: $SyncRoot"
Write-Host ""
Write-Host "To uninstall, run: .\scripts\drive\Uninstall-OpenCfDrive.ps1"
