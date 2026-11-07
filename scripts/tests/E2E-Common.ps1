# Shared implementation for every OpenCfDrive end-to-end run.
#
# Do not run this file directly. The entry points are:
#
#   E2E-Run.ps1       Registers a real Cloud Files sync root through
#                     OpenCfDrive.Service (shown in Explorer) and adds the
#                     mount/Explorer assertions on top of the common flow.
#   E2E-Run-Mock.ps1  The same flow with the engine in forced mock mode over a
#                     plain directory (no Cloud Files registration).
#
# All of them take -Backend webdav|s3. The backend is only a server process
# (rclone serve webdav / s3) plus an rclone remote, and the same scenarios run
# against each, which shows the client depends on rclone only, not on a
# particular protocol. Nothing but rclone and the .NET SDK is needed.
#
# Server-side changes (seed, add, update, delete, rename) are made the way
# another user or app would make them: through a *separate* rclone process
# (the CLI), never through the rclone RC instance the client uses. Nothing
# tells the client about them. Most protocols have no change notifications,
# and the client does not rely on any: it finds server-side changes only when
# a folder is listed. That is exactly what the server-side scenarios check.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

# Repository root (this file lives in scripts\tests), resolved once when this
# file is dot-sourced (PSScriptRoot inside a function is not reliable).
$script:E2ERoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

$script:E2ESummaryLog = ""
$script:E2ERcPort = 0
# The harness's rclone wants a login, as one a user runs should: never
# --rc-no-auth, which lets any program on the PC at the remotes.
$script:E2ERcUser = "opencfdrive-e2e"
$script:E2ERcPass = [guid]::NewGuid().ToString("N")
$script:E2ERemoteName = "e2e"
$script:E2ERemoteSpec = "e2e:"
$script:E2EBackend = "webdav"
$script:E2EServerHashes = $false
$script:E2EServerKeepsEmptyFolders = $true
$script:E2EServerSlowTimes = $false
$script:E2ERemoteBase = ""
$script:E2ETestFolder = ""
$script:E2ERemoteUrlVariable = ""
$script:E2EChunkerVariables = @{}
$script:E2EBackendUrl = ""
$script:E2ETempRoot = ""

. (Join-Path $PSScriptRoot "E2E-Scenarios.ps1")

# --- logging ---------------------------------------------------------------

function Write-E2ELog([string]$Message) {
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $line = "[$ts] $Message"
    Write-Host $line

    if ([string]::IsNullOrWhiteSpace($script:E2ESummaryLog)) { return }
    $dir = Split-Path -Parent $script:E2ESummaryLog
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    try {
        Add-Content -Path $script:E2ESummaryLog -Value $line -Encoding UTF8
    } catch {
        # Logging must never abort the run.
    }
}

# --- process / filesystem helpers -----------------------------------------

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$PassThruOutput
    )

    # Native tools (rclone, dotnet) routinely write NOTICEs to stderr.
    # Under $ErrorActionPreference='Stop' Windows PowerShell turns those into a
    # terminating NativeCommandError, so relax it for the call and judge the
    # result by $LASTEXITCODE instead.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        # Piped: PowerShell captures a Windows (no console) app's output only
        # when it goes into a pipeline, not straight into a variable.
        $output = & $FilePath @Arguments 2>&1 | ForEach-Object { $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }

    if ($PassThruOutput) {
        return [pscustomobject]@{ ExitCode = $exitCode; Output = @($output) }
    }
    return $exitCode
}

function Wait-ForCondition {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [int]$TimeoutSeconds = 30,
        [int]$PollMilliseconds = 300
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    return $false
}

