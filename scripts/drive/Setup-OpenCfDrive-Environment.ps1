# Setup script: a local demo backend (a WebDAV server + rclone rcd), all
# rclone, no Docker. Call this before Start-OpenCfDrive.ps1 to try the drive
# without a real remote. With a real remote, run your own
# `rclone rcd --rc-serve` instead.
#
# What it does:
#   1. Finds rclone
#   2. Starts a WebDAV server (rclone serve webdav, user demo:demo) over
#      -ServerData, %LOCALAPPDATA%\OpenCfDrive\demo-server by default, so the
#      files on the "server" survive a re-run
#   3. Creates an rclone config for it, with the password obscured
#   4. Starts rclone rcd --rc-serve (no auth) on port 5572: the client's rclone
#   5. Verifies /operations/list responds with the configured remote
#   6. Writes no machine environment variables (the service keeps its config
#      in its state database)
#
# Both rclone processes keep running after the script ends. Run it again to
# replace them; other rclone processes on the machine are left alone.
#
# Usage (no elevation needed):
#   .\scripts\drive\Setup-OpenCfDrive-Environment.ps1

param(
    [string]$RemoteName = "e2e",
    [int]$RcPort = 5572,
    [int]$WebDavPort = 5573,
    [string]$ServerData = "$env:LOCALAPPDATA\OpenCfDrive\demo-server",
    [string]$TempRoot = "$env:TEMP\opencfdrive-setup"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== OpenCfDrive Environment Setup ===" -ForegroundColor Cyan

# --- 1. Check prerequisites --------------------------------------------------
# rclone on PATH, or rclone.exe in the repository root (this script lives in
# scripts\drive; a local rclone.exe there is git-ignored).
$repoRoot = Split-Path (Split-Path $PSScriptRoot)
$rclonePath = Join-Path $repoRoot "rclone.exe"
if (-not (Test-Path $rclonePath)) {
    $command = Get-Command rclone -ErrorAction SilentlyContinue
    if (-not $command) { throw "rclone not found on PATH or as $rclonePath. Install rclone or put rclone.exe there." }
    $rclonePath = $command.Source
}

New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
New-Item -ItemType Directory -Path $ServerData -Force | Out-Null
$ServerData = (Resolve-Path $ServerData).Path

# Replaces the rclone processes an earlier run of this script started (same
# data folder or config, same port); nothing else.
function Stop-PreviousRclone([string]$Marker, [string]$Address) {
    Get-CimInstance Win32_Process -Filter "Name = 'rclone.exe'" -ErrorAction SilentlyContinue | Where-Object {
        "$($_.CommandLine)" -like "*$Marker*" -and "$($_.CommandLine)" -like "*$Address*"
    } | ForEach-Object {
        Write-Host "Stopping the previous rclone (PID $($_.ProcessId))..."
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

# --- 2. Start the WebDAV server ----------------------------------------------
$webDavUrl = "http://127.0.0.1:$WebDavPort/"
Write-Host "Starting the WebDAV server over $ServerData..." -ForegroundColor Yellow
Stop-PreviousRclone -Marker $ServerData -Address "127.0.0.1:$WebDavPort"
Start-Process -FilePath $rclonePath -ArgumentList @(
    "serve", "webdav", "`"$ServerData`"",
    "--addr", "127.0.0.1:$WebDavPort",
    "--user", "demo", "--pass", "demo",
    "--log-file", "`"$(Join-Path $TempRoot 'webdav.log')`"",
    "--log-level", "INFO"
) -WindowStyle Hidden | Out-Null
Write-Host "WebDAV URL: $webDavUrl" -ForegroundColor Green

# --- 3. Create rclone config ------------------------------------------------
$rcloneConf = Join-Path $TempRoot "rclone.conf"
if (Test-Path $rcloneConf) { Remove-Item $rcloneConf -Force | Out-Null }

# rclone stores passwords obscured; a plain-text one fails with "couldn't
# decrypt password".
Write-Host "Creating rclone config '$RemoteName' -> $webDavUrl..." -ForegroundColor Yellow
$obscured = & $rclonePath "obscure" "demo" 2>$null
if (-not $obscured) { throw "rclone obscure failed" }
$confContent = @"
[$RemoteName]
type = webdav
url = $webDavUrl
user = demo
pass = $($obscured.Trim())
vendor = other
"@
[System.IO.File]::WriteAllText($rcloneConf, $confContent)
Write-Host "rclone config: $rcloneConf" -ForegroundColor Green

# --- 4. Start rclone rcd -----------------------------------------------------
Write-Host "Starting rclone rcd --rc-serve (port $RcPort, no auth)..." -ForegroundColor Yellow
$rcloneLog = Join-Path $TempRoot "rcd.log"
if (Test-Path $rcloneLog) { Clear-Content $rcloneLog -ErrorAction SilentlyContinue }
Stop-PreviousRclone -Marker $rcloneConf -Address "127.0.0.1:$RcPort"
Start-Process -FilePath $rclonePath -ArgumentList @(
    "--config", "`"$rcloneConf`"",
    "rcd",
    "--rc-addr", "127.0.0.1:$RcPort",
    "--rc-no-auth",
    "--rc-serve",
    "--log-file", "`"$rcloneLog`"",
    "--log-level", "INFO"
) -WindowStyle Hidden | Out-Null

# --- 5. Verify RC endpoint ---------------------------------------------------
# fs must be "<RemoteName>:" (with colon) to match rclone's remote spec. This
# also proves the WebDAV server answers.
$rcFs = $RemoteName + ":"
$rcUrl = "http://127.0.0.1:$RcPort"
$rcReady = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $body = [ordered]@{ fs = $rcFs; remote = "" } | ConvertTo-Json
        $resp = Invoke-WebRequest -Uri "$rcUrl/operations/list" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 5 -UseBasicParsing -ErrorAction SilentlyContinue
        if ($null -ne $resp -and $resp.StatusCode -eq 200) {
            $rcReady = $true
            break
        }
    } catch {
        if ($i % 10 -eq 0) {
            Write-Host "  Waiting for rclone RC... ($i): $($_.Exception.Message)" -ForegroundColor DarkGray
        }
    }
    Start-Sleep -Milliseconds 500
}

if (-not $rcReady) {
    $log = if (Test-Path $rcloneLog) { Get-Content $rcloneLog -Raw } else { "(no log)" }
    throw "rclone RC did not become ready at $rcUrl. Log: $log"
}

Write-Host "rclone RC ready at $rcUrl" -ForegroundColor Green
Write-Host ""
Write-Host "=== Environment values for Start-OpenCfDrive.ps1 ===" -ForegroundColor Cyan
Write-Host "  SyncRoot : $env:USERPROFILE\OpenCfDrive"
Write-Host "  Remote   : ${RemoteName}:"
Write-Host "  RcUrl    : $rcUrl"
Write-Host ""
Write-Host "Example:" -ForegroundColor Cyan
Write-Host "  .\scripts\drive\Start-OpenCfDrive.ps1 -SyncRoot '$env:USERPROFILE\OpenCfDrive' -Remote '${RemoteName}:' -RcUrl '$rcUrl'"
Write-Host ""
Write-Host "Then open Explorer to: $env:USERPROFILE\OpenCfDrive" -ForegroundColor White
Write-Host "WebDAV server: $webDavUrl, files in $ServerData (kept between runs)" -ForegroundColor DarkGray
Write-Host "rclone RC: $rcUrl (logs in $TempRoot)" -ForegroundColor DarkGray
