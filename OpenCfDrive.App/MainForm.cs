using System.Globalization;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;

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
    private readonly Label _rcUrl = ValueLabel();
    private readonly Label _rcloneState = ValueLabel();
    private readonly Label _rcloneRemote = ValueLabel();
    private readonly Button _start = ActionButton("Start");
    private readonly Button _stop = ActionButton("Stop");
    private readonly Button _restart = ActionButton("Restart");
    private readonly Button _viewLog = ActionButton("View log");
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
    private readonly Label _rcTest = Hint("");
    private readonly TextBox _rcdCommand = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly CheckBox _signIn = new() { Text = "Start OpenCfDrive when I sign in to Windows", AutoSize = true };
    private readonly Label _overrides = new() { AutoSize = true, ForeColor = BusyColor, Visible = false };
    private readonly Label _settingsFile = Hint("");
    private readonly Button _save = ActionButton("Save");
    private readonly Button _discard = ActionButton("Discard changes");
    private readonly Label _saveResult = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 0, 0) };

    private DriveSnapshot? _snapshot;
    private string? _busy;
    private (string Folder, string Remote, string RcUrl, bool SignIn)? _loaded;
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
        ClientSize = new Size(760, 620);
        MinimumSize = new Size(620, 540);

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

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), WrapContents = false };
        buttons.Controls.AddRange([_start, _stop, _restart, _viewLog]);

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
            ("Remote control", _rcUrl),
            ("Status", _rcloneState),
            ("Remote", _rcloneRemote));

        _activity.Columns.Add("Time", 70);
        _activity.Columns.Add("What happened", 400);
        _activity.SizeChanged += (_, _) => FitActivityColumns();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Group("Sync service", serviceBox), 0, 0);
        layout.Controls.Add(Group("rclone", rclone), 0, 1);
        layout.Controls.Add(Group("Recent activity", _activity, fill: true), 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private void BuildSettingsTab()
    {
        var browse = ActionButton("Browse...");
        browse.Click += (_, _) => BrowseForFolder();
        var refresh = ActionButton("Refresh list");
        refresh.Click += async (_, _) => await TestRcloneAsync(fillRemotes: true);
        var test = ActionButton("Test");
        test.Click += async (_, _) => await TestRcloneAsync(fillRemotes: true);
        _save.Click += async (_, _) => await SaveAsync();
        _discard.Click += (_, _) =>
        {
            if (_snapshot is not null)
            {
                LoadSettings(_snapshot);
            }
        };

        foreach (var input in new Control[] { _folderBox, _remoteBox, _rcUrlBox })
        {
            input.TextChanged += (_, _) => ClearSaveResult();
        }

        _rcUrlBox.TextChanged += (_, _) => ShowRcdCommand();
        _signIn.CheckedChanged += (_, _) => ClearSaveResult();
        _rcTest.Visible = false; // until Test is pressed
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

        Row("Drive folder", _folderBox, browse);
        Note(Hint("Where the drive appears in Explorer. Moving it needs a new or empty folder; the old one stays " +
                  "as an ordinary folder with the files already downloaded."));
        Row("rclone remote", _remoteBox, refresh);
        Note(Hint("A remote in rclone's config, optionally with a path: myremote: or myremote:backup. " +
                  "Another remote needs a new or empty drive folder."));
        Row("rclone remote control", _rcUrlBox, test);
        Note(_rcTest);
        Note(Hint("Where rclone rcd listens. OpenCfDrive does not start rclone; run it yourself, for example:"));
        Note(_rcdCommand);
        Note(_signIn);
        Note(_overrides);
        Note(_settingsFile);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, WrapContents = false };
        buttons.Controls.AddRange([_save, _discard, _saveResult]);

        _settingsTab.Controls.Add(grid);
        _settingsTab.Controls.Add(buttons);
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

        if (!Edited)
        {
            LoadSettings(snapshot);
        }

        ShowOverrides(snapshot.Settings);
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
            ServiceState.Starting => BusyColor,
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
        _rcUrl.Text = snapshot.Status?.RcUrl ?? snapshot.Settings.RcUrl.Value;
        if (rclone is null)
        {
            _rcloneState.Text = "Checking...";
            _rcloneState.ForeColor = SystemColors.ControlText;
            _rcloneRemote.Text = "-";
            return;
        }

        _rcloneState.Text = rclone.Usable
            ? $"Answers (rclone {rclone.Version})"
            : rclone.Problem ?? "Does not answer.";
        _rcloneState.ForeColor = rclone.Usable ? GoodColor : ProblemColor;

        var remote = snapshot.Status?.Remote ?? snapshot.Settings.Remote.Value;
        var name = RcloneProbe.RemoteName(remote);
        if (!snapshot.Settings.HasRemote && snapshot.Status is null)
        {
            _rcloneRemote.Text = "None chosen yet";
            _rcloneRemote.ForeColor = SystemColors.ControlText;
        }
        else if (rclone.Remotes is null || name is null)
        {
            _rcloneRemote.Text = remote;
            _rcloneRemote.ForeColor = SystemColors.ControlText;
        }
        else if (rclone.Remotes.Contains(name))
        {
            _rcloneRemote.Text = $"\"{name}\" is in rclone's config";
            _rcloneRemote.ForeColor = GoodColor;
        }
        else
        {
            _rcloneRemote.Text = rclone.Remotes.Count == 0
                ? $"\"{name}\" is not in rclone's config, which has no remotes"
                : $"\"{name}\" is not in rclone's config, which has: {string.Join(", ", rclone.Remotes)}";
            _rcloneRemote.ForeColor = ProblemColor;
        }
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
            _signIn.Checked = SignInStartup.IsEnabled;
            if (snapshot.Rclone?.Remotes is { } remotes)
            {
                FillRemotes(remotes);
            }

            ShowRcdCommand();
            _loaded = FieldValues;
        }
        finally
        {
            _loading = false;
        }
    }

    private (string Folder, string Remote, string RcUrl, bool SignIn) FieldValues =>
        (_folderBox.Text, _remoteBox.Text, _rcUrlBox.Text, _signIn.Checked);

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

    private async Task TestRcloneAsync(bool fillRemotes)
    {
        _rcTest.Visible = true;
        _rcTest.Text = "Asking rclone...";
        _rcTest.ForeColor = SystemColors.GrayText;
        var result = await RcloneProbe.CheckAsync(_rcUrlBox.Text.Trim());
        _rcTest.Text = result.Usable
            ? $"rclone {result.Version} answers, with {result.Remotes!.Count} remote(s) in its config."
            : result.Problem;
        _rcTest.ForeColor = result.Usable ? GoodColor : ProblemColor;
        if (fillRemotes && result.Remotes is { } remotes)
        {
            FillRemotes(remotes);
        }
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

        string? invalid = null;
        if (!Path.IsPathFullyQualified(root))
        {
            invalid = "The drive folder must be a full path, like C:\\Users\\you\\OpenCfDrive.";
        }
        else if (remote.Length == 0 || remote.StartsWith(':'))
        {
            invalid = "Choose an rclone remote, like myremote:.";
        }
        else if (!Uri.TryCreate(rcUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
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
            StartAtSignIn = _signIn.Checked,
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
