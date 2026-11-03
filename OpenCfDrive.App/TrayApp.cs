using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>
/// The icon in the notification area (behind the arrow next to the clock,
/// unless the user pins it to the taskbar), its menu, and the window it opens.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan VisibleRefresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HiddenRefresh = TimeSpan.FromSeconds(15);

    private readonly SingleInstance _instance;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _headline;
    private readonly ToolStripMenuItem _openFolder;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly ToolStripMenuItem _installUpdate;
    private readonly ToolStripMenuItem _exit;
    private readonly System.Windows.Forms.Timer _timer = new();
    private MainForm? _window;
    private Action? _balloonClicked;
    private UpdateStage _updateStage;
    private bool _busy;
    private bool _exiting;

    public TrayApp(SingleInstance instance, bool showWindow)
    {
        _instance = instance;

        _headline = new ToolStripMenuItem("OpenCfDrive") { Enabled = false };
        _openFolder = new ToolStripMenuItem("Open the OpenCfDrive folder", null, (_, _) => OpenFolder());
        var openWindow = new ToolStripMenuItem("Status and settings", null, (_, _) => ShowWindow())
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        _start = new ToolStripMenuItem("Start syncing", null, async (_, _) => await StartAsync());
        _stop = new ToolStripMenuItem("Stop syncing", null, async (_, _) => await StopAsync());
        _restart = new ToolStripMenuItem("Restart syncing", null, async (_, _) => await RestartAsync());
        var openLog = new ToolStripMenuItem("View the log", null, (_, _) => OpenLog());
        _installUpdate = new ToolStripMenuItem("Restart and update", null, async (_, _) => await InstallUpdateAsync()) { Visible = false };
        _exit = new ToolStripMenuItem("Exit", null, (_, _) => Exit());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _headline, new ToolStripSeparator(),
            _openFolder, openWindow, new ToolStripSeparator(),
            _start, _stop, _restart, new ToolStripSeparator(),
            openLog, _installUpdate, _exit,
        ]);
        _menu.Opening += (_, _) => UpdateMenu();

        // Creating the menu installed the UI thread's synchronization context.
        _ui = SynchronizationContext.Current!;

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Small,
            Text = "OpenCfDrive",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };
        _tray.BalloonTipClicked += (_, _) => (_balloonClicked ?? (() => ShowWindow(settings: !(Monitor.Latest?.Settings.HasRemote ?? false))))();

        _instance.OnSignal(_instance.ShowRequested, () => _ui.Post(_ => ShowWindow(), null));
        _instance.OnSignal(_instance.QuitRequested, () => _ui.Post(_ => Exit(), null));

        // Only the copy the installer put in place updates itself: the MSI
        // installs there, not over a build someone runs from elsewhere.
        Updates = new Updater(
            AppVersion.Current,
            AppVersion.UpdateRepository,
            Updater.DefaultFolder,
            automatic: () => DriveSettingsFile.Load().CheckForUpdates != false,
            unavailable: SignInStartup.IsInstalledCopy
                ? null
                : $"Only the installed copy updates itself; this one runs from {Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}.");
        _updateStage = Updates.Status.Stage;
        Updates.Changed += status => _ui.Post(_ => OnUpdateChanged(status), null);

        Monitor.Updated += OnUpdated;
        _timer.Interval = (int)HiddenRefresh.TotalMilliseconds;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ui.Post(async _ => await StartupAsync(showWindow), null);
    }

    public DriveMonitor Monitor { get; } = new();

    public Updater Updates { get; }

    public bool Busy => _busy;

    private async Task StartupAsync(bool showWindow)
    {
        ApplyDefaultSignInStartup();
        await RefreshAsync(probeRclone: true);

        var latest = Monitor.Latest!;
        if (latest.SettingsProblem is null && latest.Settings.HasRemote && !latest.Running)
        {
            await StartAsync();
        }

        if (showWindow)
        {
            ShowWindow(settings: !latest.Settings.HasRemote);
        }
        else if (!latest.Settings.HasRemote)
        {
            ShowBalloon("OpenCfDrive is not set up", "Click here to choose your rclone remote.", ToolTipIcon.Info);
        }

        // After the drive: what an earlier update left, then the checks.
        Updates.Start();
    }

    /// <summary>
    /// The installed app starts at sign-in unless the user turned that off.
    /// Decided once, on its first start; a build someone runs from the
    /// repository never adds itself.
    /// </summary>
    private static void ApplyDefaultSignInStartup()
    {
        try
        {
            var saved = DriveSettingsFile.Load();
            if (saved.StartAtSignIn is null && SignInStartup.IsInstalledCopy)
            {
                SignInStartup.Enable();
                DriveSettingsFile.Save(saved with { StartAtSignIn = true });
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The window shows the settings problem.
        }
    }

    public async Task RefreshAsync(bool probeRclone = false)
    {
        var visible = _window is { Visible: true };
        _timer.Interval = (int)(visible ? VisibleRefresh : HiddenRefresh).TotalMilliseconds;
        try
        {
            await Monitor.RefreshAsync(
                rcloneMaxAge: probeRclone ? TimeSpan.Zero : visible ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1),
                remoteMaxAge: probeRclone ? TimeSpan.Zero : TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Refresh failed: {ex}");
        }
    }

    private void OnUpdated(DriveSnapshot snapshot)
    {
        var (_, headline, _) = snapshot.Summary;
        var text = $"OpenCfDrive: {headline}";
        _tray.Text = text.Length <= 127 ? text : text[..127];
        _window?.ShowSnapshot(snapshot);
    }

    private void UpdateMenu()
    {
        var latest = Monitor.Latest;
        var (_, headline, _) = latest?.Summary ?? (Health.Idle, "Checking...", "");
        _headline.Text = latest?.Settings.HasRemote == true ? $"{headline}: {latest.Settings.Remote.Value}" : headline;
        _openFolder.Enabled = latest is not null && Directory.Exists(latest.Settings.SyncRoot.Value);

        var running = latest?.Running ?? false;
        _start.Enabled = !_busy && latest is { Running: false, SettingsProblem: null } && latest.Settings.HasRemote;
        _stop.Enabled = !_busy && running;
        _restart.Enabled = !_busy && running;
        var update = Updates.Status;
        _installUpdate.Visible = update.Stage == UpdateStage.Ready;
        _installUpdate.Text = update.Available is { } version ? $"Restart and update to {GitHubReleases.Format(version)}" : "Restart and update";
        _installUpdate.Enabled = !_busy;
        _exit.Text = running ? "Exit (syncing continues)" : "Exit";
    }

    private void OnUpdateChanged(UpdateStatus status)
    {
        if (_exiting)
        {
            return;
        }

        _window?.ShowUpdate(status);

        // Said once when a download becomes ready, and again at each start
        // while it waits (as Nextcloud does).
        if (status.Stage == UpdateStage.Ready && _updateStage != UpdateStage.Ready && status.Available is { } version)
        {
            ShowBalloon(
                $"OpenCfDrive {GitHubReleases.Format(version)} is ready",
                "Click here to restart OpenCfDrive and install it.",
                ToolTipIcon.Info,
                async () => await InstallUpdateAsync(ask: true));
        }

        // Checking briefly leaves Ready during a check that finds the same download.
        if (status.Stage is not UpdateStage.Checking)
        {
            _updateStage = status.Stage;
        }
    }

    /// <summary>
    /// Stops syncing, so nothing the installer replaces is in use, starts the
    /// downloaded installer and exits; the installer opens the app again,
    /// which starts syncing.
    /// </summary>
    public async Task InstallUpdateAsync(bool ask = false)
    {
        var status = Updates.Status;
        if (status.Stage != UpdateStage.Ready || status.Available is not { } version)
        {
            return;
        }

        if (ask && MessageBox.Show(
                $"Install OpenCfDrive {GitHubReleases.Format(version)} now?\n\nOpenCfDrive closes, stops syncing for the moment " +
                "the installer takes, and opens again.",
                "Update OpenCfDrive",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        if (Monitor.Latest is { Running: true } latest &&
            await RunAsync("Stopping syncing for the update", () => SyncService.StopAsync(latest.Settings.SyncRoot.Value)) is not null)
        {
            return;
        }

        if (Updates.BeginInstall() is not { } installer)
        {
            ShowProblem("The downloaded update is gone; check for updates again.");
            return;
        }

        try
        {
            Process.Start(Updater.InstallCommand(installer, Updates.InstallLog, Environment.ProcessPath!))?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowProblem($"The installer could not be started: {ex.Message}");
            return;
        }

        Exit();
    }

    public void ShowWindow(bool settings = false)
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainForm(this);
            _window.VisibleChanged += async (_, _) => await RefreshAsync(probeRclone: _window.Visible);
        }

        if (Monitor.Latest is { } latest)
        {
            _window.ShowSnapshot(latest);
        }

        _window.ShowUpdate(Updates.Status);
        if (settings)
        {
            _window.ShowSettingsTab();
        }

        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized)
        {
            _window.WindowState = FormWindowState.Normal;
        }

        _window.Activate();
    }

    public async Task StartAsync()
    {
        var latest = Monitor.Latest;
        if (latest is null)
        {
            return;
        }

        await RunAsync("Starting", () => SyncService.StartAsync(latest.Settings.SyncRoot.Value));
    }

    public async Task StopAsync()
    {
        var latest = Monitor.Latest;
        if (latest is null)
        {
            return;
        }

        await RunAsync("Stopping", () => SyncService.StopAsync(latest.Settings.SyncRoot.Value));
    }

    public async Task RestartAsync()
    {
        var latest = Monitor.Latest;
        if (latest is null)
        {
            return;
        }

        var root = latest.Settings.SyncRoot.Value;
        await RunAsync("Restarting", async () => await SyncService.StopAsync(root) ?? await SyncService.StartAsync(root));
    }

    /// <summary>
    /// Saves new settings and carries them out: a moved drive is unregistered
    /// from its old folder (left as an ordinary folder), and a running service
    /// restarts with them. Returns why it could not, or null.
    /// </summary>
    public async Task<string?> ApplySettingsAsync(DriveSettings settings, DriveChange change)
    {
        var before = Monitor.Latest!.Settings;
        var after = DriveSettingsFile.Resolve(settings);
        var syncChanged =
            !string.Equals(before.SyncRoot.Value, after.SyncRoot.Value, StringComparison.OrdinalIgnoreCase) ||
            before.Remote.Value != after.Remote.Value ||
            before.ManageRclone != after.ManageRclone ||
            (after.ManageRclone
                ? before.RclonePath != after.RclonePath || before.RcloneConfig != after.RcloneConfig
                : before.RcUrl.Value != after.RcUrl.Value);

        return await RunAsync("Applying the settings", async () =>
        {
            if (syncChanged && await SyncService.StopAsync(before.SyncRoot.Value) is { } notStopped)
            {
                return notStopped;
            }

            // First, while nothing else has changed yet: the state of a folder
            // that held another remote's drive no longer applies.
            if (change.DiscardOldState)
            {
                SqliteMetadataStore.Delete(SyncStatePaths.DatabasePath(after.SyncRoot.Value));
            }

            if (change.OldSyncRootToUnregister is { } oldRoot && SyncRootRegistrar.IsSyncRoot(oldRoot))
            {
                var result = await Task.Run(() => new SyncRootRegistrar().Unregister(oldRoot));
                if (!result.Success)
                {
                    return $"The old drive folder could not be unregistered: {result.Message}";
                }
            }

            DriveSettingsFile.Save(settings);
            if (settings.StartAtSignIn == true)
            {
                SignInStartup.Enable();
            }
            else
            {
                SignInStartup.Disable();
            }

            return syncChanged && after.HasRemote
                ? await SyncService.StartAsync(after.SyncRoot.Value)
                : null;
        }, report: false);
    }

    /// <summary>
    /// Downloads rclone from rclone.org into OpenCfDrive's own folder (it is
    /// used when none was chosen and none is on PATH), then lets a service
    /// that was missing it start again. Returns why it failed, or null.
    /// </summary>
    public async Task<string?> DownloadRcloneAsync(IProgress<string> progress)
    {
        return await RunAsync("Downloading rclone", async () =>
        {
            try
            {
                await RcloneDownloader.DownloadAsync(RcloneLocator.DownloadFolder, progress);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException
                                           or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return $"rclone could not be downloaded: {ex.Message}";
            }

            var latest = Monitor.Latest;
            if (latest is null || !latest.Settings.HasRemote || !latest.Settings.ManageRclone)
            {
                return null;
            }

            var root = latest.Settings.SyncRoot.Value;
            return latest.Status?.Rclone?.State is "NotFound" or "Failed"
                ? await SyncService.StopAsync(root) ?? await SyncService.StartAsync(root)
                : latest.Running ? null : await SyncService.StartAsync(root);
        }, report: false);
    }

    /// <summary>The log of the rclone the service runs.</summary>
    public void OpenRcloneLog()
    {
        var log = Monitor.Latest?.Status?.Rclone?.LogPath
                  ?? Path.Combine(Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "logs", "rclone.log");
        if (File.Exists(log))
        {
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
        }
        else
        {
            ShowProblem($"There is no rclone log yet ({log}).");
        }
    }

    /// <summary>Runs one start, stop or save at a time; returns why it failed, or null.</summary>
    private async Task<string?> RunAsync(string what, Func<Task<string?>> action, bool report = true)
    {
        if (_busy)
        {
            return "OpenCfDrive is busy with something else; try again in a moment.";
        }

        _busy = true;
        _window?.ShowBusy(what);
        string? problem;
        try
        {
            problem = await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = ex.Message;
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync(probeRclone: true);
        _window?.ShowBusy(null);
        if (problem is not null && report)
        {
            ShowProblem(problem);
        }

        return problem;
    }

    public void ShowProblem(string problem)
    {
        if (_window is { Visible: true })
        {
            MessageBox.Show(_window, problem, "OpenCfDrive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            ShowBalloon("OpenCfDrive", problem, ToolTipIcon.Warning);
        }
    }

    /// <summary>A notification; clicking it runs <paramref name="clicked"/>, or opens the window.</summary>
    private void ShowBalloon(string title, string text, ToolTipIcon icon, Action? clicked = null)
    {
        _balloonClicked = clicked;
        _tray.ShowBalloonTip(10_000, title, text, icon);
    }

    public void OpenFolder()
    {
        var root = Monitor.Latest?.Settings.SyncRoot.Value;
        if (root is not null && Directory.Exists(root))
        {
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>The service's log file: where the running service writes, or where it would.</summary>
    public static string LogPath(DriveSnapshot? snapshot) =>
        string.IsNullOrEmpty(snapshot?.Status?.LogPath)
            ? Path.Combine(Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "logs", "OpenCfDrive.log")
            : snapshot.Status.LogPath;

    public void OpenLog()
    {
        var log = LogPath(Monitor.Latest);
        if (File.Exists(log))
        {
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
        }
        else
        {
            ShowProblem($"There is no log yet ({log}).");
        }
    }

    private void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _timer.Stop();
        Updates.Dispose();
        _window?.CloseForExit();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }
}
