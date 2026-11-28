using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;

namespace OpenCfDrive.App;

/// <summary>
/// The Troubleshooting page, for when something is wrong: what the sync
/// service and rclone report, where the logs and settings are, and how to
/// report a problem.
/// </summary>
internal sealed partial class MainForm
{
    private readonly Label _service = Theme.Text();
    private readonly Label _state = Theme.Text();
    private readonly Label _rcloneRunBy = Theme.Text();
    private readonly Label _rcloneProgram = Theme.Text();
    private readonly Label _rcUrl = Theme.Text();
    private readonly Label _rcloneState = Theme.Text();
    private readonly Label _rcloneRemote = Theme.Text();
    private readonly Label _logFile = Theme.Text();
    private readonly Label _rcloneLogFile = Theme.Text();
    private readonly Label _settingsFile = Theme.Text();

    private Panel BuildTroubleshootingPage()
    {
        var (page, column) = Page();

        var service = new Card(2);
        service.AddRow(Theme.Caption("Service"), _service);
        service.AddRow(Theme.Caption("State"), _state);

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

        // Where the user guide sends people: an issue, with both logs.
        var report = Theme.Button("Report a problem");
        report.Click += (_, _) => _app.Open(AppVersion.ProjectPage + "/issues/new/choose");
        report.Anchor = AnchorStyles.Right;
        report.Margin = new Padding(12, 0, 0, 0);
        var reportText = Theme.Text("Open an issue on GitHub, and attach the service log and rclone's log.");
        reportText.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        reportText.Dock = DockStyle.None;
        var reporting = new Card(2, stretch: 0);
        reporting.AddRow(reportText, report);

        Add(
            column,
            Heading("Sync service"), service,
            Heading("rclone"), rclone,
            Heading("Logs and settings"), files,
            Heading("Report a problem"), reporting);
        _troubleshootingPage = page;
        return page;
    }

    private void ShowTroubleshooting(DriveSnapshot snapshot)
    {
        ShowService(snapshot);
        ShowRclone(snapshot);
        _logFile.Text = TrayApp.LogPath(snapshot);
        _rcloneLogFile.Text = TrayApp.RcloneLogPath(snapshot);
        _settingsFile.Text = DriveSettingsFile.Path;
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
