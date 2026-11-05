# Architecture

How OpenCfDrive turns an rclone remote into a cloud drive, and the rules the
code relies on. Many of them come from bugs, so read this before changing the
sync engine.

- [Overview](#overview)
- [The namespace model](#the-namespace-model)
- [Invariants](#invariants)
- [Detecting conflicts](#detecting-conflicts)
- [What tells a change](#what-tells-a-change)
- [Expected behaviour, not bugs](#expected-behaviour-not-bugs)
- [Why .NET and Explorer sometimes disagree on placeholder state](#why-net-and-explorer-sometimes-disagree-on-placeholder-state)

## Overview

OpenCfDrive is a sync client built on the Windows Cloud Files API (`cfapi`, the
API OneDrive uses, and the "Cf" in the name). It talks to the server only
through the [rclone Remote Control API](https://rclone.org/rc/), so it works
with any backend rclone supports.

```
Explorer, other programs
      │  list, open, save, rename, delete
      ▼
Cloud Files driver (built into Windows)
      │  callbacks and notifications
      ▼
OpenCfDrive.Service ──── HTTP ────► rclone rcd ────► your remote
      │        ▲
      │        └──── named pipe: status, stop, sync ──── OpenCfDrive.exe (the app)
      ▼
SQLite state in %LOCALAPPDATA%\OpenCfDrive\state
```

- **`OpenCfDrive.Service`** registers the drive folder as a sync root, answers
  the Cloud Files callbacks (listing folders, downloading file data,
  acknowledging deletes and renames), watches the folder for local changes,
  and runs the background sync. It starts and supervises its own `rclone rcd`,
  unless the user runs one.
- **`OpenCfDrive.App`** is the notification area app. It edits the settings,
  starts and stops the service, shows its status over a named pipe, and
  updates itself.
- **`OpenCfDrive.Core`** holds everything shared: the interop, the sync
  engine, the rclone clients, the store and the settings.

The provider behaviour is modelled on Microsoft's
[CloudMirror sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CloudMirror),
the reference cfapi provider. Syncing behaves like Nextcloud's and OneDrive's
clients, and where a rule is copied from Nextcloud the text says so. Unlike
CloudMirror, which creates the whole namespace up front, OpenCfDrive fills each
folder on demand.

## The namespace model

- The remote is the source of truth, read one folder at a time, the first
  time each folder is opened. Nothing is crawled.
- A folder once listed stays fully populated until the poll interval passes.
  The background sync keeps it current: a folder that holds anything local is
  listed every run; an online-only folder (nothing downloaded or kept in it)
  is not re-listed - it is left for the next time it is opened, and once it is
  older than the poll interval it is marked for on-demand population again.
  Sync runs happen at start, right after the server is back, and on request.
- Local changes are recorded from the Cloud Files notifications and a file
  watcher, and sent to the server in one plan per sync run, which starts a
  second after the last change.
- When the server can't be reached, the drive is offline. It serves what is
  local and keeps local changes until the server answers.

The invariants below spell out how.

## Invariants

- **The remote is the source of truth, read one folder at a time.** Nothing is
  crawled. The first enumeration of a folder by another process (Explorer,
  `dir`, file dialogs) raises `FETCH_PLACEHOLDERS` (population policy `FULL`),
  and `SyncRootConnection` answers it from a live, non-recursive
  `operations/list` of that one directory. It hands out every server entry
  that has no placeholder on disk, including known items whose placeholder is
  gone (after unregistering and registering again, for instance). The
  exceptions are an item the user deleted, moved or renamed whose change has
  not reached the server yet, and a new local file not uploaded yet where the
  server has one of its own (its upload keeps both). A server folder found on
  disk as a plain folder (unregistering leaves them so) is converted back into
  a placeholder with on-demand population, so it is listed when opened. The
  callback must always be completed. An unanswered request blocks the shell
  until cfapi gives up after 60 s with `ERROR_CLOUD_FILE_REQUEST_TIMEOUT`.
- **Listed folders stay listed; the background sync keeps them current.** The
  answer sets `CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION`
  and the folder stays fully populated, as Nextcloud's and OneDrive's folders
  do. Windows lists it from disk from then on, and Explorer shows it as
  available: a folder not fully populated carries `OFFLINE` and
  `RECALL_ON_DATA_ACCESS`, which is the cloud icon. A fully populated folder is
  also what the platform needs before it lets a new name be created there; in
  a "partially populated" one it asks again and again until the create fails
  with `ERROR_CANT_RESOLVE_FILENAME`. Server changes in listed folders come from
  the background sync (`SyncScheduler`, copied from Nextcloud's sync runs): one
  run at start, one every 30 s (Nextcloud's `remotePollInterval`;
  `OPENCFDRIVE_REMOTE_POLL_SECONDS` overrides it), one right after the server
  is back, and one on the service's `sync` control request. A run first sends
  the deletes and renames kept from an offline spell, then lists the listed
  folders (the platform keeps that state on disk) one at a time, parents
  first, never recursively. A folder that holds nothing local is online-only:
  it is not listed every run; once it is older than the poll interval it is
  given on-demand population again, and Windows lists it from the server when
  it is next opened. Folders never opened are not listed until they are. The
  client never relies on server notifications.
- **Fetch from the remote, fall back to the cache.** A listing that fails, or
  gets no answer within 10 s (offline usually means rclone is up but keeps
  retrying an unreachable server), is answered with nothing new and no server
  comparison. The folder then shows what is already local, so nothing local is
  deleted; a folder never opened shows empty and stays unlisted, so it is
  listed once the server answers.
- **Offline is a state, as in Nextcloud and OneDrive.** A request that cannot
  reach the server (no answer in time, no connection to rclone, or rclone's
  own network error) puts the drive offline (`ServerConnection`). Until the
  server answers again, a folder never opened shows what is local at once
  instead of waiting 10 s, an online-only file fails to open at once, as in
  Nextcloud, and nothing made here is lost: the runs send nothing and every
  local change waits, deletes and renames in the store's journal
  (`PendingChanges`, so they survive a restart, like Nextcloud's journal), with
  the items they change left alone by listings meanwhile. The first run once
  the server answers sends them in one plan.
  The server is checked again when Windows reports a network change, when the
  drive is used, and on Nextcloud's schedule: 62 s after going offline, then
  one more 62 s per failed check, at most 8 × 62 s. The app shows "Offline".
- **Changes made while the service was not running are sent when it starts**,
  as Nextcloud's local discovery finds them: edited and new files are
  uploaded, new folders created. Deletes are not taken from it: unregistering
  the drive removes every online-only file, and that must never delete them on
  the server. What is missing comes back from the server instead; the deletes
  made before the service stopped are in the journal.
- **New folders, empty ones too, are created on the server**
  (`operations/mkdir`) by the run after they are made, so Explorer's "New
  folder" renamed right away reaches the server under the name that stays. A
  folder moved in from outside the drive is reported alone, so what it
  holds is uploaded with it. Remotes without empty folders (rclone's
  `CanHaveEmptyDirectories`, false for S3 and other bucket stores) cannot hold
  an empty one: it stays here, and its absence on the server is not taken for
  a delete.
- **Free up space never drops the only copy of a change.** A file with local
  changes the server does not have yet is uploaded first and freed after, as
  OneDrive does; the platform's own refusal covers only what in-sync tells.
- **A placeholder's size is the platform's record, not the file's.** A save
  in place leaves the platform's record at the old size. A file it made
  bigger, once freed, downloads back only that much and zeros after it. The
  size is given again when a file is marked in sync after an upload and when
  it is freed, as Nextcloud does (`EditedPlaceholderContractTest`; the E2E
  scenarios C25, C29 and S25 fail without it).
- **"Always keep on this device" downloads through a handle without data
  access.** A write handle is refused while a program has the file open
  sharing reads only, and refuses such a program for a moment after the file
  already shows as downloaded (`HydratePlaceholderContractTest`).
- **Local changes are sent in one plan per sync run, as Nextcloud's sync runs
  do.** Nothing is sent the moment it is reported. Cloud Files'
  `NOTIFY_DELETE` / `NOTIFY_RENAME` and the file watcher only record what
  changed (`LocalChanges`), and a run starts 1 s after the last change
  (`SyncScheduler`); the timer's full runs, "sync now" and back online send
  them too. The run plans every item once, read from the drive as it is then
  (`LocalChangePlanner`), and carries the plan out in order: moves in the order
  they were made, deletes, new folders (parents first), then uploads, three at
  a time. Changes the server made are applied after the plan, in the same run;
  one run at a time, so nothing of one runs beside anything else. Deletes and
  renames are written to the store's journal (`PendingChanges`) the moment
  they are reported and leave it once they reached the server, so one made
  just before the service stops, or while offline, still reaches the server.
- **Deletes and renames must be acknowledged.** `NOTIFY_DELETE` and
  `NOTIFY_RENAME` arrive *before* the operation, and the platform holds it until
  the provider answers `ACK_DELETE` / `ACK_RENAME`; without that the user's
  delete or rename hangs. Both are always allowed, recorded for the next run
  just before. For placeholders these notifications are the source of truth:
  the file watcher reports a move between folders as delete + create, and its
  own delete and rename events for placeholders are ignored.
- **A delete is read from the drive when the run plans it.** It is reported
  before it happens, and it may not end in a delete:
  - A move out of the drive (Recycle Bin) is announced before Windows downloads
    the file to take it along. Deleting the server copy at once made that
    download fail and the move with it.
  - Replacing a file (an editor saving through a temp file, or delete then
    rename into place) reports a delete of the original.

  So the run deletes the server copy only if the path is empty. If a different
  file (another NTFS file id) is there, it was replaced: its upload updates the
  server copy, with no delete. If the same file is still there, the delete
  waits for a later run (the next download that completes starts one), and
  30 s after it was reported the operation counts as failed and the server copy
  is kept.
- **A folder deleted with what is inside is one delete,** whatever was
  reported inside it and in whichever order. The run deletes the files this
  client knows in it, then removes the server folder only if it is empty
  (`operations/rmdirs`), so files added on the server that this client never
  listed survive. A file renamed and then deleted is deleted under its old
  name, the one the server has.
- **Mock mode has only the file watcher, which reports a move between folders
  as delete + create.** A file appearing elsewhere with the size and exact
  write time the deleted one last had in sync (or the same file id) is the
  same file moved, and becomes one server move instead of a delete and an
  upload. A renamed item the server never had is sent under its new name.
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
- **Folders renamed** locally are moved on the server (`sync/move`), and every
  row and placeholder identity below it is re-keyed so files inside still open.
  A program can list such a folder for the first time before the move reaches
  the server (Explorer's navigation pane does, the moment it's renamed). The
  folder is then listed from the path the server still has, and its placeholder
  still names. Its entries go into the folder under its new name, and the move
  re-keys them with the rest. Such a listing and the move never overlap. A
  listing waits for a move that's under way, then lists the folder where it
  went. Answered with nothing, the folder would be marked fully populated and
  stay empty.
- **Downloads and moves don't overlap either.** A file renamed here, or in a
  folder renamed here, whose move hasn't reached the server yet, is read from
  the path its placeholder names, which the server still has. The move waits
  until that read has begun. A read that comes while the move is under way
  waits for it, then reads from where the move put the file. The move updates
  the placeholders only after it lets reads go. Marking a file in sync waits
  for a download of it that is still open, while a new identity doesn't
  (`RenamedFolderContractTest`).
- **A change to a file being downloaded waits for the download.** The drive
  connects with `CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH`, without which
  downloads come with no path and couldn't be told by their file. While a
  download runs, a change to that file (or a delete of its folder) is kept
  for later. When the download ends, sent or failed, the next run is asked
  for. What a download writes is no edit either way: it leaves the file in
  sync, which is what tells an edit.
- **A change reported again while it's being sent isn't lost.** It keeps its
  place, and the run that is sending the earlier one leaves it for the next
  run. An edit saved while the previous version is uploading is sent once that
  upload ends.
- **Changes and deletes are applied after the reply.** `TRANSFER_PLACEHOLDERS`
  can only add entries, so the same listing is compared against the store (which
  holds the server's last known size and time, also after uploads). The next
  run, after its local changes, deletes locally the files deleted on the server, and
  changed files get `CfUpdatePlaceholder(MARK_IN_SYNC | DEHYDRATE)` with the
  new size and time. A local edit is first kept as a conflict copy (see
  [Detecting conflicts](#detecting-conflicts)), so this leaves out
  `VERIFY_IN_SYNC`, which would make the platform refuse a file edited
  locally. Instead the update refuses a file whose size or write time changed
  since the client looked at it (see
  [Putting the server's version in place](#putting-the-servers-version-in-place)).
  A plain local file is
  converted to a placeholder first. Only a file whose own upload or rename is
  still on its way is left for the next listing, because this listing may
  predate it. Deletes that come from the server are not sent back to it.
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
- **On-demand population is enabled on the root and every new directory**
  until it is first listed. The root is registered without
  `CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT`, and directory
  placeholders are created without
  `CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION`. A folder made here
  is converted to a placeholder fully populated: all it holds is local.
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
    machine) before falling back to `CfRegisterSyncRoot` alone, with
    `CF_REGISTER_FLAG_UPDATE` retries: the drive then syncs, but has no
    Explorer entry until the next start. The log says which one happened.
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

## Detecting conflicts

The rules are Nextcloud's, and OneDrive keeps both versions the same way. The
user-facing table is in the [user guide](user-guide.md#conflicts). A conflict
is a file that has local changes not yet sent to the server, while the
server's copy is no longer the one this client last saw.

The conflict copy's name is built the way Nextcloud's
`Utility::makeConflictFileName` builds it: the tag goes before the extension,
or at the end of a name without one (`.hidden`, `Makefile`).

The client notices a conflict in two places:

- **When a folder is listed**, by the background sync or the first time it's
  opened. The listing is compared with what the client last saw for each file
  (see [What tells a change](#what-tells-a-change)).
- **Just before an upload or a delete.** It looks up that one file on the
  server (`operations/stat`), and sends the change only if the server still
  has the version the client last saw. This covers an edit saved while the
  server copy changed and no listing happened in between.

### Putting the server's version in place

What the local file holds goes only if it is still what the client decided
about: the server's content, or a version kept as a conflict copy. A save made
in between must not be thrown away, so the client notes the file's size and
write time when it looks at it, and the step that replaces the file checks
them again:

- **Cloud mode:** the check and `CfUpdatePlaceholder` run through one handle
  from `CfOpenFileWithOplock(EXCLUSIVE)`, which shares nothing. The platform
  gives none while another program has the file open (then the file is "in
  use" and a later run tries again). While it's held, another program's open
  waits for it, so nothing can write to the file between the check and the
  update (`TheExclusiveHandle_IsNotGivenWhileTheFileIsOpen_AndKeepsOthersOut`).
  The plain write handle shares everything and is given even while the file
  is open elsewhere. The update's own USN check (`UpdateUsn`) can't serve: the
  platform refuses even the USN read just before the call, and Explorer
  states written on the file change it too.
- **Mock mode:** the file is opened sharing nothing, checked, then written.
- A file that changed meanwhile is left as it is and recorded as a local
  change again. The check before its upload then finds the server's newer
  version and keeps this one as a conflict copy too.
- When the server's version can't be put in place after the copy was made
  (the file was in use), the next try uses that copy again: a copy with the
  file's current size and write time (`ConflictCopies.KeptCopyOf`).
- When both sides hold the same content, what is recorded as in sync is the
  size and write time from before the content was compared, so a save made
  meanwhile still counts as an edit.

Known limits:

- On a remote without content hashes, a server change is recognised by size
  and modification time (a difference of 1 s or more), so a change that keeps
  both isn't seen there.
- The check before an upload and the upload are two separate calls. A server
  change that lands between them is overwritten by the upload.
- A file the client is still uploading, or has just renamed, is skipped by a
  listing that may predate that change. The check before the upload covers it.
- A file renamed or deleted here while it's being uploaded isn't a failed
  upload. That change was recorded too, and the next run sends it.

## What tells a change

The client works with any rclone remote, and remotes differ in what they can
tell about a file. So it asks once at start (`operations/fsinfo`, logged in one
line) and uses the most each one offers, the way rclone itself decides
(`RemoteCapabilities`, `FileVersions`):

- **The size first**, then **the remote's own content hash** where both sides
  have one, else **the time**. A server that writes the same content again (a
  re-upload, a copy) has not changed the file; an edit that keeps size and time
  has. The hash is the remote's own type (S3, Azure, Google Cloud Storage and
  Swift keep MD5; B2 and Box SHA-1; OneDrive QuickXorHash; Dropbox its own;
  Google Drive several, of which SHA-1 is used), read with the listing at no
  extra cost, and kept in the store with its type (`md5:…`). Remotes that keep
  none (plain WebDAV, FTP, SMB, HTTP, Mega, encrypted remotes) are told by size
  and time; so are a local disk and SFTP, where rclone would read every file to
  hash it. A file the remote keeps no hash for (on S3, one uploaded in parts by
  another tool) is told by its time.
- **Times to within the remote's precision**, at least a second. A remote that
  can't set file times (plain WebDAV) lists its own upload times, so a local
  and a server time never say "the same file" there.
- **Listings ask for no MIME types**, and on a remote where each file's time
  costs a request of its own (S3: one HEAD per file, measured), **a folder
  listed before is listed without times**. Unchanged files are told by their
  hash, and only new or changed files, or ones without a hash, are looked up
  (or, past eight, the folder is listed again with times). A sync run of
  unchanged folders is then one request per folder.
- **A local file is hashed only in a conflict**, with rclone
  (`operations/hashsumfile`, which makes any type, a remote's own included),
  and only when it's fully on disk. Local edits are told by size and time, as
  in Nextcloud.

rclone has no folder ETags or change feed through its API, so every listed
folder is still listed each run. IDs that would show a server-side rename as a
move exist on only some remotes, so they aren't used either.

## Expected behaviour, not bugs

- Files that already existed locally before the root was registered (for
  example, downloaded files left behind by a previous uninstall) stay normal
  local files. cfapi placeholders can't be created over an existing file, and
  CloudMirror behaves the same way. Edits to them are still uploaded by the
  watcher.
- Files added, changed or deleted on the remote show up in folders already
  opened within 30 s (the next background sync run), as with Nextcloud - but
  only for folders that hold something local, which the sync lists every run.
  A purely online-only folder picks up server changes when it is next opened
  after the relist age (the poll interval; `OPENCFDRIVE_RELIST_SECONDS`
  overrides it): a sync run then marks it, and its next opening, F5 included,
  lists it. Before that, F5 doesn't ask the server sooner. Requests from
  Windows Search for a folder listed before are answered from disk, so the
  indexer never brings the per-folder listings back. Folders never opened are
  listed when they are, and are empty while the server can't be reached.
- The background sync lists every folder that was ever opened, one at a time,
  every 30 s - but only while the folder holds something local (a downloaded,
  kept or edited file, a pinned folder, or the root). A folder holding only
  online-only placeholders is not re-listed; it is marked for on-demand
  population once the relist age passes, and listed from the server when
  opened. Which folders hold something local is worked out once per run, in
  one pass over the store with one attribute lookup per file (no handle
  opened). On remotes that count requests (cloud APIs with quotas), many
  opened folders no longer mean many listings every 30 s.
- Offline, a file that was never downloaded can't be opened; only downloaded
  files (and ones kept with "Always keep on this device") are readable. Edits,
  new folders, deletes and renames made offline are sent once the server
  answers again.
- A conflict leaves a conflict copy next to the file ("name (conflicted copy
  date time).ext"), on the server too. A file open in another program is
  updated on a later run, which uses the copy already kept of that version
  (same size and write time) instead of making another.
- On S3 and other bucket stores, an empty folder exists only here until a file
  is put in it, because those stores have no folders of their own.
- A program that opens a file in the few milliseconds while the client works
  on it (makes it a placeholder, frees up its space) can get "being used by
  another process". Opening it again works. While a server change is put in
  place, an open waits for those milliseconds instead (mock mode: it is
  refused).

## Why .NET and Explorer sometimes disagree on placeholder state

Windows has a per-process filter (`RtlSetProcessPlaceholderCompatibilityMode`)
that hides placeholder bits (`ReparsePoint`, `Offline`, `Sparse`) from
applications that aren't in the Windows system directories. The three values
are `PHCM_APPLICATION_DEFAULT` (0), `PHCM_DISGUISE_PLACEHOLDER` (1), and
`PHCM_EXPOSE_PLACEHOLDERS` (2). A .NET process outside `C:\Windows` often gets
disguised attributes (`0x20` Archive), while PowerShell 5.1
(`C:\Windows\System32`) or `fsutil` see the real `0x420` (Archive +
ReparsePoint) with tag `0x9000601A`.

This does *not* mean the placeholder is broken. `QueryState` uses the native
`GetFileAttributesExW` and `GetFileInformationByHandleEx` APIs (the same path
the service uses), and Explorer reads the identity independently through
`cfapi.dll`.

Overlay icons, such as OneDrive's `FileSyncShell64.dll` and Google's
`drivefsext.dll`, come from a separate mechanism (`ShellIconOverlayIdentifiers`)
and work whatever the attribute mask. OpenCfDrive has no overlay DLL
registered, so Explorer shows its files with default icons and no cloud badge.
Their state shows in Explorer's Status column.
