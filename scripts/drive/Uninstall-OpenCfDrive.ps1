# OpenCfDrive Uninstall Script
# Stops the service process and unregisters the sync root (driver registration,
# Explorer entry, and any online-only placeholders left in the folder).
#
# Does NOT delete the sync root directory (that is user data).
# Does NOT write or remove machine environment variables (none were set there).
#
# Usage (no elevation needed):
#   .\scripts\drive\Uninstall-OpenCfDrive.ps1 -SyncRoot "C:\path\to\sync\root"

param(
    [string]$SyncRoot     = "$env:USERPROFILE\OpenCfDrive",
    # <repository root>\publish (this script lives in scripts\drive).
    [string]$PublishDir   = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) "publish"),
    [switch]$RemovePublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

Write-Host "=== OpenCfDrive Uninstaller ===" -ForegroundColor Cyan

# --- 1. Stop the service process --------------------------------------------
$stopped = Get-Process -Name "OpenCfDrive.Service" -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Stopping PID $($_.Id)..." -ForegroundColor Yellow
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        $true
    }
if ($stopped) {
    Start-Sleep -Seconds 1
} else {
    Write-Host "No OpenCfDrive.Service process running." -ForegroundColor DarkGray
}

# --- 2. Unregister the sync root via cldapi.dll -----------------------------
$serviceExe = Join-Path $PublishDir "OpenCfDrive.Service.exe"
if (Test-Path $serviceExe) {
    Write-Host "Unregistering sync root $SyncRoot..." -ForegroundColor Yellow
    $env:OPENCFDRIVE_ROOT = $SyncRoot
    $result = & $serviceExe --unregister 2>&1 | Out-String -Width 200
    Write-Host $result
    Remove-Item Env:\OPENCFDRIVE_ROOT -ErrorAction SilentlyContinue
}
else {
    Write-Host "Service binary not published; skipping the unregister." -ForegroundColor Yellow
    Write-Host "If the sync root is still registered, access to it will fail until the provider re-registers." -ForegroundColor DarkGray
}

# --- 3. Optionally remove published output ----------------------------------
if ($RemovePublish -and (Test-Path $PublishDir)) {
    Write-Host "Removing published output..." -ForegroundColor Yellow
    Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 4. Summary -------------------------------------------------------------
Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
Write-Host "The sync root ($SyncRoot) was NOT deleted - it contains your data." -ForegroundColor DarkGray
Write-Host "State DB remains at %LOCALAPPDATA%\OpenCfDrive\state\ (not in the sync root)." -ForegroundColor DarkGray
Write-Host "Sign out or restart Explorer (explorer.exe) for the namespace to fully disappear." -ForegroundColor DarkGray
