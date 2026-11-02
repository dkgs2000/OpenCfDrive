# Cloud E2E against an S3 server instead of WebDAV.
#
# Exactly E2E-Run.ps1 -Backend s3: the same mount checks and the same
# server-side and client-side scenarios, against a plain S3-protocol server
# (rclone serve s3, a bucket mapped to the drive). Nothing in the client
# changes; it only sees a different rclone remote. Passing on both backends is
# what shows the client depends on rclone only.

param(
    [string]$Only = "",
    [switch]$KeepRegistration
)

& (Join-Path $PSScriptRoot "E2E-Run.ps1") -Backend s3 -Only $Only -KeepRegistration:$KeepRegistration
exit $LASTEXITCODE