# Waits until $Condition holds, checking it again each time Windows reports
# that the attributes of the file at $Path changed: a download finishing, a
# local copy freed, a pin set. -NotifyFilter adds other changes of it, such as
# its size. No polling; the timeout only bounds a failure.
function Wait-ForAttributes {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [int]$TimeoutSeconds = 15,
        [System.IO.NotifyFilters]$NotifyFilter = [System.IO.NotifyFilters]::Attributes
    )

    # Distinct names: $Condition runs in this scope (see Wait-Local).
    $attributeCheck = $Condition
    $attributeEvents = "attributes-" + [guid]::NewGuid()
    $attributeWatcher = New-Object System.IO.FileSystemWatcher (Split-Path -Parent $Path), (Split-Path -Leaf $Path)
    $attributeWatcher.NotifyFilter = $NotifyFilter
    Register-ObjectEvent -InputObject $attributeWatcher -EventName Changed -SourceIdentifier $attributeEvents | Out-Null
    $attributeWatcher.EnableRaisingEvents = $true
    try {
        # Checked only once the watcher runs, so a change made before it
        # started is seen by the check and one made after raises an event.
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (-not (& $attributeCheck)) {
            $left = [math]::Ceiling(($deadline - (Get-Date)).TotalSeconds)
            if ($left -le 0 -or $null -eq (Wait-Event -SourceIdentifier $attributeEvents -Timeout $left)) {
                return [bool](& $attributeCheck)
            }
            Remove-Event -SourceIdentifier $attributeEvents -ErrorAction SilentlyContinue
        }
        return $true
    }
    finally {
        Unregister-Event -SourceIdentifier $attributeEvents -ErrorAction SilentlyContinue
        Remove-Event -SourceIdentifier $attributeEvents -ErrorAction SilentlyContinue
        $attributeWatcher.Dispose()
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

function Remove-IfExists([string]$PathToDelete) {
    if ($PathToDelete -and (Test-Path $PathToDelete)) {
        Remove-Item -LiteralPath $PathToDelete -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Stops what a previous (possibly interrupted) run left behind, and nothing
# else: the service only when it runs from this checkout's bin folders (never
# an installed copy, which drives a real folder), and rclone
# only when it uses the harness's own config under %TEMP%\opencfdrive-e2e*
# (never an rclone GUI or a user's own mount).
function Stop-StaleProcesses {
    $binRoot = $script:E2ERoot.TrimEnd('\') + '\'
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        ($_.Name -in @("OpenCfDrive.Service.exe", "OpenCfDrive.MockService.exe") -and
         "$($_.ExecutablePath)" -like "$binRoot*\bin\*") -or
        ($_.Name -eq "rclone.exe" -and "$($_.CommandLine)" -like "*opencfdrive-e2e*")
    }
    foreach ($process in @($processes)) {
        try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Milliseconds 400
}

# Asks the cloud service to look at the server now, the way its background
# sync does every 30 s, and returns once that run has finished (the service
# answers on its control pipe only then). Mock mode never looks at the server.
$script:E2ESyncRoot = ""
function Sync-Remote([int]$TimeoutSeconds = 120) {
    if (-not $script:E2EIsCloud) { return }
    $pipeName = [System.IO.Path]::GetFileNameWithoutExtension((Get-StateDbPath $script:E2ESyncRoot))
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine("sync")
        $reader = New-Object System.IO.StreamReader($pipe)
        $answer = $reader.ReadLineAsync()
        if (-not $answer.Wait($TimeoutSeconds * 1000)) { throw "The service did not finish a sync run within $TimeoutSeconds s." }
        if ($answer.Result -notmatch '"synced":true') { throw "The service's sync run did not finish: $($answer.Result)" }
    } finally {
        $pipe.Dispose()
    }
}

# Watches the cloud service the way the app does for its notification area
# icon: the service's "watch" answer is a line at once and one at every change
# (its state and what it is doing: sending, downloading, waiting offline,
# failed). Returns the first that meets $Condition (given the line, parsed),
# waiting on the service's lines themselves; throws $What, with the last line,
# if none does within $TimeoutSeconds.
function Wait-ServicePulse([scriptblock]$Condition, [string]$What, [int]$TimeoutSeconds = 30) {
    $pipeName = [System.IO.Path]::GetFileNameWithoutExtension((Get-StateDbPath $script:E2ESyncRoot))
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $last = "nothing"
    try {
        $pipe.Connect(10000)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine("watch")
        $reader = New-Object System.IO.StreamReader($pipe)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ($true) {
            $remaining = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
            $line = $reader.ReadLineAsync()
            if (-not $line.Wait($remaining)) { throw "$What (the service last said: $last)" }
            if ($null -eq $line.Result) { throw "$What (the service ended its watch; it last said: $last)" }
            $last = $line.Result
            $pulse = $last | ConvertFrom-Json
            if (& $Condition $pulse) { return $pulse }
        }
    } finally {
        $pipe.Dispose()
    }
}

function Read-LogText([string]$Path) {
    if (-not (Test-Path $Path)) { return "" }
    try { return (Get-Content -Path $Path -Raw -ErrorAction SilentlyContinue) } catch { return "" }
}

function Test-HttpReady([string]$Url) {
    try {
        $client = [System.Net.Http.HttpClient]::new()
        try {
            $resp = $client.GetAsync($Url).GetAwaiter().GetResult()
            return ([int]$resp.StatusCode -lt 500)
        } finally {
            $client.Dispose()
        }
    } catch {
        return $false
    }
}

# --- state location (mirrors OpenCfDrive.Core/Storage/SyncStatePaths.cs) ---

function Get-StateDbPath([string]$SyncRoot) {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $stateDir = Join-Path $env:TEMP "OpenCfDrive\state"
    } else {
        $stateDir = Join-Path $localAppData "OpenCfDrive\state"
    }

    $full = [System.IO.Path]::GetFullPath($SyncRoot).TrimEnd('\', '/')
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($full.ToLowerInvariant()))
    } finally {
        $sha.Dispose()
    }

    $hex = -join ($bytes | ForEach-Object { $_.ToString("x2") })
    return (Join-Path $stateDir ("opencfdrive-{0}.db" -f $hex.Substring(0, 16)))
}

# --- rclone RC: the only way this harness touches the remote ---------------

function Resolve-RclonePath {
    $local = Join-Path $script:E2ERoot "rclone.exe"
    if (Test-Path $local) { return $local }

    $cmd = Get-Command rclone -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        if ($cmd.Path) { return $cmd.Path }
        if ($cmd.Source) { return $cmd.Source }
    }

    throw "rclone not found: install it on PATH or put rclone.exe in the repository root."
}

function Invoke-Rc([string]$Command, [hashtable]$Parameters) {
    $client = [System.Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(60)
        $login = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("$($script:E2ERcUser):$($script:E2ERcPass)"))
        $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $login)
        $json = $Parameters | ConvertTo-Json -Depth 6 -Compress
        $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
        $resp = $client.PostAsync("http://127.0.0.1:$($script:E2ERcPort)/$Command", $content).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $resp.IsSuccessStatusCode) {
            throw "rclone rc $Command failed ($([int]$resp.StatusCode)): $body"
        }
        if ([string]::IsNullOrWhiteSpace($body)) { return $null }
        return ($body | ConvertFrom-Json)
    } finally {
        $client.Dispose()
    }
}

# "e2e:" + "a/b" -> "e2e:a/b"; "e2e:bucket" + "a" -> "e2e:bucket/a".
function Join-RemoteSpec([string]$Path) {
    $path = $Path.Trim('/')
    if ($path.Length -eq 0) { return $script:E2ERemoteSpec }
    if ($script:E2ERemoteSpec.EndsWith(":")) { return "$($script:E2ERemoteSpec)$path" }
    return "$($script:E2ERemoteSpec)/$path"
}

# --- the "server side": a separate rclone process --------------------------
#
# Stands in for another user or app working on the server. It shares only the
# remote definition (rclone.conf) with the client's rclone RC instance, no
# process, cache or connection.

$script:E2ERcloneExe = ""
$script:E2ERcloneConf = ""

