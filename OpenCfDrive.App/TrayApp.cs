using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Storage;

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
    private readonly ToolStripMenuItem _exit;
    private readonly System.Windows.Forms.Timer _timer = new();
    private MainForm? _window;
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
        _exit = new ToolStripMenuItem("Exit", null, (_, _) => Exit());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _headline, new ToolStripSeparator(),
            _openFolder, openWindow, new ToolStripSeparator(),
            _start, _stop, _restart, new ToolStripSeparator(),
            openLog, _exit,
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
        _tray.BalloonTipClicked += (_, _) => ShowWindow(settings: !(Monitor.Latest?.Settings.HasRemote ?? false));

        _instance.OnSignal(_instance.ShowRequested, () => _ui.Post(_ => ShowWindow(), null));
        _instance.OnSignal(_instance.QuitRequested, () => _ui.Post(_ => Exit(), null));

        Monitor.Updated += OnUpdated;
        _timer.Interval = (int)HiddenRefresh.TotalMilliseconds;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ui.Post(async _ => await StartupAsync(showWindow), null);
    }

    public DriveMonitor Monitor { get; } = new();

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
            _tray.ShowBalloonTip(
                10_000, "OpenCfDrive is not set up", "Click here to choose your rclone remote.", ToolTipIcon.Info);
        }
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
        _exit.Text = running ? "Exit (syncing continues)" : "Exit";
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
            before.RcUrl.Value != after.RcUrl.Value;

        return await RunAsync("Applying the settings", async () =>
        {
            if (syncChanged && await SyncService.StopAsync(before.SyncRoot.Value) is { } notStopped)
            {
                return notStopped;
            }

            if (change.OldSyncRootToUnregister is { } oldRoot && SyncRootRegistrar.IsSyncRoot(oldRoot))
            {
                var result = await Task.Run(() => new SyncRootRegistrar().Unregister(oldRoot));
                if (!result.Success)
                {
                    return $"The old drive folder could not be unregistered: {result.Message}";
                }
            }

            if (change.DiscardOldState)
            {
                DeleteState(after.SyncRoot.Value);
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

    /// <summary>The state of a folder that held another remote's drive: it no longer applies.</summary>
    private static void DeleteState(string syncRoot)
    {
        var database = SyncStatePaths.DatabasePath(syncRoot);
        foreach (var file in new[] { database, database + "-wal", database + "-shm" })
        {
            File.Delete(file);
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
            _tray.ShowBalloonTip(10_000, "OpenCfDrive", problem, ToolTipIcon.Warning);
        }
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
        _window?.CloseForExit();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }
}
