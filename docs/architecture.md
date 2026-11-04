# Architecture

How OpenCfDrive turns an rclone remote into a cloud drive, and the rules the
code relies on. Many of them come from bugs, so read this before changing the
sync engine.

- [Overview](#overview)
- [The namespace model](#the-namespace-model)
- [Invariants](#invariants)
- [Detecting conflicts](#detecting-conflicts)
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
- A folder once listed stays fully populated, and the background sync keeps it
  current: every 30 s, right after the server is back, and on request.
- Local changes are picked up by the Cloud Files notifications and a file
  watcher, and sent to the server.
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
  first, never recursively, creates placeholders (`CfCreatePlaceholders`) for
  what the server added, and applies its changes and deletes. Folders never
  opened are not listed until they are. The client never relies on server
  notifications.
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
  Nextcloud, and nothing made here is lost: edits and new folders wait and are
  sent once the server answers, and deletes and renames are kept in the store
  (`PendingChanges`, so they survive a restart, like Nextcloud's journal), in
  the order made, with the items they change left alone by listings meanwhile.
  The server is checked again when Windows reports a network change, when the
  drive is used, and on Nextcloud's schedule: 62 s after going offline, then
  one more 62 s per failed check, at most 8 × 62 s. The app shows "Offline".
- **Changes made while the service was not running are sent when it starts**,
  as Nextcloud's local discovery finds them: edited and new files are
  uploaded, new folders created. Deletes are not taken from it: unregistering
  the drive removes every online-only file, and that must never delete them on
  the server. What is missing comes back from the server instead.
- **New folders, empty ones too, are created on the server**
  (`operations/mkdir`), held for a moment like an upload so that Explorer's
  "New folder" renamed right away reaches the server under the name that
  stays. A folder moved in from outside the drive is reported alone, so what it
  holds is uploaded with it. Remotes without empty folders (rclone's
  `CanHaveEmptyDirectories`, false for S3 and other bucket stores) cannot hold
  an empty one: it stays here, and its absence on the server is not taken for
  a delete.
- **Free up space never drops the only copy of a change.** A file with local
  changes the server does not have yet is uploaded first and freed after, as
  OneDrive does; the platform's own refusal covers only what in-sync tells.
- **A placeholder's size is the platform's record, not the file's.** A save
  that changes a file's size in place leaves the platform's record at the old
  size, and a file freed after that downloads back only that much (the rest
  reads as zeros). The size is given again when a file is marked in sync after
  an upload and when it is freed, as Nextcloud does
  (`EditedPlaceholderContractTest`).
- **"Always keep on this device" downloads through a handle without data
  access.** A write handle is refused while a program has the file open
  sharing reads only, and refuses such a program for a moment after the file
  already shows as downloaded (`HydratePlaceholderContractTest`).
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
  new size and time. A local edit is first kept as a conflict copy (see
  [Detecting conflicts](#detecting-conflicts)), so this leaves out
  `VERIFY_IN_SYNC`, which would make the platform refuse a file edited
  locally. A plain local file is converted to a placeholder first. Only a file
  whose own upload or rename is still on its way is left for the next listing,
  because this listing may predate it. Deletes that come from the server are
  not sent back to it.
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
  opened. The listing is compared with the size and time the client last saw
  for each file.
- **Just before an upload or a delete.** It looks up that one file on the
  server (`operations/stat`), and sends the change only if the server still
  has the version the client last saw. This covers an edit saved while the
  server copy changed and no listing happened in between.

Known limits:

- A server change is recognised by size and modification time (a difference of
  1 s or more), not by content. A change that keeps both isn't seen.
- The check before an upload and the upload are two separate calls. A server
  change that lands between them is overwritten by the upload.
- A file the client is still uploading, or has just renamed, is skipped by a
  listing that may predate that change. The check before the upload covers it.

## Expected behaviour, not bugs

- Files that already existed locally before the root was registered (for
  example, downloaded files left behind by a previous uninstall) stay normal
  local files. cfapi placeholders can't be created over an existing file, and
  CloudMirror behaves the same way. Edits to them are still uploaded by the
  watcher.
- Files added, changed or deleted on the remote show up in folders already
  opened within 30 s (the next background sync run), as with Nextcloud.
  Refreshing Explorer (F5) doesn't ask the server sooner. Folders never opened
  are listed when they are, and are empty while the server can't be reached.
- The background sync lists every folder that was ever opened, one at a time,
  every 30 s. On remotes that count requests (cloud APIs with quotas), many
  opened folders mean many listings.
- Offline, a file that was never downloaded can't be opened; only downloaded
  files (and ones kept with "Always keep on this device") are readable. Edits,
  new folders, deletes and renames made offline are sent once the server
  answers again.
- A conflict leaves a conflict copy next to the file ("name (conflicted copy
  date time).ext"), on the server too. A file open in another program is
  updated on a later run.
- On S3 and other bucket stores, an empty folder exists only here until a file
  is put in it, because those stores have no folders of their own.
- A program that opens a file in the few milliseconds while the client applies
  a server change to it can get "being used by another process". Opening it
  again works.

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
