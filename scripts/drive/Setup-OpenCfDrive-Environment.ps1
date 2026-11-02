# Setup script: a local demo backend (Docker WebDAV server + rclone rcd).
# Call this before Start-OpenCfDrive.ps1 to try the drive without a real
# remote. With a real remote, run your own `rclone rcd --rc-serve` instead.
#
# What it does:
#   1. Verifies docker / rclone present
#   2. Starts bytemark/webdav container (demo:demo) backed by the named volume
#      opencfdrive-webdav-data, so remote files survive a re-run
#   3. Creates rclone config via `rclone obscure` (properly obscured password)
#   4. Starts rclone rcd --rc-serve (no auth) on port 5572
#   5. Verifies /operations/list responds with the configured remote
#   6. (No machine env writes; config lives in SQLite DB)
#
# Usage (no elevation needed):
#   .\scripts\drive\Setup-OpenCfDrive-Environment.ps1

param(
    [string]$WebDavImage = "bytemark/webdav",
    [string]$RemoteName = "e2e",
    [int]$RcPort = 5572,
    [string]$TempRoot = "$env:TEMP\opencfdrive-setup"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== OpenCfDrive Environment Setup ===" -ForegroundColor Cyan

# --- 1. Check prerequisites --------------------------------------------------
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker not found on PATH." }
# rclone on PATH, or rclone.exe in the repository root (this script lives in
# scripts\drive; a local rclone.exe there is git-ignored).
$repoRoot = Split-Path (Split-Path $PSScriptRoot)
if (-not (Get-Command rclone -ErrorAction SilentlyContinue)) {
    $localRclone = Join-Path $repoRoot "rclone.exe"
    if (Test-Path $localRclone) {
        $env:PATH += ";" + $repoRoot
    } else {
        throw "rclone not found on PATH or as $localRclone. Install rclone or put rclone.exe there."
    }
}


# --- 2. Start WebDAV container -----------------------------------------------
$containerName = "opencfdrive-webdav"
$volumeName = "opencfdrive-webdav-data"
Write-Host "Starting WebDAV container '$containerName' (image: $WebDavImage)..." -ForegroundColor Yellow

# The remote content lives in a named volume, so re-running this script (or
# recreating the container) never discards files the sync root uploaded.
docker volume create $volumeName 2>&1 | Out-Null
docker rm -f $containerName 2>&1 | Out-Null

# The WebDAV server runs as www-data; make sure the volume is writable by it
# before the server starts (a fresh volume is owned by root).
docker run --rm -v "${volumeName}:/data" --entrypoint chown $WebDavImage -R www-data:www-data /data 2>&1 | Out-Null

docker run -d --name $containerName -p "127.0.0.1::80" `
    -v "${volumeName}:/var/lib/dav/data" `
    -e USERNAME=demo -e PASSWORD=demo `
    $WebDavImage 2>&1 | Out-Null

# Wait for mapped port
$mappedPort = $null
for ($i = 0; $i -lt 60; $i++) {
    $portInfo = docker port $containerName 80/tcp 2>$null
    if ($portInfo -match ":(\d+)$") { $mappedPort = $matches[1]; break }
    Start-Sleep -Milliseconds 500
}

if (-not $mappedPort) { throw "WebDAV container did not expose port 80." }
$webDavUrl = "http://127.0.0.1:$mappedPort/"
Write-Host "WebDAV URL: $webDavUrl" -ForegroundColor Green

# --- 3. Create rclone config ------------------------------------------------
if (-not (Test-Path $TempRoot)) { New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null }
$rcloneConf = Join-Path $TempRoot "rclone.conf"

# Remove any stale config so rclone reads the fresh one below
if (Test-Path $rcloneConf) { Remove-Item $rcloneConf -Force | Out-Null }

# Create rclone config manually with an obscured password (like `rclone config create`
# would produce). Plain-text passwords fail with "couldn't decrypt password".
Write-Host "Creating rclone config '$RemoteName' -> $webDavUrl..." -ForegroundColor Yellow
$rclonePath = (Get-Command rclone).Source
if (-not $rclonePath) { $rclonePath = Join-Path $repoRoot "rclone.exe" }

# Obscure the password (rclone stores passwords in obscured form)
$obscured = & $rclonePath "obscure" "demo" 2>$null
if (-not $obscured) { throw "rclone obscure failed" }
$obscured = $obscured.Trim()

# Write config file
$confContent = @"
[$RemoteName]
type = webdav
url = $webDavUrl
user = demo
pass = $obscured
vendor = other
"@
[System.IO.File]::WriteAllText($rcloneConf, $confContent)

if (-not (Test-Path $rcloneConf)) {
    throw "rclone config file was not created at $rcloneConf"
}
Write-Host "rclone config: $rcloneConf" -ForegroundColor Green

# --- 4. Start rclone rcd -----------------------------------------------------
Write-Host "Starting rclone rcd --rc-serve (port $RcPort, no auth)..." -ForegroundColor Yellow
$rcloneLog = Join-Path $TempRoot "rcd.log"

# Clear any stale log so we only see this run's entries
if (Test-Path $rcloneLog) { Clear-Content $rcloneLog -ErrorAction SilentlyContinue }

# Replace an rcd this script started earlier (same config, same port). Other
# rclone processes on the machine are left alone.
Get-CimInstance Win32_Process -Filter "Name = 'rclone.exe'" -ErrorAction SilentlyContinue | Where-Object {
    "$($_.CommandLine)" -like "*rcd*" -and "$($_.CommandLine)" -like "*127.0.0.1:$RcPort*" -and
    "$($_.CommandLine)" -like "*$rcloneConf*"
} | ForEach-Object {
    Write-Host "Stopping the previous rclone rcd (PID $($_.ProcessId))..."
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

Start-Process -FilePath $rclonePath -ArgumentList @(
    "--config", $rcloneConf,
    "rcd",
    "--rc-addr", "127.0.0.1:$RcPort",
    "--rc-no-auth",
    "--rc-serve",
    "--log-file", $rcloneLog,
    "--log-level", "INFO"
) -WindowStyle Hidden -PassThru | Out-Null

# Give rclone a moment to bind the port and load the config
Start-Sleep -Seconds 2
Write-Host "rclone rcd started." -ForegroundColor Green

# --- 5. Verify RC endpoint ---------------------------------------------------
# fs must be "<RemoteName>:" (with colon) to match rclone's remote spec.
$rcFs = $RemoteName + ":"
$rcUrl = "http://127.0.0.1:$RcPort"
$rcReady = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $payload = [ordered]@{
            fs = $rcFs
            remote = ""
            opt = [ordered]@{ recurse = $true }
        }
        $body = $payload | ConvertTo-Json -Depth 5
        $resp = Invoke-WebRequest -Uri "$rcUrl/operations/list" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 5 -UseBasicParsing -ErrorAction SilentlyContinue
        if ($null -ne $resp -and $resp.StatusCode -eq 200) {
            $rcReady = $true
            break
        }
        if ($i -eq 0 -or $i % 10 -eq 0) {
            $status = if ($null -ne $resp) { $resp.StatusCode } else { "(no response)" }
            Write-Host "  Waiting for rclone RC... ($i): HTTP $status" -ForegroundColor DarkGray
        }
    } catch {
        if ($i -eq 0 -or $i % 10 -eq 0) {
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
Write-Host "Docker container: $containerName (WebDAV at $webDavUrl)" -ForegroundColor DarkGray
Write-Host "Remote data volume: $volumeName (survives container recreation)" -ForegroundColor DarkGray
Write-Host "rclone RC: $rcUrl (log: $rcloneLog)" -ForegroundColor DarkGray
