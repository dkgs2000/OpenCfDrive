# Development

How to build, run and package OpenCfDrive from source. Tests have their own
page, [Testing](testing.md), and so does the design:
[Architecture](architecture.md).

- [Prerequisites](#prerequisites)
- [Build and test](#build-and-test)
- [Run from source](#run-from-source)
- [Configuration](#configuration)
- [How the service runs rclone](#how-the-service-runs-rclone)
- [State and the status channel](#state-and-the-status-channel)
- [Project layout](#project-layout)
- [Checking a running drive](#checking-a-running-drive)
- [Installers](#installers)
- [The updater](#the-updater)
- [CI and releases](#ci-and-releases)
- [Third-party software](#third-party-software)

## Prerequisites

- Windows 10 version 2004 (build 19041) or later. The projects target
  `net10.0-windows10.0.19041.0` for the Windows storage provider APIs.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- [rclone](https://rclone.org), on `PATH`. You can also drop `rclone.exe` into
  the repository root, where git ignores it. The tests use rclone as their
  server too.

None of this needs an elevated shell. Run everything as the user who owns the
drive, because the drive's registration is per user.

## Build and test

```powershell
dotnet build .\OpenCfDrive.slnx
dotnet test .\OpenCfDrive.slnx
```

`dotnet test` runs the unit and contract tests. The end-to-end suite is a set
of PowerShell scripts, described in [Testing](testing.md).

## Run from source

```powershell
dotnet run --project .\OpenCfDrive.App       # the app, which starts the service
dotnet run --project .\OpenCfDrive.Service   # or the service on its own
```

A build of the app finds the service in the service project's output for the
same configuration. Installed, the two sit side by side. A build never adds
itself to the sign-in start; only the installed app does, on its first start.

The service registers the drive folder as a Cloud Files sync root, shown in
Explorer as **OpenCfDrive**, and fills each folder from the rclone remote when
it's opened. Without the Cloud Files API it logs why and syncs nothing. It never
falls back to downloading the remote into a plain folder.

The service is a Windows app without a console window. Its log goes to the
console it was started from, or to redirected output, or, with neither (as
from the Start Menu), to `%LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.log`.
One copy runs per drive.

Command-line options, used by the installer and the tests:

| Program | Option | What it does |
| --- | --- | --- |
| `OpenCfDrive.Service.exe` | `--stop` | Ends a running copy of the same exe. |
| `OpenCfDrive.Service.exe` | `--unregister` | Unregisters the drive, leaving a normal folder. |
| `OpenCfDrive.exe` | `--background` | Starts the app without showing its window. |
| `OpenCfDrive.exe` | `--quit` | Closes a running copy of the app. |
| `OpenCfDrive.exe` | `--uninstall` | `--quit`, then removes the sign-in start and the downloaded rclone. |

## Configuration

The app saves its settings in `%LOCALAPPDATA%\OpenCfDrive\settings.json`, and
the service reads them when it starts. An environment variable, when set, wins
over the file. Scripts and tests rely on that, and the app shows which ones are
set.

| Setting | `settings.json` | Environment variable | Default |
| --- | --- | --- | --- |
| Drive folder | `SyncRoot` | `OPENCFDRIVE_ROOT` | `%USERPROFILE%\OpenCfDrive` |
| rclone remote | `Remote` | `OPENCFDRIVE_REMOTE` | none (the service then uses `e2e:`, the name the tests give their remote) |
| OpenCfDrive runs rclone | `ManageRclone` | | yes; setting `OPENCFDRIVE_RC_URL` means no |
| rclone program | `RclonePath` | `OPENCFDRIVE_RCLONE` | on `PATH`, else the downloaded copy |
| rclone config file | `RcloneConfig` | | rclone's own default |
| Remote control you run | `RcUrl` | `OPENCFDRIVE_RC_URL` | `http://127.0.0.1:5572` |
| Its login | `RcUser`, `RcPasswordProtected` | `OPENCFDRIVE_RC_USER`, `OPENCFDRIVE_RC_PASS` | none |
| Start at sign-in | `StartAtSignIn` | | on for the installed app |
| Check for updates | `CheckForUpdates` | | on |
| Background sync interval | | `OPENCFDRIVE_REMOTE_POLL_SECONDS` | 30 s |
| Age at which a folder holding nothing local is marked to be listed on its next opening | | `OPENCFDRIVE_RELIST_SECONDS` | the sync interval |

`OPENCFDRIVE_SETTINGS` points both the app and the service at another
settings file.

None of these variables is `RCLONE_...`, on purpose: rclone reads every
`RCLONE_<NAME>` variable as its `--<name>` flag, and the rclone the service runs
inherits them. `RCLONE_FS` and `RCLONE_RC_URL`, the names the remote and the
remote control address had before, are no longer read; the service logs a
warning when one is set.

## How the service runs rclone

With **OpenCfDrive runs it** (the default), the service starts
`rclone rcd --rc-serve` itself:

- It listens only on `127.0.0.1`, on a free port.
- It needs a login made up for each run. The service passes it through the
  environment, so it never shows up in a process list.
- It runs in a Windows job object with the service, so Windows ends rclone
  whenever the service ends, even if the service is killed.
- If rclone stops on its own, the service starts it again, after 1 s, then 5,
  15 and 60 s, on the same port and with the same login.
- Its log is `%LOCALAPPDATA%\OpenCfDrive\logs\rclone.log`.

With **I run rclone rcd myself**, the service uses the remote control at
`RcUrl` with the login it was started with (`--rc-user`, `--rc-pass`). The app
saves the password encrypted for the Windows user (DPAPI), so the settings
file never holds it in plain text. A login in the environment comes whole from
there: user and password together. Never run it with `--rc-no-auth`: that lets
any program on the PC, and possibly a web page, use every remote in its config
and read the config's secrets. The end-to-end tests run their rclone with a
login too.

## State and the status channel

Engine state (the SQLite metadata) lives outside the sync root:

```
%LOCALAPPDATA%\OpenCfDrive\state\opencfdrive-<hash>.db
```

The hash comes from the full sync-root path, so each sync root has its own
database. State is never written inside the sync root, because that folder is
user data and is watched for uploads. If `%LOCALAPPDATA%` isn't writable, the
service falls back to `%TEMP%\OpenCfDrive\state`.

The app asks the running service for its status through a named pipe,
`opencfdrive-<hash>` (the same hash as the database), which only the same user
can open. A request is one line, such as `status`, `stop` or `sync`, and the
answer is one line of JSON.

## Project layout

| Project / folder | Purpose |
| --- | --- |
| `OpenCfDrive.Core` | Cloud Files interop, rclone clients, the sync engine, SQLite metadata, settings, the status channel and the updater |
| `OpenCfDrive.App` | The app (`OpenCfDrive.exe`): settings and status behind a notification area icon. It starts and stops the service. |
| `OpenCfDrive.Service` | The background service that registers and serves the sync root |
| `OpenCfDrive.MockService` | Test host: the same engine in mock mode, over a plain folder |
| `OpenCfDrive.Tests` | Unit and contract tests: interop layout, Cloud Files behaviour, SQLite, the rclone API |
| `scripts\tests` | The end-to-end tests (see [Testing](testing.md)) |
| `scripts\assets` | `Build-Icon.ps1`, which rebuilds the icons from `logo.svg` |
| `assets` | `logo.svg` (the source), `opencfdrive.ico` (the app's and service's icon, which Explorer also shows for the drive), and `logo-64.png` (the `.exe` installer's logo) |
| `installer` | `Build-Installer.ps1`, the WiX projects for the MSI (`msi`) and the `.exe` bootstrapper (`bundle`), and the third-party notices installed with the app |
| `.github` | The Test and Release workflows and their shared setup step |

## Checking a running drive

Once the service logs `Mode: cloud | ... registered=True | connected=True`,
Explorer should list the drive folder instantly. Online-only files show the
cloud icon, and opening one downloads it (the service log shows a `FETCH_DATA`
line). A quick check:

```powershell
$sw = [Diagnostics.Stopwatch]::StartNew()
Get-ChildItem "$env:USERPROFILE\OpenCfDrive" -Force | Format-Table Name, Attributes
$sw.ElapsedMilliseconds   # must be milliseconds, never ~60000
```

To remove a drive run from source, stop the service and run it once with
`--unregister`. That removes its Explorer entry and any online-only
placeholders and leaves a normal folder; the folder itself, which is user
data, is never deleted. Sign out or restart Explorer afterwards, so the drive
disappears from the navigation pane.

A sync root registered by an older build is upgraded in place the next time the
service starts: registering an existing id updates its policies without
touching data.

## Installers

```powershell
.\installer\Build-Installer.ps1 -Version 1.2.3                          # both installers, into .\artifacts
.\installer\Build-Installer.ps1 -Version 1.2.3 -Repository owner/name   # ...that update from that repository's releases
```

The script publishes the app and the service self-contained, each as a single
compressed file, then packages them with the WiX Toolset v5 (MS-RL license),
which the build restores from NuGet. It produces:

- `OpenCfDrive-<version>-x64.msi`, the Windows Installer package;
- `OpenCfDrive-<version>-x64-setup.exe`, a bootstrapper that installs the MSI.

Both install per user into `%LOCALAPPDATA%\Programs\OpenCfDrive`, with no
administrator rights. On uninstall, the MSI closes the app, stops the service,
unregisters the drive, and runs the app's `--uninstall`. On an upgrade it only
closes the app and stops the service, so the registration stays.

`assets\logo.svg` is the source of the icons. After changing it, run
`.\scripts\assets\Build-Icon.ps1`, which renders it with Microsoft Edge.

## The updater

The installed app updates itself from the GitHub releases of the repository it
was built from, the way Nextcloud's desktop client does on Windows
(`src/gui/updater/ocupdater.cpp`).

- It asks GitHub for the latest release (not drafts or pre-releases) 3 s after
  it starts and then every 10 hours, unless `CheckForUpdates` is off. **Check
  for updates** on the About page asks at once either way.
- A newer version's setup (`OpenCfDrive-<version>-x64-setup.exe`) downloads in
  the background into `%LOCALAPPDATA%\OpenCfDrive\updates` and must match the
  size and SHA-256 that GitHub lists for it. The updater uses the setup rather
  than the MSI because it upgrades an install made with either. The MSI run over
  a setup's install would leave the old version listed in Apps.
- **Restart and update** stops syncing, runs the setup with `/passive` (a
  progress bar and no questions) from a PowerShell that opens the app again
  afterwards, and closes the app. The new app starts syncing again.
- On the next start the app tells whether the update installed. If it did, the
  installer is removed. If not, it's offered again with the installer's log
  (`updates\install.log`).

Only the installed copy updates itself, and only a build made with
`Build-Installer.ps1 -Repository owner/name` knows where its releases are. The
release workflow passes its own repository. Other builds show why they don't
update.

## CI and releases

Two GitHub Actions workflows run on Windows runners:

- **Test** (`.github/workflows/test.yml`) runs on every push to `main`, every
  pull request, and by hand. It runs the unit and contract tests, and the E2E
  suite in all four combinations (cloud and mock mode, WebDAV and S3), with the
  servers as local processes. Each job keeps its logs as an artifact. Every
  push to `main` is tested to the end, even when another push follows it, so
  each change there has a run of its own. On a pull request, a new push
  cancels the run for the one before.
- **Release** (`.github/workflows/release.yml`) runs when a version tag is
  pushed, or by hand with a version. It runs every test first, then builds both
  64-bit installers, set to update from this repository's releases, and
  publishes a GitHub release with them attached.

`.github/actions/setup` installs the .NET SDK and a pinned rclone release for
both.

Dependabot (`.github/dependabot.yml`) checks the NuGet packages and the
actions every week, and proposes each kind's updates in one pull request, once
a release is a week old. WiX and the rclone that CI tests against are updated
by hand.

To make a release, push a version tag. Installed copies find the new release
within 10 hours.

```powershell
git tag v1.2.3
git push origin v1.2.3
```

## Third-party software

This repository holds only OpenCfDrive's own code. What it works with is
installed or downloaded separately and keeps its own license:

- **rclone** ([MIT license](https://github.com/rclone/rclone/blob/master/COPYING)).
  OpenCfDrive runs it as a separate program and talks to it over its HTTP
  Remote Control API. No rclone code is included. OpenCfDrive runs a copy
  that's already installed, or downloads the official release from rclone.org
  when asked. The tests also run it as their WebDAV or S3 server.
- **NuGet packages** listed in the project files (`Microsoft.Data.Sqlite`,
  `SQLitePCLRaw`, `Microsoft.Extensions.Hosting`, and the xunit test packages),
  restored at build time.

Anyone who ships a build together with these must include their license
notices. The installers do: the app is self-contained (the .NET runtime and
these packages are inside it), and `installer\THIRD-PARTY-NOTICES.txt` is
installed next to it. The installers don't include rclone. A package that
bundled `rclone.exe` would also need rclone's `COPYING` file.
