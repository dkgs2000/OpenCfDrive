# Scenario matrix for the end-to-end runs: one row per user-visible action, on
# both sides. Dot-sourced by E2E-Common.ps1; relies on its helpers.
#
#   S## server side  Another app changes the server through its own rclone
#                    process. Nothing notifies the client: the scenario lists
#                    the folder (what Explorer does when you open it or press
#                    F5) and expects the local side to match the server
#                    afterwards. Cloud mode only: mock mode never lists the
#                    server after startup.
#   C## client side  A local action in the drive. The scenario expects the
#                    server to match, checked through the separate rclone
#                    process. Runs in both modes.
#
# Every scenario also declares the most work the client may do for it
# (-Budget: uploads, bytes downloaded, server renames and deletes). After it
# runs, the client's log for that stretch is counted; anything a scenario does
# not name is allowed zero times, and any error line fails it. So a needless
# download, re-upload or duplicate server call fails the scenario even when
# the end state is right.
#
# Every scenario uses its own names, so one failing does not affect the
# others, and all of them run even when some fail. The table at the end lists
# PASS / FAIL / SKIP per scenario and what the client did.

$script:E2EScenarioRoot = ""
$script:E2EServiceLog = ""
$script:E2ERcloneLog = ""
$script:E2EServiceProcess = $null

# Files the client-side scenarios start from. They are on the server before
# the service starts, so they exist in both modes (cloud: placeholders that
# were never opened; mock: plain files).
function Initialize-E2EScenarioFixtures {
    Set-RemoteFile "client/edit-me.txt" "edit-me-v1"
    Set-RemoteFile "client/same-size.txt" "aaaa"
    Set-RemoteFile "client/overwrite.txt" "overwrite-me-original-content"
    Set-RemoteFile "client/delete-me.txt" "delete-me"
    Set-RemoteFile "client/rename-me.txt" "rename-me"
    Set-RemoteFile "client/move-me.txt" "move-me"
    Set-RemoteFile "client/recycle-me.txt" "recycle-me"
    Set-RemoteFile "client/safe-save.txt" "safe-save-v1"
    Set-RemoteFile "client-folder-delete/x.txt" "x"
    Set-RemoteFile "client-folder-delete/sub/y.txt" "y"
    Set-RemoteFile "client-folder-rename/z.txt" "z"
    Set-RemoteFile "client/conflict.txt" "conflict-v1"
    Set-RemoteFile "client/rename-edit.txt" "rename-edit-v1"
    Set-RemoteFile "client/page.txt" ("p" * 4096)
    # For the unregister / register again check in E2E-Common.ps1.
    Set-RemoteFile "restart/never-opened.txt" "never-opened"
}

# --- helpers -------------------------------------------------------------------

