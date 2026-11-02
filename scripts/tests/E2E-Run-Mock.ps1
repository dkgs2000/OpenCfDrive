# Mock E2E: the same end-to-end flow, without a Cloud Files mount.
#
# Identical to E2E-Run.ps1 (server process -> rclone rcd --rc-serve -> sync
# engine -> download / edit / upload -> scenario matrix, state isolation
# checks) except that it launches OpenCfDrive.MockService, which runs the
# engine in forced mock mode: no sync root registration, and the sync root is
# a plain directory whose files hold the real remote bytes. Mock mode lists
# the server only at startup, so the server-side (S##) scenarios are skipped;
# the client-side (C##) ones all run.
#
#   -Backend webdav|s3   which server to run (default webdav)
#   -Only "C0*"          run only these scenarios

param(
    [ValidateSet("webdav", "s3")][string]$Backend = "webdav",
    [string]$Only = "",
    [string]$SyncRoot = "",
    [string]$RemoteName = "e2e",
    [int]$RcPort = 0,
    [string]$TempRoot = "",
    [string]$SummaryLog = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "E2E-Common.ps1")

Invoke-OpenCfDriveE2E `
    -Mode "mock" `
    -Backend $Backend `
    -Only $Only `
    -SyncRoot $SyncRoot `
    -RemoteName $RemoteName `
    -RcPort $RcPort `
    -TempRoot $TempRoot `
    -SummaryLog $SummaryLog

# A failure throws above. Otherwise succeed explicitly, whatever exit code the
# last native command left in $LASTEXITCODE (see E2E-Run.ps1).
exit 0
