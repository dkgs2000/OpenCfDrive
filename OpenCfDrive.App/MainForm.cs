using System.Globalization;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>
/// The window the notification area icon opens: what the service and rclone
/// are doing (Status), and what the drive syncs with (Settings). Closing it
/// only hides it; the icon stays.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color GoodColor = Color.FromArgb(0x10, 0x7C, 0x10);
    private static readonly Color BusyColor = Color.FromArgb(0x8A, 0x53, 0x00);
    private static readonly Color ProblemColor = Color.FromArgb(0xC4, 0x2B, 0x1C);

    private readonly TrayApp _app;

    private readonly Label _headline = new() { AutoSize = true };
    private readonly Label _headlineDetail = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _settingsTab = new("Settings") { Padding = new Padding(12) };

    // Status
    private readonly Label _service = ValueLabel();
    private readonly Label _state = ValueLabel();
    private readonly LinkLabel _folder = new() { AutoSize = true, Margin = ValueMargin };
    private readonly Label _remote = ValueLabel();
    private readonly Label _items = ValueLabel();
    private readonly Label _rcloneRunBy = ValueLabel();
    private readonly Label _rcloneProgram = ValueLabel();
    private readonly Label _rcUrl = ValueLabel();
    private readonly Label _rcloneState = ValueLabel();
    private readonly Label _rcloneRemote = ValueLabel();
    private readonly Label _remoteKind = ValueLabel();
    private readonly Label _remoteSpace = ValueLabel();
    private readonly Button _start = ActionButton("Start");
    private readonly Button _stop = ActionButton("Stop");
    private readonly Button _restart = ActionButton("Restart");
    private readonly Button _viewLog = ActionButton("View log");
    private readonly Button _viewRcloneLog = ActionButton("rclone's log");
    private readonly Label _version = ValueLabel();
    private readonly Label _update = ValueLabel();
    private readonly Button _checkUpdates = ActionButton("Check for updates");
    private readonly Button _installUpdate = ActionButton("Restart and update");
    private readonly LinkLabel _releasePage = new() { Text = "Release page", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 0, 0) };
    private readonly ListView _activity = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        ShowItemToolTips = true,
    };

    // Settings
    private readonly TextBox _folderBox = new() { Dock = DockStyle.Fill, AccessibleName = "Drive folder" };
    private readonly ComboBox _remoteBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "rclone remote" };
    private readonly TextBox _rcUrlBox = new() { Dock = DockStyle.Fill, AccessibleName = "rclone remote control" };
    private readonly RadioButton _manageRclone = new() { Text = "OpenCfDrive runs it", AutoSize = true, Checked = true };
    private readonly RadioButton _ownRclone = new() { Text = "I run rclone rcd myself", AutoSize = true };
    private readonly TextBox _programBox = new()
    {
        Dock = DockStyle.Fill,
        AccessibleName = "rclone program",
        PlaceholderText = "Find it: on PATH, else the copy downloaded from rclone.org",
    };
    private readonly TextBox _configBox = new()
    {
        Dock = DockStyle.Fill,
        AccessibleName = "rclone config file",
        PlaceholderText = "rclone's own default",
    };
    private readonly Label _programNote = Hint("");
    private readonly Label _configNote = Hint("");
    private readonly Button _download = ActionButton("Download rclone from rclone.org");
    private readonly Button _test = ActionButton("Test");
    private readonly Label _downloadProgress = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly Label _rcdHint = Hint("Where your rclone rcd listens. Run it with --rc-serve and --rc-no-auth, for example:");
    private readonly System.Windows.Forms.Timer _checkSoon = new() { Interval = 800 };
    private readonly Label _rcTest = Hint("");
    private readonly Label _remoteDetails = Hint("");
    private readonly System.Windows.Forms.Timer _describeSoon = new() { Interval = 600 };
    private readonly TextBox _rcdCommand = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly CheckBox _signIn = new() { Text = "Start OpenCfDrive when I sign in to Windows", AutoSize = true };
    private readonly CheckBox _autoUpdate = new() { Text = "Check for new versions and download them (every 10 hours, from GitHub)", AutoSize = true };
    private readonly Label _overrides = new() { AutoSize = true, ForeColor = BusyColor, Visible = false };
    private readonly Label _settingsFile = Hint("");
    private readonly Button _save = ActionButton("Save");
    private readonly Button _discard = ActionButton("Discard changes");
    private readonly Label _saveResult = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 0, 0) };

    private DriveSnapshot? _snapshot;
    private string? _busy;
    private FieldSet? _loaded;
    private bool _loading;
    private bool _exiting;
    private (int Count, DateTimeOffset Last) _shownActivity;

    public MainForm(TrayApp app)
    {
        _app = app;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        SuspendLayout();

        Text = "OpenCfDrive";
        Icon = AppIcon.Large;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 700);
        MinimumSize = new Size(620, 600);

        _headline.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);
        _tabs.TabPages.Add(BuildStatusTab());
        BuildSettingsTab();
        _tabs.TabPages.Add(_settingsTab);

        Controls.Add(_tabs);
        Controls.Add(BuildHeader());
        ResumeLayout(performLayout: true);

        FormClosing += (_, e) =>
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    // --- layout ----------------------------------------------------------

    private static readonly Padding ValueMargin = new(3, 4, 3, 4);

    private static Label ValueLabel() => new() { AutoSize = true, Margin = ValueMargin, UseMnemonic = false };

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Margin = ValueMargin, ForeColor = SystemColors.GrayText };

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8), UseMnemonic = false };

    private static Button ActionButton(string text) =>
        new() { Text = text, AutoSize = true, MinimumSize = new Size(88, 0), Padding = new Padding(6, 1, 6, 1) };

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(14, 12, 14, 10),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var icon = new PictureBox
        {
            Image = AppIcon.Large.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(36, 36),
            Margin = new Padding(0, 2, 10, 0),
        };
        header.Controls.Add(icon, 0, 0);
        header.SetRowSpan(icon, 2);
        header.Controls.Add(_headline, 1, 0);
        header.Controls.Add(_headlineDetail, 1, 1);
        WrapWithin(header, _headlineDetail, 1);
        return header;
    }

    /// <summary>Two columns of caption and value, wrapping long values.</summary>
    private static TableLayoutPanel Grid(params (string Caption, Control Value)[] rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (caption, value) in rows)
        {
            grid.Controls.Add(Caption(caption));
            grid.Controls.Add(value);
            if (value is Label label)
            {
                WrapWithin(grid, label, 1);
            }
        }

        return grid;
    }

    /// <summary>Keeps an auto-sized label within its column, so it wraps instead of growing wider.</summary>
    private static void WrapWithin(TableLayoutPanel grid, Label label, int column)
    {
        grid.SizeChanged += (_, _) =>
        {
            var widths = grid.GetColumnWidths();
            if (column < widths.Length && widths[column] > 0)
            {
                label.MaximumSize = new Size(Math.Max(80, widths[column] - label.Margin.Horizontal), 0);
            }
        };
    }

    private static GroupBox Group(string title, Control content, bool fill = false)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = fill ? DockStyle.Fill : DockStyle.Top,
            AutoSize = !fill,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 10),
        };
        group.Controls.Add(content);
        return group;
    }

    private TabPage BuildStatusTab()
    {
        var page = new TabPage("Status") { Padding = new Padding(12) };

        _folder.LinkClicked += (_, _) => _app.OpenFolder();
        _start.Click += async (_, _) => await _app.StartAsync();
        _stop.Click += async (_, _) => await _app.StopAsync();
        _restart.Click += async (_, _) => await _app.RestartAsync();
        _viewLog.Click += (_, _) => _app.OpenLog();
        _viewRcloneLog.Click += (_, _) => _app.OpenRcloneLog();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), WrapContents = false };
        buttons.Controls.AddRange([_start, _stop, _restart, _viewLog, _viewRcloneLog]);

        var service = Grid(
            ("Service", _service),
            ("State", _state),
            ("Drive folder", _folder),
            ("Remote", _remote),
            ("Items", _items));
        var serviceBox = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        serviceBox.Controls.Add(service);
        serviceBox.Controls.Add(buttons);

        var rclone = Grid(
            ("Run by", _rcloneRunBy),
            ("Program", _rcloneProgram),
            ("Remote control", _rcUrl),
            ("Status", _rcloneState),
            ("Remote", _rcloneRemote),
            ("Kind", _remoteKind),
            ("Space", _remoteSpace));

        _version.Text = $"OpenCfDrive {GitHubReleases.Format(_app.Updates.Current)}";
        _checkUpdates.Click += async (_, _) => await _app.Updates.CheckNowAsync();
        _installUpdate.Click += async (_, _) => await _app.InstallUpdateAsync();
        _releasePage.LinkClicked += (_, _) =>
        {
            if (_app.Updates.Status.Page is { Length: > 0 } page)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
            }
        };
        var updateButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), WrapContents = false };
        updateButtons.Controls.AddRange([_checkUpdates, _installUpdate, _releasePage]);
        var updates = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        updates.Controls.Add(Grid(("Version", _version), ("Updates", _update)));
        updates.Controls.Add(updateButtons);

        _activity.Columns.Add("Time", 70);
        _activity.Columns.Add("What happened", 400);
        _activity.SizeChanged += (_, _) => FitActivityColumns();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Group("Sync service", serviceBox), 0, 0);
        layout.Controls.Add(Group("rclone", rclone), 0, 1);
        layout.Controls.Add(Group("OpenCfDrive", updates), 0, 2);
        layout.Controls.Add(Group("Recent activity", _activity, fill: true), 0, 3);
        page.Controls.Add(layout);
        return page;
    }

    private void BuildSettingsTab()
    {
        var browse = ActionButton("Browse...");
        browse.Click += (_, _) => BrowseForFolder();
        var refresh = ActionButton("Refresh list");
        refresh.Click += async (_, _) => await TestRcloneAsync(fillRemotes: true);
        _test.Click += async (_, _) => await TestRcloneAsync(fillRemotes: true);
        _save.Click += async (_, _) => await SaveAsync();
        _discard.Click += (_, _) =>
        {
            if (_snapshot is not null)
            {
                LoadSettings(_snapshot);
            }
        };

        var browseProgram = ActionButton("Browse...");
        browseProgram.Click += (_, _) => BrowseForFile(_programBox, "Choose rclone.exe", "rclone|rclone.exe|Programs|*.exe");
        var browseConfig = ActionButton("Browse...");
        browseConfig.Click += (_, _) => BrowseForFile(_configBox, "Choose rclone's config file", "rclone config|*.conf|All files|*.*");
        _download.Click += async (_, _) => await DownloadRcloneAsync();

        foreach (var input in new Control[] { _folderBox, _remoteBox, _rcUrlBox, _programBox, _configBox })
        {
            input.TextChanged += (_, _) => ClearSaveResult();
        }

        _rcUrlBox.TextChanged += (_, _) => ShowRcdCommand();
        _signIn.CheckedChanged += (_, _) => ClearSaveResult();
        _autoUpdate.CheckedChanged += (_, _) => ClearSaveResult();
        _manageRclone.CheckedChanged += (_, _) =>
        {
            ClearSaveResult();
            ShowRcloneMode();
        };
        _rcTest.Visible = false; // until Test is pressed

        // The chosen remote's details, and the chosen rclone, once typing pauses.
        foreach (var input in new Control[] { _remoteBox, _rcUrlBox, _programBox, _configBox })
        {
            input.TextChanged += (_, _) => DescribeSoon();
        }

        foreach (var input in new Control[] { _rcUrlBox, _programBox, _configBox })
        {
            input.TextChanged += (_, _) => CheckSoon();
        }

        _manageRclone.CheckedChanged += (_, _) => CheckSoon();
        _remoteBox.SelectedIndexChanged += (_, _) => DescribeSoon();
        _describeSoon.Tick += async (_, _) =>
        {
            _describeSoon.Stop();
            await DescribeTypedRemoteAsync();
        };
        _checkSoon.Tick += async (_, _) =>
        {
            _checkSoon.Stop();
            await TestRcloneAsync(fillRemotes: true);
        };
        _rcdCommand.BackColor = SystemColors.Control;
        _rcdCommand.Font = new Font(FontFamily.GenericMonospace, Font.SizeInPoints);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Explicit cells: a hidden note (no overrides, no test yet) would
        // otherwise shift every cell after it.
        var row = 0;
        void Row(string caption, Control input, Control button)
        {
            var label = new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(input, 1, row);
            grid.Controls.Add(button, 2, row);
            row++;
        }

        void Note(Control note)
        {
            grid.Controls.Add(note, 1, row);
            grid.SetColumnSpan(note, 2);
            if (note is Label label)
            {
                WrapWithin(grid, label, 1);
            }

            row++;
        }

        var mode = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
        mode.Controls.AddRange([_manageRclone, _ownRclone]);
        var download = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        download.Controls.AddRange([_download, _downloadProgress]);

        Row("Drive folder", _folderBox, browse);
        Note(Hint("Where the drive appears in Explorer. Moving it needs a new or empty folder; the old one stays " +
                  "as an ordinary folder with the files already downloaded."));
        Row("rclone", mode, new Label { AutoSize = true });
        Row("rclone program", _programBox, browseProgram);
        Note(_programNote);
        Note(download);
        Row("rclone config", _configBox, browseConfig);
        Note(_configNote);
        Row("Remote control", _rcUrlBox, _test);
        Note(_rcTest);
        Note(_rcdHint);
        Note(_rcdCommand);
        Row("rclone remote", _remoteBox, refresh);
        Note(_remoteDetails);
        Note(Hint("A remote in rclone's config, optionally with a path: myremote: or myremote:backup. " +
                  "Another remote needs a new or empty drive folder. Create remotes with: rclone config"));
        Note(_signIn);
        Note(_autoUpdate);
        Note(_overrides);
        Note(_settingsFile);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([_save, _discard, _saveResult]);

        // The fields scroll; Save stays in view.
        var fields = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fields.Controls.Add(grid);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(fields, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        _settingsTab.Controls.Add(layout);
        ShowRcloneMode();
    }

    /// <summary>The fields that matter for the chosen way of running rclone.</summary>
    private void ShowRcloneMode()
    {
        var managed = _manageRclone.Checked;
        _programBox.Enabled = managed;
        _configBox.Enabled = managed;
        _download.Enabled = managed && _busy is null;
        _rcUrlBox.Enabled = !managed;
        _test.Enabled = !managed;
        _rcdHint.Visible = !managed;
        _rcdCommand.Visible = !managed;
        _programNote.Visible = managed;
        _configNote.Visible = managed;
        _rcTest.Visible = !managed && _rcTest.Text.Length > 0;
    }

    // --- showing the state -------------------------------------------------

    public void ShowSettingsTab() => _tabs.SelectedTab = _settingsTab;

    /// <summary>Marks a start, stop or save in progress (or none).</summary>
    public void ShowBusy(string? what)
    {
        _busy = what;
        UseWaitCursor = what is not null;
        if (_snapshot is not null)
        {
            ShowSnapshot(_snapshot);
        }
    }

    public void ShowSnapshot(DriveSnapshot snapshot)
    {
        _snapshot = snapshot;
        var (health, headline, detail) = snapshot.Summary;
        if (_busy is not null)
        {
            (health, headline, detail) = (Health.Busy, _busy + "...", "One moment.");
        }

        _headline.Text = headline;
        _headline.ForeColor = ColorOf(health);
        _headlineDetail.Text = detail;

        ShowService(snapshot);
        ShowRclone(snapshot);
        ShowActivity(snapshot.Status?.RecentActivity ?? []);

        var idle = _busy is null && !_app.Busy;
        _start.Enabled = idle && !snapshot.Running && snapshot.Settings.HasRemote && snapshot.SettingsProblem is null;
        _stop.Enabled = idle && snapshot.Running;
        _restart.Enabled = idle && snapshot.Running;
        _save.Enabled = idle;
        _download.Enabled = idle && _manageRclone.Checked;

        if (!Edited)
        {
            LoadSettings(snapshot);
        }

        ShowOverrides(snapshot.Settings);
    }

    /// <summary>Where updating stands: checked, downloading, ready to install, or why not.</summary>
    public void ShowUpdate(UpdateStatus status)
    {
        _update.Text = status.CheckedAt is { } checkedAt && status.Stage is UpdateStage.UpToDate or UpdateStage.Ready
            ? $"{status.Detail} (Checked {checkedAt.LocalDateTime:g}.)"
            : status.Detail;
        _update.ForeColor = status.Stage switch
        {
            UpdateStage.UpToDate => GoodColor,
            UpdateStage.Ready or UpdateStage.Checking or UpdateStage.Downloading => BusyColor,
            UpdateStage.Failed => ProblemColor,
            _ => SystemColors.ControlText,
        };
        _checkUpdates.Visible = status.Stage != UpdateStage.Unavailable;
        _checkUpdates.Enabled = status.Stage is not (UpdateStage.Checking or UpdateStage.Downloading);
        _installUpdate.Visible = status.Stage == UpdateStage.Ready;
        _installUpdate.Text = status.Available is { } version ? $"Restart and update to {GitHubReleases.Format(version)}" : "Restart and update";
        _releasePage.Visible = status.Page is { Length: > 0 } && status.Stage is UpdateStage.Ready or UpdateStage.Failed;
        _autoUpdate.Enabled = status.Stage != UpdateStage.Unavailable;
    }

    private void ShowService(DriveSnapshot snapshot)
    {
        var status = snapshot.Status;
        _service.Text = status is not null
            ? $"Running (process {status.ProcessId}, since {status.StartedAt.LocalDateTime:g}, version {VersionText(status.Version)})"
            : snapshot.Running ? "Running" : "Not running";

        // The service's own state; the header adds what rclone says.
        _state.Text = status?.Detail ?? (snapshot.Running ? "Not reported." : "-");
        _state.ForeColor = status?.State switch
        {
            ServiceState.Syncing => GoodColor,
            ServiceState.Starting or ServiceState.Offline => BusyColor,
            ServiceState.Failed or ServiceState.Unavailable => ProblemColor,
            _ => SystemColors.ControlText,
        };

        var root = status?.SyncRoot ?? snapshot.Settings.SyncRoot.Value;
        _folder.Text = root;
        _folder.LinkArea = Directory.Exists(root) ? new LinkArea(0, root.Length) : new LinkArea(0, 0);
        _remote.Text = status?.Remote ?? (snapshot.Settings.HasRemote ? snapshot.Settings.Remote.Value : "None chosen yet");
        _items.Text = status is null
            ? "-"
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} {1} tracked; {2} downloaded since it started",
                status.ItemsTracked,
                status.ItemsTracked == 1 ? "item" : "items",
                FormatBytes(status.BytesDownloaded));
    }

    private void ShowRclone(DriveSnapshot snapshot)
    {
        var rclone = snapshot.Rclone;
        _remoteKind.Text = "-";
        _remoteSpace.Text = "-";

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
            _rcloneProgram.ForeColor = program is null && rclone is not null ? ProblemColor : SystemColors.ControlText;
            _rcUrl.Text = own is { Url.Length: > 0 } ? own.Url : "-";
            (_rcloneState.Text, _rcloneState.ForeColor) = own is not null
                ? (own.Detail, own.State switch
                {
                    "Running" => GoodColor,
                    "Starting" or "Restarting" => BusyColor,
                    _ => ProblemColor,
                })
                : rclone is null
                    ? ("Checking...", SystemColors.ControlText)
                    : rclone.Usable
                        ? ("Not running: the sync service starts it.", SystemColors.ControlText)
                        : (rclone.Problem ?? "Does not run.", ProblemColor);
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
                    ? ($"Answers (rclone {rclone.Version})", GoodColor)
                    : (rclone.Problem ?? "Does not answer.", ProblemColor);
        }

        if (rclone is null)
        {
            _rcloneRemote.Text = "-";
            return;
        }

        var remote = snapshot.Remote;
        var name = RcloneProbe.RemoteName(remote);
        if (!snapshot.Settings.HasRemote && snapshot.Status is null)
        {
            _rcloneRemote.Text = "None chosen yet";
            _rcloneRemote.ForeColor = SystemColors.ControlText;
            return;
        }

        if (rclone.Remotes is null || name is null)
        {
            _rcloneRemote.Text = remote;
            _rcloneRemote.ForeColor = SystemColors.ControlText;
        }
        else if (rclone.Remotes.Contains(name))
        {
            _rcloneRemote.Text = $"{remote} (\"{name}\" is in rclone's config)";
            _rcloneRemote.ForeColor = GoodColor;
        }
        else
        {
            _rcloneRemote.Text = rclone.Remotes.Count == 0
                ? $"\"{name}\" is not in rclone's config, which has no remotes"
                : $"\"{name}\" is not in rclone's config, which has: {string.Join(", ", rclone.Remotes)}";
            _rcloneRemote.ForeColor = ProblemColor;
            return;
        }

        if (rclone.Usable)
        {
            (_remoteKind.Text, _remoteSpace.Text) = snapshot.RemoteInfo is { } info
                ? (info.Kind ?? info.Problem ?? "-", info.Problem is null ? SpaceText(info) : "-")
                : ("Asking rclone...", "Asking the server...");
        }
    }

    /// <summary>"3.1 GB used, 6.9 GB free of 10 GB", or why there is no number.</summary>
    private static string SpaceText(RemoteInfo info)
    {
        if (info.Space is not { } space || (space.Total, space.Used, space.Free) == (null, null, null))
        {
            return info.SpaceProblem ?? "Not reported by the server.";
        }

        var parts = new List<string>();
        if (space.Used is { } used)
        {
            parts.Add($"{FormatBytes(used)} used");
        }

        if (space.Free is { } free)
        {
            parts.Add($"{FormatBytes(free)} free");
        }

        var text = string.Join(", ", parts);
        return space.Total is { } total
            ? text.Length == 0 ? $"{FormatBytes(total)} in all" : $"{text} of {FormatBytes(total)}"
            : text;
    }

    private static string SourceText(string? source) => source switch
    {
        nameof(RcloneSource.Chosen) => "chosen in Settings",
        nameof(RcloneSource.Path) => "found on PATH",
        nameof(RcloneSource.Downloaded) => "downloaded from rclone.org",
        _ => "found",
    };

    private void DescribeSoon()
    {
        if (!_loading)
        {
            _describeSoon.Stop();
            _describeSoon.Start();
        }
    }

    private void CheckSoon()
    {
        if (!_loading)
        {
            _checkSoon.Stop();
            _checkSoon.Start();
        }
    }

    /// <summary>The rclone the Settings fields describe, before they are saved.</summary>
    private RcloneAccess FormAccess() => new(
        _manageRclone.Checked,
        Blank(_programBox.Text),
        Blank(_configBox.Text),
        _rcUrlBox.Text.Trim());

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Describes the remote typed in Settings, so it can be checked before saving.</summary>
    private async Task DescribeTypedRemoteAsync()
    {
        var remote = _remoteBox.Text.Trim();
        var access = FormAccess();
        if (remote.Length == 0)
        {
            _remoteDetails.Text = "";
            return;
        }

        var normalized = SyncStatePaths.NormalizeRemote(remote);
        if (_snapshot?.RemoteInfo is { } known && known.Remote == normalized &&
            access == RcloneAccess.From(_snapshot.Settings))
        {
            ShowRemoteDetails(known);
            return;
        }

        _remoteDetails.Text = "Asking rclone about it...";
        _remoteDetails.ForeColor = SystemColors.GrayText;
        var info = await RcloneProbe.DescribeAsync(access, normalized);
        if (_remoteBox.Text.Trim() == remote && FormAccess() == access)
        {
            ShowRemoteDetails(info);
        }
    }

    private void ShowRemoteDetails(RemoteInfo info)
    {
        _remoteDetails.Text = info.Problem ?? $"{info.Kind ?? info.Remote}. Space: {SpaceText(info)}";
        _remoteDetails.ForeColor = info.Problem is null ? SystemColors.ControlText : ProblemColor;
    }

    private void ShowActivity(IReadOnlyList<ActivityEntry> entries)
    {
        var shown = (entries.Count, entries.Count > 0 ? entries[^1].Time : default);
        if (shown == _shownActivity)
        {
            return;
        }

        _shownActivity = shown;
        _activity.BeginUpdate();
        _activity.Items.Clear();
        foreach (var entry in entries.Reverse())
        {
            var item = new ListViewItem([entry.Time.LocalDateTime.ToString("T", CultureInfo.CurrentCulture), entry.Message])
            {
                ToolTipText = entry.Message,
            };
            if (entry.Level is "Warning")
            {
                item.ForeColor = BusyColor;
            }
            else if (entry.Level is "Error" or "Critical")
            {
                item.ForeColor = ProblemColor;
            }

            _activity.Items.Add(item);
        }

        _activity.EndUpdate();
        FitActivityColumns();
    }

    private void FitActivityColumns()
    {
        // The visible width, so long lines end in "..." (their tooltip has
        // all of it) instead of adding a horizontal scroll bar.
        if (_activity.Columns.Count == 2)
        {
            var time = LogicalToDeviceUnits(80);
            _activity.Columns[0].Width = time;
            _activity.Columns[1].Width = Math.Max(LogicalToDeviceUnits(100),
                _activity.ClientSize.Width - time - SystemInformation.VerticalScrollBarWidth);
        }
    }

    private void ShowOverrides(EffectiveSettings settings)
    {
        var overridden = new[] { settings.SyncRoot, settings.Remote, settings.RcUrl }
            .Where(s => s.Source == SettingSource.Environment)
            .Select(s => s.EnvironmentVariable)
            .ToList();
        _overrides.Visible = overridden.Count > 0;
        _overrides.Text = overridden.Count == 0
            ? ""
            : $"Set in your environment, so the service uses those values instead of these: {string.Join(", ", overridden)}.";
        _settingsFile.Text = $"Saved in {DriveSettingsFile.Path}";
    }

    private static Color ColorOf(Health health) => health switch
    {
        Health.Good => GoodColor,
        Health.Busy => BusyColor,
        Health.Problem => ProblemColor,
        _ => SystemColors.GrayText,
    };

    private static string VersionText(string version) => string.IsNullOrEmpty(version) ? "unknown" : version;

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", bytes)
            : string.Format(CultureInfo.CurrentCulture, "{0:N1} {1}", value, units[unit]);
    }

    // --- settings ----------------------------------------------------------

    private void LoadSettings(DriveSnapshot snapshot)
    {
        _loading = true;
        try
        {
            var saved = snapshot.Saved;
            _folderBox.Text = saved.SyncRoot ?? snapshot.Settings.SyncRoot.Value;
            _remoteBox.Text = saved.Remote ?? "";
            _rcUrlBox.Text = saved.RcUrl ?? DriveSettingsFile.DefaultRcUrl;
            _manageRclone.Checked = saved.ManageRclone != false;
            _ownRclone.Checked = saved.ManageRclone == false;
            _programBox.Text = saved.RclonePath ?? "";
            _configBox.Text = saved.RcloneConfig ?? "";
            _signIn.Checked = SignInStartup.IsEnabled;
            _autoUpdate.Checked = saved.CheckForUpdates != false;
            if (snapshot.Rclone?.Remotes is { } remotes)
            {
                FillRemotes(remotes);
            }

            ShowRcdCommand();
            ShowRcloneMode();
            if (snapshot.Rclone is { } probe && probe.Key == FormAccess().Key)
            {
                ShowRcloneCheck(probe);
            }

            _loaded = FieldValues;

            // What the monitor already knows about this remote, or ask once.
            var remote = _remoteBox.Text.Trim();
            if (remote.Length == 0)
            {
                _remoteDetails.Text = "";
            }
            else if (snapshot.RemoteInfo is { } info && info.Remote == SyncStatePaths.NormalizeRemote(remote))
            {
                ShowRemoteDetails(info);
            }
            else if (_remoteDetails.Text.Length == 0)
            {
                _describeSoon.Start();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>What the Settings fields hold, to tell whether the user changed any.</summary>
    private sealed record FieldSet(
        string Folder, string Remote, string RcUrl, bool SignIn, bool AutoUpdate, bool ManageRclone, string Program, string Config);

    private FieldSet FieldValues => new(
        _folderBox.Text, _remoteBox.Text, _rcUrlBox.Text, _signIn.Checked, _autoUpdate.Checked, _manageRclone.Checked, _programBox.Text,
        _configBox.Text);

    /// <summary>The fields differ from what was loaded into them: the user is editing.</summary>
    private bool Edited => _loaded is { } loaded && loaded != FieldValues;

    private void ClearSaveResult()
    {
        if (!_loading)
        {
            _saveResult.Text = "";
        }
    }

    private void FillRemotes(IReadOnlyList<string> remotes)
    {
        var text = _remoteBox.Text;
        var items = remotes.Select(r => r + ":").ToArray();
        if (!_remoteBox.Items.Cast<string>().SequenceEqual(items))
        {
            _remoteBox.Items.Clear();
            _remoteBox.Items.AddRange(items);
            _remoteBox.Text = text;
        }
    }

    private void ShowRcdCommand()
    {
        var address = Uri.TryCreate(_rcUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Host}:{uri.Port}"
            : "127.0.0.1:5572";
        _rcdCommand.Text = $"rclone rcd --rc-serve --rc-no-auth --rc-addr {address}";
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the drive folder: a new or empty one, or the current drive",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_folderBox.Text) ? _folderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseForFile(TextBox box, string title, string filter)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (File.Exists(box.Text.Trim()))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(box.Text.Trim());
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            box.Text = dialog.FileName;
        }
    }

    /// <summary>Checks the rclone the Settings fields describe, and lists its remotes.</summary>
    private async Task TestRcloneAsync(bool fillRemotes)
    {
        var access = FormAccess();
        var note = access.Managed ? _programNote : _rcTest;
        note.Visible = true;
        note.Text = access.Managed ? "Looking for rclone..." : "Asking rclone...";
        note.ForeColor = SystemColors.GrayText;
        var result = await RcloneProbe.CheckAsync(access);
        if (FormAccess() != access)
        {
            return; // the fields changed meanwhile; their own check follows
        }

        ShowRcloneCheck(result);
        if (fillRemotes && result.Remotes is { } remotes)
        {
            FillRemotes(remotes);
        }
    }

    private void ShowRcloneCheck(RcloneProbeResult result)
    {
        if (_manageRclone.Checked)
        {
            _programNote.Text = result.Program is { } program
                ? result.Usable
                    ? $"Runs {program.Path} ({SourceText(program.Source.ToString())}, {result.Version}), with " +
                      $"{result.Remotes!.Count} remote(s) in its config."
                    : result.Problem
                : $"{result.Problem} Install rclone, choose rclone.exe, or download it here.";
            _programNote.ForeColor = result.Usable ? GoodColor : ProblemColor;
            _configNote.Text = result.ConfigFile is { } file ? $"rclone reads {file}" : "";
            return;
        }

        _rcTest.Visible = true;
        _rcTest.Text = result.Usable
            ? $"rclone {result.Version} answers, with {result.Remotes!.Count} remote(s) in its config."
            : result.Problem;
        _rcTest.ForeColor = result.Usable ? GoodColor : ProblemColor;
    }

    private async Task DownloadRcloneAsync()
    {
        _download.Enabled = false;
        var progress = new Progress<string>(line => _downloadProgress.Text = line);
        var problem = await _app.DownloadRcloneAsync(progress);
        _downloadProgress.Text = problem ?? $"Downloaded into {RcloneLocator.DownloadFolder}.";
        _downloadProgress.ForeColor = problem is null ? GoodColor : ProblemColor;
        _downloadProgress.MaximumSize = new Size(Math.Max(200, _settingsTab.ClientSize.Width - _download.Width - 80), 0);
        _download.Enabled = _manageRclone.Checked;
        await TestRcloneAsync(fillRemotes: true);
    }

    private async Task SaveAsync()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var root = _folderBox.Text.Trim();
        var remote = _remoteBox.Text.Trim();
        var rcUrl = _rcUrlBox.Text.Trim();
        var managed = _manageRclone.Checked;
        var program = Blank(_programBox.Text);
        var config = Blank(_configBox.Text);

        string? invalid = null;
        if (!Path.IsPathFullyQualified(root))
        {
            invalid = "The drive folder must be a full path, like C:\\Users\\you\\OpenCfDrive.";
        }
        else if (remote.Length == 0 || remote.StartsWith(':'))
        {
            invalid = "Choose an rclone remote, like myremote:.";
        }
        else if (managed && program is not null && !File.Exists(program))
        {
            invalid = $"There is no rclone program at {program}.";
        }
        else if (managed && config is not null && !File.Exists(config))
        {
            invalid = $"There is no rclone config file at {config}.";
        }
        else if (!managed && (!Uri.TryCreate(rcUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            invalid = "The remote control address must be an http:// address, like http://127.0.0.1:5572.";
        }

        if (invalid is not null)
        {
            ShowSaveResult(invalid, ProblemColor);
            return;
        }

        var settings = snapshot.Saved with
        {
            SyncRoot = root,
            Remote = remote,
            RcUrl = rcUrl,
            ManageRclone = managed,
            RclonePath = program,
            RcloneConfig = config,
            StartAtSignIn = _signIn.Checked,
            CheckForUpdates = _autoUpdate.Checked,
        };
        var before = snapshot.Settings;
        var after = DriveSettingsFile.Resolve(settings);
        var change = DriveChange.Check(before.SyncRoot.Value, before.Remote.Value, after.SyncRoot.Value, after.Remote.Value);
        if (!change.Allowed)
        {
            ShowSaveResult(change.Problem!, ProblemColor);
            return;
        }

        if (change.OldSyncRootToUnregister is { } oldRoot &&
            MessageBox.Show(
                this,
                $"The drive moves to {change.NewSyncRoot}.\n\n{oldRoot} stops being a drive: it is removed from " +
                "Explorer's navigation pane and stays as an ordinary folder with the files already downloaded.",
                "Move the drive",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        var problem = await _app.ApplySettingsAsync(settings, change);
        if (problem is null)
        {
            if (_app.Monitor.Latest is { } latest)
            {
                LoadSettings(latest);
            }

            ShowSaveResult(after.HasRemote && _app.Monitor.Latest?.Running == true ? "Saved; the drive is syncing." : "Saved.", GoodColor);
        }
        else
        {
            ShowSaveResult(problem, ProblemColor);
        }
    }

    private void ShowSaveResult(string text, Color color)
    {
        _saveResult.Text = text;
        _saveResult.ForeColor = color;
        _saveResult.MaximumSize = new Size(Math.Max(200, _settingsTab.ClientSize.Width - _save.Width - _discard.Width - 60), 0);
    }

    /// <summary>Closes for real: the app is exiting.</summary>
    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }
}