function Invoke-ServerRclone([string[]]$Arguments) {
    # rclone writes UTF-8; Windows PowerShell reads a program's output in the
    # console's code page, which turns non-English names (C26, S23) into '?'.
    $previous = [Console]::OutputEncoding
    try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }
    try {
        $result = Invoke-Native -FilePath $script:E2ERcloneExe `
            -Arguments (@("--config", $script:E2ERcloneConf) + $Arguments) -PassThruOutput
    } finally {
        try { [Console]::OutputEncoding = $previous } catch { }
    }
    if ($result.ExitCode -ne 0) {
        throw "rclone $($Arguments -join ' ') failed (exit $($result.ExitCode)): $($result.Output -join ' ')"
    }
    return $result.Output
}

function New-StagingDirectory {
    $staging = Join-Path $script:E2ETempRoot ("staging\" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    return $staging
}

function Set-RemoteBytes([string]$Path, [byte[]]$Bytes) {
    $staging = New-StagingDirectory
    try {
        $file = Join-Path $staging "payload"
        [System.IO.File]::WriteAllBytes($file, $Bytes)
        # --ignore-times: always upload. On a server without modification
        # times (plain WebDAV) rclone otherwise compares sizes only and skips
        # a same-size update.
        Invoke-ServerRclone @("copyto", "--ignore-times", $file, (Join-RemoteSpec $Path)) | Out-Null
    } finally {
        Remove-IfExists $staging
    }
}

function Set-RemoteFile([string]$Path, [string]$Content) {
    Set-RemoteBytes $Path ([System.Text.Encoding]::UTF8.GetBytes($Content))
}

function Get-RemoteBytes([string]$Path) {
    $staging = New-StagingDirectory
    try {
        $file = Join-Path $staging "payload"
        Invoke-ServerRclone @("copyto", (Join-RemoteSpec $Path), $file) | Out-Null
        if (-not (Test-Path -LiteralPath $file)) { throw "Remote file '$Path' does not exist." }
        return [System.IO.File]::ReadAllBytes($file)
    } finally {
        Remove-IfExists $staging
    }
}

function Get-RemoteFile([string]$Path) {
    return [System.Text.Encoding]::UTF8.GetString((Get-RemoteBytes $Path))
}

# True when a file or folder exists at the path.
# A folder as the root rclone opens, with a trailing slash: rclone then
# takes it for a folder. Without it the WebDAV backend asks for the path as a
# file first, which some servers answer with a redirect to http:// that
# rclone refuses.
function Join-RemoteFolder([string]$Path) {
    $spec = Join-RemoteSpec $Path
    if ($spec.EndsWith(":") -or $spec.EndsWith("/")) { return $spec }
    return "$spec/"
}

function Test-RemoteItem([string]$Path) {
    $path = $Path.Trim('/')
    $parent = if ($path.Contains('/')) { $path.Substring(0, $path.LastIndexOf('/')) } else { "" }
    $name = if ($path.Contains('/')) { $path.Substring($path.LastIndexOf('/') + 1) } else { $path }
    try {
        $entries = Invoke-ServerRclone @("lsf", (Join-RemoteFolder $parent))
    } catch {
        return $false
    }
    return @($entries | Where-Object { "$_".TrimEnd('/') -eq $name }).Count -gt 0
}

function Remove-RemoteFile([string]$Path) {
    Invoke-ServerRclone @("deletefile", (Join-RemoteSpec $Path)) | Out-Null
}

function Remove-RemoteFolder([string]$Path) {
    Invoke-ServerRclone @("purge", (Join-RemoteFolder $Path)) | Out-Null
}

function New-RemoteFolder([string]$Path) {
    Invoke-ServerRclone @("mkdir", (Join-RemoteFolder $Path)) | Out-Null
}

function Move-RemoteFile([string]$From, [string]$To) {
    Invoke-ServerRclone @("moveto", (Join-RemoteSpec $From), (Join-RemoteSpec $To)) | Out-Null
}

function Move-RemoteFolder([string]$From, [string]$To) {
    Invoke-ServerRclone @("move", (Join-RemoteFolder $From), (Join-RemoteFolder $To), "--delete-empty-src-dirs") | Out-Null
    try { Invoke-ServerRclone @("rmdirs", (Join-RemoteFolder $From)) | Out-Null } catch { }
}

# Every path on the remote (files, and folders with a trailing '/').
function Get-RemoteListing {
    return @(Invoke-ServerRclone @("lsf", "-R", (Join-RemoteFolder "")) | ForEach-Object { "$_" } | Where-Object { $_ })
}

function Test-RcloneHealthy {
    try {
        # The drive's own folder: on a real server, nothing outside the run's.
        # The remote's root as the fs and the folder on its own, as the client
        # sends it.
        $colon = $script:E2ERemoteSpec.IndexOf(":")
        Invoke-Rc "operations/list" @{ fs = $script:E2ERemoteSpec.Substring(0, $colon + 1); remote = $script:E2ERemoteSpec.Substring($colon + 1).Trim('/') } | Out-Null
        return $true
    } catch {
        return $false
    }
}

# --- backends ----------------------------------------------------------------

# Pauses and resumes the server process (see Suspend-E2EBackend).
if (-not ("OpenCfDriveE2E.ProcessControl" -as [type])) {
    Add-Type -Namespace OpenCfDriveE2E -Name ProcessControl -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("ntdll.dll")]
public static extern int NtSuspendProcess(System.IntPtr processHandle);
[System.Runtime.InteropServices.DllImport("ntdll.dll")]
public static extern int NtResumeProcess(System.IntPtr processHandle);
'@
}

# The server: rclone's own WebDAV or S3 server ("rclone serve webdav" /
# "rclone serve s3") over a folder under the temp root, in a process of its
# own. It is not the client's rclone: the client reaches it only over HTTP,
# through its own rclone and an rclone remote, as it would any server.
function Start-E2EBackend {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("webdav", "s3", "remote")][string]$Backend,
        [Parameter(Mandatory = $true)][string]$RclonePath,
        [Parameter(Mandatory = $true)][string]$RcloneConf,
        [Parameter(Mandatory = $true)][string]$RemoteName,
        [string]$Remote = "",
        # -Backend remote: the drive is "remote:<test folder>" instead of the
        # remote's root moved to the folder.
        [switch]$RemoteAsPath,
        # -Backend remote: through rclone's chunker, files above this size in pieces.
        [string]$ChunkSize = ""
    )

    $script:E2ERcloneExe = $RclonePath
    $script:E2ERcloneConf = $RcloneConf

    # What the server keeps, as the scenarios need to know it: rclone's own
    # serve webdav keeps no hashes; S3 keeps MD5s, no empty folders, and
    # costs a request per file time.
    $script:E2EServerHashes = $Backend -eq "s3"
    $script:E2EServerKeepsEmptyFolders = $Backend -ne "s3"
    $script:E2EServerSlowTimes = $Backend -eq "s3"

    if ($Backend -eq "remote") {
        # A real server: nothing is started, and everything happens in a new
        # folder of its own, which Stop-E2EBackend deletes again.
        $base = $Remote.Trim()
        if (-not $base.Contains(":")) { $base += ":" }
        $name = $base.Substring(0, $base.IndexOf(":"))
        $folder = "opencfdrive-e2e-" + (Get-Date -Format "yyyyMMdd-HHmmss")
        $inBase = if ($base.EndsWith(":")) { $folder } else { "$($base.Substring($base.IndexOf(':') + 1).Trim('/'))/$folder" }
        $script:E2ERemoteBase = "${name}:"
        $script:E2ETestFolder = $inBase
        $script:E2ERemoteName = $name
        Write-E2ELog "Testing against the real remote $base, in $folder (deleted at the end)"

        # Made from the remote's root, naming the folder: some servers answer a
        # folder path without a trailing slash with a redirect to http://,
        # which rclone refuses (it would send the password in clear).
        Invoke-ServerRclone @("rc", "--loopback", "operations/mkdir", "fs=$($script:E2ERemoteBase)", "remote=$inBase") | Out-Null

        # The drive is that folder. A WebDAV remote's root is moved there
        # (RCLONE_CONFIG_<NAME>_URL, for the rclones this run starts only), so
        # the drive is the remote's root, as the user's own is; others get the
        # folder as a path.
        $type = Get-RcloneConfigValue $RcloneConf $name "type"
        $url = Get-RcloneConfigValue $RcloneConf $name "url"
        if ($RemoteAsPath) {
            # A drive whose remote has a path, as a user may set one up.
            $script:E2ERemoteSpec = "${name}:$inBase"
            Write-E2ELog "The drive is the remote with a path: $($script:E2ERemoteSpec)"
        } elseif ($type -eq "webdav" -and $url) {
            $script:E2ERemoteUrlVariable = "RCLONE_CONFIG_$($name.ToUpperInvariant() -replace '[^A-Z0-9]', '_')_URL"
            $escaped = @($inBase -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
            $rootUrl = $url.TrimEnd('/') + "/" + $escaped + "/"
            Set-Item -Path "Env:\$($script:E2ERemoteUrlVariable)" -Value $rootUrl
            $script:E2ERemoteSpec = "${name}:"
        } else {
            $script:E2ERemoteSpec = "${name}:$inBase"
        }

        # -ChunkSize: the drive goes through rclone's chunker over that, as a
        # server whose proxy caps a request's size needs (Cloudflare: 100 MB).
        # Files larger than the size are stored as pieces. The chunker remote
        # exists only for this run's rclones (RCLONE_CONFIG_E2ECHUNKER_*); the
        # scenarios' own server side goes through it too, as any other rclone
        # client of that server has to.
        if ($ChunkSize) {
            $script:E2EChunkerVariables = @{
                "RCLONE_CONFIG_E2ECHUNKER_TYPE" = "chunker"
                "RCLONE_CONFIG_E2ECHUNKER_REMOTE" = $script:E2ERemoteSpec
                "RCLONE_CONFIG_E2ECHUNKER_CHUNK_SIZE" = $ChunkSize
                "RCLONE_CONFIG_E2ECHUNKER_HASH_TYPE" = "none"
            }
            foreach ($variable in $script:E2EChunkerVariables.GetEnumerator()) {
                Set-Item -Path "Env:\$($variable.Key)" -Value $variable.Value
            }
            Write-E2ELog "Through rclone's chunker over $($script:E2ERemoteSpec): files above $ChunkSize are stored in pieces"
            $script:E2ERemoteSpec = "e2echunker:"
        }

        $features = (Invoke-ServerRclone @("backend", "features", (Join-RemoteFolder ""))) -join "`n" | ConvertFrom-Json
        $script:E2EServerHashes = @($features.Hashes).Count -gt 0
        $script:E2EServerKeepsEmptyFolders = [bool]$features.Features.CanHaveEmptyDirectories
        # As RemoteCapabilities.ListsWithoutTimes decides it.
        $script:E2EServerSlowTimes = [bool]$features.Features.SlowModTime -and $script:E2EServerHashes
        Write-E2ELog "Server: $($features.Name) ($($features.Root)); hashes: $(if ($script:E2EServerHashes) { @($features.Hashes) -join ', ' } else { 'none' }); empty folders: $script:E2EServerKeepsEmptyFolders; slow times: $script:E2EServerSlowTimes"
        return
    }

    $data = Join-Path $script:E2ETempRoot "server-data"
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $port = Get-FreeTcpPort
    $serveArgs = switch ($Backend) {
        "webdav" { @("serve", "webdav", "`"$data`"", "--addr", "127.0.0.1:$port", "--user", "demo", "--pass", "demo") }
        "s3"     { @("serve", "s3", "`"$data`"", "--addr", "127.0.0.1:$port", "--auth-key", "e2ekey,e2esecret") }
    }
    $serveArgs += @("--log-file", "`"$(Join-Path $script:E2ETempRoot 'server.log')`"", "--log-level", "INFO")

    Write-E2ELog "Starting the $Backend server (rclone serve $Backend)"
    $script:E2EBackendProcess = Start-Process -FilePath $RclonePath -ArgumentList $serveArgs -WindowStyle Hidden -PassThru
    $script:E2EBackendUrl = "http://127.0.0.1:$port/"

    Write-E2ELog "$Backend endpoint: $script:E2EBackendUrl"
    if (-not (Wait-ForCondition { Test-HttpReady $script:E2EBackendUrl } -TimeoutSeconds 60)) {
        throw "$Backend did not become ready at $script:E2EBackendUrl"
    }

    switch ($Backend) {
        "webdav" {
            $configArgs = @("config", "create", $RemoteName, "webdav",
                            "url=$script:E2EBackendUrl", "user=demo", "pass=demo", "vendor=other")
            $script:E2ERemoteSpec = "${RemoteName}:"
        }
        "s3" {
            $configArgs = @("config", "create", $RemoteName, "s3",
                            "provider=Rclone", "endpoint=$script:E2EBackendUrl",
                            "access_key_id=e2ekey", "secret_access_key=e2esecret",
                            "force_path_style=true")
            # S3 has buckets, so the drive maps to a bucket, not the remote
            # root: this also covers remotes with a path ("remote:sub").
            $script:E2ERemoteSpec = "${RemoteName}:opencfdrive"
        }
    }

    $configExit = Invoke-Native -FilePath $RclonePath -Arguments ($configArgs + @("--config", $RcloneConf))
    if ($configExit -ne 0) { throw "rclone config create failed for $Backend (exit $configExit)" }

    if ($Backend -eq "s3") {
        Invoke-ServerRclone @("mkdir", $script:E2ERemoteSpec) | Out-Null
    }
}

function Stop-E2EBackend {
    if ($null -ne $script:E2EBackendProcess -and -not $script:E2EBackendProcess.HasExited) {
        try { Stop-Process -Id $script:E2EBackendProcess.Id -Force -ErrorAction Stop } catch { }
    }

    # A real server: only the run's own folder goes, named from the remote's
    # own root (the drive's root override is dropped first).
    if ($script:E2EBackend -eq "remote") {
        foreach ($variable in @($script:E2EChunkerVariables.Keys)) {
            Remove-Item -Path "Env:\$variable" -ErrorAction SilentlyContinue
        }
        $script:E2EChunkerVariables = @{}
        if ($script:E2ERemoteUrlVariable) {
            Remove-Item -Path "Env:\$($script:E2ERemoteUrlVariable)" -ErrorAction SilentlyContinue
            $script:E2ERemoteUrlVariable = ""
        }
        if ($script:E2ETestFolder -match '(^|/)opencfdrive-e2e-\d{8}-\d{6}$') {
            try {
                Invoke-ServerRclone @("rc", "--loopback", "operations/purge", "fs=$($script:E2ERemoteBase)", "remote=$($script:E2ETestFolder)") | Out-Null
                Write-E2ELog "Deleted the test folder $($script:E2ERemoteBase)$($script:E2ETestFolder) from the server"
            } catch {
                Write-E2ELog "Could not delete the test folder $($script:E2ERemoteBase)$($script:E2ETestFolder) from the server: $($_.Exception.Message)"
            }
            $script:E2ETestFolder = ""
        }
    }
}

# One value of a remote in an rclone config file, read as text (never the
# whole section: it holds the remote's password).
function Get-RcloneConfigValue([string]$Config, [string]$Remote, [string]$Key) {
    $section = $null
    foreach ($line in Get-Content -LiteralPath $Config) {
        if ($line -match '^\s*\[(.+)\]\s*$') { $section = $Matches[1]; continue }
        if ($section -eq $Remote -and $line -match ('^\s*' + [regex]::Escape($Key) + '\s*=\s*(.*?)\s*$')) { return $Matches[1] }
    }
    return ""
}

# Takes the server offline the way a lost network does: its process is
# paused, so connections hang instead of being refused, while the client's
# rclone keeps running. Only a server this run started can be paused.
function Suspend-E2EBackend {
    if ($null -eq $script:E2EBackendProcess) { throw "A real server cannot be taken offline by the test." }
    [void][OpenCfDriveE2E.ProcessControl]::NtSuspendProcess($script:E2EBackendProcess.Handle)
}

function Resume-E2EBackend {
    [void][OpenCfDriveE2E.ProcessControl]::NtResumeProcess($script:E2EBackendProcess.Handle)
}

# Starts the service (or the mock) and waits for its ready line. Returns the process.
function Start-E2EService {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceExe,
        [Parameter(Mandatory = $true)][string]$SyncRoot,
        [Parameter(Mandatory = $true)][string]$OutLog,
        [Parameter(Mandatory = $true)][string]$ErrLog,
        [Parameter(Mandatory = $true)][string]$ReadyPattern,
        [Parameter(Mandatory = $true)][string]$FailurePattern
    )

    Remove-Item -LiteralPath $OutLog, $ErrLog -Force -ErrorAction SilentlyContinue

    [Environment]::SetEnvironmentVariable("OPENCFDRIVE_ROOT", $SyncRoot, "Process")
    [Environment]::SetEnvironmentVariable("OPENCFDRIVE_REMOTE", $script:E2ERemoteSpec, "Process")
    [Environment]::SetEnvironmentVariable("OPENCFDRIVE_RC_URL", "http://127.0.0.1:$($script:E2ERcPort)", "Process")
    [Environment]::SetEnvironmentVariable("OPENCFDRIVE_RC_USER", $script:E2ERcUser, "Process")
    [Environment]::SetEnvironmentVariable("OPENCFDRIVE_RC_PASS", $script:E2ERcPass, "Process")
    # The background sync runs only when a scenario asks (Sync-Remote), so each
    # one sees exactly the server calls it caused. Its schedule is unit-tested.
    [Environment]::SetEnvironmentVariable("OPENCFDRIVE_REMOTE_POLL_SECONDS", "86400", "Process")
    # Every sync run marks a folder holding nothing local to be listed when it
    # is next opened (the service waits one poll interval), so a scenario's
    # own listing of it (Update-LocalFolder) asks the server.
    [Environment]::SetEnvironmentVariable("OPENCFDRIVE_RELIST_SECONDS", "0", "Process")
    $script:E2ESyncRoot = $SyncRoot

    Write-E2ELog "Starting $(Split-Path -Leaf $ServiceExe)"
    $service = Start-Process -FilePath $ServiceExe -WorkingDirectory $SyncRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog

    $started = Wait-ForCondition {
        $txt = Read-LogText $OutLog
        if ($txt -match $FailurePattern) { return $true }
        return ($txt -match $ReadyPattern) -or $service.HasExited
    } -TimeoutSeconds 60

    $startupLog = Read-LogText $OutLog
    if (-not $started) {
        throw "Service did not report ready within 60s. Log: $startupLog Err: $(Read-LogText $ErrLog)"
    }
    if ($startupLog -match $FailurePattern) {
        throw "Service failed to initialise: $startupLog"
    }
    if ($service.HasExited) {
        throw "Service exited early (exit $($service.ExitCode)). Log: $startupLog Err: $(Read-LogText $ErrLog)"
    }
    Write-E2ELog "Service ready (pid $($service.Id))"
    return $service
}

# --- sync root registration (cloud mode only) -----------------------------

function Invoke-ServiceUnregister([string]$ServiceExe, [string]$SyncRoot) {
    $previous = $env:OPENCFDRIVE_ROOT
    try {
        $env:OPENCFDRIVE_ROOT = $SyncRoot
        $result = Invoke-Native -FilePath $ServiceExe -Arguments @("--unregister") -PassThruOutput
        $message = (@($result.Output) -join ' ').Trim()
        if ($result.ExitCode -eq 0) {
            Write-E2ELog "Cleared the sync root registration: $message"
        } else {
            Write-E2ELog "No registration to clear: $message"
        }
    } finally {
        $env:OPENCFDRIVE_ROOT = $previous
    }
}

# --- the single end-to-end flow ------------------------------------------

function Invoke-OpenCfDriveE2E {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("cloud", "mock")][string]$Mode,
        [ValidateSet("webdav", "s3", "remote")][string]$Backend = "webdav",
        # -Backend remote: the rclone remote to test against, and its config.
        [string]$Remote = "",
        [string]$RcloneConfig = "",
        [switch]$RemoteAsPath,
        [string]$ChunkSize = "",
        [string]$SyncRoot = "",
        [string]$RemoteName = "e2e",
        [int]$RcPort = 0,
        [string]$TempRoot = "",
        [string]$SummaryLog = "",
        [scriptblock]$MountCheck = $null,
        [string]$Only = "",
        [switch]$KeepRegistration
    )

    $isCloud = $Mode -eq "cloud"
    $script:E2EIsCloud = $isCloud
    $script:E2EBackend = $Backend

    if ([string]::IsNullOrWhiteSpace($TempRoot)) {
        $suffix = if ($isCloud) { "" } else { "-mock" }
        if ($Backend -ne "webdav") { $suffix += "-$Backend" }
        $TempRoot = Join-Path $env:TEMP "opencfdrive-e2e$suffix"
    }
    if ([string]::IsNullOrWhiteSpace($SyncRoot)) {
        $SyncRoot = Join-Path $TempRoot "drive"
    }
    if ([string]::IsNullOrWhiteSpace($SummaryLog)) {
        $SummaryLog = Join-Path $TempRoot "summary.log"
    }

    $SyncRoot = [System.IO.Path]::GetFullPath($SyncRoot)
    $TempRoot = [System.IO.Path]::GetFullPath($TempRoot)
    $SummaryLog = [System.IO.Path]::GetFullPath($SummaryLog)

    $script:E2ERemoteName = $RemoteName
    $script:E2ETempRoot = $TempRoot
    $script:E2ERcPort = if ($RcPort -gt 0) { $RcPort } else { Get-FreeTcpPort }
    $script:E2EBackendUrl = ""
    $script:E2EBackendProcess = $null

    $serviceDir = if ($isCloud) { "OpenCfDrive.Service" } else { "OpenCfDrive.MockService" }
    $serviceExeName = if ($isCloud) { "OpenCfDrive.Service.exe" } else { "OpenCfDrive.MockService.exe" }
    $serviceExe = Join-Path $script:E2ERoot "$serviceDir\bin\Debug\net10.0-windows10.0.19041.0\$serviceExeName"

    $readyPattern = if ($isCloud) { "Mode: cloud" } else { "MOCK: READY" }
    $failurePattern = if ($isCloud) { "Sync engine initialisation failed" } else { "MOCK: FAILED" }

    $markers = if ($isCloud) {
        @("Registered sync root", "Connected sync root callbacks", "FETCH_DATA", "FETCH_PLACEHOLDERS", "Uploaded 'alpha.txt'")
    } else {
        @("MOCK MODE", "MOCK: materialised", "MOCK: watching", "Uploaded 'alpha.txt'")
    }

    $stateDb = Get-StateDbPath $SyncRoot
    $serviceLog = Join-Path $TempRoot "service.log"
    $serviceErr = Join-Path $TempRoot "service.err"
    $proc = $null
    $rcloneRc = $null

    try {
        $script:E2ESummaryLog = ""
        Write-E2ELog "=== OpenCfDrive $Mode E2E on $Backend ==="
        Write-E2ELog "Sync root: $SyncRoot"
        Write-E2ELog "Temp root: $TempRoot"
        Stop-StaleProcesses

        # --- build ---------------------------------------------------------
        # Built first so cloud mode can unregister a stale registration below.
        Write-E2ELog "Building solution"
        $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
        if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
        $build = Invoke-Native -FilePath $dotnet `
            -Arguments @("build", (Join-Path $script:E2ERoot "OpenCfDrive.slnx"), "-nologo", "-v:m") `
            -PassThruOutput
        if ($build.ExitCode -ne 0) {
            throw "Build failed (exit $($build.ExitCode)): $($build.Output -join "`n")"
        }
        if (-not (Test-Path $serviceExe)) {
            throw "Service executable missing at $serviceExe"
        }

        # --- prerequisites -------------------------------------------------
        $rclonePath = Resolve-RclonePath

        # --- clean slate ---------------------------------------------------
        # Unregister a registration left by an interrupted run *before*
        # deleting the old drive folder: while it is registered some files can
        # survive the delete, and unregistering afterwards turns them into
        # plain files that the new run would pick up instead of placeholders.
        if ($isCloud) {
            Invoke-ServiceUnregister -ServiceExe $serviceExe -SyncRoot $SyncRoot
        }
        Remove-IfExists $TempRoot
        Remove-IfExists $SyncRoot
        if ((Test-Path -LiteralPath $SyncRoot) -or (Test-Path -LiteralPath $TempRoot)) {
            throw "Could not clear the previous run's folders under $TempRoot."
        }
        New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $SyncRoot -Force | Out-Null

        $script:E2ESummaryLog = $SummaryLog
        "OpenCfDrive $Mode/$Backend E2E summary" | Set-Content -Path $SummaryLog -Encoding UTF8

        # --- state reset ---------------------------------------------------
        # Engine state lives outside the sync root and is reset so every run
        # starts from the remote, not from previous local metadata.
        foreach ($suffix in @("", "-journal", "-wal", "-shm")) {
            Remove-IfExists ($stateDb + $suffix)
        }

        # --- backend + rclone RC (--rc-serve) -----------------------------
        $rcloneConf = Join-Path $TempRoot "rclone.conf"
        if ($Backend -eq "remote") {
            # The user's own remote definition, used as it is (only read).
            $rcloneConf = if ($RcloneConfig) { $RcloneConfig } else {
                (Invoke-Native -FilePath $rclonePath -Arguments @("config", "file") -PassThruOutput).Output |
                    Where-Object { $_ -match '\.conf\s*$' } | Select-Object -Last 1
            }
            $rcloneConf = "$rcloneConf".Trim()
            if (-not $Remote -or -not (Test-Path -LiteralPath $rcloneConf)) {
                throw "-Backend remote needs -Remote (for example 'myremote:') and an rclone config (found '$rcloneConf')."
            }
        }
        Start-E2EBackend -Backend $Backend -RclonePath $rclonePath -RcloneConf $rcloneConf -RemoteName $RemoteName -Remote $Remote -RemoteAsPath:$RemoteAsPath -ChunkSize $ChunkSize

        # DEBUG logs every RC call the client makes; the scenario audit counts them.
        Write-E2ELog "Starting rclone RC on http://127.0.0.1:$script:E2ERcPort"
        $rcloneLog = Join-Path $TempRoot "rclone-rc.log"
        # Kept, so cleanup stops this very process: a -TempRoot outside
        # %TEMP%\opencfdrive-e2e* is not matched by Stop-StaleProcesses.
        # On S3 every request to the server is logged too (--dump headers):
        # a scenario counts the per-file HEADs a listing with times costs there.
        $rcArgs = @(
            "--config", $rcloneConf,
            "rcd",
            "--rc-addr", "127.0.0.1:$($script:E2ERcPort)",
            "--rc-serve",
            "--log-file", $rcloneLog,
            "--log-level", "DEBUG"
        )
        if ($Backend -eq "s3") { $rcArgs += @("--dump", "headers") }
        # The login goes in through rclone's own variables (its --rc-user and
        # --rc-pass), so it is not in the process list; only this rclone gets them.
        try {
            $env:RCLONE_RC_USER = $script:E2ERcUser
            $env:RCLONE_RC_PASS = $script:E2ERcPass
            $rcloneRc = Start-Process -FilePath $rclonePath -ArgumentList $rcArgs -WindowStyle Hidden -PassThru
        } finally {
            Remove-Item Env:\RCLONE_RC_USER, Env:\RCLONE_RC_PASS -ErrorAction SilentlyContinue
        }

        if (-not (Wait-ForCondition { Test-RcloneHealthy } -TimeoutSeconds 45)) {
            throw "rclone RC did not become healthy on http://127.0.0.1:$($script:E2ERcPort). Log: $(Read-LogText (Join-Path $TempRoot 'rclone-rc.log'))"
        }
        Write-E2ELog "rclone RC healthy on http://127.0.0.1:$($script:E2ERcPort); drive = $script:E2ERemoteSpec"

        # --- seed the remote ----------------------------------------------
        # A nested folder matters: it proves the provider creates real
        # *directory* placeholder batches, not plain directories that the
        # platform has to populate on demand (which nothing handles).
        Write-E2ELog "Seeding remote with alpha.txt, beta.txt, docs/readme.txt and the scenario fixtures"
        Set-RemoteFile "alpha.txt" "hello-from-e2e"
        Set-RemoteFile "beta.txt" "beta-from-e2e"
        Set-RemoteFile "docs/readme.txt" "readme-from-e2e"
        Initialize-E2EScenarioFixtures

        # --- start the service --------------------------------------------
        $proc = Start-E2EService -ServiceExe $serviceExe -SyncRoot $SyncRoot -OutLog $serviceLog -ErrLog $serviceErr `
            -ReadyPattern $readyPattern -FailurePattern $failurePattern

        # --- namespace populated -------------------------------------------
        $alphaPath = Join-Path $SyncRoot "alpha.txt"
        $betaPath = Join-Path $SyncRoot "beta.txt"
        $docsPath = Join-Path $SyncRoot "docs"
        $readmePath = Join-Path $docsPath "readme.txt"

        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $seen = Wait-ForCondition {
            (Test-Path $alphaPath) -and (Test-Path $betaPath) -and
            (Test-Path $docsPath) -and (Test-Path $readmePath)
        } -TimeoutSeconds 60
        if (-not $seen) {
            throw "The namespace never appeared in $SyncRoot. Log: $(Read-LogText $serviceLog)"
        }
        Write-E2ELog "Namespace populated: alpha.txt, beta.txt, docs\, docs\readme.txt ($($clock.ElapsedMilliseconds) ms)"

        # --- cloud-only: real Explorer mount checks ------------------------
        if ($null -ne $MountCheck) {
            & $MountCheck $SyncRoot
        }

        # --- download ------------------------------------------------------
        $clock.Restart()
        $alphaData = [System.IO.File]::ReadAllText($alphaPath)
        if ($alphaData -ne "hello-from-e2e") {
            throw "Reading alpha.txt returned '$alphaData' instead of 'hello-from-e2e'."
        }
        Write-E2ELog "Download verified: alpha.txt == 'hello-from-e2e' ($($clock.ElapsedMilliseconds) ms)"

        # Nested files exercise the per-parent placeholder batches: docs\ must
        # be a real directory placeholder before readme.txt inside it hydrates.
        $clock.Restart()
        $readmeData = [System.IO.File]::ReadAllText($readmePath)
        if ($readmeData -ne "readme-from-e2e") {
            throw "Reading docs\readme.txt returned '$readmeData' instead of 'readme-from-e2e'."
        }
        Write-E2ELog "Nested download verified: docs\readme.txt == 'readme-from-e2e' ($($clock.ElapsedMilliseconds) ms)"

        # --- edit + upload --------------------------------------------------
        $clock.Restart()
        [System.IO.File]::AppendAllText($alphaPath, "-edited")
        $uploaded = Wait-ForCondition {
            try { return ((Get-RemoteFile "alpha.txt") -eq "hello-from-e2e-edited") } catch { return $false }
        } -TimeoutSeconds 60
        if (-not $uploaded) {
            $remoteNow = try { Get-RemoteFile "alpha.txt" } catch { "(unreadable)" }
            throw "Local edit was not uploaded. Remote alpha.txt='$remoteNow'. Log: $(Read-LogText $serviceLog)"
        }
        Write-E2ELog "Upload verified: alpha.txt is now 'hello-from-e2e-edited' on the remote ($($clock.ElapsedMilliseconds) ms)"

        $clock.Restart()
        [System.IO.File]::AppendAllText($readmePath, "-edited")
        $nestedUploaded = Wait-ForCondition {
            try { return ((Get-RemoteFile "docs/readme.txt") -eq "readme-from-e2e-edited") } catch { return $false }
        } -TimeoutSeconds 60
        if (-not $nestedUploaded) {
            $nestedNow = try { Get-RemoteFile "docs/readme.txt" } catch { "(unreadable)" }
            throw "Nested edit was not uploaded. Remote docs/readme.txt='$nestedNow'. Log: $(Read-LogText $serviceLog)"
        }
        Write-E2ELog "Nested upload verified: docs\readme.txt is now 'readme-from-e2e-edited' on the remote ($($clock.ElapsedMilliseconds) ms)"

        # --- scenario matrix: server-side and client-side actions -----------
        $scenarioFailures = Invoke-E2EScenarios -SyncRoot $SyncRoot -IsCloud:$isCloud -Only $Only `
            -ServiceLog $serviceLog -ServiceProcess $proc -RcloneLog $rcloneLog

        # --- log markers ----------------------------------------------------
        $logText = Read-LogText $serviceLog
        foreach ($marker in $markers) {
            $ok = $logText -match [regex]::Escape($marker)
            Write-E2ELog ("[LOG {0}] {1}" -f $marker, $(if ($ok) { "OK" } else { "MISSING" }))
            if (-not $ok) {
                throw "Service log missing expected marker: $marker"
            }
        }

        # --- state isolation --------------------------------------------------
        if (-not (Test-Path $stateDb)) {
            throw "Engine state database was not created at $stateDb"
        }
        $syncRootPrefix = $SyncRoot.TrimEnd('\') + '\'
        if ($stateDb.StartsWith($syncRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "State database must not live inside the sync root: $stateDb"
        }
        Write-E2ELog "State DB outside sync root: $stateDb"

        # Engine state must never appear in the synced namespace.
        $stateFileName = Split-Path -Leaf $stateDb
        $strayInRoot = Join-Path $SyncRoot $stateFileName
        if (Test-Path -LiteralPath $strayInRoot) {
            throw "State file found inside the sync root: $strayInRoot"
        }
        $strayState = @(Get-ChildItem -Path $SyncRoot -File -Filter "opencfdrive-*.db*" -ErrorAction SilentlyContinue)
        if ($strayState.Count -gt 0) {
            throw "State files found inside the sync root: $($strayState.FullName -join ', ')"
        }
        Write-E2ELog "No engine state inside the sync root"

        $remoteNames = Get-RemoteListing
        $leaked = @($remoteNames | Where-Object { $_ -like "*opencfdrive-*.db*" })
        if ($leaked.Count -gt 0) {
            throw "Engine state leaked to the remote: $($leaked -join ', ')"
        }
        Write-E2ELog "Remote listing clean ($($remoteNames.Count) entries)"

        # --- registered again: everything is listed from the server again -----
        # Unregistering drops every online-only placeholder and turns folder
        # placeholders into plain folders, but keeps the store. Registered
        # again, opening a folder must still show what the server has: the
        # remote is the source of truth, nothing is crawled at startup, and
        # what went missing is never taken for deleted here.
        if ($isCloud -and -not $KeepRegistration) {
            $restartDir = Join-Path $SyncRoot "restart"
            $restartFile = Join-Path $restartDir "never-opened.txt"
            Get-ChildItem -LiteralPath $SyncRoot -Force | Out-Null
            Get-ChildItem -LiteralPath $restartDir -Force | Out-Null
            if (-not (Test-Path -LiteralPath $restartFile)) {
                throw "restart\never-opened.txt was not listed before unregistering."
            }

            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $proc.WaitForExit(10000) | Out-Null
            Invoke-ServiceUnregister -ServiceExe $serviceExe -SyncRoot $SyncRoot
            if (Test-Path -LiteralPath $restartFile) {
                throw "Unregistering kept the online-only restart\never-opened.txt."
            }

            $proc = Start-E2EService -ServiceExe $serviceExe -SyncRoot $SyncRoot `
                -OutLog (Join-Path $TempRoot "service-restart.log") -ErrLog (Join-Path $TempRoot "service-restart.err") `
                -ReadyPattern $readyPattern -FailurePattern $failurePattern
            # A folder listed before stays listed; the background sync brings
            # back what the server has in it.
            $back = Wait-ForCondition {
                Sync-Remote
                Get-ChildItem -LiteralPath $SyncRoot -Force -ErrorAction SilentlyContinue | Out-Null
                Get-ChildItem -LiteralPath $restartDir -Force -ErrorAction SilentlyContinue | Out-Null
                Test-Path -LiteralPath $restartFile
            } -TimeoutSeconds 30 -PollMilliseconds 200
            if (-not $back) {
                throw "Registered again, restart\never-opened.txt did not come back. Log: $(Read-LogText (Join-Path $TempRoot 'service-restart.log'))"
            }
            $restartText = [System.IO.File]::ReadAllText($restartFile)
            if ($restartText -ne "never-opened") {
                throw "Registered again, restart\never-opened.txt reads '$restartText'."
            }
            Write-E2ELog "Registered again: restart\never-opened.txt was listed from the server again and reads '$restartText'"
        }

        # --- never a crawl ------------------------------------------------------
        # Cloud mode lists one folder at a time, when it is opened. Every call
        # the client made is in rclone's debug log.
        if ($isCloud) {
            $crawls = @(Select-String -LiteralPath $rcloneLog -Pattern 'rc: "operations/list": with parameters.*recurse:true')
            if ($crawls.Count -gt 0) {
                throw "The client listed the remote recursively $($crawls.Count) time(s); it must only list one folder at a time."
            }
            Write-E2ELog "No recursive listing: the client only ever listed one folder at a time"
        }

        # --- uninstall leaves a usable folder ---------------------------------
        # What Uninstall does: stop the service, unregister. Afterwards the
        # folder must be an ordinary folder the user can delete: no orphaned
        # placeholders that can no longer be opened or removed.
        if ($isCloud -and -not $KeepRegistration) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $proc.WaitForExit(10000) | Out-Null
            Invoke-ServiceUnregister -ServiceExe $serviceExe -SyncRoot $SyncRoot
            try {
                Remove-Item -LiteralPath $SyncRoot -Recurse -Force -ErrorAction Stop
            } catch {
                throw "After unregistering, the drive folder could not be deleted: $($_.Exception.Message)"
            }
            Write-E2ELog "Unregistered; the drive folder was an ordinary folder and deleted cleanly"
        }

        if ($scenarioFailures -gt 0) {
            throw "$scenarioFailures scenario(s) failed; see the table above."
        }

        # --- result -----------------------------------------------------------
        Write-E2ELog "E2E RESULT: OK ($Mode on $Backend)"
        Write-E2ELog "SyncRoot : $SyncRoot"
        Write-E2ELog "Remote   : $script:E2ERemoteSpec ($script:E2EBackendUrl)"
        Write-E2ELog "Service  : $($proc.Id)"
        Write-E2ELog "Log      : $serviceLog"
        Write-E2ELog "Summary  : $SummaryLog"
    } catch {
        Write-E2ELog "E2E FAILED ($Mode on $Backend): $($_.Exception.Message)"
        throw
    } finally {
        if ($null -ne $proc -and -not $proc.HasExited) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        Start-Sleep -Milliseconds 500

        if ($isCloud -and -not $KeepRegistration -and (Test-Path $serviceExe)) {
            Invoke-ServiceUnregister -ServiceExe $serviceExe -SyncRoot $SyncRoot
        }

        if ($null -ne $rcloneRc -and -not $rcloneRc.HasExited) {
            try {
                Stop-Process -Id $rcloneRc.Id -Force -ErrorAction Stop
                [void]$rcloneRc.WaitForExit(10000)
            } catch { }
        }

        Stop-StaleProcesses
        Stop-E2EBackend

        Write-E2ELog "Cleanup complete: service, rclone and the $Backend server stopped."
        Write-E2ELog "Artifacts preserved under: $TempRoot"
    }
}
