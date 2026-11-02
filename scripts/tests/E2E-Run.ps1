# Cloud E2E: a real Cloud Files sync root, registered with Explorer.
#
# Runs the same flow as E2E-Run-Mock.ps1 (server process -> rclone rcd
# --rc-serve -> sync engine -> download / edit / upload -> the server-side and
# client-side scenario matrix in E2E-Scenarios.ps1) and adds the part that
# needs Cloud Files: the service registers a real sync root, Explorer shows it,
# items are dehydrated placeholders, a normal file read hydrates them, and
# server-side changes are picked up when a folder is listed.
#
# No elevation is needed. The sync root is created under %TEMP% and removed
# on exit, so nothing of the user's own folders is touched.
#
#   -Backend webdav|s3   which server to run (default webdav; E2E-Run-S3.ps1
#                        is the S3 shortcut)
#   -Only "S0*,C11"      run only these scenarios
#   -SyncRoot / -KeepRegistration   mount elsewhere / leave the mount in place

param(
    [ValidateSet("webdav", "s3")][string]$Backend = "webdav",
    [string]$Only = "",
    [string]$SyncRoot = "",
    [string]$RemoteName = "e2e",
    [int]$RcPort = 0,
    [string]$TempRoot = "",
    [string]$SummaryLog = "",
    [switch]$KeepRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "E2E-Common.ps1")

