using System.Globalization;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>
/// The About page: the version and its updates, then what the sync service
/// and rclone report (for when something is wrong), and where the logs and
/// settings are.
/// </summary>
internal sealed partial class MainForm
{
    private readonly PictureBox _aboutIcon = new() { SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 2, 16, 0) };
    private readonly Label _version = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false };
    private readonly Label _update = Theme.Text();
    private readonly Button _checkUpdates = Theme.Button("Check for updates");
    private readonly Button _installUpdate = Theme.Button("Restart and update");
    private readonly LinkLabel _releasePage = new() { Text = "Release page", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 4, 0, 4) };
    private readonly Label _service = Theme.Text();
    private readonly Label _state = Theme.Text();
    private readonly Label _items = Theme.Text();
    private readonly Label _rcloneRunBy = Theme.Text();
    private readonly Label _rcloneProgram = Theme.Text();
    private readonly Label _rcUrl = Theme.Text();
    private readonly Label _rcloneState = Theme.Text();
    private readonly Label _rcloneRemote = Theme.Text();
    private readonly Label _logFile = Theme.Text();
    private readonly Label _rcloneLogFile = Theme.Text();
    private readonly Label _settingsFile = Theme.Text();

    private Panel BuildAboutPage()
    {
        var (page, column) = Page();

        _checkUpdates.Click += async (_, _) => await _app.CheckForUpdatesAsync();
        _installUpdate.Click += async (_, _) => await _app.InstallUpdateAsync();
        _releasePage.LinkClicked += (_, _) =>
        {
            if (_app.UpdateStatus.Page is { Length: > 0 } release)
            {
                _app.Open(release);
            }
        };

        // The app, and its updates.
        _version.Font = _titleFont;
        _version.Text = $"OpenCfDrive {GitHubReleases.Format(_app.CurrentVersion)}";
        var app = new Card(2) { Padding = new Padding(20, 16, 20, 14) };
        app.AddRow(_aboutIcon, _version);
        app.SetRowSpan(_aboutIcon, 3);
        app.AddRow(null, _update);
        app.AddRow(null, Theme.ButtonRow(_checkUpdates, _installUpdate, _releasePage));
        _update.Margin = new Padding(0, 0, 0, 6);

        var service = new Card(2);
        service.AddRow(Theme.Caption("Service"), _service);
        service.AddRow(Theme.Caption("State"), _state);
        service.AddRow(Theme.Caption("Items"), _items);

        var rclone = new Card(2);
        rclone.AddRow(Theme.Caption("Run by"), _rcloneRunBy);
        rclone.AddRow(Theme.Caption("Program"), _rcloneProgram);
        rclone.AddRow(Theme.Caption("Remote control"), _rcUrl);
        rclone.AddRow(Theme.Caption("Status"), _rcloneState);
        rclone.AddRow(Theme.Caption("Remote"), _rcloneRemote);

        var openLog = Theme.Button("Open");
        openLog.Click += (_, _) => _app.OpenLog();
        var openRcloneLog = Theme.Button("Open");
        openRcloneLog.Click += (_, _) => _app.OpenRcloneLog();
        var openSettings = Theme.Button("Show");
        openSettings.Click += (_, _) => _app.Open(Path.GetDirectoryName(DriveSettingsFile.Path)!);
        var files = new Card(3);
        files.AddRow(Theme.FieldCaption("Service log"), _logFile, openLog);
        files.AddRow(Theme.FieldCaption("rclone log"), _rcloneLogFile, openRcloneLog);
        files.AddRow(Theme.FieldCaption("Settings"), _settingsFile, openSettings);
        files.SameWidthButtons();
        foreach (var path in new[] { _logFile, _rcloneLogFile, _settingsFile })
        {
            path.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            path.Dock = DockStyle.None;
        }

        var legal = Theme.Hint(
            "OpenCfDrive is public domain software, released under the Unlicense, and comes with no warranty. " +
            "It works with rclone but isn't part of it.");
        legal.Margin = new Padding(2, 14, 0, 0);

        Add(column, app, Heading("Sync service"), service, Heading("rclone"), rclone, Heading("Logs and settings"), files, legal);
        _aboutPage = page;
        return page;
    }

    private void ShowAbout(DriveSnapshot snapshot)
    {
        ShowService(snapshot);
        ShowRclone(snapshot);
        _logFile.Text = TrayApp.LogPath(snapshot);
        _rcloneLogFile.Text = TrayApp.RcloneLogPath(snapshot);
        _settingsFile.Text = DriveSettingsFile.Path;
        var idle = _busy is null && !_app.Busy;
        _installUpdate.Enabled = idle;
        _updateNow.Enabled = idle;
    }

    /// <summary>Where updating stands: checked, downloading, ready to install, or why not.</summary>
    public void ShowUpdate(UpdateStatus status)
    {
        using var batch = Theme.OneLayout(this);
        _update.Text = status.CheckedAt is { } checkedAt && status.Stage is UpdateStage.UpToDate or UpdateStage.Ready
            ? $"{status.Detail} (Checked {checkedAt.LocalDateTime:g}.)"
            : status.Detail;
        _update.ForeColor = status.Stage switch
        {
            UpdateStage.UpToDate => Theme.Good,
            UpdateStage.Ready or UpdateStage.Checking or UpdateStage.Downloading => Theme.Busy,
            UpdateStage.Failed => Theme.Problem,
            _ => SystemColors.ControlText,
        };
        _checkUpdates.Visible = status.Stage != UpdateStage.Unavailable;
        _checkUpdates.Enabled = status.Stage is not (UpdateStage.Checking or UpdateStage.Downloading);
        _installUpdate.Visible = status.Stage == UpdateStage.Ready;
        _installUpdate.Text = status.Available is { } version ? $"Restart and update to {GitHubReleases.Format(version)}" : "Restart and update";
        _releasePage.Visible = status.Page is { Length: > 0 } && status.Stage is UpdateStage.Ready or UpdateStage.Failed;
        _autoUpdate.Enabled = status.Stage != UpdateStage.Unavailable;
        ShowUpdateBar(status);
    }

    private void ShowService(DriveSnapshot snapshot)
    {
        var status = snapshot.Status;
        _service.Text = status is not null
            ? $"Running (process {status.ProcessId}, since {status.StartedAt.LocalDateTime:g}, version {VersionText(status.Version)})"
            : snapshot.Running ? "Running" : "Not running";

        // The service's own state; the Status page adds what rclone says.
        _state.Text = status?.Detail ?? (snapshot.Running ? "Not reported." : "-");
        _state.ForeColor = status?.State switch
        {
            ServiceState.Syncing => Theme.Good,
            ServiceState.Starting or ServiceState.Offline => Theme.Busy,
            ServiceState.Failed or ServiceState.Unavailable => Theme.Problem,
            _ => SystemColors.ControlText,
        };
        _items.Text = status is null
            ? "-"
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} {1} tracked; {2} downloaded since it started",
                status.ItemsTracked,
                status.ItemsTracked == 1 ? "item" : "items",
                FormatBytes(status.BytesDownloaded));
    }

    private static string VersionText(string version) => string.IsNullOrEmpty(version) ? "unknown" : version;

    private void ShowRclone(DriveSnapshot snapshot)
    {
        var rclone = snapshot.Rclone;

        // The rclone the running service runs, else the one it would run.
        var own = snapshot.Status?.Rclone;
        if (own?.Managed ?? snapshot.Settings.ManageRclone)
        {
            _rcloneRunBy.Text = own?.ProcessId is { } pid ? $"OpenCfDrive (process {pid})" : "OpenCfDrive, while the service runs";
            var program = own?.Program ?? rclone?.Program?.Path;
            var source = own?.Source ?? rclone?.Program?.Source.ToString();
            var version = own?.Version ?? rclone?.Version;
            _rcloneProgram.Text = program is null
                ? rclone?.Problem ?? "Not found."
                : $"{program} ({SourceText(source)}{(version is null ? "" : $", {version}")})";
            _rcloneProgram.ForeColor = program is null && rclone is not null ? Theme.Problem : SystemColors.ControlText;
            _rcUrl.Text = own is { Url.Length: > 0 } ? own.Url : "-";
            (_rcloneState.Text, _rcloneState.ForeColor) = own is not null
                ? (own.Detail, own.State switch
                {
                    "Running" => Theme.Good,
                    "Starting" or "Restarting" => Theme.Busy,
                    _ => Theme.Problem,
                })
                : rclone is null
                    ? ("Checking...", SystemColors.ControlText)
                    : rclone.Usable
                        ? ("Not running: the sync service starts it.", SystemColors.ControlText)
                        : (rclone.Problem ?? "Does not run.", Theme.Problem);
        }
        else
        {
            _rcloneRunBy.Text = "You (rclone rcd)";
            _rcloneProgram.Text = "-";
            _rcloneProgram.ForeColor = SystemColors.ControlText;
            _rcUrl.Text = own?.Url ?? snapshot.Settings.RcUrl.Value;
            (_rcloneState.Text, _rcloneState.ForeColor) = rclone is null
                ? ("Checking...", SystemColors.ControlText)
                : rclone.Usable
                    ? ($"Answers (rclone {rclone.Version})", Theme.Good)
                    : (rclone.Problem ?? "Does not answer.", Theme.Problem);
        }

        var remote = snapshot.Remote;
        var name = RcloneProbe.RemoteName(remote);
        (_rcloneRemote.Text, _rcloneRemote.ForeColor) = rclone is null
            ? ("-", SystemColors.ControlText)
            : !snapshot.Settings.HasRemote && snapshot.Status is null
                ? ("None chosen yet", SystemColors.ControlText)
                : rclone.Remotes is null || name is null
                    ? (remote, SystemColors.ControlText)
                    : rclone.Remotes.Contains(name)
                        ? ($"{remote} (\"{name}\" is in rclone's config)", Theme.Good)
                        : rclone.Remotes.Count == 0
                            ? ($"\"{name}\" is not in rclone's config, which has no remotes", Theme.Problem)
                            : ($"\"{name}\" is not in rclone's config, which has: {string.Join(", ", rclone.Remotes)}", Theme.Problem);
    }
}
