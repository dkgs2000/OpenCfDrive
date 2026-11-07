namespace OpenCfDrive.Core.Updates;

/// <summary>A notification about an update.</summary>
/// <param name="Install">Clicking it installs the downloaded update.</param>
/// <param name="Page">Otherwise, the release page clicking it opens, if any.</param>
public sealed record UpdateNotice(string Title, string Text, bool Install, string? Page);

/// <summary>
/// Decides when the app tells the user about an update, from each status the
/// <see cref="Updater"/> reports, as Nextcloud's updater does:
/// <list type="bullet">
/// <item>a newer version is downloaded and ready: when it becomes ready, and
/// again at each start while it waits;</item>
/// <item>a newer version is out but could not be downloaded: once per version
/// while the app runs, pointing to its release page (Nextcloud shows a dialog
/// with "Get update" then).</item>
/// </list>
/// Nothing is said while checks are off, or for a copy that does not update
/// itself.
/// </summary>
public sealed class UpdateNotifier(UpdateStage initial)
{
    private UpdateStage _stage = initial;
    private Version? _toldOut;

    /// <summary>The notification for this status, or null.</summary>
    public UpdateNotice? Next(UpdateStatus status)
    {
        UpdateNotice? notice = null;
        if (status.Stage == UpdateStage.Ready && _stage != UpdateStage.Ready && status.Available is { } ready)
        {
            notice = new UpdateNotice(
                $"OpenCfDrive {GitHubReleases.Format(ready)} is ready",
                "Click here to restart OpenCfDrive and install it.",
                Install: true,
                status.Page);
        }
        else if (status.Stage == UpdateStage.Failed && status.Available is { } found && found != _toldOut)
        {
            _toldOut = found;
            notice = new UpdateNotice(
                $"OpenCfDrive {GitHubReleases.Format(found)} is available",
                status.Page is null
                    ? "It could not be downloaded; OpenCfDrive's window says why."
                    : "It could not be downloaded. Click here to open its release page and install it from there.",
                Install: false,
                status.Page);
        }

        // A check that finds the same download leaves Ready only while it asks.
        if (status.Stage is not UpdateStage.Checking)
        {
            _stage = status.Stage;
        }

        return notice;
    }
}
