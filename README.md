<p align="center">
  <img src="assets/logo.svg" width="128" height="128" alt="OpenCfDrive logo: a cloud with a drive slot">
</p>

# OpenCfDrive

**An Explorer cloud drive for any rclone remote.**

OpenCfDrive shows any rclone remote as a cloud folder in Windows Explorer. It is
a sync client built on the Windows Cloud Files API (`cfapi`, the API OneDrive
uses; "Cf" in the name) that talks to the server only through the rclone Remote
Control API, so it works with any backend rclone supports.

It works with [rclone](https://rclone.org) but is not part of it, and is not
affiliated with or endorsed by the rclone project.

- Files appear as placeholders and download when opened ("Free up space" and
  "Always keep on this device" work from Explorer's menu).
- The remote is the source of truth, and it is read one folder at a time:
  opening or refreshing a folder lists that folder from the server, and
  server-side adds, changes, deletes and renames show up then. The drive is
  never crawled, not even at startup, and no server notifications are needed.
- If the server cannot be reached, a folder shows what is already local (its
  cache). A folder that was never opened is then empty.
- Local adds, edits, deletes, renames and moves are sent to the server.
- **Conflicts: the server wins.** If a file changed (or was deleted) on the
  server while it has local changes that were not sent yet, the server's
  version replaces the local one and the local changes are discarded. See
  [Conflicts](#conflicts).
- The folder is registered with Explorer (navigation pane entry) through
  `StorageProviderSyncRootManager`.
- The OpenCfDrive app, behind an icon in the notification area, sets it up and
  shows what the sync service and rclone are doing.

## Prerequisites

- Windows 10 version 2004 (build 19041) or later
- .NET 10 SDK (the projects target `net10.0-windows10.0.19041.0` for the
  Windows storage provider APIs)
- rclone, on `PATH` (or `rclone.exe` dropped in the repository root; it is
  git-ignored). The tests and the demo backend also use it as their server.

## Install

Download the 64-bit installer from the GitHub release: either
`OpenCfDrive-<version>-x64-setup.exe` or `OpenCfDrive-<version>-x64.msi`
(the `.exe` installs that same MSI). Both install for the current user only,
into `%LOCALAPPDATA%\Programs\OpenCfDrive`, without administrator rights, and
add an "OpenCfDrive" Start Menu entry. .NET is included. The installers are
not code-signed yet, so Windows SmartScreen may warn before running them.

OpenCfDrive talks to the server through rclone, which it runs itself but does
not include. It runs the `rclone.exe` chosen in its settings; without one, the
one on `PATH` (installed with `winget install Rclone.Rclone`, for example);
without that, a copy it downloads from rclone.org when you press "Download
rclone" in its settings, checked against the release's published SHA-256
checksums, into `%LOCALAPPDATA%\OpenCfDrive\rclone`.

Set up the remote first with rclone's own configuration wizard, which asks for
the server's details and, for services like Google Drive or OneDrive, opens the
browser to sign in (rclone's documentation has a page for each provider). With
the downloaded copy, that is
`& "$env:LOCALAPPDATA\OpenCfDrive\rclone\rclone.exe" config`.

```powershell
rclone config     # create the remote, e.g. "myremote"
```

After installing, the OpenCfDrive app opens. In its Settings tab, choose the
rclone remote (the list comes from rclone's config, and a path is allowed, as
in `myremote:backup`; Refresh list shows remotes added later), and press Save:
the sync service starts rclone, then the drive, which appears in Explorer's
navigation pane at `%USERPROFILE%\OpenCfDrive` unless you chose another folder.

The rclone the service runs (`rclone rcd --rc-serve`) listens only on
`127.0.0.1`, on a free port, and wants a login made up for each run, which the
service gives it through its environment, so it never shows in a process list.
It runs in a Windows job object with the service: whenever the service ends,
even killed, Windows ends rclone too. If rclone stops on its own, the service
starts it again (after 1 s, then 5, 15 and 60 s), on the same port and login.
Its log is `%LOCALAPPDATA%\OpenCfDrive\logs\rclone.log`.

To use an `rclone rcd` you run yourself instead, choose "I run rclone rcd
myself" and give its address; it needs `--rc-serve --rc-no-auth`, since
OpenCfDrive sends no login to it.

The app then lives in the notification area, behind the arrow next to the
clock (drag its icon onto the taskbar to keep it in view). Clicking the icon
opens the app; its menu opens the drive folder, starts, stops or restarts
syncing, and shows the log. The Status tab shows:

- the sync service: whether it runs, since when, its version, and whether the
  drive is registered with Explorer and connected; how many items it tracks and
  how much it downloaded;
- rclone: who runs it, which program (and whether it was chosen, found on
  `PATH` or downloaded) and its version, where it listens, whether it runs (or
  is being restarted, and why), and whether the chosen remote is in rclone's
  config;
- the remote: what kind it is and where it points (for example
  `s3 (AWS), region eu-west-1` or `crypt over gdrive:secret`, never a password,
  key or token), and the space the server reports, if it does. The Settings
  tab shows the same for a remote while you choose it;
- the service's recent activity (uploads, downloads, deletes, warnings).

The installed app starts when you sign in to Windows (a setting in the app),
and starts the sync service when a remote is chosen. Closing the app's window
only hides it; Exit in the icon's menu closes the app, and syncing continues.
The service's log is `%LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.log`.

Uninstalling (Settings > Apps) closes the app, stops the service and its
rclone, removes the sign-in start and the rclone OpenCfDrive downloaded, and
unregisters the drive, which removes it from Explorer and leaves
`%USERPROFILE%\OpenCfDrive` as an ordinary folder with whatever was downloaded.
An upgrade keeps the registration and the settings.

## Build

```powershell
dotnet build .\OpenCfDrive.slnx
dotnet test .\OpenCfDrive.slnx
.\installer\Build-Installer.ps1 -Version 1.2.3   # both installers, into .\artifacts
```

The installers use the WiX Toolset v5 (MS-RL license), which the build restores
from NuGet. `assets\logo.svg` is the source of the icon; after changing it, run
`.\scripts\assets\Build-Icon.ps1`.

## Run

```powershell
dotnet run --project .\OpenCfDrive.App\OpenCfDrive.App.csproj       # the app, which starts the service
dotnet run --project .\OpenCfDrive.Service\OpenCfDrive.Service.csproj  # or the service alone
```

A build of the app finds the service in the service project's output for the
same configuration (installed, they sit side by side). A build never adds
itself to the sign-in start; only the installed app does, on its first start.

The service registers `%USERPROFILE%\OpenCfDrive` as the sync root, shown in Explorer as "OpenCfDrive", and fills each folder from the configured rclone remote when it is opened. Run it as the user who owns the drive: the registration is per user. It does not need an elevated shell. Without the Cloud Files API it logs why and syncs nothing; it never falls back to downloading the remote into a plain folder.

It is a Windows app without a console window. Its log goes to the console it
was started from, to a redirected output, or, with neither (the Start Menu),
to `%LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.log`. One copy runs per drive.
Two command-line options are for the installer and scripts: `--stop` ends a
running copy of the same exe, and `--unregister` unregisters the drive.

### Configuration

The app saves the settings in `%LOCALAPPDATA%\OpenCfDrive\settings.json`, and
the service reads them when it starts. An environment variable, when set, wins
over the file (scripts and tests rely on that); the app then says which ones
are set.

| Setting | `settings.json` | Environment variable | Default |
| --- | --- | --- | --- |
| Drive folder | `SyncRoot` | `OPENCFDRIVE_ROOT` | `%USERPROFILE%\OpenCfDrive` |
| rclone remote | `Remote` | `RCLONE_FS` | none (the service then uses `e2e:`, the demo backend's) |
| OpenCfDrive runs rclone | `ManageRclone` | | yes; setting `RCLONE_RC_URL` means no |
| rclone program | `RclonePath` | `OPENCFDRIVE_RCLONE` | on `PATH`, else the downloaded copy |
| rclone config file | `RcloneConfig` | | rclone's own default (`rclone config file` says which) |
| Remote control you run | `RcUrl` | `RCLONE_RC_URL` | `http://127.0.0.1:5572` |
| Start at sign-in | `StartAtSignIn` | | on for the installed app |

`OPENCFDRIVE_SETTINGS` points both the app and the service at another
settings file.

A drive folder belongs to one remote: its placeholders and state describe that
remote's files. So the app only moves the drive to a folder that is new or
empty (or that was this same remote's drive before), and switching to another
remote needs such a folder too. Otherwise the client would take the old
remote's files for server-side deletes, or the downloaded ones for new local
files. A move unregisters the old folder, which stays as an ordinary folder
with the files already downloaded.

The app asks the running service for its status through a named pipe,
`opencfdrive-<hash>` (the same hash as the state database), which only the
same user can open. A request is one line, `status` or `stop`; the answer is
one line of JSON.

### State location

Engine state (SQLite metadata + config) lives outside the sync root, in:

```
%LOCALAPPDATA%\OpenCfDrive\state\opencfdrive-<hash>.db
```

The hash is derived from the full sync-root path, so each sync root gets its own database. State is never written inside the sync root: that folder is user data and is watched for uploads. If `%LOCALAPPDATA%` is not writable, the service falls back to `%TEMP%\OpenCfDrive\state`.

## Conflicts

The rule today is simple: **the server wins.** A conflict is a file that has
local changes not yet sent to the server, while the server's copy is no longer
the one this client last saw. In that case:

| Server side | Local side | Result |
| --- | --- | --- |
| changed | edited, not sent yet | The server's version replaces the local file (it downloads again when opened). The local edit is discarded. |
| deleted | edited, not sent yet | The local file is deleted. Nothing is sent back. |
| a file this client never saw | a new local file at the same path | The server's file replaces the local one. |
| unchanged | edited | Not a conflict: the edit is uploaded. |

The client notices a conflict in two places:

- **When a folder is listed** (opened or refreshed). The listing is compared
  with the size and time the client last saw for each file.
- **Just before an upload.** It looks up that one file on the server
  (`operations/stat`), and uploads only if the server still has the version
  the client last saw. This covers an edit saved while the server copy changed
  and no listing happened in between.

Discarded local changes are not kept anywhere: there is no conflict copy.

Known limits of this first version:

- A server change is recognised by size and modification time (a difference of
  1 s or more), not by content. A change that keeps both is not seen.
- The check before an upload and the upload are two separate calls. A server
  change that lands between them is overwritten by the upload.
- A file the client is still uploading, or has just renamed, is skipped by a
  listing that may predate that change; the check before the upload covers it.

A better conflict policy is planned, for example keeping the local version as
a separate conflict copy, or conditional uploads that close the window above.

## Continuous integration

Two GitHub Actions workflows, both on Windows runners:

- **Test** (`.github/workflows/test.yml`), on every push to `main`, every pull
  request, and by hand. It runs the unit and contract tests, and the E2E suite
  in all four combinations (cloud and mock mode, WebDAV and S3) with the servers
  as local processes. Each job keeps its logs as an artifact.
- **Release** (`.github/workflows/release.yml`), when a version tag is pushed,
  or by hand with a version. It runs every test first, then builds both 64-bit
  installers and publishes a GitHub release with them attached:

  ```powershell
  git tag v1.2.3
  git push origin v1.2.3
  ```

`.github/actions/setup` installs the .NET SDK and a pinned rclone release for
both.

## E2E tests

The end-to-end tests live in `scripts\tests`. All runs share one implementation
(`E2E-Common.ps1`) and one scenario matrix (`E2E-Scenarios.ps1`). They differ in
two things:

- **Mode:** a real Cloud Files sync root registered with Explorer
  (`E2E-Run.ps1`), or the engine in mock mode over a plain folder
  (`E2E-Run-Mock.ps1`).
- **Backend:** `-Backend webdav` (default) or `-Backend s3` (a plain S3
  server with a bucket mapped to the drive). `E2E-Run-S3.ps1` is the shortcut
  for the cloud run on S3.

The server is rclone's own: `rclone serve webdav` or `rclone serve s3` over a
folder under the run's temp folder, in a process of its own. It is not the
client's rclone; the client reaches it only over HTTP, through its own rclone,
as it would any server. So the tests need nothing but the .NET SDK and rclone,
the same on a developer machine and on CI.

```powershell
.\scripts\tests\E2E-Run.ps1                  # cloud mode, WebDAV
.\scripts\tests\E2E-Run-S3.ps1               # cloud mode, S3
.\scripts\tests\E2E-Run-Mock.ps1             # mock mode, WebDAV  (add -Backend s3 for S3)
.\scripts\tests\E2E-Run.ps1 -Only "S0*,C11"  # only some scenarios
```

No elevation is needed.

### What every run does

1. Builds the solution and starts a disposable server process and a private
   `rclone rcd --rc-serve` (the client's rclone).
2. Seeds the server, starts the service, and waits for the namespace.
3. Reads and edits `alpha.txt` and `docs\readme.txt` and checks the bytes on
   both sides.
4. Runs the scenario matrix and prints a PASS / FAIL / SKIP table. Every
   scenario uses its own names and all of them run even if some fail.
5. Checks the service log markers, and that engine state stays outside the sync
   root and off the server. In cloud mode it also checks:
   - Unregistering and registering the folder again (which drops online-only
     placeholders but keeps the store) lists a folder from the server again
     when it is opened.
   - rclone never received a recursive listing during the whole run.
   - After a final unregister the drive folder is an ordinary folder that
     deletes cleanly.

### Backend-agnostic by construction

The client only ever talks to rclone. The harness talks to the server the same
way another user or app would: through a **separate rclone process** (the CLI)
that shares only `rclone.conf` with the client's rclone instance. It never uses
the backend's own protocol and never uses the client's rclone.

Nothing notifies the client of server-side changes. The servers are plain, with
no change notifications, and the client relies on none: it finds server changes
only when a folder is listed. The same matrix passing on WebDAV and on S3 is the
evidence that the client depends on rclone alone.

### Scenario matrix

**Server side (`S01`–`S18`, cloud mode only).** Another app changes the server,
then the scenario lists the folder, as Explorer does when you open it or press
F5, and expects the local side to match:

- add a file; in an existing folder; nested folders
- update a file that is downloaded; one never opened; same size, only the time
  changes
- delete a file (downloaded or not); delete a folder with files
- rename a file; move a file to another folder; rename a folder
- a 3 MB binary file (byte-exact); 40 files at once
- both sides edit the same file: the server's version wins and nothing is
  uploaded
- the server deletes a file that has an unsynced local edit: the server wins,
  the local file is deleted and nothing is sent back
- "Always keep on this device" on a 300 MB server file: one request for the
  whole file, streamed in 4 MB chunks, byte-exact, and the service's memory
  stays far below the file size
- offline (the server process is paused, so requests hang as on a lost
  network): the first listing waits for it in vain and puts the drive offline,
  after which an opened folder lists its local entries at once and a
  downloaded file still opens, a folder never opened is empty, and back online
  (using the drive checks the server) the unopened folder lists the server

Mock mode skips these. It is a test stand-in over a plain folder, with no way
to know when a folder is opened, so it copies the server once at startup.

**Client side (`C01`–`C19`, both modes).** A local action in the drive, then the
server is expected to match:

- add a file; in an existing folder; in new nested folders
- edit a file never opened; overwrite with the same size; overwrite shorter
- delete a file; delete a folder with files
- rename a file; move a file to another folder; rename a folder (files inside
  still open)
- move a file out of the drive, like the Recycle Bin (a server delete)
- save through a temp file that replaces the original
- copy or move a file in from outside the drive
- a 3 MB binary file (byte-exact); 40 files at once
- a file open locally is changed on the server, then saved, with no listing in
  between: the check before the upload sees it and the server's version wins
- rename a file, then edit it: the edit is uploaded (a rename is not mistaken
  for a server change)

**No needless work.** Every scenario also declares the most it may cost: uploads,
bytes downloaded, server renames, deletes and lookups. The harness counts every
call the client made to rclone (from rclone's debug log, so nothing the client
leaves unlogged is missed) and the bytes Windows asked it to download, and fails
the scenario if it went over or logged an error. For example, renaming a file
must cost exactly one server move: no download, no upload. The results table
shows what each scenario cost.

### Cloud-only mount checks (`E2E-Run.ps1`)

- enumerating the sync root is fast (the regression guard for the 60 s cfapi
  time-out)
- `docs` is a directory placeholder and `beta.txt` a dehydrated placeholder; a
  plain read downloads it and the bytes stay
- a lookup for a missing name returns immediately
- the root has an Explorer (`SyncRootManager`) entry
- `attrib +U` (Free up space) on a file and on a folder removes the local
  copies; `attrib +P` (Always keep on this device) downloads without opening
- an Explorer window can be opened at the sync root

The sync root defaults to `%TEMP%\opencfdrive-e2e[-mock][-s3]\drive` and is
removed at the end, so the tests never touch your own folders. Pass `-SyncRoot`
to mount somewhere else, or `-KeepRegistration` to leave the mount in place.
Artifacts (service log, rclone log, summary) stay in that `%TEMP%` folder. The
harness only stops rclone processes it started itself (matched by that folder),
never another rclone running on the machine.

## Cloud Files namespace model

The provider behaviour is modelled on Microsoft's
[CloudMirror sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CloudMirror),
the reference cfapi provider. Unlike CloudMirror, which creates the whole
namespace up front, OpenCfDrive fills each folder on demand.

### Invariants

- **The remote is the source of truth, read one folder at a time.** Nothing is
  listed at startup and the drive is never crawled. Every directory enumeration
  by another process (Explorer, `dir`, file dialogs) raises
  `FETCH_PLACEHOLDERS` (population policy `FULL`), and `SyncRootConnection`
  answers it from a live, non-recursive `operations/list` of that one
  directory. It hands out every server entry that has no placeholder on disk,
  including known items whose placeholder is gone (after unregistering and
  registering again, for instance). The exception is an item the user deleted,
  moved or renamed whose change has not reached the server yet. A server
  folder found on disk as a plain folder (unregistering leaves them so) is
  converted back into a placeholder with on-demand population, so it is
  listed when opened. The callback must always be completed. An unanswered
  request blocks the shell until cfapi gives up after 60 s with
  `ERROR_CLOUD_FILE_REQUEST_TIMEOUT`. The client never relies on server
  notifications: listing a folder is the only way it learns about changes.
- **Fetch from the remote, fall back to the cache.** A listing that fails, or
  gets no answer within 10 s (offline usually means rclone is up but keeps
  retrying an unreachable server), is answered with nothing new and no server
  comparison. The folder then shows what is already local, so nothing local is
  deleted; a folder never opened shows empty.
- **Offline is a state, as in Nextcloud and OneDrive.** A request that cannot
  reach the server (no answer in time, no connection to rclone, or rclone's
  own network error) puts the drive offline (`ServerConnection`). Until the
  server answers again, every folder shows what is local at once instead of
  waiting 10 s per folder (Windows may ask for a folder's parent too, which
  would double that), an online-only file fails to open at once, as in
  Nextcloud, and edits wait and are uploaded once the server answers, as on
  Nextcloud's next sync. The server is checked again when Windows reports a
  network change, when the drive is used, and on Nextcloud's schedule: 62 s
  after going offline, then one more 62 s per failed check, at most 8 × 62 s.
  The app shows "Offline". Deletes and renames made offline are not yet kept
  for later: they fail, and the server's copy stays.
- **Answered folders are marked full, then switched back.** The answer sets
  `CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION`, because
  creating a new name in a "partially populated" folder makes the platform ask
  again and again until the create fails with `ERROR_CANT_RESOLVE_FILENAME`
  ("The name of the file cannot be resolved by the system"). One second later
  `CfUpdatePlaceholder(ENABLE_ON_DEMAND_POPULATION)` switches the folder back,
  so the next listing asks the server again.
- **Deletes and renames must be acknowledged.** `NOTIFY_DELETE` and
  `NOTIFY_RENAME` arrive *before* the operation, and the platform holds it until
  the provider answers `ACK_DELETE` / `ACK_RENAME`; without that the user's
  delete or rename hangs. Both are always allowed, and the server is updated
  afterwards. For placeholders these notifications are the source of truth:
  the file watcher reports a move between folders as delete + create, and its
  own rename events for placeholders are ignored, so nothing reaches the server
  twice.
- **A server delete waits until the item has really gone.** Both notifications
  come before the operation, and it may not end in a delete:
  - A move out of the drive (Recycle Bin) is announced before Windows downloads
    the file to take it along. Deleting the server copy at once made that
    download fail and the move with it.
  - Replacing a file (an editor saving through a temp file, or delete then
    rename into place) reports a delete of the original.
  So the server copy is deleted only once the path has stayed empty for a
  second. If a different file (another NTFS file id) appears there, it was
  replaced: its upload updates the server copy, with no delete in between. If
  the same file is still there 30 s after its last download, the operation
  failed and the server copy is kept.
- **Mock mode has only the file watcher, which reports a move between folders
  as delete + create.** So a watcher delete is held for a second too. A file
  back at the same path is a replacement. A file appearing elsewhere with the
  size and exact write time the deleted one last had in sync is the same file
  moved, and becomes one server move instead of a delete and an upload.
- **In-sync alone does not prove a file unchanged.** The platform clears
  in-sync on most writes, but not all: an append that starts on a page boundary
  (to a downloaded 4 KiB file, say) leaves it set. So, like other sync clients
  (Nextcloud compares size and modification time with its journal), the
  provider also compares the file's size and write time with what they were
  when it was last in sync, and only a file that is in sync and matches both
  counts as unchanged. That is the server's size and time for a placeholder
  made from a listing or updated from the server (downloading and freeing up
  space keep both), and the file's own after an upload, since servers usually
  stamp their own time; the store keeps them.
- **A rename clears in-sync; the provider sets it again.** The platform clears
  a placeholder's in-sync state on every rename. The provider reads the state
  in `NOTIFY_RENAME`, before acknowledging, and once the rename has reached the
  server it sets the new identity together with `MARK_IN_SYNC`, but only if the
  file was unchanged (see above) and has not been written since. Otherwise the
  renamed file would look edited and be downloaded and uploaded again.
- **Folders.** A folder deleted locally deletes the files this client knows and
  then removes the server folder only if it is empty (`operations/rmdirs`), so
  files added on the server that this client never listed survive. Each file
  is deleted once: its own notification and its folder's delete claim it in
  the store, whichever comes first, and the folder waits for claimed deletes
  still in flight before one `rmdirs`. A subfolder of a folder being deleted
  leaves the work to its parent. A folder renamed locally is moved on the
  server (`sync/move`), and every row and placeholder identity below it is
  re-keyed so files inside still open.
- **Changes and deletes are applied after the reply.** `TRANSFER_PLACEHOLDERS`
  can only add entries, so the same listing is compared against the store (which
  holds the server's last known size and time, also after uploads). Once the
  shell has its reply, files deleted on the server are deleted locally, and
  changed files get `CfUpdatePlaceholder(MARK_IN_SYNC | DEHYDRATE)` with the
  new size and time. The server wins (see [Conflicts](#conflicts)), so this
  leaves out `VERIFY_IN_SYNC`, which would make the platform refuse a file
  edited locally. A plain local file is converted to a placeholder first. Only
  a file whose own upload or rename is still on its way is left for the next
  listing, because this listing may predate it. Deletes that come from the
  server are not sent back to it.
- **Placeholder updates need the Win32 handle.** `CfOpenFileWithOplock` returns a
  protected handle that `CfUpdatePlaceholder` and friends reject with `E_HANDLE`.
  `CloudPlaceholderService.WithWin32Handle` references it and passes
  `CfGetWin32HandleFromProtectedHandle` instead.
- **Metadata-only updates use a handle without data access.** Opening a
  placeholder that is not fully on disk with write access makes the platform
  download all of it first. So a new identity, `MARK_IN_SYNC`, and a server
  change to a file that is not fully local go through a `WRITE_DAC`-only handle
  (`WithMetadataHandle`), and nothing is downloaded.
- **Downloads are streamed in 4 MB chunks.** A `FETCH_DATA` range is handed
  over in several `TRANSFER_DATA` calls (each on a 4 KB boundary), so memory
  use stays at one chunk whatever the file size. A failed download is reported
  for the bytes that did not arrive. A zero-length failure is rejected, and the
  reading program would then wait for the platform's 60 s timeout.
- **After an upload, one lookup.** The store keeps the server's own size and
  time, which most remotes stamp themselves, so the client reads them back with
  `operations/stat` on that one file, not a listing of its folder.
- **One remote listing per directory per second.** Enumerations by the provider
  process itself never raise the callback.
- **On-demand population is enabled on the root and every directory.** The
  root is registered without `CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT`,
  directory placeholders are created without
  `CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION`, and startup calls
  `CfUpdatePlaceholder(ENABLE_ON_DEMAND_POPULATION)` on the root and every
  directory the store knows. That is local work, with no server calls, and it
  re-enables population on directories that older versions created with it
  disabled or that a stop left marked full.
- **Directories are placeholders too.** Folders are handed out as *directory*
  placeholders (`FILE_ATTRIBUTE_DIRECTORY`, size 0). Plain
  `Directory.CreateDirectory` folders are invisible to cfapi.
- **One directory per answer.** `RelativeFileName` is a single name: the API
  rejects names containing path separators, so each `TRANSFER_PLACEHOLDERS`
  answer holds the entries of the one directory that was opened.
- **Registered with Explorer, not only the driver.** `SyncRootRegistrar`
  registers through `StorageProviderSyncRootManager.Register`. That does the
  Cloud Files driver registration (what `CfRegisterSyncRoot` does) and also
  adds the root to Explorer: a navigation pane entry with a name and icon, plus
  the "Always keep on this device" / "Free up space" menu (`AllowPinning`).
  - The id is `OpenCfDrive!<user SID>!<hash of the folder path>`, so every sync
    root, including the E2E's temp root, has its own entry and none can replace
    another.
  - Registering an existing id updates it in place, which is how upgrades
    re-apply policies. Roots registered by earlier versions through
    `CfRegisterSyncRoot` alone are upgraded the same way.
  - A failed registration is tried twice more (it failed once on a fresh
    machine) before falling back to `CfRegisterSyncRoot` alone: the drive then
    syncs, but has no Explorer entry until the next start.
  - Unregistering goes by that id: `GetSyncRootInformationForFolder` throws and
    `GetCurrentSyncRoots` does not list these roots. Registration state is read
    from `HKLM\...\Explorer\SyncRootManager\<id>`.
  - Unregistering must leave an ordinary folder. Neither `CfUnregisterSyncRoot`
    nor the shell's `Unregister` reliably removes placeholders that only exist
    on the server, and orphaned ones can no longer be opened or even deleted
    ("The cloud file provider exited unexpectedly"). So the registrar
    re-registers the folder for a moment, connects a throwaway provider that
    only acknowledges deletes (not the sync engine, so nothing reaches a
    server), deletes those online-only placeholders, and unregisters again.
    Running `--unregister` on a folder left behind by an earlier version cleans
    it the same way.
  - If shell registration fails, the registrar falls back to
    `CfRegisterSyncRoot` with `CF_REGISTER_FLAG_UPDATE` retries, as before, so
    syncing still works without the Explorer extras. The log says which one
    happened.
  - Never unregister to "reset" policies: unregistering permanently deletes
    every dehydrated placeholder in the tree.
- **"Free up space" / "Always keep on this device" are the provider's job.**
  Explorer and Storage Sense only set the unpinned / pinned attribute (the same
  as `attrib +U` / `attrib +P`). The engine's watcher includes attribute changes
  and dehydrates or hydrates the file (`CfDehydratePlaceholder` /
  `CfHydratePlaceholder`). On a folder, it applies to the local files inside.
  Dehydrating is refused for a file with local edits that have not been
  uploaded, so freeing space never loses an edit.

Mock mode alone uses a recursive listing, to copy the server into its plain
folder at startup. That listing only reports files, so ancestor directories are
synthesised from file paths (`CloudSyncEngine.BuildNamespace`); directories the
remote does report are kept.

### Things that are expected, not bugs

- Files that already existed locally before the root was registered (for
  example, hydrated files restored by a previous uninstall) stay normal local
  files. cfapi placeholders cannot be created over an existing file; CloudMirror
  behaves the same way. Edits to them are still uploaded by the watcher.
- Files added, changed or deleted on the remote show up the next time their
  folder is listed (open it or press F5 in Explorer). A re-list within 1 s of
  the previous one reuses that listing. Folders that are never opened are not
  refreshed, and are empty while the server cannot be reached.
- Offline, a file that was never downloaded cannot be opened; only downloaded
  files (and ones kept with "Always keep on this device") are readable. Edits
  are uploaded once the server answers again, but deletes and renames made
  offline are not kept for later yet.
- A local edit that has not reached the server yet is discarded if the server
  copy changed or was deleted in the meantime: the server wins (see
  [Conflicts](#conflicts)). A file open in another program is updated on a
  later listing.
- A program that opens a file in the few milliseconds while the client applies
  a server change to it can get "being used by another process"; opening it
  again works.

### Why .NET / Explorer sometimes disagree on placeholder state

Windows has a per-process filter (`RtlSetProcessPlaceholderCompatibilityMode`) that
masks placeholder bits (`ReparsePoint`, `Offline`, `Sparse`) from applications
that are not in the Windows system directories. The three values are
`PHCM_APPLICATION_DEFAULT` (0), `PHCM_DISGUISE_PLACEHOLDER` (1), and
`PHCM_EXPOSE_PLACEHOLDERS` (2). A .NET process outside `C:\Windows` often gets
disguised (`0x20` Archive) while PowerShell 5.1 (`C:\Windows\System32`) or `fsutil`
see the real `0x420` (Archive + ReparsePoint) with tag `0x9000601A`.

This does NOT mean the placeholder is broken; `QueryState` uses the native
`GetFileAttributesExW` + `GetFileInformationByHandleEx` APIs (same path the
service uses), and Explorer reads the identity independently through `cfapi.dll`.
Overlay icons (OneDrive `FileSyncShell64.dll`, Google `drivefsext.dll`) come from
a separate mechanism (`ShellIconOverlayIdentifiers`) and work regardless of the
attribute mask; our provider has no overlay DLL registered, so Explorer shows the
file with default icons rather than a cloud badge.

## Layout

| Project / file | Purpose |
| --- | --- |
| `OpenCfDrive.Core` | Cloud Files interop, rclone clients, sync engine, SQLite metadata, settings, and the status channel |
| `OpenCfDrive.App` | The app (`OpenCfDrive.exe`): settings and status, behind a notification area icon; starts and stops the service |
| `OpenCfDrive.Service` | The background service that registers and serves the sync root |
| `OpenCfDrive.MockService` | Test host: same engine in mock mode over a plain folder |
| `OpenCfDrive.Tests` | Unit and contract tests (interop layout, Cloud Files behaviour, SQLite, rclone API) |
| `scripts\drive` | Using the drive: `Install-`, `Start-` and `Uninstall-OpenCfDrive.ps1`, plus `Setup-OpenCfDrive-Environment.ps1` (optional local demo backend: `rclone serve webdav` + `rclone rcd`) |
| `scripts\tests` | End-to-end tests: `E2E-Run.ps1`, `E2E-Run-S3.ps1`, `E2E-Run-Mock.ps1`, with the shared harness `E2E-Common.ps1` and scenario matrix `E2E-Scenarios.ps1` |
| `assets` | The logo (`logo.svg`, the source), `opencfdrive.ico` (the app's and the service's icon, which Explorer also shows for the drive), and `logo-64.png` (the `.exe` installer's logo) |
| `scripts\assets` | `Build-Icon.ps1`: rebuilds `opencfdrive.ico` and `logo-64.png` from `logo.svg` (rendered by Microsoft Edge) |
| `installer` | `Build-Installer.ps1`, the WiX projects for the MSI (`msi`) and the `.exe` bootstrapper (`bundle`), and the third-party notices installed with the app |
| `.github` | The Test and Release workflows and their shared setup step |

## Using it

The scripts default to the same values, so they can be run in order without
arguments. None of them needs an elevated shell; run them as the user who owns
the drive.

| Value | Default |
| --- | --- |
| Sync root | `%USERPROFILE%\OpenCfDrive` |
| rclone remote | `e2e:` (the demo backend; pass `-Remote` for your own) |
| rclone RC URL | `http://127.0.0.1:5572` |

```powershell
.\scripts\drive\Install-OpenCfDrive.ps1              # build + publish -> .\publish, create the sync root
.\scripts\drive\Setup-OpenCfDrive-Environment.ps1    # optional: demo backend (rclone WebDAV server + rclone rcd)
.\scripts\drive\Start-OpenCfDrive.ps1                # launch the service, wait for "Mode: cloud"
# open %USERPROFILE%\OpenCfDrive in Explorer, read/write files
.\scripts\drive\Uninstall-OpenCfDrive.ps1            # stop the service, unregister the sync root
```

To use your own remote instead of the demo backend, run rclone's remote control
yourself and point the service at it:

```powershell
rclone rcd --rc-serve --rc-no-auth --rc-addr 127.0.0.1:5572
.\scripts\drive\Start-OpenCfDrive.ps1 -Remote "myremote:" -RcUrl "http://127.0.0.1:5572"
```

What each script does and does not do:

- **Install** builds the solution, publishes `OpenCfDrive.Service` to
  `.\publish`, and creates the sync-root folder. It writes nothing else.
- **Setup** starts a WebDAV server, `rclone serve webdav` on port 5573 (user
  `demo`, password `demo`), over `%LOCALAPPDATA%\OpenCfDrive\demo-server`, so
  the "server" files survive re-runs. It writes the rclone config to
  `%TEMP%\opencfdrive-setup\rclone.conf` with an obscured password, and starts
  `rclone rcd --rc-serve` on port 5572. Both keep running in the background. A
  re-run replaces only the two rclone processes it started itself; other rclone
  processes are left alone.
- **Start** passes `OPENCFDRIVE_ROOT`, `RCLONE_FS` and `RCLONE_RC_URL` to the
  service **process only** (Process scope, never machine scope), writes the
  service log to `%TEMP%\opencfdrive-service\`, and waits for the
  `Mode: cloud | ... items=N` line.
- **Uninstall** stops the `OpenCfDrive.Service` process and unregisters the sync
  root, which also removes its Explorer entry and any online-only placeholders,
  leaving an ordinary folder. It never deletes the sync root (that is user data)
  and never touches machine environment variables. Pass `-RemovePublish` to
  delete `.\publish` as well.

State stays at `%LOCALAPPDATA%\OpenCfDrive\state\opencfdrive-<hash>.db`, never
inside the sync root. Sign out or restart Explorer after uninstalling so the
namespace disappears from the navigation pane.

After `Start-OpenCfDrive.ps1` reports `Mode: cloud | ... registered=True |
connected=True`, Explorer should navigate the folder instantly: entries list
immediately, dehydrated files show the cloud icon, and opening one hydrates it
(the service log shows a `FETCH_DATA` line). Quick check:

```powershell
$sw = [Diagnostics.Stopwatch]::StartNew()
Get-ChildItem "$env:USERPROFILE\OpenCfDrive" -Force | Format-Table Name, Attributes
$sw.ElapsedMilliseconds   # must be milliseconds, never ~60000
```

A sync root registered by an older build is upgraded in place the next time the
service starts: registering an existing id updates its policies without
touching data.

## License

Public domain, under [the Unlicense](LICENSE): anyone may use, copy, modify and
distribute this code for any purpose, and nobody owns it.

The software is provided "as is", without warranty of any kind. It reads,
writes, moves and deletes files on your computer and on your rclone remote; use
it at your own risk. Nobody involved is liable for lost data or any other damage.

### Third-party software

This repository contains only OpenCfDrive's own code. What it works with is
installed or downloaded separately and keeps its own license:

- **rclone** ([MIT license](https://github.com/rclone/rclone/blob/master/COPYING)).
  OpenCfDrive runs it as a separate program and talks to it over its HTTP
  Remote Control API; no rclone code is included. It runs one already
  installed, or downloads the official release from rclone.org when asked.
  The tests and the demo backend also run it as their WebDAV or S3 server.
- **NuGet packages** listed in the project files (`Microsoft.Data.Sqlite`,
  `SQLitePCLRaw`, `Microsoft.Extensions.Hosting`, and the xunit test packages),
  restored at build time.

Anyone who ships a build together with these must include their license
notices. The installers do: the app is self-contained (the .NET runtime and
these packages are inside it), and `installer\THIRD-PARTY-NOTICES.txt` is
installed next to it. They do not include rclone; a package that bundled
`rclone.exe` would also need rclone's `COPYING` file.
