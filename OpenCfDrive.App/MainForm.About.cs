using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.App;

/// <summary>
/// The About page: what OpenCfDrive is, its version and updates, where the
/// project lives, and the credits and license, as an app's About page has
/// them. What the sync service and rclone report is on the Troubleshooting
/// page.
/// </summary>
internal sealed partial class MainForm
{
    private readonly PictureBox _aboutIcon = new() { SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 2, 16, 0) };
    private readonly Label _version = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false };
    private readonly Label _update = Theme.Text();
    private readonly Button _checkUpdates = Theme.Button("Check for updates");
    private readonly Button _installUpdate = Theme.Button("Restart and update");
    private readonly LinkLabel _releasePage = new() { Text = "Release page", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 4, 0, 4) };
    private readonly Label _rcloneCredit = Theme.Text();

    private Panel BuildAboutPage()
    {
        var (page, column) = Page();
        var project = AppVersion.ProjectPage;

        _checkUpdates.Click += async (_, _) => await _app.CheckForUpdatesAsync();
        _installUpdate.Click += async (_, _) => await _app.InstallUpdateAsync();
        _releasePage.LinkClicked += (_, _) =>
        {
            if (_app.UpdateStatus.Page is { Length: > 0 } release)
            {
                _app.Open(release);
            }
        };

        // What the app is, and where it lives.
        _version.Font = _titleFont;
        _version.Text = $"OpenCfDrive {GitHubReleases.Format(_app.CurrentVersion)}";
        var tagline = Theme.Text("Any rclone remote, as a cloud drive in Windows Explorer.", Theme.Subtle);
        tagline.Margin = new Padding(0, 0, 0, 4);
        var app = new Card(2) { Padding = new Padding(20, 16, 20, 14) };
        app.AddRow(_aboutIcon, _version);
        app.SetRowSpan(_aboutIcon, 3);
        app.AddRow(null, tagline);
        app.AddRow(null, Links(
            ("GitHub project", project),
            ("Release notes", project + "/releases"),
            ("User guide", project + "/blob/main/docs/user-guide.md")));

        // The version's updates: checked and installed here, as browsers do.
        var updates = new Card();
        updates.AddRow(_update);
        updates.AddRow(Theme.ButtonRow(_checkUpdates, _installUpdate, _releasePage));

        // What it is built on, from THIRD-PARTY-NOTICES.txt.
        var credits = new Card(2);
        credits.AddRow(Theme.Caption("rclone"), _rcloneCredit);
        credits.AddRow(Theme.Caption(".NET"), Theme.Text("The runtime and Windows Forms, by Microsoft. MIT License."));
        credits.AddRow(Theme.Caption("SQLite"), Theme.Text(
            "Keeps track of the drive's files. Public domain; used through Microsoft.Data.Sqlite (MIT License) " +
            "and SQLitePCLRaw (Apache License 2.0)."));
        credits.AddRow(Theme.Caption("C#/WinRT"), Theme.Text(
            "Registers the drive with Explorer and shows notifications. MIT License; the Windows SDK projections " +
            "under the Windows SDK license."));
        credits.AddIndented(Links(("Third-party notices", Installed("THIRD-PARTY-NOTICES.txt", project + "/blob/main/installer/THIRD-PARTY-NOTICES.txt"))));

        // The license, opened from the copy the installer puts beside the app.
        var license = "MIT License";
        var legal = new LinkLabel
        {
            Text = $"OpenCfDrive is released under the {license} and comes with no warranty. Copyright © 2026 dkgs2000.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Subtle,
            Margin = new Padding(2, 14, 0, 0),
            UseMnemonic = false,
        };
        legal.LinkArea = new LinkArea(legal.Text.IndexOf(license, StringComparison.Ordinal), license.Length);
        legal.LinkClicked += (_, _) => _app.Open(Installed("LICENSE.txt", project + "/blob/main/LICENSE"));

        Add(column, app, Heading("Updates"), updates, Heading("Credits"), credits, legal);
        _aboutPage = page;
        return page;
    }

    /// <summary>Links side by side, each opening its page.</summary>
    private WrapRow Links(params (string Text, string Target)[] links)
    {
        var row = new WrapRow { Margin = new Padding(0, 2, 0, 0) };
        foreach (var (text, target) in links)
        {
            var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 3, 20, 3), UseMnemonic = false };
            link.LinkClicked += (_, _) => _app.Open(target);
            row.Controls.Add(link);
        }

        return row;
    }

    /// <summary>A file the installer puts beside the app; a build that isn't installed has the project's copy.</summary>
    private static string Installed(string name, string online)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : online;
    }

    private void ShowAbout(DriveSnapshot snapshot)
    {
        // The rclone in use, for a bug report: the running one, else the one found.
        var version = snapshot.Status?.Rclone?.Version ?? snapshot.Rclone?.Version;
        _rcloneCredit.Text =
            $"Talks to your storage{(string.IsNullOrEmpty(version) ? "" : $"; this PC runs {version}")}. A separate project " +
            "under the MIT License: OpenCfDrive runs it, but isn't part of it or endorsed by it.";
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
        _releasePage.Text = status.Available is { } available ? $"What's new in {GitHubReleases.Format(available)}" : "Release page";
        _releasePage.Visible = status.Page is { Length: > 0 } && status.Stage is UpdateStage.Ready or UpdateStage.Failed;
        _autoUpdate.Enabled = status.Stage != UpdateStage.Unavailable;
        ShowUpdateBar(status);
    }
}