function Get-LocalPath([string]$Relative) {
    if ([string]::IsNullOrEmpty($Relative)) { return $script:E2EScenarioRoot }
    return (Join-Path $script:E2EScenarioRoot ($Relative -replace '/', '\'))
}

function Get-OutsidePath([string]$Name) {
    $outside = Join-Path $script:E2ETempRoot "outside"
    New-Item -ItemType Directory -Path $outside -Force | Out-Null
    return (Join-Path $outside $Name)
}

# Lists a local folder the way Explorer does. In cloud mode this is the only
# thing that makes the client look at the server.
function Update-LocalFolder([string]$Folder) {
    $path = Get-LocalPath $Folder
    if (Test-Path -LiteralPath $path -PathType Container) {
        Get-ChildItem -LiteralPath $path -Force -ErrorAction SilentlyContinue | Out-Null
    }
}

# Re-lists $Folder until $Condition holds. Polls a little slower than the
# provider's 1 s listing reuse window, so every poll is a fresh server listing.
function Wait-Local {
    param(
        [string]$Folder,
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][string]$Because,
        [int]$TimeoutSeconds = 20
    )
    # Distinct names: PowerShell resolves variables in a script block by the
    # caller's scope at run time, and inside Wait-ForCondition "$Condition"
    # would be the wrapper itself (endless recursion).
    $localFolder = $Folder
    $localCheck = $Condition
    # A check that throws counts as "not yet": while the client updates a
    # placeholder after a server change it holds a write handle for a few
    # milliseconds, and a read in that moment fails with a sharing violation.
    $ok = Wait-ForCondition { Update-LocalFolder $localFolder; try { [bool](& $localCheck) } catch { $false } } `
        -TimeoutSeconds $TimeoutSeconds -PollMilliseconds 1200
    if (-not $ok) { throw $Because }
}

function Wait-Remote {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][string]$Because,
        [int]$TimeoutSeconds = 30
    )
    $remoteCheck = $Condition   # see Wait-Local
    $ok = Wait-ForCondition { try { [bool](& $remoteCheck) } catch { $false } } `
        -TimeoutSeconds $TimeoutSeconds -PollMilliseconds 500
    if (-not $ok) { throw $Because }
}

function Test-Local([string]$Relative) {
    return (Test-Path -LiteralPath (Get-LocalPath $Relative))
}

# A read in the moment the client updates the file from the server (it holds
# a write handle for a few milliseconds) fails with a sharing violation; only
# that is retried, briefly.
function Read-Local([string]$Relative) {
    for ($attempt = 1; ; $attempt++) {
        try {
            return [System.IO.File]::ReadAllText((Get-LocalPath $Relative))
        } catch {
            $failure = $_.Exception
            if ($failure.InnerException) { $failure = $failure.InnerException }
            $sharingViolation = $failure.HResult -eq -2147024864   # 0x80070020
            if (-not $sharingViolation -or $attempt -ge 10) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}

function Write-Local([string]$Relative, [string]$Content) {
    [System.IO.File]::WriteAllText((Get-LocalPath $Relative), $Content)
}

function Get-LocalLength([string]$Relative) {
    return (Get-Item -LiteralPath (Get-LocalPath $Relative) -Force).Length
}

function Assert-Local([string]$Relative, [string]$Expected) {
    $actual = Read-Local $Relative
    if ($actual -ne $Expected) {
        throw "Local '$Relative' is '$actual', expected '$Expected'."
    }
}

function Assert-RemoteText([string]$Path, [string]$Expected) {
    $ok = Wait-ForCondition { try { (Get-RemoteFile $Path) -eq $Expected } catch { $false } } `
        -TimeoutSeconds 30 -PollMilliseconds 500
    if (-not $ok) {
        $actual = try { "'$(Get-RemoteFile $Path)'" } catch { "missing" }
        throw "Server '$Path' is $actual, expected '$Expected'."
    }
}

function Assert-RemoteMissing([string]$Path) {
    Wait-Remote { -not (Test-RemoteItem $Path) } "Server still has '$Path'."
}

function New-RandomBytes([int]$Length, [int]$Seed) {
    $bytes = New-Object byte[] $Length
    (New-Object System.Random $Seed).NextBytes($bytes)
    return , $bytes
}

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return -join ($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString("x2") }) } finally { $sha.Dispose() }
}

# Writes $Length seeded random bytes to $Path a few MB at a time and returns
# their SHA-256, so large files never sit in memory.
function New-RandomFile([string]$Path, [long]$Length, [int]$Seed) {
    $random = New-Object System.Random $Seed
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $buffer = New-Object byte[] (4MB)
    $stream = [System.IO.File]::Create($Path)
    try {
        $remaining = $Length
        while ($remaining -gt 0) {
            $count = [int][Math]::Min($buffer.Length, $remaining)
            $random.NextBytes($buffer)
            $stream.Write($buffer, 0, $count)
            [void]$sha.TransformBlock($buffer, 0, $count, $null, 0)
            $remaining -= $count
        }
        [void]$sha.TransformFinalBlock($buffer, 0, 0)
        return -join ($sha.Hash | ForEach-Object { $_.ToString("x2") })
    } finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function New-E2EScenario {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Run,
        [hashtable]$Budget = @{},
        [switch]$CloudOnly
    )
    [pscustomobject]@{ Id = $Id; Name = $Name; Run = $Run; Budget = $Budget; CloudOnly = [bool]$CloudOnly }
}

# --- activity audit --------------------------------------------------------------

function Get-LogLength([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return 0 }
    return (Get-Item -LiteralPath $Path).Length
}

# Waits until neither the service nor rclone has written anything for
# $QuietMs, so work a scenario set off (debounced uploads, deletes) is
# counted with it and not with the next one.
function Wait-ServiceQuiet([int]$QuietMs = 2000, [int]$TimeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $length = (Get-LogLength $script:E2EServiceLog) + (Get-LogLength $script:E2ERcloneLog)
    $stableSince = Get-Date
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $now = (Get-LogLength $script:E2EServiceLog) + (Get-LogLength $script:E2ERcloneLog)
        if ($now -ne $length) {
            $length = $now
            $stableSince = Get-Date
        } elseif (((Get-Date) - $stableSince).TotalMilliseconds -ge $QuietMs) {
            return
        }
    }
}

function Read-LogSlice([string]$Path, [long]$From, [long]$To) {
    if ($To -le $From) { return "" }
    $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($From, 'Begin')
        $bytes = New-Object byte[] ($To - $From)
        $read = $stream.Read($bytes, 0, $bytes.Length)
        return [System.Text.Encoding]::UTF8.GetString($bytes, 0, $read)
    } finally {
        $stream.Dispose()
    }
}

# Counts what the client did: every call it made to rclone (from rclone's own
# debug log, so nothing the client leaves unlogged is missed), plus the bytes
# Windows asked it to download and any error it logged.
function Measure-ServiceActivity([string]$ServiceLog, [string]$RcloneLog) {
    $activity = [ordered]@{
        Uploads = 0; DownloadBytes = 0L; ServerRenames = 0; ServerDeletes = 0
        Listings = 0; Lookups = 0; OtherCalls = 0; Errors = 0
    }
    $lines = New-Object System.Collections.Generic.List[string]

    foreach ($line in ($RcloneLog -split "`r?`n")) {
        if ($line -notmatch 'rc: "([^"]+)": with parameters (.*)$') { continue }
        $call = $Matches[1]
        $parameters = $Matches[2]   # read first: switch -Regex replaces $Matches
        switch -Regex ($call) {
            '^operations/uploadfile$' { $activity.Uploads++ }
            '^(operations/movefile|operations/moveto|sync/move)$' { $activity.ServerRenames++ }
            '^(operations/deletefile|operations/rmdirs?|operations/purge|operations/delete)$' { $activity.ServerDeletes++ }
            '^operations/list$' { $activity.Listings++ }
            '^operations/stat$' { $activity.Lookups++ }
            default { $activity.OtherCalls++ }
        }
        if ($parameters.Length -gt 160) { $parameters = $parameters.Substring(0, 160) + "..." }
        $lines.Add("rc $call $parameters")
    }

    foreach ($line in ($ServiceLog -split "`r?`n")) {
        $text = $line.Trim()
        if ($text -match "FETCH_DATA '[^']*' offset=\d+ length=(\d+)") {
            $activity.DownloadBytes += [long]$Matches[1]
        } elseif ($text -match "FETCH_DATA failed|propagation failed|Upload failed|reported failure|handler failed|Could not delete remote|Skipped upload|remote listing failed") {
            $activity.Errors++
        } else {
            continue
        }
        $lines.Add($text)
    }
    return [pscustomobject]@{ Counts = $activity; Lines = $lines }
}

function Format-Activity($Counts) {
    return "up {0}, down {1} B, mv {2}, rm {3}, list {4}, stat {5}, other {6}, err {7}" -f `
        $Counts.Uploads, $Counts.DownloadBytes, $Counts.ServerRenames, $Counts.ServerDeletes,
        $Counts.Listings, $Counts.Lookups, $Counts.OtherCalls, $Counts.Errors
}

# Throws if the client did more than the scenario's budget allows. Listings
# and lookups follow from how often a scenario lists folders, so they are
# only checked where a scenario sets them; everything else defaults to zero.
function Assert-WithinBudget($Activity, [hashtable]$Budget) {
    $over = @()
    foreach ($key in @($Activity.Counts.Keys)) {
        if ($key -in @("Listings", "Lookups") -and -not $Budget.ContainsKey($key)) { continue }
        $allowed = if ($Budget.ContainsKey($key)) { $Budget[$key] } else { 0 }
        if ($Activity.Counts[$key] -gt $allowed) { $over += "$key $($Activity.Counts[$key]) > $allowed" }
    }
    if ($over.Count -gt 0) {
        throw "The client did more than needed ($($over -join '; ')): $($Activity.Lines -join ' | ')"
    }
}

# --- the matrix ----------------------------------------------------------------

function Get-E2EScenarios {
    @(
        # ---------------- server side -> client ----------------
        New-E2EScenario "S01" "server adds a file" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s01.txt" "s01"
            Wait-Local "" { Test-Local "s01.txt" } "s01.txt did not appear locally."
            Assert-Local "s01.txt" "s01"
        }
        New-E2EScenario "S02" "server adds a file in an existing folder" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "docs/s02.txt" "s02"
            Wait-Local "docs" { Test-Local "docs/s02.txt" } "docs\s02.txt did not appear locally."
            Assert-Local "docs/s02.txt" "s02"
        }
        New-E2EScenario "S03" "server adds nested folders with a file" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s03-dir/sub/s03.txt" "s03"
            Wait-Local "" { Test-Local "s03-dir" } "s03-dir did not appear locally."
            Wait-Local "s03-dir" { Test-Local "s03-dir/sub" } "s03-dir\sub did not appear locally."
            Wait-Local "s03-dir/sub" { Test-Local "s03-dir/sub/s03.txt" } "s03-dir\sub\s03.txt did not appear locally."
            Assert-Local "s03-dir/sub/s03.txt" "s03"
        }
        New-E2EScenario "S04" "server updates a file that is downloaded" -Budget @{ DownloadBytes = 21 } -CloudOnly {
            Set-RemoteFile "s04.txt" "s04-v1"
            Wait-Local "" { Test-Local "s04.txt" } "s04.txt did not appear locally."
            Assert-Local "s04.txt" "s04-v1"
            Set-RemoteFile "s04.txt" "s04-version-two"
            Wait-Local "" { (Get-LocalLength "s04.txt") -eq 15 } "s04.txt did not get the server's new size."
            Assert-Local "s04.txt" "s04-version-two"
        }
        New-E2EScenario "S05" "server updates a file that was never opened" -Budget @{ DownloadBytes = 15 } -CloudOnly {
            Set-RemoteFile "s05.txt" "s05-v1"
            Wait-Local "" { Test-Local "s05.txt" } "s05.txt did not appear locally."
            Set-RemoteFile "s05.txt" "s05-version-two"
            Wait-Local "" { (Get-LocalLength "s05.txt") -eq 15 } "s05.txt did not get the server's new size."
            Assert-Local "s05.txt" "s05-version-two"
        }
        New-E2EScenario "S06" "server updates a file, same size (only the time changes)" -Budget @{ DownloadBytes = 16 } -CloudOnly {
            Set-RemoteFile "s06.txt" "s06-aaaa"
            Wait-Local "" { Test-Local "s06.txt" } "s06.txt did not appear locally."
            Assert-Local "s06.txt" "s06-aaaa"
            Start-Sleep -Seconds 2
            Set-RemoteFile "s06.txt" "s06-bbbb"
            Wait-Local "" { (Read-Local "s06.txt") -eq "s06-bbbb" } "s06.txt still has the old content."
        }
        New-E2EScenario "S07" "server deletes a file that was never opened" -Budget @{} -CloudOnly {
            Set-RemoteFile "s07.txt" "s07"
            Wait-Local "" { Test-Local "s07.txt" } "s07.txt did not appear locally."
            Remove-RemoteFile "s07.txt"
            Wait-Local "" { -not (Test-Local "s07.txt") } "s07.txt is still there locally."
        }
        New-E2EScenario "S08" "server deletes a file that is downloaded" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s08.txt" "s08"
            Wait-Local "" { Test-Local "s08.txt" } "s08.txt did not appear locally."
            Assert-Local "s08.txt" "s08"
            Remove-RemoteFile "s08.txt"
            Wait-Local "" { -not (Test-Local "s08.txt") } "s08.txt is still there locally."
        }
        New-E2EScenario "S09" "server deletes a folder with files" -Budget @{ DownloadBytes = 1 } -CloudOnly {
            Set-RemoteFile "s09-dir/a.txt" "a"
            Set-RemoteFile "s09-dir/b.txt" "b"
            Wait-Local "" { Test-Local "s09-dir" } "s09-dir did not appear locally."
            Wait-Local "s09-dir" { (Test-Local "s09-dir/a.txt") -and (Test-Local "s09-dir/b.txt") } "s09-dir's files did not appear locally."
            Assert-Local "s09-dir/a.txt" "a"
            Remove-RemoteFolder "s09-dir"
            Wait-Local "" { -not (Test-Local "s09-dir") } "s09-dir is still there locally."
        }
        New-E2EScenario "S10" "server renames a file" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s10-old.txt" "s10"
            Wait-Local "" { Test-Local "s10-old.txt" } "s10-old.txt did not appear locally."
            Move-RemoteFile "s10-old.txt" "s10-new.txt"
            Wait-Local "" { (-not (Test-Local "s10-old.txt")) -and (Test-Local "s10-new.txt") } "The rename did not reach the local side."
            Assert-Local "s10-new.txt" "s10"
        }
        New-E2EScenario "S11" "server moves a file to another folder" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s11.txt" "s11"
            Wait-Local "" { Test-Local "s11.txt" } "s11.txt did not appear locally."
            Move-RemoteFile "s11.txt" "docs/s11.txt"
            Wait-Local "" { -not (Test-Local "s11.txt") } "s11.txt is still in the root locally."
            Wait-Local "docs" { Test-Local "docs/s11.txt" } "docs\s11.txt did not appear locally."
            Assert-Local "docs/s11.txt" "s11"
        }
        New-E2EScenario "S12" "server renames a folder" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s12-old/f.txt" "s12"
            Wait-Local "" { Test-Local "s12-old" } "s12-old did not appear locally."
            Wait-Local "s12-old" { Test-Local "s12-old/f.txt" } "s12-old\f.txt did not appear locally."
            Move-RemoteFolder "s12-old" "s12-new"
            Wait-Local "" { (-not (Test-Local "s12-old")) -and (Test-Local "s12-new") } "The folder rename did not reach the local side."
            Wait-Local "s12-new" { Test-Local "s12-new/f.txt" } "s12-new\f.txt did not appear locally."
            Assert-Local "s12-new/f.txt" "s12"
        }
        New-E2EScenario "S13" "server adds a 3 MB binary file" -Budget @{ DownloadBytes = 3MB } -CloudOnly {
            $bytes = New-RandomBytes (3 * 1024 * 1024) 13
            Set-RemoteBytes "s13.bin" $bytes
            Wait-Local "" { Test-Local "s13.bin" } "s13.bin did not appear locally."
            $local = [System.IO.File]::ReadAllBytes((Get-LocalPath "s13.bin"))
            if ((Get-Sha256Hex $local) -ne (Get-Sha256Hex $bytes)) {
                throw "s13.bin downloaded $($local.Length) bytes that differ from the server's $($bytes.Length)."
            }
        }
        New-E2EScenario "S14" "server adds 40 files at once" -Budget @{ DownloadBytes = 6 } -CloudOnly {
            $staging = New-StagingDirectory
            1..40 | ForEach-Object { [System.IO.File]::WriteAllText((Join-Path $staging "f$_.txt"), "s14-$_") }
            Invoke-ServerRclone @("copy", $staging, (Join-RemoteSpec "s14-many")) | Out-Null
            Remove-IfExists $staging
            Wait-Local "" { Test-Local "s14-many" } "s14-many did not appear locally."
            Wait-Local "s14-many" { @(Get-ChildItem -LiteralPath (Get-LocalPath "s14-many") -File).Count -eq 40 } "s14-many does not have 40 files locally."
            Assert-Local "s14-many/f40.txt" "s14-40"
        }
        # Conflicts: the server wins. The local edit is discarded, never sent:
        # no upload, and only the server check before it (one lookup).
        New-E2EScenario "S15" "both sides edit the same file: the server's version wins" -Budget @{ DownloadBytes = 33; Lookups = 1 } -CloudOnly {
            Set-RemoteFile "s15.txt" "s15-server-v1"
            Wait-Local "" { Test-Local "s15.txt" } "s15.txt did not appear locally."
            Assert-Local "s15.txt" "s15-server-v1"
            Set-RemoteFile "s15.txt" "s15-server-v2-longer"
            Write-Local "s15.txt" "s15-local-edit"
            Update-LocalFolder ""
            Wait-Local "" { (Read-Local "s15.txt") -eq "s15-server-v2-longer" } "s15.txt did not take the server's version."
            Assert-RemoteText "s15.txt" "s15-server-v2-longer"
        }
        New-E2EScenario "S16" "server deletes a file that has an unsynced local edit: the server wins, the local file goes" -Budget @{ DownloadBytes = 6; Lookups = 1 } -CloudOnly {
            Set-RemoteFile "s16.txt" "s16-v1"
            Wait-Local "" { Test-Local "s16.txt" } "s16.txt did not appear locally."
            Assert-Local "s16.txt" "s16-v1"
            Remove-RemoteFile "s16.txt"
            Write-Local "s16.txt" "s16-local-edit"
            Update-LocalFolder ""
            Wait-Local "" { -not (Test-Local "s16.txt") } "s16.txt is still there locally."
            Start-Sleep -Seconds 2
            if (Test-RemoteItem "s16.txt") { throw "The local edit reached the server; the server's delete should have won." }
        }
        New-E2EScenario "S17" "client pins a 300 MB server file (Always keep on this device)" -Budget @{ DownloadBytes = 300MB + 1234 } -CloudOnly {
            # Pinning asks for the whole file in one request. It is larger
            # than the service may buffer, so it only works if the bytes are
            # handed over in chunks. The odd size gives an unaligned last chunk.
            $size = 300MB + 1234
            $staging = New-StagingDirectory
            try {
                $source = Join-Path $staging "s17.bin"
                $expected = New-RandomFile $source $size 17
                Invoke-ServerRclone @("copyto", "--ignore-times", $source, (Join-RemoteSpec "s17.bin")) | Out-Null
            } finally {
                Remove-IfExists $staging
            }
            Wait-Local "" { Test-Local "s17.bin" } "s17.bin did not appear locally."

            & attrib.exe +P (Get-LocalPath "s17.bin")
            $done = "transferred $size bytes at 0 "
            $finished = Wait-ForCondition {
                $log = Read-LogText $script:E2EServiceLog
                $log.Contains($done) -or $log.Contains("FETCH_DATA failed")
            } -TimeoutSeconds 300 -PollMilliseconds 500
            $log = Read-LogText $script:E2EServiceLog
            if (-not $log.Contains($done)) {
                $fetches = ($log -split "`r?`n" | Where-Object { $_ -match "s17\.bin|FETCH_DATA failed" }) -join " | "
                throw "Pinning did not download s17.bin in one request (finished=$finished): $fetches"
            }
            $transfer = ($log -split "`r?`n" | Where-Object { $_.Contains($done) } | Select-Object -First 1).Trim()
            Write-E2ELog "  $transfer"

            $actual = (Get-FileHash -LiteralPath (Get-LocalPath "s17.bin") -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $expected) { throw "s17.bin's local bytes differ from the server's." }

            $script:E2EServiceProcess.Refresh()
            $peak = $script:E2EServiceProcess.PeakWorkingSet64
            Write-E2ELog "  service peak working set: $([Math]::Round($peak / 1MB)) MB"
            if ($peak -ge $size) { throw "The service's peak memory ($peak bytes) reached the file size: it buffered the whole file." }
        }
        # Offline the way a lost network is: the server stops answering while
        # the client's rclone keeps running. The first listing waits for it in
        # vain (the one error allowed) and puts the drive offline; from then
        # on, as in Nextcloud and OneDrive, folders show what is local at once.
        # Using the drive checks the server again, so back online it lists it.
        New-E2EScenario "S18" "offline: an opened folder keeps what is local, an unopened one is empty, back online lists the server" -Budget @{ DownloadBytes = 9; Errors = 1 } -CloudOnly {
            Set-RemoteFile "s18-seen/cached.txt" "seen"
            Set-RemoteFile "s18-unseen/later.txt" "later"
            Wait-Local "" { (Test-Local "s18-seen") -and (Test-Local "s18-unseen") } "s18-seen and s18-unseen did not appear locally."
            Wait-Local "s18-seen" { Test-Local "s18-seen/cached.txt" } "s18-seen\cached.txt did not appear locally."
            Assert-Local "s18-seen/cached.txt" "seen"

            Suspend-E2EBackend
            try {
                # Past the listing reuse window, so the next listings ask the server.
                Start-Sleep -Milliseconds 1500
                $clock = [System.Diagnostics.Stopwatch]::StartNew()
                $seen = @(Get-ChildItem -LiteralPath (Get-LocalPath "s18-seen") -Force | ForEach-Object Name)
                $seenMs = $clock.ElapsedMilliseconds
                if ($seen -notcontains "cached.txt") { throw "Offline, s18-seen lost its local entry (it has: $($seen -join ', '))." }
                Assert-Local "s18-seen/cached.txt" "seen"

                $clock.Restart()
                $unseen = @(Get-ChildItem -LiteralPath (Get-LocalPath "s18-unseen") -Force)
                $unseenMs = $clock.ElapsedMilliseconds
                if ($unseen.Count -ne 0) { throw "Offline, the never-opened s18-unseen is not empty: $($unseen.Name -join ', ')." }

                Write-E2ELog "  offline listings fell back to what is local in $seenMs ms and $unseenMs ms"
                if ([Math]::Max($seenMs, $unseenMs) -gt 30000) { throw "An offline listing took over 30 s to fall back." }
            } finally {
                Resume-E2EBackend
            }

            Wait-Local "s18-unseen" { Test-Local "s18-unseen/later.txt" } "Back online, s18-unseen was not listed from the server." -TimeoutSeconds 60
            Assert-Local "s18-unseen/later.txt" "later"
        }

        # ---------------- client side -> server ----------------
        New-E2EScenario "C01" "client adds a file" -Budget @{ Uploads = 1; Lookups = 2 } {
            Write-Local "c01.txt" "c01"
            Assert-RemoteText "c01.txt" "c01"
        }
        New-E2EScenario "C02" "client adds a file in an existing folder" -Budget @{ Uploads = 1; Lookups = 2 } {
            Write-Local "docs/c02.txt" "c02"
            Assert-RemoteText "docs/c02.txt" "c02"
        }
        New-E2EScenario "C03" "client adds nested folders with a file" -Budget @{ Uploads = 1; Lookups = 2 } {
            New-Item -ItemType Directory -Path (Get-LocalPath "c03-dir/sub") -Force | Out-Null
            Write-Local "c03-dir/sub/c03.txt" "c03"
            Assert-RemoteText "c03-dir/sub/c03.txt" "c03"
        }
        New-E2EScenario "C04" "client edits a file that was never opened" -Budget @{ DownloadBytes = 10; Uploads = 1; Lookups = 2 } {
            [System.IO.File]::AppendAllText((Get-LocalPath "client/edit-me.txt"), "-edited")
            Assert-RemoteText "client/edit-me.txt" "edit-me-v1-edited"
        }
        New-E2EScenario "C05" "client overwrites a file with the same size" -Budget @{ Uploads = 1; Lookups = 2 } {
            Write-Local "client/same-size.txt" "bbbb"
            Assert-RemoteText "client/same-size.txt" "bbbb"
        }
        New-E2EScenario "C06" "client overwrites a file with shorter content" -Budget @{ Uploads = 1; Lookups = 2 } {
            Write-Local "client/overwrite.txt" "short"
            Assert-RemoteText "client/overwrite.txt" "short"
        }
        New-E2EScenario "C07" "client deletes a file" -Budget @{ ServerDeletes = 1 } {
            Remove-Item -LiteralPath (Get-LocalPath "client/delete-me.txt") -Force
            Assert-RemoteMissing "client/delete-me.txt"
        }
        New-E2EScenario "C08" "client renames a file" -Budget @{ ServerRenames = 1 } {
            Rename-Item -LiteralPath (Get-LocalPath "client/rename-me.txt") -NewName "renamed.txt"
            Assert-RemoteText "client/renamed.txt" "rename-me"
            Assert-RemoteMissing "client/rename-me.txt"
        }
        New-E2EScenario "C09" "client moves a file to another folder" -Budget @{ ServerRenames = 1 } {
            Move-Item -LiteralPath (Get-LocalPath "client/move-me.txt") -Destination (Get-LocalPath "docs/moved.txt")
            Assert-RemoteText "docs/moved.txt" "move-me"
            Assert-RemoteMissing "client/move-me.txt"
        }
        # The two files, then one removal of the emptied folder (its
        # subfolder included).
        New-E2EScenario "C10" "client deletes a folder with files" -Budget @{ ServerDeletes = 3 } {
            Remove-Item -LiteralPath (Get-LocalPath "client-folder-delete") -Recurse -Force
            Assert-RemoteMissing "client-folder-delete/x.txt"
            Assert-RemoteMissing "client-folder-delete/sub/y.txt"
            Assert-RemoteMissing "client-folder-delete"
        }
        # rclone's folder move leaves the emptied source folder behind on
        # remotes without a server-side folder move, hence one removal.
        New-E2EScenario "C11" "client renames a folder" -Budget @{ ServerRenames = 1; ServerDeletes = 1; DownloadBytes = 1 } {
            Rename-Item -LiteralPath (Get-LocalPath "client-folder-rename") -NewName "client-folder-renamed"
            Assert-RemoteText "client-folder-renamed/z.txt" "z"
            Assert-RemoteMissing "client-folder-rename"
            # The file inside still opens under its new path.
            Assert-Local "client-folder-renamed/z.txt" "z"
        }
        New-E2EScenario "C12" "client moves a file out of the drive (like the Recycle Bin)" -Budget @{ DownloadBytes = 10; ServerDeletes = 1 } {
            Move-Item -LiteralPath (Get-LocalPath "client/recycle-me.txt") -Destination (Get-OutsidePath "recycle-me.txt")
            Assert-RemoteMissing "client/recycle-me.txt"
            $stray = @(Get-RemoteListing | Where-Object { $_ -like "*recycle-me*" })
            if ($stray.Count -gt 0) { throw "The file reappeared on the server as: $($stray -join ', ')" }
        }
        New-E2EScenario "C13" "client saves through a temp file that replaces the original" -Budget @{ Uploads = 1; Lookups = 2 } {
            Write-Local "client/safe-save.txt.tmp" "safe-save-v2"
            Move-Item -LiteralPath (Get-LocalPath "client/safe-save.txt.tmp") -Destination (Get-LocalPath "client/safe-save.txt") -Force
            Assert-RemoteText "client/safe-save.txt" "safe-save-v2"
            Start-Sleep -Seconds 2
            if (Test-RemoteItem "client/safe-save.txt.tmp") { throw "The temp file was left on the server." }
        }
        New-E2EScenario "C14" "client copies a file in from outside the drive" -Budget @{ Uploads = 1; Lookups = 2 } {
            $outside = Get-OutsidePath "copied-in.txt"
            [System.IO.File]::WriteAllText($outside, "copied-in")
            Copy-Item -LiteralPath $outside -Destination (Get-LocalPath "client/copied-in.txt")
            Assert-RemoteText "client/copied-in.txt" "copied-in"
        }
        New-E2EScenario "C15" "client moves a file in from outside the drive" -Budget @{ Uploads = 1; Lookups = 2 } {
            $outside = Get-OutsidePath "moved-in.txt"
            [System.IO.File]::WriteAllText($outside, "moved-in")
            Move-Item -LiteralPath $outside -Destination (Get-LocalPath "client/moved-in.txt")
            Assert-RemoteText "client/moved-in.txt" "moved-in"
        }
        New-E2EScenario "C16" "client adds a 3 MB binary file" -Budget @{ Uploads = 1; Lookups = 2 } {
            $bytes = New-RandomBytes (3 * 1024 * 1024) 16
            [System.IO.File]::WriteAllBytes((Get-LocalPath "c16.bin"), $bytes)
            $expected = Get-Sha256Hex $bytes
            Wait-Remote { (Get-Sha256Hex (Get-RemoteBytes "c16.bin")) -eq $expected } "The server's c16.bin does not match the local bytes."
        }
        New-E2EScenario "C17" "client adds 40 files at once" -Budget @{ Uploads = 40; Lookups = 80 } {
            New-Item -ItemType Directory -Path (Get-LocalPath "c17-many") -Force | Out-Null
            1..40 | ForEach-Object { Write-Local "c17-many/f$_.txt" "c17-$_" }
            Wait-Remote { @(Get-RemoteListing | Where-Object { $_ -like "c17-many/*.txt" }).Count -eq 40 } "The server does not have all 40 files of c17-many."
            Assert-RemoteText "c17-many/f40.txt" "c17-40"
        }
        # No listing in between, so only the check before the upload can see
        # the server's change. The file is opened before the server changes
        # it, so saving looks up no name (which could list the folder).
        New-E2EScenario "C18" "the server changes a file that is open locally, then it is saved: the server's version wins" -Budget @{ DownloadBytes = 29; Lookups = 1 } {
            Wait-Local "client" { Test-Local "client/conflict.txt" } "client\conflict.txt did not appear locally."
            $stream = [System.IO.File]::Open((Get-LocalPath "client/conflict.txt"), 'Open', 'ReadWrite', 'ReadWrite')
            try {
                Set-RemoteFile "client/conflict.txt" "conflict-server-v2"
                $edit = [System.Text.Encoding]::UTF8.GetBytes("conflict-local-edit")
                $stream.SetLength(0)
                $stream.Write($edit, 0, $edit.Length)
            } finally {
                $stream.Dispose()
            }

            $won = Wait-ForCondition { try { (Read-Local "client/conflict.txt") -eq "conflict-server-v2" } catch { $false } } `
                -TimeoutSeconds 30 -PollMilliseconds 500
            if (-not $won) { throw "client\conflict.txt did not take the server's version (it reads '$(Read-Local "client/conflict.txt")')." }
            Assert-RemoteText "client/conflict.txt" "conflict-server-v2"
        }
        # A rename must not look like a server change to the check before an
        # upload, or the edit after it would be discarded.
        New-E2EScenario "C19" "client renames a file, then edits it: the edit is uploaded" -Budget @{ ServerRenames = 1; Uploads = 1; Lookups = 2; DownloadBytes = 14 } {
            Rename-Item -LiteralPath (Get-LocalPath "client/rename-edit.txt") -NewName "renamed-edit.txt"
            Assert-RemoteText "client/renamed-edit.txt" "rename-edit-v1"
            [System.IO.File]::AppendAllText((Get-LocalPath "client/renamed-edit.txt"), "-edited")
            Assert-RemoteText "client/renamed-edit.txt" "rename-edit-v1-edited"
        }
        # Windows does not clear in-sync for an append that starts on a page
        # boundary, so the client must tell the edit by the file's size and
        # write time, as other sync clients do.
        New-E2EScenario "C20" "client appends to a downloaded file that ends on a page boundary: the edit is uploaded" -Budget @{ DownloadBytes = 4096; Uploads = 1; Lookups = 2 } {
            if ((Read-Local "client/page.txt").Length -ne 4096) { throw "client\page.txt did not download whole." }
            [System.IO.File]::AppendAllText((Get-LocalPath "client/page.txt"), "-edited")
            $expected = ("p" * 4096) + "-edited"
            Wait-Remote { [System.Text.Encoding]::UTF8.GetString((Get-RemoteBytes "client/page.txt")) -eq $expected } `
                "The server's client/page.txt does not end in the appended '-edited'."
        }
    )
}