# Cloud-only checks. Everything before this point is identical to the mock run.
$mountCheck = {
    param([string]$Root)

    # 1. Directory enumeration is the operation that used to fail: the sync root
    #    was a not-fully-populated placeholder, the platform asked the provider
    #    to populate it (FETCH_PLACEHOLDERS), nothing answered, and the shell
    #    gave up after the 60 s cfapi timeout. Enumerating must now be fast
    #    because the provider answers FETCH_PLACEHOLDERS from a live rclone
    #    listing. 15 s is a generous ceiling that would
    #    still catch a regression long before the 60 s platform timeout.
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $rootItems = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop)
    $clock.Stop()
    if ($clock.Elapsed.TotalSeconds -gt 15) {
        throw "Enumerating the sync root took $([int]$clock.Elapsed.TotalSeconds) s; the shell would time out (cfapi limit is 60 s)."
    }
    $names = @($rootItems | ForEach-Object { $_.Name })
    foreach ($expected in @("alpha.txt", "beta.txt", "docs")) {
        if ($names -notcontains $expected) {
            throw "Enumerating the sync root did not list '$expected' (saw: $($names -join ', '))."
        }
    }
    Write-E2ELog "MOUNT: sync root enumerated in $($clock.ElapsedMilliseconds) ms: $($names -join ', ')"

    # 2. A remote folder must be a directory placeholder, not a plain directory
    #    that the platform would try to populate on demand.
    $docs = Join-Path $Root "docs"
    $docsAttrs = (Get-Item -LiteralPath $docs -Force).Attributes
    if (($docsAttrs -band [System.IO.FileAttributes]::Directory) -eq 0) {
        throw "docs is not a directory (attributes: $docsAttrs)."
    }
    if (($docsAttrs -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "docs is a plain directory, not a Cloud Files directory placeholder (attributes: $docsAttrs)."
    }
    Write-E2ELog "MOUNT: docs is a directory placeholder (attributes: $docsAttrs)"

    # 3. Items must be cloud placeholders, not plain local copies: this is the
    #    state Explorer renders with the cloud icon.
    $beta = Join-Path $Root "beta.txt"
    $attrs = (Get-Item -LiteralPath $beta -Force).Attributes
    # RecallOnDataAccess (0x400000) is not a named FileAttributes member in
    # this build, so test it numerically. Offline = 0x1000, ReparsePoint = 0x400.
    $RecallOnDataAccess = 0x400000
    $isPlaceholder = (($attrs -band $RecallOnDataAccess) -ne 0) -or
                     (($attrs -band [System.IO.FileAttributes]::Offline) -ne 0)
    if (-not $isPlaceholder) {
        throw "beta.txt is a plain local copy, not a Cloud Files placeholder (attributes: $attrs)."
    }
    if (($attrs -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "beta.txt is not a reparse point, so it cannot be a cloud placeholder (attributes: $attrs)."
    }
    Write-E2ELog "MOUNT: beta.txt is a dehydrated placeholder (attributes: $attrs)"

    # 4. A plain Win32 read - what double-clicking in Explorer does - hydrates it.
    $content = [System.IO.File]::ReadAllText($beta)
    if ($content -ne "beta-from-e2e") {
        throw "A normal file read of beta.txt returned '$content' instead of 'beta-from-e2e'."
    }
    Write-E2ELog "MOUNT: beta.txt hydrated to '$content' through a normal file read"

    # The hydrated bytes must be kept: if the placeholder is still marked for
    # recall afterwards, every later read and any write will hit cfapi timeouts.
    $after = (Get-Item -LiteralPath $beta -Force).Attributes
    if (($after -band $RecallOnDataAccess) -ne 0) {
        throw "beta.txt is still dehydrated after a successful read (attributes: $after); hydration is not persisting."
    }
    Write-E2ELog "MOUNT: beta.txt is locally available (attributes: $after)"

    # 5. A file inside a directory placeholder hydrates the same way.
    $readme = Join-Path $docs "readme.txt"
    $nestedContent = [System.IO.File]::ReadAllText($readme)
    if ($nestedContent -ne "readme-from-e2e") {
        throw "A normal file read of docs\readme.txt returned '$nestedContent' instead of 'readme-from-e2e'."
    }
    Write-E2ELog "MOUNT: docs\readme.txt hydrated to '$nestedContent' through a normal file read"

    # 6. A lookup that misses must not stall either: before the provider was
    #    fixed, even a negative probe in the sync root blocked for 60 s.
    $clock.Restart()
    $missing = Test-Path -LiteralPath (Join-Path $Root "does-not-exist.txt")
    $clock.Stop()
    if ($missing) {
        throw "A lookup for does-not-exist.txt unexpectedly succeeded."
    }
    if ($clock.Elapsed.TotalSeconds -gt 10) {
        throw "A missing-file lookup took $([int]$clock.Elapsed.TotalSeconds) s; the shell would time out."
    }
    Write-E2ELog "MOUNT: missing-file lookup returned in $($clock.ElapsedMilliseconds) ms"

    # Server-side adds, updates, deletes and renames are covered by the S##
    # scenarios in E2E-Scenarios.ps1, which run after these mount checks.

    # 7. The root is registered with Explorer (StorageProviderSyncRootManager),
    #    not only with the Cloud Files driver: that is what gives it a
    #    navigation pane entry and the "Free up space" / "Always keep on this
    #    device" menu.
    $shellKey = Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager" |
        Where-Object { $_.PSChildName -like "OpenCfDrive!*" } |
        Where-Object {
            $users = Get-ItemProperty -LiteralPath (Join-Path $_.PSPath "UserSyncRoots") -ErrorAction SilentlyContinue
            $users -and @($users.PSObject.Properties | Where-Object { $_.Value -eq $Root }).Count -gt 0
        } | Select-Object -First 1
    if ($null -eq $shellKey) {
        throw "The sync root is not registered with Explorer (no SyncRootManager entry for $Root)."
    }
    $display = (Get-ItemProperty -LiteralPath $shellKey.PSPath).DisplayNameResource
    Write-E2ELog "MOUNT: registered with Explorer as '$($shellKey.PSChildName)' ('$display')"

    # 8. "Free up space" and "Always keep on this device" only set the
    #     unpinned / pinned attribute (attrib +U / +P is the same thing); the
    #     provider must drop or download the local copy.
    $isDehydrated = { param($p) ((Get-Item -LiteralPath $p -Force).Attributes -band $RecallOnDataAccess) -ne 0 }

    & attrib.exe -P +U $beta | Out-Null
    if (-not (Wait-ForCondition { & $isDehydrated $beta } -TimeoutSeconds 15)) {
        throw "Free up space (attrib +U) did not remove the local copy of beta.txt."
    }
    Write-E2ELog "MOUNT: free up space removed the local copy of beta.txt"

    & attrib.exe -U +P $beta | Out-Null
    if (-not (Wait-ForCondition { -not (& $isDehydrated $beta) } -TimeoutSeconds 15)) {
        throw "Always keep on this device (attrib +P) did not download beta.txt."
    }
    $pinnedContent = [System.IO.File]::ReadAllText($beta)
    if ($pinnedContent -ne "beta-from-e2e") {
        throw "beta.txt read back '$pinnedContent' after pinning instead of 'beta-from-e2e'."
    }
    & attrib.exe -P $beta | Out-Null
    Write-E2ELog "MOUNT: always keep on this device downloaded beta.txt without opening it"

    # On a folder: everything local inside it is freed.
    & attrib.exe +U $docs | Out-Null
    if (-not (Wait-ForCondition { & $isDehydrated $readme } -TimeoutSeconds 15)) {
        throw "Free up space on the docs folder did not remove the local copies inside it."
    }
    & attrib.exe -U $docs | Out-Null
    Write-E2ELog "MOUNT: free up space on docs\ removed the local copies inside it"

    # 9. An actual Explorer window should be able to navigate to the sync root.
    $url = "file:///" + ($Root -replace '\\', '/')
    Start-Process -FilePath "explorer.exe" -ArgumentList $Root | Out-Null

    $findWindow = {
        try {
            foreach ($window in @((New-Object -ComObject Shell.Application).Windows())) {
                try {
                    if ($window.LocationURL -eq $url) { return $window }
                    if ($window.Document.Folder.Self.Path -eq $Root) { return $window }
                } catch { }
            }
        } catch { }
        return $null
    }
    $windowSeen = Wait-ForCondition { $null -ne (& $findWindow) } -TimeoutSeconds 15

    if ($windowSeen) {
        Write-E2ELog "MOUNT: an Explorer window is open at the sync root"
        # Closed again so the scenarios decide exactly when folders are listed.
        try { (& $findWindow).Quit() } catch { }
    } else {
        Write-E2ELog "MOUNT: no interactive Explorer window detected (non-interactive session); mount checks passed"
    }
}

Invoke-OpenCfDriveE2E `
    -Mode "cloud" `
    -Backend $Backend `
    -Only $Only `
    -SyncRoot $SyncRoot `
    -RemoteName $RemoteName `
    -RcPort $RcPort `
    -TempRoot $TempRoot `
    -SummaryLog $SummaryLog `
    -MountCheck $mountCheck `
    -KeepRegistration:$KeepRegistration

# A failure throws above. Otherwise succeed explicitly: the last native
# command (the final unregister, with nothing left to clear) leaves a non-zero
# $LASTEXITCODE, which CI's PowerShell wrapper would report as the result.
exit 0
