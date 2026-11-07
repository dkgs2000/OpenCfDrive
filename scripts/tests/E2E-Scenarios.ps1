# Scenario matrix for the end-to-end runs: one row per user-visible action, on
# both sides. Dot-sourced by E2E-Common.ps1; relies on its helpers.
#
#   S## server side  Another app changes the server through its own rclone
#                    process. Nothing notifies the client: the scenario asks
#                    the client for the background sync run it makes every
#                    30 s (Sync-Remote), lists the folder the way Explorer
#                    does, and expects the local side to match the server
#                    afterwards. Cloud mode only: mock mode never looks at the
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
    Set-RemoteFile "client-folder-listed/w.txt" "w"
    Set-RemoteFile "client-folder-listed/sub/v.txt" "v"
    Set-RemoteFile "client/conflict.txt" "conflict-v1"
    Set-RemoteFile "client/rename-edit.txt" "rename-edit-v1"
    Set-RemoteFile "client/page.txt" ("p" * 4096)
    Set-RemoteFile "client/offline-delete.txt" "offline-delete"
    Set-RemoteFile "client/offline-rename.txt" "offline-rename"
    Set-RemoteFile "client/free-after-upload.txt" "free-after-upload-v1"
    Set-RemoteFile "client/edit-same.txt" "same-v1"
    Set-RemoteFile "client/edit-shorter.txt" "edit-shorter-original"
    Set-RemoteFile "client/edit-longer.txt" "grow-v1"
    Set-RemoteFile "client/offline-edit.txt" "offline-edit-v1"
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

# A name in Arabic script, from its code points: Windows PowerShell 5.1 reads
# a script saved without a byte order mark in the ANSI code page, so the
# names cannot be written out in this file.
function Get-ArabicName([int[]]$CodePoints) {
    return -join ($CodePoints | ForEach-Object { [char]$_ })
}

# Lists a local folder the way Explorer does. The first listing of a folder
# asks the server; after that the folder is listed from disk.
function Update-LocalFolder([string]$Folder) {
    $path = Get-LocalPath $Folder
    if (Test-Path -LiteralPath $path -PathType Container) {
        Get-ChildItem -LiteralPath $path -Force -ErrorAction SilentlyContinue | Out-Null
    }
}

# Until $Condition holds: a background sync run of the client (it waits for
# the run to finish), then a listing of $Folder, the way Explorer shows it.
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
    $ok = Wait-ForCondition {
        if ($(try { [bool](& $localCheck) } catch { $false })) { return $true }
        Sync-Remote
        Update-LocalFolder $localFolder
        try { [bool](& $localCheck) } catch { $false }
    } -TimeoutSeconds $TimeoutSeconds -PollMilliseconds 200
    if (-not $ok) { throw $Because }
}

# The attributes Windows gives a folder that is not fully populated: listing it
# asks the provider, and Explorer shows it as online-only (the cloud icon).
function Test-OnlineOnlyFolder([string]$Relative) {
    $attributes = [int](Get-Item -LiteralPath (Get-LocalPath $Relative) -Force).Attributes
    return ($attributes -band 0x400000) -ne 0
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

# Saves by replacing the file, as File.WriteAllText and many programs do:
# over a placeholder, the result is a plain file.
function Write-Local([string]$Relative, [string]$Content) {
    [System.IO.File]::WriteAllText((Get-LocalPath $Relative), $Content)
}

# Saves into the same file, as Notepad does: opened, emptied and written. A
# placeholder stays one, and Windows' own record of its size does not follow
# the new content (EditedPlaceholderContractTest).
function Edit-Local([string]$Relative, [string]$Content) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Content)
    $stream = [System.IO.File]::Open((Get-LocalPath $Relative), 'Open', 'Write', 'ReadWrite')
    try {
        $stream.SetLength(0)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally {
        $stream.Dispose()
    }
}