# Runs the matrix and returns the number of failures. -Only takes a
# comma-separated list of ids or wildcards ("S0*,C11").
function Invoke-E2EScenarios {
    param(
        [Parameter(Mandatory = $true)][string]$SyncRoot,
        [switch]$IsCloud,
        [string]$Only = "",
        [string]$ServiceLog = "",
        [System.Diagnostics.Process]$ServiceProcess = $null,
        [string]$RcloneLog = ""
    )

    $script:E2EScenarioRoot = $SyncRoot
    $script:E2EServiceLog = $ServiceLog
    $script:E2ERcloneLog = $RcloneLog
    $script:E2EServiceProcess = $ServiceProcess
    $filters = @($Only -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $results = New-Object System.Collections.Generic.List[object]
    $quiet = $false

    Write-E2ELog "--- scenarios ---"
    foreach ($scenario in Get-E2EScenarios) {
        if ($filters.Count -gt 0 -and -not ($filters | Where-Object { $scenario.Id -like $_ })) {
            continue
        }

        if ($scenario.CloudOnly -and -not $IsCloud) {
            $results.Add([pscustomobject]@{ Id = $scenario.Id; Result = "SKIP"; Ms = 0; Name = $scenario.Name; Activity = ""; Detail = "mock mode does not list the server after startup" })
            continue
        }

        # Start from a quiet client so earlier work is not counted here. After
        # the first scenario it already is: each one ends by waiting for quiet.
        if (-not $quiet) { Wait-ServiceQuiet }
        $serviceStart = Get-LogLength $script:E2EServiceLog
        $rcloneStart = Get-LogLength $script:E2ERcloneLog
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $failure = $null
        try {
            & $scenario.Run | Out-Null
        } catch {
            $failure = $_.Exception.Message
        }
        $ms = $clock.ElapsedMilliseconds

        Wait-ServiceQuiet
        $quiet = $true
        $activity = Measure-ServiceActivity `
            (Read-LogSlice $script:E2EServiceLog $serviceStart (Get-LogLength $script:E2EServiceLog)) `
            (Read-LogSlice $script:E2ERcloneLog $rcloneStart (Get-LogLength $script:E2ERcloneLog))
        if (-not $failure) {
            try { Assert-WithinBudget $activity $scenario.Budget } catch { $failure = $_.Exception.Message }
        }

        $done = Format-Activity $activity.Counts
        if ($failure) {
            $results.Add([pscustomobject]@{ Id = $scenario.Id; Result = "FAIL"; Ms = $ms; Name = $scenario.Name; Activity = $done; Detail = $failure })
            Write-E2ELog "[FAIL] $($scenario.Id) $($scenario.Name) [$done]: $failure"
        } else {
            $results.Add([pscustomobject]@{ Id = $scenario.Id; Result = "PASS"; Ms = $ms; Name = $scenario.Name; Activity = $done; Detail = "" })
            Write-E2ELog "[PASS] $($scenario.Id) $($scenario.Name) ($ms ms) [$done]"
        }
    }

    Write-E2ELog "--- scenario results ---"
    foreach ($row in $results) {
        $line = "{0,-4} {1,-4} {2,7} ms  {3}" -f $row.Id, $row.Result, $row.Ms, $row.Name
        if ($row.Activity) { $line += "  [$($row.Activity)]" }
        if ($row.Detail) { $line += "  ($($row.Detail))" }
        Write-E2ELog $line
    }
    $failed = @($results | Where-Object { $_.Result -eq "FAIL" }).Count
    $passed = @($results | Where-Object { $_.Result -eq "PASS" }).Count
    $skipped = @($results | Where-Object { $_.Result -eq "SKIP" }).Count
    Write-E2ELog "Scenarios: $passed passed, $failed failed, $skipped skipped"

    return $failed
}
