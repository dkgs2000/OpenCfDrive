# Testing

OpenCfDrive has two kinds of tests: unit and contract tests, run by
`dotnet test`, and an end-to-end suite of PowerShell scripts. Both need only
the .NET SDK and rclone, on a developer machine or on CI. None of them needs an
elevated shell.

- [Unit and contract tests](#unit-and-contract-tests)
- [End-to-end tests](#end-to-end-tests)
- [What every run does](#what-every-run-does)
- [Backend-agnostic by construction](#backend-agnostic-by-construction)
- [Scenario matrix](#scenario-matrix)
- [Cloud-only mount checks](#cloud-only-mount-checks)
- [Where runs keep their files](#where-runs-keep-their-files)

## Unit and contract tests

```powershell
dotnet test .\OpenCfDrive.slnx
```

`OpenCfDrive.Tests` covers the interop layout, the SQLite store, settings, the
sync scheduler, conflict copies, the updater, the app's window, and contract
tests against the real Cloud Files API and a real rclone. The tests find `rclone.exe` next to the
build output or in a parent folder (the repository root, for instance), else on
`PATH`. A test that needs rclone or the Cloud Files API is skipped, with the
reason, when it's missing.

`MainFormLayoutTests` opens the app's window, unseen, for each state the drive
can be in (not set up, up to date, syncing, offline, rclone missing, your own
`rclone rcd`, an update ready, long names). It does so on each screen scale the
PC has, with normal and larger text, at the size the window opens at and at
its smallest. On every page it checks that no text is cut off, every button
fits its words, nothing overlaps or sticks out, inputs keep room to type, cards
have no empty rows, and the activity list keeps some lines. It also checks that
the spacing follows the screen's scale. To look at the pages yourself, set
`OPENCFDRIVE_UI_PICTURES` to a folder before `dotnet test`. The tests then save
a picture of each page there.

## End-to-end tests

The end-to-end tests live in `scripts\tests`. All runs share one implementation
(`E2E-Common.ps1`) and one scenario matrix (`E2E-Scenarios.ps1`). Runs differ in
two ways:

- **Mode:** a real Cloud Files sync root registered with Explorer
  (`E2E-Run.ps1`), or the engine in mock mode over a plain folder
  (`E2E-Run-Mock.ps1`).
- **Backend:** `-Backend webdav` (the default) or `-Backend s3` (a plain S3
  server with a bucket mapped to the drive).

The server is rclone's own: `rclone serve webdav` or `rclone serve s3` over a
folder in the run's temp folder, in a process of its own. It isn't the
client's rclone. The client reaches it only over HTTP, through its own rclone,
as it would any server.

```powershell
.\scripts\tests\E2E-Run.ps1                  # cloud mode, WebDAV
.\scripts\tests\E2E-Run.ps1 -Backend s3      # cloud mode, S3
.\scripts\tests\E2E-Run-Mock.ps1             # mock mode, WebDAV (add -Backend s3 for S3)
.\scripts\tests\E2E-Run.ps1 -Only "S0*,C11"  # only some scenarios
```

### Against a real server

`-Backend remote -Remote "myremote:"` runs the cloud scenarios against an
rclone remote you already use, with your rclone config (`-RcloneConfig` picks
another file). The config is only read. Nothing outside one new folder,
`opencfdrive-e2e-<date>-<time>`, is touched, and that folder is deleted at
the end:

- The folder is made and deleted from the remote's root, naming it. Some
  servers redirect a folder path without a trailing slash to `http://`, which
  rclone refuses because it would send the password in clear.
- For a WebDAV remote the folder becomes the remote's root for the rclones the
  run starts (`RCLONE_CONFIG_<NAME>_URL`), so the drive is a remote's root, as a
  user's usually is. Other remotes get the folder as a path. `-RemoteAsPath`
  gives a WebDAV remote the folder as a path too (`myremote:opencfdrive-e2e-…`),
  for a drive set up on a remote with a path.
- What the server keeps (hashes, empty folders, file times) is read from
  `rclone backend features` instead of assumed.
- The scenarios that take the server offline (S18, S31, S33, C24, C30) are
  skipped: only a server the run started can be paused.

While it runs, a drive of yours on that remote shows the folder too.

```powershell
.\scripts\tests\E2E-Run.ps1 -Backend remote -Remote "myremote:"
```

## What every run does

1. Builds the solution, then starts a disposable server process and a private
   `rclone rcd --rc-serve` (the client's rclone).
2. Seeds the server, starts the service, and waits for the namespace.
3. Reads and edits `alpha.txt` and `docs\readme.txt`, and checks the bytes on
   both sides.
4. Runs the scenario matrix and prints a PASS / FAIL / SKIP table. Every
   scenario uses its own names, and all of them run even if some fail.
5. Checks the service log markers, and that engine state stays outside the sync
   root and off the server. In cloud mode it also checks that:
   - unregistering and registering the folder again (which drops online-only
     placeholders but keeps the store) lists a folder from the server again
     when it's opened;
   - rclone never received a recursive listing during the whole run;
   - after a final unregister, the drive folder is a normal folder that
     deletes cleanly.

## Backend-agnostic by construction

The client only ever talks to rclone. The harness talks to the server the way
another user or app would: through a **separate rclone process** (the CLI) that
shares only `rclone.conf` with the client's rclone. It never uses the backend's
own protocol and never uses the client's rclone.

Nothing notifies the client of server-side changes. The servers are plain, with
no change notifications, and the client relies on none. It finds server changes
by listing the folders it has (its background sync), and when a folder is
opened for the first time. The same matrix passing on WebDAV and on S3 is the
evidence that the client depends on rclone alone.

In the E2E service, the background sync runs only when a scenario asks for a
run, through the service's `sync` control request, which is answered once the
run has finished. So each scenario sees exactly the server calls it caused. The
30 s schedule itself is unit-tested (`SyncSchedulerTests`).

## Scenario matrix

**Two ways to save.** Programs save a file in one of two ways, and the
scenarios use both. Many replace it with a new file (.NET's
`File.WriteAllText`, or a temp file renamed over it); over a placeholder that
leaves a plain file. Others, Notepad among them, write into the same file,
which stays a placeholder whose size Windows keeps a record of. The edit
scenarios below are run both ways where the two take different paths.

**Server side (`S01`–`S35`, cloud mode only).** Another app changes the server.
The scenario then has the client run its background sync, lists the folder as
Explorer does, and expects the local side to match:

- add a file; in an existing folder; nested folders
- update a file that is downloaded; one never opened; same size, only the time
  changes
- delete a file (downloaded or not); delete a folder with files
- rename a file; move a file to another folder; rename a folder
- a 3 MB binary file (byte-exact); 40 files at once
- both sides edit the same file: both are kept, the local edit as a conflict
  copy that is uploaded too (the local edit saved both ways)
- the server deletes a file that has an unsynced local edit: the edit is kept
  and uploaded again (saved both ways; saved in place, it also opens whole
  after Free up space)
- the server deletes a folder while a file in it has an unsynced edit: that
  file stays and goes back, the rest of the folder goes
- the client deletes a file the server changed meanwhile: the server's version
  comes back
- the client and the server add different files at the same new path: both
  are kept
- a folder once listed shows as available (no cloud icon) and still gets what
  the server adds
- files and folders with Arabic names, added and renamed on the server
- "Always keep on this device" on a 300 MB server file: one request for the
  whole file, streamed in 4 MB chunks, byte-exact, and the service's memory
  stays far below the file size
- offline (the server process is paused, so requests hang as on a lost
  network): an opened folder lists its local entries at once, the first
  listing of a folder never opened waits for the server in vain and puts the
  drive offline, that folder is empty, and back online (using the drive checks
  the server) it lists the server
- content hashes, where the server keeps them (S3 here; the WebDAV server
  keeps none, so the same scenarios check the fallback): both sides making the
  same edit is no conflict; the server writing a file again with the same
  content leaves it local; and a sync run of a folder listed before sends no
  per-file request for an unchanged file (counted from rclone's request log,
  which the S3 run keeps with `--dump headers`)

Mock mode skips these. It's a test stand-in over a plain folder, with no way
to know when a folder is opened, so it copies the server once at startup.

**Client side (`C01`–`C33`, both modes unless noted).** A local action in the
drive, after which the server is expected to match:

- add a file; in an existing folder; in new nested folders
- edit a file never opened; save a file by replacing it, the same size and
  shorter
- edit a downloaded file in place, as Notepad does, the same size, shorter
  and longer: it's uploaded, and after Free up space (cloud mode) it opens
  whole, not cut at its old size
- delete a file; delete a folder with files
- rename a file; move a file to another folder; rename a folder (files inside
  still open); rename a folder never opened and list it at once, as Explorer
  can (cloud mode): it and the folder inside it show their files, and a file
  in it opens, before the rename has reached the server
- move a file out of the drive, like the Recycle Bin does (a server delete)
- save through a temp file that replaces the original
- copy or move a file in from outside the drive
- a 3 MB binary file (byte-exact); 40 files at once
- a file open locally is changed on the server, then saved, with no listing in
  between: the check before the upload sees it, and both versions are kept
- rename a file, then edit it: the edit is uploaded (a rename isn't mistaken
  for a server change)
- append to a downloaded file that ends on a page boundary: the edit is
  uploaded
- create an empty folder; create one and rename it right away (Explorer's New
  folder: only the final name reaches the server); create one, let a sync run
  pass (which marks it to be listed when next opened), then rename it (cloud
  mode): the server has it under the new name; move a folder with files
  and a subfolder in from outside the drive. On S3, which keeps no empty
  folders, an empty folder stays here and is never taken for deleted
- folders and files with Arabic names, and a rename
- offline (cloud mode): a delete and a rename made while the server can't be
  reached are kept and reach the server once it answers; nothing comes back
  meanwhile. An edit saved in place offline is uploaded once it answers
- online-only folders are not re-listed by background sync; folders holding
  local files are refreshed, failed listings fall back to their disk contents,
  and an unopened folder is listed when Explorer opens it. A sync run marks an
  online-only folder to be listed when next opened, and opening it shows what
  the server changed meanwhile (the E2E sets `OPENCFDRIVE_RELIST_SECONDS=0`,
  so every run marks them)
- Explorer item states cover waiting to upload (cleared after a successful
  upload), conflict copy (kept after it is uploaded), and a failed sync. A file
  that only came from the server never gets one
- Free up space on a file with an unsynced edit (cloud mode): it's uploaded
  first, then freed, and opens whole again
- what the app's icon shows (cloud mode), read the way the app reads it: the
  service's `watch` answer on its control pipe, a line at every change. While
  a new file is sent (the server paused mid-upload) the service reports it
  as sending, and once it's on the server, nothing left to do. Offline, an
  edit is reported as waiting for the server (S33)

**No needless work.** Every scenario also declares the most it may cost:
uploads, bytes downloaded, server renames, deletes and lookups. The harness
counts every call the client made to rclone (from rclone's debug log, so
nothing the client leaves unlogged is missed) and the bytes Windows asked it to
download. It fails the scenario if the cost went over, or if the scenario
logged an error. For example, renaming a file must cost exactly one server
move: no download, no upload. The results table shows what each scenario cost.

Where the cost differs by server, the scenario says so. On S3 a sync run looks
up the time of each file it finds new or changed (a listing with times would
pay a request per file, changed or not), and a conflict hashes the local file
first (`Get-Budget -TimeLookups`, `-HashesLocal`).

## Cloud-only mount checks

`E2E-Run.ps1` also checks the mount itself:

- enumerating the sync root is fast (the regression guard for the 60 s cfapi
  time-out)
- `docs` is a directory placeholder and `beta.txt` a dehydrated placeholder; a
  plain read downloads it and the bytes stay
- a lookup for a missing name returns immediately
- the root has an Explorer (`SyncRootManager`) entry
- `attrib +U` (Free up space) on a file and on a folder removes the local
  copies; `attrib +P` (Always keep on this device) downloads without opening
- an Explorer window can be opened at the sync root

Whether a file is downloaded is checked by waiting on Windows' attribute change
notifications for it (`Wait-ForAttributes`), not by polling: Windows marks a
file downloaded a moment after the read that downloaded it returns.

## Where runs keep their files

The sync root defaults to `%TEMP%\opencfdrive-e2e[-mock][-s3]\drive` and is
removed at the end, so the tests never touch your own folders. Pass `-SyncRoot`
to mount somewhere else, or `-KeepRegistration` to leave the mount in place.

Artifacts (the service log, rclone log and summary) stay in that `%TEMP%`
folder. The harness only stops rclone processes it started itself, matched by
that folder, never another rclone running on the machine.

On CI, the Test workflow runs the suite in all four combinations and keeps each
job's logs as an artifact (see [Development](development.md#ci-and-releases)).