# Free up space on a file, then open it again: it must come back whole, as
# the server has it.
function Assert-FreedAndOpened([string]$Relative, [string]$Expected) {
    $path = Get-LocalPath $Relative
    & attrib.exe -P +U $path | Out-Null
    if (-not (Wait-ForAttributes $path { ((Get-Item -LiteralPath $path -Force).Attributes -band 0x400000) -ne 0 })) {
        throw "$Relative was not freed."
    }
    & attrib.exe -U $path | Out-Null
    Assert-Local $Relative $Expected
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

# The one conflict copy of "$BaseName.*" in $Folder (named as Nextcloud names
# them: "name (conflicted copy 2026-11-03 175504).txt"), as a relative path.
function Get-ConflictCopy([string]$Folder, [string]$BaseName) {
    $copies = @(Get-ChildItem -LiteralPath (Get-LocalPath $Folder) -Force -File |
        Where-Object { $_.Name -like "$BaseName (conflicted copy *)*" })
    if ($copies.Count -ne 1) {
        # Not $copies.Name: with none found, strict mode rejects it and hides this message.
        throw "Expected one conflict copy of $BaseName in '$Folder', found $($copies.Count): $((@($copies | ForEach-Object { $_.Name })) -join ', ')"
    }
    return $(if ($Folder) { "$Folder/$($copies[0].Name)" } else { $copies[0].Name })
}

# What the server keeps (set by Start-E2EBackend; on a real server, from
# rclone's own report of its features).
# Whether the server can hold a folder with nothing in it (S3 cannot: a
# folder there is only a part of file names).
function Test-ServerKeepsEmptyFolders { return $script:E2EServerKeepsEmptyFolders }

# S3 keeps an MD5 of every file; the WebDAV server keeps no hashes (rclone
# reads them from WebDAV only on Nextcloud and ownCloud).
function Test-ServerHasHashes { return $script:E2EServerHashes }

# On S3 each file's time costs a request of its own, so a folder listed
# before is listed without times: a sync run looks up the time of each file
# it finds new or changed (the listing with times paid one per file, changed
# or not).
function Test-ServerListsWithoutTimes { return $script:E2EServerSlowTimes }

# A budget for what differs by server. -TimeLookups: files a sync run finds
# new or changed, each one lookup more on S3. -HashesLocal: a conflict, where
# the server keeps hashes, first hashes the local file (it may hold the
# server's content already, which is no conflict).
function Get-Budget([hashtable]$Budget, [int]$TimeLookups = 0, [switch]$HashesLocal) {
    $budget = $Budget.Clone()
    if ($TimeLookups -gt 0 -and (Test-ServerListsWithoutTimes)) { $budget.Lookups = $budget.Lookups + $TimeLookups }
    if ($HashesLocal -and (Test-ServerHasHashes)) { $budget.LocalHashes = 1 }
    return $budget
}

# The client's requests to the server for one file's own metadata (S3 HEADs,
# logged only on S3) since a point in rclone's log.
function Get-ServerHeads([long]$From, [string]$Under) {
    $text = Read-LogSlice $script:E2ERcloneLog $From (Get-LogLength $script:E2ERcloneLog)
    return @($text -split "`r?`n" | Where-Object { $_ -match "DEBUG : HEAD /\S*$([regex]::Escape($Under))" })
}

# operations/list calls in an rclone log slice whose remote path ends with
# $Under (for example "s29-dir"). It matches both "e2e:" style remotes
# (remote = "s29-dir") and bucketed ones (remote = "opencfdrive/s29-dir").
# A scenario uses it to prove a folder was - or was not - listed by a sync run.
function Get-RcloneListings([long]$From, [string]$Under) {
    $text = Read-LogSlice $script:E2ERcloneLog $From (Get-LogLength $script:E2ERcloneLog)
    # rclone logs the parameters as a Go map, the remote last:
    #   map[fs:e2e: opt:map[...] remote:opencfdrive/s29-dir]
    $pattern = 'rc: "operations/list": with parameters map\[.*\sremote:(?:\S*/)?' + [regex]::Escape($Under) + '\]'
    return @($text -split "`r?`n" | Where-Object { $_ -match $pattern })
}

# The sync state the shell reports for an item (System.StorageProviderCustomStates),
# as text, so a scenario can assert a state such as "Waiting to upload" or
# "Conflict copy" is shown or cleared. Returns "" when the shell cannot be asked.
# A file that came from the server has nothing to send, so no sync state may
# have been shown for it. The run that follows a change has dropped the
# client's own writes by the time Sync-Remote answers; the service logs every
# state it applies ("Explorer state for '<path>': <state>.").
function Assert-NoStateShown([string]$Relative) {
    Sync-Remote
    $name = [regex]::Escape((Get-LocalPath $Relative))
    $shown = @((Read-LogText $script:E2EServiceLog) -split "`r?`n" | Where-Object { $_ -match "Explorer state for '$name': " })
    if ($shown.Count -gt 0) { throw "$Relative came from the server, yet a sync state was shown for it: $($shown -join ' | ')" }
}

function Get-ShellState([string]$Relative) {
    $path = Get-LocalPath $Relative
    $parent = (Split-Path -Parent $path).Replace("'", "''")
    $name = (Split-Path -Leaf $path).Replace("'", "''")
    try {
        # Query from a fresh Shell.Application process. The in-process COM
        # object can cache the old property value while the provider's async
        # item-property writer is updating it.
        $command = "`$i = (New-Object -ComObject Shell.Application).Namespace('$parent').ParseName('$name'); " +
            "`$v = `$i.ExtendedProperty('System.StorageProviderCustomStates'); " +
            "if (`$null -eq `$v) { '' } elseif (`$v -is [byte[]]) { [Text.Encoding]::Unicode.GetString([byte[]]`$v) } elseif (`$v -is [string]) { [string]`$v } elseif (`$v -is [array]) { ((@(`$v) | ForEach-Object { if (`$null -ne `$_.Value) { [string]`$_.Value } else { [string]`$_ } }) -join ' ') } elseif (`$null -ne `$v.Value) { [string]`$v.Value } else { [string]`$v }"
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = 'powershell.exe'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add('-NoProfile')
        $start.ArgumentList.Add('-Command')
        $start.ArgumentList.Add($command)
        $process = [System.Diagnostics.Process]::Start($start)
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        return $output.Trim()
    } catch {
        return ""
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
        [switch]$CloudOnly,
        # Takes the server offline (Suspend-E2EBackend): only a server the run
        # started can be, so it is skipped against a real one.
        [switch]$PausesServer
    )
    [pscustomobject]@{ Id = $Id; Name = $Name; Run = $Run; Budget = $Budget; CloudOnly = [bool]$CloudOnly; PausesServer = [bool]$PausesServer }
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
        Uploads = 0; DownloadBytes = 0L; ServerRenames = 0; ServerDeletes = 0; ServerFolders = 0
        Listings = 0; Lookups = 0; LocalHashes = 0; OtherCalls = 0; Errors = 0
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
            '^operations/mkdir$' { $activity.ServerFolders++ }
            '^operations/list$' { $activity.Listings++ }
            '^operations/stat$' { $activity.Lookups++ }
            '^operations/hashsumfile$' { $activity.LocalHashes++ }
            default { $activity.OtherCalls++ }
        }
        if ($parameters.Length -gt 160) { $parameters = $parameters.Substring(0, 160) + "..." }
        $lines.Add("rc $call $parameters")
    }

    foreach ($line in ($ServiceLog -split "`r?`n")) {
        $text = $line.Trim()
        if ($text -match "FETCH_DATA '[^']*' (for .+ )?offset=\d+ length=(?<length>\d+)") {
            $activity.DownloadBytes += [long]$Matches['length']
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
    return "up {0}, down {1} B, mv {2}, rm {3}, mkdir {4}, list {5}, stat {6}, hash {7}, other {8}, err {9}" -f `
        $Counts.Uploads, $Counts.DownloadBytes, $Counts.ServerRenames, $Counts.ServerDeletes, $Counts.ServerFolders,
        $Counts.Listings, $Counts.Lookups, $Counts.LocalHashes, $Counts.OtherCalls, $Counts.Errors
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
            Assert-NoStateShown "s01.txt"
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
            Assert-NoStateShown "s04.txt"
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
            Invoke-ServerRclone @("copy", $staging, (Join-RemoteFolder "s14-many")) | Out-Null
            Remove-IfExists $staging
            Wait-Local "" { Test-Local "s14-many" } "s14-many did not appear locally."
            Wait-Local "s14-many" { @(Get-ChildItem -LiteralPath (Get-LocalPath "s14-many") -File).Count -eq 40 } "s14-many does not have 40 files locally."
            Assert-Local "s14-many/f40.txt" "s14-40"
        }
        # Conflicts keep both, as in Nextcloud and OneDrive: the server's
        # version keeps the name, the local edit is kept beside it as a
        # conflict copy and uploaded as a file of its own (its two lookups,
        # plus the check that found the conflict).
        New-E2EScenario "S15" "both sides edit the same file: both versions are kept" -Budget (Get-Budget @{ DownloadBytes = 33; Uploads = 1; Lookups = 3 } -TimeLookups 1 -HashesLocal) -CloudOnly {
            Set-RemoteFile "s15.txt" "s15-server-v1"
            Wait-Local "" { Test-Local "s15.txt" } "s15.txt did not appear locally."
            Assert-Local "s15.txt" "s15-server-v1"
            Set-RemoteFile "s15.txt" "s15-server-v2-longer"
            Write-Local "s15.txt" "s15-local-edit"
            Wait-Local "" { (Read-Local "s15.txt") -eq "s15-server-v2-longer" } "s15.txt did not take the server's version."
            $copy = Get-ConflictCopy "" "s15"
            Assert-Local $copy "s15-local-edit"
            Assert-RemoteText $copy "s15-local-edit"
            Assert-RemoteText "s15.txt" "s15-server-v2-longer"
        }
        # Nextcloud restores it: the edit is the only copy left.
        New-E2EScenario "S16" "server deletes a file that has an unsynced local edit: the edit is kept and uploaded again" -Budget (Get-Budget @{ DownloadBytes = 6; Uploads = 1; Lookups = 2 } -TimeLookups 1) -CloudOnly {
            Set-RemoteFile "s16.txt" "s16-v1"
            Wait-Local "" { Test-Local "s16.txt" } "s16.txt did not appear locally."
            Assert-Local "s16.txt" "s16-v1"
            Remove-RemoteFile "s16.txt"
            Write-Local "s16.txt" "s16-local-edit"
            Assert-RemoteText "s16.txt" "s16-local-edit"
            Sync-Remote
            Assert-Local "s16.txt" "s16-local-edit"
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
        # the client's rclone keeps running. An opened folder is listed from
        # disk and never waits; the first listing of a folder never opened
        # waits for the server in vain (the one error allowed) and puts the
        # drive offline; from then on, as in Nextcloud and OneDrive, folders
        # show what is local at once. Using the drive checks the server again,
        # so back online opening it lists it.
        New-E2EScenario "S18" "offline: an opened folder keeps what is local, an unopened one is empty, back online lists the server" -Budget @{ DownloadBytes = 9; Errors = 1 } -CloudOnly -PausesServer {
            Set-RemoteFile "s18-seen/cached.txt" "seen"
            Set-RemoteFile "s18-unseen/later.txt" "later"
            Wait-Local "" { (Test-Local "s18-seen") -and (Test-Local "s18-unseen") } "s18-seen and s18-unseen did not appear locally."
            Wait-Local "s18-seen" { Test-Local "s18-seen/cached.txt" } "s18-seen\cached.txt did not appear locally."
            Assert-Local "s18-seen/cached.txt" "seen"

            Suspend-E2EBackend
            try {
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
        # Nextcloud keeps the edited file, and its folder, and uploads it again.
        New-E2EScenario "S19" "server deletes a folder while a file in it has an unsynced local edit: that file stays and goes back, the rest goes" -Budget (Get-Budget @{ DownloadBytes = 6; Uploads = 1; Lookups = 2 } -TimeLookups 1) -CloudOnly {
            Set-RemoteFile "s19-dir/edited.txt" "s19-v1"
            Set-RemoteFile "s19-dir/other.txt" "other"
            Wait-Local "" { Test-Local "s19-dir" } "s19-dir did not appear locally."
            Wait-Local "s19-dir" { (Test-Local "s19-dir/edited.txt") -and (Test-Local "s19-dir/other.txt") } "s19-dir's files did not appear locally."
            Assert-Local "s19-dir/edited.txt" "s19-v1"
            Remove-RemoteFolder "s19-dir"
            Write-Local "s19-dir/edited.txt" "s19-local-edit"
            Assert-RemoteText "s19-dir/edited.txt" "s19-local-edit"
            Wait-Local "s19-dir" { -not (Test-Local "s19-dir/other.txt") } "s19-dir\other.txt is still there locally."
            Assert-Local "s19-dir/edited.txt" "s19-local-edit"
            if (Test-RemoteItem "s19-dir/other.txt") { throw "s19-dir/other.txt came back on the server." }
        }
        # A delete here does not throw away a change the server made since
        # this client last saw the file: that version comes back instead.
        New-E2EScenario "S20" "client deletes a file the server changed meanwhile: the server's version comes back" -Budget (Get-Budget @{ DownloadBytes = 15; Lookups = 1 } -TimeLookups 2) -CloudOnly {
            Set-RemoteFile "s20.txt" "s20-v1"
            Wait-Local "" { Test-Local "s20.txt" } "s20.txt did not appear locally."
            Set-RemoteFile "s20.txt" "s20-version-two"
            Remove-Item -LiteralPath (Get-LocalPath "s20.txt") -Force
            Wait-Local "" { Test-Local "s20.txt" } "s20.txt did not come back with the server's version."
            Assert-Local "s20.txt" "s20-version-two"
            Assert-RemoteText "s20.txt" "s20-version-two"
        }
        # The same new path on both sides, different files (Nextcloud's
        # new/new conflict): the server's file keeps the name, this PC's is
        # kept as a conflict copy and uploaded.
        New-E2EScenario "S21" "client and server add different files at the same new path: both are kept" -Budget (Get-Budget @{ DownloadBytes = 10; Uploads = 1; Lookups = 3 } -HashesLocal) -CloudOnly {
            Set-RemoteFile "s21.txt" "s21-server"
            Write-Local "s21.txt" "s21-local"
            Wait-Local "" { (Read-Local "s21.txt") -eq "s21-server" } "s21.txt did not take the server's file."
            $copy = Get-ConflictCopy "" "s21"
            Assert-Local $copy "s21-local"
            Assert-RemoteText $copy "s21-local"
            Assert-RemoteText "s21.txt" "s21-server"
        }
        # A folder Windows has listed stays fully populated: Explorer shows it
        # as available, not with the cloud icon, and the background sync
        # keeps it current.
        New-E2EScenario "S22" "a folder once listed shows as available, not online-only, and gets what the server adds" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s22-dir/f.txt" "s22"
            Wait-Local "" { Test-Local "s22-dir" } "s22-dir did not appear locally."
            if (-not (Test-OnlineOnlyFolder "s22-dir")) { throw "s22-dir shows as available before it was ever listed." }
            Wait-Local "s22-dir" { Test-Local "s22-dir/f.txt" } "s22-dir\f.txt did not appear locally."
            # Keep one file locally so this folder exercises the local-folder
            # polling path; online-only folders are intentionally refreshed only
            # when they are opened again after the poll interval.
            Assert-Local "s22-dir/f.txt" "s22"
            if (Test-OnlineOnlyFolder "s22-dir") { throw "s22-dir still shows as online-only after it was listed." }
            Set-RemoteFile "s22-dir/g.txt" "g"
            Wait-Local "s22-dir" { Test-Local "s22-dir/g.txt" } "s22-dir\g.txt, added on the server later, did not appear."
            if (Test-OnlineOnlyFolder "s22-dir") { throw "s22-dir went back to online-only." }
        }
        New-E2EScenario "S23" "server adds and renames files with Arabic names" -Budget @{ DownloadBytes = 20 } -CloudOnly {
            $folder = "s23-" + (Get-ArabicName 0x0645, 0x062C, 0x0644, 0x062F)          # "folder"
            $file = (Get-ArabicName 0x062A, 0x0642, 0x0631, 0x064A, 0x0631) + ".txt"     # "report"
            $renamed = (Get-ArabicName 0x0645, 0x0644, 0x0641) + ".txt"                 # "file"
            $text = Get-ArabicName 0x0645, 0x0631, 0x062D, 0x0628, 0x0627               # "hello"
            Set-RemoteFile "$folder/$file" $text
            Wait-Local "" { Test-Local $folder } "$folder did not appear locally."
            Wait-Local $folder { Test-Local "$folder/$file" } "$folder\$file did not appear locally."
            Assert-Local "$folder/$file" $text
            Move-RemoteFile "$folder/$file" "$folder/$renamed"
            Wait-Local $folder { (-not (Test-Local "$folder/$file")) -and (Test-Local "$folder/$renamed") } "The rename did not reach the local side."
            Assert-Local "$folder/$renamed" $text
        }
        # S15 and S16 with the edit saved in place (Edit-Local), as Notepad
        # does: the file is still a placeholder, not a plain file, when the
        # server's change meets it.
        New-E2EScenario "S24" "both sides edit the same file, the local edit saved in place: both versions are kept" -Budget (Get-Budget @{ DownloadBytes = 33; Uploads = 1; Lookups = 3 } -TimeLookups 1 -HashesLocal) -CloudOnly {
            Set-RemoteFile "s24.txt" "s24-server-v1"
            Wait-Local "" { Test-Local "s24.txt" } "s24.txt did not appear locally."
            Assert-Local "s24.txt" "s24-server-v1"
            Set-RemoteFile "s24.txt" "s24-server-v2-longer"
            Edit-Local "s24.txt" "s24-local-edit"
            Wait-Local "" { (Read-Local "s24.txt") -eq "s24-server-v2-longer" } "s24.txt did not take the server's version."
            $copy = Get-ConflictCopy "" "s24"
            Assert-Local $copy "s24-local-edit"
            Assert-RemoteText $copy "s24-local-edit"
            Assert-RemoteText "s24.txt" "s24-server-v2-longer"
        }
        New-E2EScenario "S25" "server deletes a file with an unsynced edit saved in place: the edit is uploaded again, and opens whole after Free up space" -Budget (Get-Budget @{ DownloadBytes = 20; Uploads = 1; Lookups = 2 } -TimeLookups 1) -CloudOnly {
            Set-RemoteFile "s25.txt" "s25-v1"
            Wait-Local "" { Test-Local "s25.txt" } "s25.txt did not appear locally."
            Assert-Local "s25.txt" "s25-v1"
            Remove-RemoteFile "s25.txt"
            Edit-Local "s25.txt" "s25-local-edit"
            Assert-RemoteText "s25.txt" "s25-local-edit"
            Sync-Remote
            Assert-Local "s25.txt" "s25-local-edit"
            Assert-FreedAndOpened "s25.txt" "s25-local-edit"
        }
        # Nextcloud's check before a conflict copy: the same content on both
        # sides is no conflict. Only a remote that keeps hashes can tell; on
        # the others the edit is kept as a conflict copy, which is safe.
        New-E2EScenario "S26" "both sides make the same edit: no conflict copy where the server keeps hashes" -Budget $(
            if (Test-ServerHasHashes) { @{ DownloadBytes = 6; Lookups = 3; LocalHashes = 1 } }
            else { @{ DownloadBytes = 6; Uploads = 1; Lookups = 3 } }) -CloudOnly {
            Set-RemoteFile "s26.txt" "s26-v1"
            Wait-Local "" { Test-Local "s26.txt" } "s26.txt did not appear locally."
            Assert-Local "s26.txt" "s26-v1"
            Set-RemoteFile "s26.txt" "s26-same-edit"
            Edit-Local "s26.txt" "s26-same-edit"
            Sync-Remote
            if (Test-ServerHasHashes) {
                $copies = @(Get-ChildItem -LiteralPath (Get-LocalPath "") -Force -File | Where-Object { $_.Name -like "s26 (conflicted copy *)*" })
                if ($copies.Count -gt 0) { throw "A conflict copy was made of a file with the same content on both sides: $($copies.Name -join ', ')" }
                Assert-Local "s26.txt" "s26-same-edit"
            } else {
                Assert-Local (Get-ConflictCopy "" "s26") "s26-same-edit"
            }
            Assert-RemoteText "s26.txt" "s26-same-edit"
        }
        # A server writing the same content again (a re-upload, a copy) has
        # not changed the file: where the remote keeps hashes it stays here.
        # Elsewhere only the new time can tell, and the file goes online-only.
        New-E2EScenario "S27" "server writes a file again with the same content: still local where the server keeps hashes" -Budget $(
            if (Test-ServerHasHashes) { @{ DownloadBytes = 8 } } else { @{ DownloadBytes = 16 } }) -CloudOnly {
            Set-RemoteFile "s27.txt" "s27-same"
            Wait-Local "" { Test-Local "s27.txt" } "s27.txt did not appear locally."
            Assert-Local "s27.txt" "s27-same"
            Set-RemoteFile "s27.txt" "s27-same"
            Sync-Remote
            Assert-Local "s27.txt" "s27-same"
        }
        # On S3 a listing with times costs one request per file (a HEAD).
        # A folder listed before is listed without them: its unchanged files
        # are told by their MD5, so a sync run asks none of them anything.
        New-E2EScenario "S28" "a sync run of a folder listed before asks no unchanged file for its time" -Budget @{ DownloadBytes = 1 } -CloudOnly {
            Set-RemoteFile "s28-dir/a.txt" "a"
            Set-RemoteFile "s28-dir/b.txt" "b"
            Set-RemoteFile "s28-dir/c.txt" "c"
            Wait-Local "" { Test-Local "s28-dir" } "s28-dir did not appear locally."
            Wait-Local "s28-dir" { (Test-Local "s28-dir/a.txt") -and (Test-Local "s28-dir/b.txt") -and (Test-Local "s28-dir/c.txt") } "s28-dir's files did not appear locally."
            # Download one file: a folder holding anything local is listed
            # every sync run, so this exercises the listed-folder path rather
            # than the online-only one.
            Assert-Local "s28-dir/a.txt" "a"
            $from = Get-LogLength $script:E2ERcloneLog
            Sync-Remote
            $heads = @(Get-ServerHeads $from "s28-dir/")
            if ($heads.Count -gt 0) { throw "The sync run asked $($heads.Count) unchanged file(s) of s28-dir for their time: $($heads -join ' | ')" }
        }

        # A folder nobody ever opened stays online-only: no sync run lists it,
        # and opening it for the first time lists it from the server.
        New-E2EScenario "S29" "a folder never opened is not listed by sync runs, then listed when it is opened" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s29-dir/f.txt" "s29"
            # Do not probe the directory with Test-Path/Get-Item before the
            # listing check: Cloud Files may interpret that probe as opening
            # the folder. The root is listed by each sync, but an online-only
            # child is intentionally not listed by those runs.
            $from = Get-LogLength $script:E2ERcloneLog
            Sync-Remote
            Sync-Remote
            $listings = @(Get-RcloneListings $from "s29-dir")
            if ($listings.Count -gt 0) { throw "A sync run listed $($listings.Count) time(s) the never-opened s29-dir: $($listings -join ' | ')" }
            # This is the first operation directed at the child folder and is
            # the equivalent of opening it in Explorer.
            Update-LocalFolder "s29-dir"
            Wait-Local "s29-dir" { Test-Local "s29-dir/f.txt" } "s29-dir\f.txt did not appear when s29-dir was opened."
            Assert-Local "s29-dir/f.txt" "s29"
        }

        # A folder holding a downloaded file is listed every run, so a server
        # change reaches it without waiting for the folder to be opened again.
        New-E2EScenario "S30" "a downloaded-file folder still gets a server change from a sync run" -Budget @{ DownloadBytes = 9 } -CloudOnly {
            Set-RemoteFile "s30-dir/f.txt" "s30"
            Wait-Local "" { Test-Local "s30-dir" } "s30-dir did not appear locally."
            Wait-Local "s30-dir" { Test-Local "s30-dir/f.txt" } "s30-dir\f.txt did not appear locally."
            Assert-Local "s30-dir/f.txt" "s30"
            Set-RemoteFile "s30-dir/f.txt" "s30-v2"
            $from = Get-LogLength $script:E2ERcloneLog
            Sync-Remote
            Assert-Local "s30-dir/f.txt" "s30-v2"
        }

        # Offline, a folder listed before shows its local files at once and a
        # new file made there reaches the server once it answers.
        New-E2EScenario "S31" "offline: a listed folder opens with its files and a new file made there is uploaded" -Budget @{ DownloadBytes = 3; Uploads = 1 } -CloudOnly -PausesServer {
            Set-RemoteFile "s31-dir/f.txt" "s31"
            Wait-Local "" { Test-Local "s31-dir" } "s31-dir did not appear locally."
            Wait-Local "s31-dir" { Test-Local "s31-dir/f.txt" } "s31-dir\f.txt did not appear locally."
            Assert-Local "s31-dir/f.txt" "s31"

            Suspend-E2EBackend
            try {
                # Make the client observe the outage before creating the file;
                # otherwise the first sync can still upload it before the
                # connection monitor has switched to offline mode.
                Sync-Remote -TimeoutSeconds 60
                $clock = [System.Diagnostics.Stopwatch]::StartNew()
                Update-LocalFolder "s31-dir"
                $offlineMs = $clock.ElapsedMilliseconds
                if (-not (Test-Local "s31-dir/f.txt")) { throw "Offline, s31-dir\f.txt is gone." }
                Write-Local "s31-dir/offline.txt" "offline"
                $kept = Wait-ForCondition {
                    (Read-LogText $script:E2EServiceLog).Contains("Offline: 's31-dir/offline.txt' is uploaded once the server answers.")
                } -TimeoutSeconds 30 -PollMilliseconds 200
                if (-not $kept) { throw "The offline file was not kept for the server." }
                Write-E2ELog "  offline listing of s31-dir took $offlineMs ms"
            } finally {
                Resume-E2EBackend
            }
            Sync-Remote
            Wait-Remote { (Get-RemoteFile "s31-dir/offline.txt") -eq "offline" } "The offline file did not reach the server."
            Assert-Local "s31-dir/offline.txt" "offline"
        }

        # A folder opened once is not re-listed on every sync run (a run may
        # mark it to be listed when next opened), and keeps its files.
        New-E2EScenario "S32" "a folder opened once is not re-listed by sync runs and keeps its files" -Budget @{ DownloadBytes = 3 } -CloudOnly {
            Set-RemoteFile "s32-dir/f.txt" "s32"
            Wait-Local "" { Test-Local "s32-dir" } "s32-dir did not appear locally."
            Wait-Local "s32-dir" { Test-Local "s32-dir/f.txt" } "s32-dir\f.txt did not appear locally."
            $from = Get-LogLength $script:E2ERcloneLog
            Sync-Remote
            Sync-Remote
            $listings = @(Get-RcloneListings $from "s32-dir")
            if ($listings.Count -gt 0) { throw "After being opened, s32-dir was listed $($listings.Count) time(s) by sync runs: $($listings -join ' | ')" }
            if (-not (Test-Local "s32-dir/f.txt")) { throw "s32-dir\f.txt is gone although s32-dir stays populated." }
        }

        # An offline edit is shown as "Waiting to upload" until it reaches the
        # server, then the state is cleared. Sync root state is a custom item
        # property the shell reports (Change 3).
        New-E2EScenario "S33" "offline: an edit shows 'waiting to upload' until uploaded, then clears" -Budget @{ DownloadBytes = 3; Uploads = 1 } -CloudOnly -PausesServer {
            Set-RemoteFile "s33.txt" "s33"
            Wait-Local "" { Test-Local "s33.txt" } "s33.txt did not appear locally."
            Assert-Local "s33.txt" "s33"

            Suspend-E2EBackend
            try {
                # Establish offline mode before the edit so it cannot race with
                # the final online upload and skip the waiting state.
                Sync-Remote -TimeoutSeconds 60
                [System.IO.File]::AppendAllText((Get-LocalPath "s33.txt"), "-edited")
                # The state is written on a background thread, so poll for it.
                $marked = Wait-ForCondition {
                    # The service log records the successful item-property
                    # write; the contract test separately reads the same
                    # property through Shell.Application. Explorer's COM
                    # property cache is not reliable while the provider is
                    # offline, so do not make this timing check depend on it.
                    (Read-LogText $script:E2EServiceLog) -match "Explorer state for .*\\s33\.txt': Waiting to upload\."
                } -TimeoutSeconds 30 -PollMilliseconds 200
                if (-not $marked) { throw "s33.txt is not marked 'waiting to upload' while its edit is offline." }

                # The app's icon shows it too: offline, with the edit waiting.
                Wait-ServicePulse { param($pulse) $pulse.State -eq "Offline" -and $pulse.Activity.Waiting -ge 1 } `
                    "The service does not report itself offline with s33.txt's edit waiting." | Out-Null
            } finally {
                Resume-E2EBackend
            }
            Sync-Remote
            $cleared = Wait-ForCondition {
                (Read-LogText $script:E2EServiceLog) -match "Cleared Explorer state for .*\\s33\.txt'."
            } -TimeoutSeconds 30 -PollMilliseconds 200
            if (-not $cleared) { throw "s33.txt is still 'waiting to upload' after its edit was uploaded." }
            Wait-Remote { (Get-RemoteFile "s33.txt") -eq "s33-edited" } "The offline edit did not reach the server."
            Assert-Local "s33.txt" "s33-edited"
            Wait-ServicePulse { param($pulse) $pulse.State -eq "Syncing" -and $pulse.Activity.Waiting -eq 0 } `
                "The service still reports s33.txt's edit as waiting after it was uploaded." | Out-Null
        }

        # A conflict copy is marked "Conflict copy" in the shell. The server's
        # version keeps the name; this PC's edit is kept beside it and uploaded.
        New-E2EScenario "S34" "a conflict copy is marked 'conflict copy'" -Budget (Get-Budget @{ DownloadBytes = 15; Uploads = 1 } -HashesLocal) -CloudOnly {
            Set-RemoteFile "s34.txt" "s34-server-v1"
            Wait-Local "" { Test-Local "s34.txt" } "s34.txt did not appear locally."
            Assert-Local "s34.txt" "s34-server-v1"
            # The server changes first, as in S15, so the check before the
            # upload always sees it. Edited first, the edit can reach the
            # server before the change does (writing the server starts an
            # rclone process, which can take longer than the client's one
            # second before it sends an edit): then it is simply uploaded and
            # overwritten, with no conflict. And to another length: a server
            # without hashes (WebDAV here) tells a change by size and time to
            # within a second, and both versions are written within one.
            Set-RemoteFile "s34.txt" "s34-server-v2-longer"
            Write-Local "s34.txt" "s34-local-edit"
            Wait-Local "" {
                @(Get-ChildItem -LiteralPath (Get-LocalPath "") -Force -File | Where-Object { $_.Name -like "s34 (conflicted copy *)*" }).Count -eq 1
            } "No conflict copy of s34.txt was kept."
            $copy = Get-ConflictCopy "" "s34"
            # The state is written on a background thread. Use the successful
            # provider write in the service log here; the shell contract test
            # verifies the serialized Explorer property. It stays after the
            # copy is uploaded, until the copy is renamed or deleted.
            $copyName = [regex]::Escape($copy)
            $marked = Wait-ForCondition {
                (Read-LogText $script:E2EServiceLog) -match "Explorer state for .*\\$copyName': Conflict copy\."
            } -TimeoutSeconds 10 -PollMilliseconds 100
            if (-not $marked) { throw "The conflict copy $copy is not marked 'conflict copy' in the provider." }
            Assert-Local $copy "s34-local-edit"
            Assert-RemoteText "s34.txt" "s34-server-v2-longer"
        }

        # A folder holding nothing local is not listed by sync runs: a run
        # marks it to be listed when it is next opened, and opening it shows
        # what changed on the server meanwhile.
        New-E2EScenario "S35" "an online-only folder is marked by a sync run, and opening it shows the server's change" -Budget @{ DownloadBytes = 15 } -CloudOnly {
            Set-RemoteFile "s35-dir/f.txt" "s35-v1"
            Wait-Local "" { Test-Local "s35-dir" } "s35-dir did not appear locally."
            Wait-Local "s35-dir" { Test-Local "s35-dir/f.txt" } "s35-dir\f.txt did not appear locally."
            Set-RemoteFile "s35-dir/f.txt" "s35-version-two"
            $from = Get-LogLength $script:E2ERcloneLog
            Sync-Remote
            $listings = @(Get-RcloneListings $from "s35-dir")
            if ($listings.Count -gt 0) { throw "A sync run listed the online-only s35-dir $($listings.Count) time(s): $($listings -join ' | ')" }
            # Read from its parent's entry, so it does not open the folder.
            if (-not (Test-OnlineOnlyFolder "s35-dir")) { throw "The sync run did not mark s35-dir to be listed when it is next opened." }

            Wait-Local "s35-dir" { (Get-LocalLength "s35-dir/f.txt") -eq 15 } "Opened again, s35-dir did not show the server's change to f.txt."
            if (@(Get-RcloneListings $from "s35-dir").Count -eq 0) { throw "Opening s35-dir did not list it from the server." }
            Assert-Local "s35-dir/f.txt" "s35-version-two"
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
        # Each new folder is created on the server first, as Nextcloud does (MKCOL).
        New-E2EScenario "C03" "client adds nested folders with a file" -Budget @{ Uploads = 1; Lookups = 2; ServerFolders = 2 } {
            New-Item -ItemType Directory -Path (Get-LocalPath "c03-dir/sub") -Force | Out-Null
            Write-Local "c03-dir/sub/c03.txt" "c03"
            Assert-RemoteText "c03-dir/sub/c03.txt" "c03"
        }
        New-E2EScenario "C04" "client edits a file that was never opened" -Budget @{ DownloadBytes = 10; Uploads = 1; Lookups = 2 } {
            [System.IO.File]::AppendAllText((Get-LocalPath "client/edit-me.txt"), "-edited")
            Assert-RemoteText "client/edit-me.txt" "edit-me-v1-edited"
        }
        New-E2EScenario "C05" "client saves a file by replacing it, same size" -Budget @{ Uploads = 1; Lookups = 2 } {
            Write-Local "client/same-size.txt" "bbbb"
            Assert-RemoteText "client/same-size.txt" "bbbb"
        }
        New-E2EScenario "C06" "client saves a file by replacing it, shorter" -Budget @{ Uploads = 1; Lookups = 2 } {
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
            # A sync run sends every local change reported before it.
            if ($script:E2EIsCloud) { Sync-Remote } else { Wait-ServiceQuiet }
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
        New-E2EScenario "C17" "client adds 40 files at once" -Budget @{ Uploads = 40; Lookups = 80; ServerFolders = 1 } {
            New-Item -ItemType Directory -Path (Get-LocalPath "c17-many") -Force | Out-Null
            1..40 | ForEach-Object { Write-Local "c17-many/f$_.txt" "c17-$_" }
            Wait-Remote { @(Get-RemoteListing | Where-Object { $_ -like "c17-many/*.txt" }).Count -eq 40 } "The server does not have all 40 files of c17-many."
            Assert-RemoteText "c17-many/f40.txt" "c17-40"
        }
        # No sync run in between, so only the check before the upload can see
        # the server's change. The file is opened before the server changes
        # it, so saving looks up no name. Both versions are kept.
        # The wait reads only the file's size, through its folder: a read of
        # the file at the moment the client puts the server's version in
        # place makes that fail ("in use by another process"), and in mock
        # mode nothing tries it again.
        New-E2EScenario "C18" "the server changes a file that is open locally, then it is saved: both versions are kept" -Budget (Get-Budget @{ DownloadBytes = 29; Uploads = 1; Lookups = 3 } -HashesLocal) {
            Wait-Local "client" { Test-Local "client/conflict.txt" } "client\conflict.txt did not appear locally."
            $c18File = Get-LocalPath "client/conflict.txt"
            $stream = [System.IO.File]::Open($c18File, 'Open', 'ReadWrite', 'ReadWrite')
            try {
                Set-RemoteFile "client/conflict.txt" "conflict-server-v2"
                $edit = [System.Text.Encoding]::UTF8.GetBytes("conflict-local-edit")
                $stream.SetLength(0)
                $stream.Write($edit, 0, $edit.Length)
            } finally {
                $stream.Dispose()
            }

            # The copy is made first, then the server's version takes the name.
            $c18Size = [System.Text.Encoding]::UTF8.GetByteCount("conflict-server-v2")
            $won = Wait-ForAttributes $c18File {
                (Get-Item -LiteralPath $c18File -Force).Length -eq $c18Size -and
                @(Get-ChildItem -LiteralPath (Get-LocalPath "client") -Force -File | Where-Object { $_.Name -like "conflict (conflicted copy *)*" }).Count -eq 1
            } -TimeoutSeconds 30 -NotifyFilter ([System.IO.NotifyFilters]'Attributes, Size, LastWrite')
            if (-not $won) { throw "client\conflict.txt did not take the server's version (it reads '$(Read-Local "client/conflict.txt")')." }
            Assert-Local "client/conflict.txt" "conflict-server-v2"
            Assert-RemoteText "client/conflict.txt" "conflict-server-v2"
            $copy = Get-ConflictCopy "client" "conflict"
            Assert-Local $copy "conflict-local-edit"
            Assert-RemoteText $copy "conflict-local-edit"
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
        # Folders are created on the server like files are uploaded, empty
        # ones too (Nextcloud and OneDrive sync them). S3 keeps no empty
        # folders; there the folder stays here, never taken for deleted.
        New-E2EScenario "C21" "client creates an empty folder" -Budget @{ ServerFolders = 1 } {
            New-Item -ItemType Directory -Path (Get-LocalPath "c21-empty") | Out-Null
            if (Test-ServerKeepsEmptyFolders) {
                Wait-Remote { Test-RemoteItem "c21-empty" } "The empty folder c21-empty did not reach the server."
            } else {
                Wait-ServiceQuiet
            }
            Sync-Remote
            if (-not (Test-Local "c21-empty")) { throw "c21-empty is gone locally after a sync run." }
        }
        New-E2EScenario "C22" "client creates a folder and renames it right away (Explorer's New folder)" -Budget @{ ServerFolders = 1 } {
            New-Item -ItemType Directory -Path (Get-LocalPath "c22-New folder") | Out-Null
            Rename-Item -LiteralPath (Get-LocalPath "c22-New folder") -NewName "c22-renamed"
            if (Test-ServerKeepsEmptyFolders) {
                Wait-Remote { Test-RemoteItem "c22-renamed" } "c22-renamed did not reach the server."
            }
            Wait-ServiceQuiet
            if (Test-RemoteItem "c22-New folder") { throw "The folder's first name reached the server." }
        }
        # Moved in from outside, a folder is reported alone, without what it holds.
        New-E2EScenario "C23" "client moves a folder with files and a subfolder in from outside the drive" -Budget @{ Uploads = 2; Lookups = 4; ServerFolders = 2 } {
            $outside = Get-OutsidePath "c23-moved-in"
            New-Item -ItemType Directory -Path (Join-Path $outside "sub") -Force | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $outside "a.txt"), "c23-a")
            [System.IO.File]::WriteAllText((Join-Path $outside "sub\b.txt"), "c23-b")
            Move-Item -LiteralPath $outside -Destination (Get-LocalPath "c23-moved-in")
            Assert-RemoteText "c23-moved-in/a.txt" "c23-a"
            Assert-RemoteText "c23-moved-in/sub/b.txt" "c23-b"
        }
        # Offline the way a lost network is (see S18). A sync run finds the
        # server gone, then a delete and a rename made here are kept, in the
        # order made, and sent once it answers; nothing comes back meanwhile.
        New-E2EScenario "C24" "offline: the client deletes and renames files; both reach the server once it answers" -Budget @{ ServerDeletes = 1; ServerRenames = 1; DownloadBytes = 14; Lookups = 1 } -CloudOnly -PausesServer {
            Wait-Local "client" { (Test-Local "client/offline-delete.txt") -and (Test-Local "client/offline-rename.txt") } "The offline fixtures did not appear locally."
            Suspend-E2EBackend
            try {
                Sync-Remote -TimeoutSeconds 60
                Remove-Item -LiteralPath (Get-LocalPath "client/offline-delete.txt") -Force
                Rename-Item -LiteralPath (Get-LocalPath "client/offline-rename.txt") -NewName "offline-renamed.txt"
                $kept = Wait-ForCondition {
                    $log = Read-LogText $script:E2EServiceLog
                    $log.Contains("Offline: 'client/offline-delete.txt' is deleted on the server once it answers.") -and
                        $log.Contains("Offline: 'client/offline-rename.txt' is renamed to 'client/offline-renamed.txt'")
                } -TimeoutSeconds 30 -PollMilliseconds 200
                if (-not $kept) { throw "The delete and rename were not kept for the server while offline." }
            } finally {
                Resume-E2EBackend
            }
            Wait-Remote { Sync-Remote; -not (Test-RemoteItem "client/offline-delete.txt") } "The delete made offline did not reach the server."
            Wait-Remote { (Test-RemoteItem "client/offline-renamed.txt") -and -not (Test-RemoteItem "client/offline-rename.txt") } "The rename made offline did not reach the server."
            Sync-Remote
            if (Test-Local "client/offline-delete.txt") { throw "The file deleted offline came back locally." }
            Assert-Local "client/offline-renamed.txt" "offline-rename"
        }
        # Never the only copy of a change: OneDrive uploads it first, then frees it.
        # Opened again, it comes back whole: the edit grew it in place, and the
        # platform's record of its size is the one the download fills.
        New-E2EScenario "C25" "free up space on a file with an unsynced edit: it is uploaded first, then freed, and opens whole" -Budget @{ DownloadBytes = 47; Uploads = 1; Lookups = 2 } -CloudOnly {
            $path = Get-LocalPath "client/free-after-upload.txt"
            Assert-Local "client/free-after-upload.txt" "free-after-upload-v1"
            [System.IO.File]::AppendAllText($path, "-edited")
            & attrib.exe -P +U $path | Out-Null
            Assert-RemoteText "client/free-after-upload.txt" "free-after-upload-v1-edited"
            if (-not (Wait-ForAttributes $path { ((Get-Item -LiteralPath $path -Force).Attributes -band 0x400000) -ne 0 })) {
                throw "client\free-after-upload.txt was not freed once its edit was uploaded."
            }
            & attrib.exe -U $path | Out-Null
            Assert-Local "client/free-after-upload.txt" "free-after-upload-v1-edited"
        }
        New-E2EScenario "C26" "client creates a folder, an empty folder and a file with Arabic names, and renames the file" -Budget @{ Uploads = 1; Lookups = 2; ServerFolders = 2; ServerRenames = 1 } {
            $folder = "c26-" + (Get-ArabicName 0x0645, 0x062C, 0x0644, 0x062F)          # "folder"
            $empty = Get-ArabicName 0x0641, 0x0627, 0x0631, 0x063A                      # "empty"
            $file = (Get-ArabicName 0x062A, 0x0642, 0x0631, 0x064A, 0x0631) + ".txt"     # "report"
            $renamed = (Get-ArabicName 0x0645, 0x0644, 0x0641) + ".txt"                 # "file"
            $text = Get-ArabicName 0x0645, 0x0631, 0x062D, 0x0628, 0x0627               # "hello"
            New-Item -ItemType Directory -Path (Get-LocalPath "$folder/$empty") -Force | Out-Null
            Write-Local "$folder/$file" $text
            Assert-RemoteText "$folder/$file" $text
            if (Test-ServerKeepsEmptyFolders) {
                Wait-Remote { Test-RemoteItem "$folder/$empty" } "The empty folder $folder/$empty did not reach the server."
            }
            Rename-Item -LiteralPath (Get-LocalPath "$folder/$file") -NewName $renamed
            Assert-RemoteText "$folder/$renamed" $text
            Assert-RemoteMissing "$folder/$file"
        }
        # Saved in place, as Notepad does (Edit-Local): the file stays a
        # placeholder, so the upload gives Windows the new size, or Free up
        # space would bring back only the old size's worth and zeros after
        # it. C05 and C06 save by replacing the file instead.
        New-E2EScenario "C27" "client edits a downloaded file in place, same size: it is uploaded, and opens whole after Free up space" -Budget @{ DownloadBytes = 14; Uploads = 1; Lookups = 2 } {
            Assert-Local "client/edit-same.txt" "same-v1"
            Edit-Local "client/edit-same.txt" "same-v2"
            Assert-RemoteText "client/edit-same.txt" "same-v2"
            if ($script:E2EIsCloud) { Assert-FreedAndOpened "client/edit-same.txt" "same-v2" }
        }
        New-E2EScenario "C28" "client edits a downloaded file in place, shorter: it is uploaded, and opens whole after Free up space" -Budget @{ DownloadBytes = 28; Uploads = 1; Lookups = 2 } {
            Assert-Local "client/edit-shorter.txt" "edit-shorter-original"
            Edit-Local "client/edit-shorter.txt" "shorter"
            Assert-RemoteText "client/edit-shorter.txt" "shorter"
            if ($script:E2EIsCloud) { Assert-FreedAndOpened "client/edit-shorter.txt" "shorter" }
        }
        New-E2EScenario "C29" "client edits a downloaded file in place, longer: it is uploaded, and opens whole after Free up space" -Budget @{ DownloadBytes = 23; Uploads = 1; Lookups = 2 } {
            Assert-Local "client/edit-longer.txt" "grow-v1"
            Edit-Local "client/edit-longer.txt" "grow-v1-and-more"
            Assert-RemoteText "client/edit-longer.txt" "grow-v1-and-more"
            if ($script:E2EIsCloud) { Assert-FreedAndOpened "client/edit-longer.txt" "grow-v1-and-more" }
        }
        # Offline the way a lost network is (see S18): the edit waits, as in
        # Nextcloud and OneDrive, and is uploaded once the server answers.
        New-E2EScenario "C30" "offline: the client edits a file in place; it is uploaded once the server answers" -Budget @{ DownloadBytes = 15; Uploads = 1; Lookups = 2 } -CloudOnly -PausesServer {
            Assert-Local "client/offline-edit.txt" "offline-edit-v1"
            Suspend-E2EBackend
            try {
                Sync-Remote -TimeoutSeconds 60
                Edit-Local "client/offline-edit.txt" "offline-edit-v2-saved-offline"
                $kept = Wait-ForCondition {
                    (Read-LogText $script:E2EServiceLog).Contains("Offline: 'client/offline-edit.txt' is uploaded once the server answers.")
                } -TimeoutSeconds 30 -PollMilliseconds 200
                if (-not $kept) { throw "The edit made offline was not kept for the server." }
            } finally {
                Resume-E2EBackend
            }
            Assert-RemoteText "client/offline-edit.txt" "offline-edit-v2-saved-offline"
            Assert-Local "client/offline-edit.txt" "offline-edit-v2-saved-offline"
        }
        # A program can list a folder the moment it is renamed (Explorer's
        # navigation pane does, to show whether it has subfolders), before
        # the rename has reached the server. Listed from where the server
        # still has it, the folder and the one inside it show their files;
        # answered with nothing, they would stay empty (CI's C11 failure).
        New-E2EScenario "C31" "client renames a folder never opened and lists it at once, as Explorer can: its files are there" -Budget @{ ServerRenames = 1; ServerDeletes = 1; DownloadBytes = 2 } -CloudOnly {
            Rename-Item -LiteralPath (Get-LocalPath "client-folder-listed") -NewName "client-folder-listed-renamed"
            $names = @(Get-ChildItem -LiteralPath (Get-LocalPath "client-folder-listed-renamed") -Force | ForEach-Object { $_.Name })
            $inside = @(Get-ChildItem -LiteralPath (Get-LocalPath "client-folder-listed-renamed/sub") -Force | ForEach-Object { $_.Name })
            if ($names -notcontains "w.txt" -or $names -notcontains "sub" -or $inside -notcontains "v.txt") {
                throw "Listed right after the rename, the folder showed '$($names -join ', ')' and its subfolder '$($inside -join ', ')'."
            }
            # Opened before the move reaches the server: read from where the
            # server still has it, and the move waits until the read began.
            Assert-Local "client-folder-listed-renamed/w.txt" "w"
            Assert-RemoteText "client-folder-listed-renamed/w.txt" "w"
            Assert-RemoteText "client-folder-listed-renamed/sub/v.txt" "v"
            Assert-RemoteMissing "client-folder-listed"
            # Once the run that sent the move is done, what was listed opens
            # from the new path.
            Sync-Remote
            Assert-Local "client-folder-listed-renamed/sub/v.txt" "v"
        }
        # Explorer's New folder, left for a while before it is renamed: the
        # sync run in between marks it to be listed from the server when next
        # opened (it holds nothing local). The rename must still reach the
        # server as a rename, not delete the folder there first.
        New-E2EScenario "C32" "client creates a folder, a sync run passes, then it is renamed: the server has it under the new name" -Budget @{ ServerFolders = 1; ServerRenames = 1; ServerDeletes = 1 } -CloudOnly {
            New-Item -ItemType Directory -Path (Get-LocalPath "c32-New folder") | Out-Null
            if (Test-ServerKeepsEmptyFolders) {
                Wait-Remote { Test-RemoteItem "c32-New folder" } "c32-New folder did not reach the server."
            } else {
                Wait-ServiceQuiet
            }
            Sync-Remote
            Wait-ServiceQuiet
            Rename-Item -LiteralPath (Get-LocalPath "c32-New folder") -NewName "c32-renamed"
            if (Test-ServerKeepsEmptyFolders) {
                Wait-Remote { Test-RemoteItem "c32-renamed" } "c32-renamed did not reach the server."
                Assert-RemoteMissing "c32-New folder"
            } else {
                Wait-ServiceQuiet
            }
            Sync-Remote
            if (-not (Test-Local "c32-renamed")) { throw "c32-renamed is gone locally after a sync run." }
            if (Test-Local "c32-New folder") { throw "c32-New folder came back locally." }
        }
        # The app's notification area icon follows what the service tells it
        # as it happens ("watch" on its control pipe): syncing while a change
        # is on its way, up to date once it is there. The server is paused
        # while the file is sent, so the upload is caught under way.
        New-E2EScenario "C33" "while a new file is sent the service reports it as sending, then nothing left to do" -Budget @{ Uploads = 1; Lookups = 2 } -CloudOnly -PausesServer {
            Suspend-E2EBackend
            try {
                Write-Local "c33.txt" "c33"
                Wait-ServicePulse { param($pulse) $pulse.State -eq "Syncing" -and $pulse.Activity.Sending -ge 1 } `
                    "The service did not report c33.txt as being sent." | Out-Null
            } finally {
                Resume-E2EBackend
            }
            Assert-RemoteText "c33.txt" "c33"
            Wait-ServicePulse {
                param($pulse)
                $pulse.State -eq "Syncing" -and $pulse.Activity.Sending -eq 0 -and $pulse.Activity.Waiting -eq 0 -and $pulse.Activity.Downloading -eq 0
            } "The service still reports activity after c33.txt reached the server." | Out-Null
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
        if ($scenario.PausesServer -and $script:E2EBackend -eq "remote") {
            $results.Add([pscustomobject]@{ Id = $scenario.Id; Result = "SKIP"; Ms = 0; Name = $scenario.Name; Activity = ""; Detail = "a real server cannot be taken offline by the test" })
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
