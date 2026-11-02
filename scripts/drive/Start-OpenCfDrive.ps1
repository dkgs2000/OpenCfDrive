# Starts the OpenCfDrive service process in the background so Explorer sees
# the registered sync root and placeholders.
#
# The service is launched as a plain process (not a Windows service). Env vars
# are passed to the process only (Process scope) - nothing is written to the
# machine. State lives in %LOCALAPPDATA%\OpenCfDrive\state\, never in the sync root.
#
# Prerequisites (run first):
#   .\scripts\drive\Install-OpenCfDrive.ps1             (build + publish)
#   .\scripts\drive\Setup-OpenCfDrive-Environment.ps1   (optional demo backend), or your own
#                                         `rclone rcd --rc-serve` with your remote
#
# Usage (as the user who owns the drive; no elevation needed):
#   .\scripts\drive\Start-OpenCfDrive.ps1 -Remote "myremote:" -RcUrl "http://127.0.0.1:5572"

param(
    [string]$SyncRoot = "$env:USERPROFILE\OpenCfDrive",
    [string]$Remote  = "e2e:",
    [string]$RcUrl   = "http://127.0.0.1:5572",
    # <repository root>\publish (this script lives in scripts\drive).
    [string]$PublishDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) "publish"),
    [string]$LogDir = "$env:TEMP\opencfdrive-service",
    [int]$ReadyTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== OpenCfDrive Service Starter ===" -ForegroundColor Cyan
Write-Host "  SyncRoot : $SyncRoot"
Write-Host "  Remote   : $Remote"
Write-Host "  RcUrl    : $RcUrl"
Write-Host ""

# --- 1. Ensure service binary exists -----------------------------------------
$serviceExe = Join-Path $PublishDir "OpenCfDrive.Service.exe"
if (-not (Test-Path $serviceExe)) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
    $proj  = Join-Path (Split-Path (Split-Path $PSScriptRoot)) "OpenCfDrive.Service\OpenCfDrive.Service.csproj"
    Write-Host "Publishing service (binary not found)..." -ForegroundColor Yellow
    & $dotnet publish $proj -c Release -o $PublishDir --self-contained false -nologo 2>&1 | Out-String -Width 200
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed"
    }
    if (-not (Test-Path $serviceExe)) {
        throw "Service executable still missing at $serviceExe"
    }
}

# --- 2. Verify rclone RC is reachable ----------------------------------------
Write-Host "Verifying rclone RC at $RcUrl..." -ForegroundColor Yellow
$rcFs = $Remote
$body = @{ fs = $rcFs; remote = ""; opt = @{ recurse = $true } } | ConvertTo-Json -Depth 5
try {
    $resp = Invoke-WebRequest -Uri "$RcUrl/operations/list" -Method Post `
        -ContentType "application/json" -Body $body -TimeoutSec 10 -UseBasicParsing
    if ($resp.StatusCode -ne 200) {
        throw "rclone RC returned HTTP $($resp.StatusCode)"
    }
    if ($resp.Content -match '"error"') {
        throw "rclone RC returned an error: $($resp.Content)"
    }
    Write-Host "rclone RC reachable." -ForegroundColor Green
}
catch {
    throw "Cannot reach rclone RC at $RcUrl with remote '$rcFs'. Run .\scripts\drive\Setup-OpenCfDrive-Environment.ps1 first. Error: $($_.Exception.Message)"
}

# --- 3. Create sync root -----------------------------------------------------
if (-not (Test-Path $SyncRoot)) {
    New-Item -ItemType Directory -Path $SyncRoot -Force | Out-Null
}

# --- 4. Prepare log directory ------------------------------------------------
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
$serviceLog = Join-Path $LogDir "service.log"
$serviceErr = Join-Path $LogDir "service.err"
Remove-Item $serviceLog, $serviceErr -Force -ErrorAction SilentlyContinue

# --- 5. Kill any stale service process ---------------------------------------
$stale = Get-Process -Name "OpenCfDrive.Service" -ErrorAction SilentlyContinue
if ($stale) {
    $stale | ForEach-Object {
        Write-Host "Killing stale service PID $($_.Id)..." -ForegroundColor DarkGray
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 800
}

# --- 6. Launch the service ---------------------------------------------------
Write-Host "Starting OpenCfDrive service..." -ForegroundColor Yellow

# Process-scope env vars only: they apply to this child and its children.
$previousRoot = $env:OPENCFDRIVE_ROOT
$previousFs   = $env:RCLONE_FS
$previousRc   = $env:RCLONE_RC_URL
try {
    $env:OPENCFDRIVE_ROOT = $SyncRoot
    $env:RCLONE_FS = $Remote
    $env:RCLONE_RC_URL = $RcUrl

    $proc = Start-Process -FilePath $serviceExe -WorkingDirectory $SyncRoot `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $serviceLog -RedirectStandardError $serviceErr
}
finally {
    $env:OPENCFDRIVE_ROOT = $previousRoot
    $env:RCLONE_FS = $previousFs
    $env:RCLONE_RC_URL = $previousRc
}

Write-Host "Started PID $($proc.Id)." -ForegroundColor Green

# --- 7. Wait for service to be ready -----------------------------------------
$ready = $false
$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) {
        $err = Get-Content $serviceErr -Raw -ErrorAction SilentlyContinue
        $out = Get-Content $serviceLog -Raw -ErrorAction SilentlyContinue
        throw "Service exited early (exit $($proc.ExitCode)).`nOut: $out`nErr: $err"
    }
    $log = Get-Content $serviceLog -Raw -ErrorAction SilentlyContinue
    if ($log -match "Mode: (cloud|simulation)") { $ready = $true; break }
    if ($log -match "initialisation failed") {
        throw "Service failed to initialise. Log: $serviceLog"
    }
    Start-Sleep -Milliseconds 500
}

if (-not $ready) {
    throw "Service did not report ready within $ReadyTimeoutSeconds s. Log: $serviceLog"
}

# Show the mode line so the operator sees cloud vs simulation
$modeLine = (Get-Content $serviceLog | Select-String -Pattern "Mode: " | Select-Object -Last 1)
if ($modeLine) { Write-Host $modeLine.ToString().Trim() -ForegroundColor Green }

Write-Host ""
Write-Host "Service is running. Open Explorer to: $SyncRoot" -ForegroundColor Green
Write-Host "Service log: $serviceLog" -ForegroundColor DarkGray
Write-Host "Service err: $serviceErr" -ForegroundColor DarkGray
Write-Host ""
Write-Host "To stop: Stop-Process -Id $($proc.Id)" -ForegroundColor Cyan
Write-Host "To unregister: .\scripts\drive\Uninstall-OpenCfDrive.ps1 -SyncRoot `"$SyncRoot`"" -ForegroundColor Cyan
