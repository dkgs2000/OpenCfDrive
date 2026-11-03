using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.Updates;

public enum UpdateStage
{
    /// <summary>This copy does not update itself (see the detail).</summary>
    Unavailable,
    NotChecked,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>A newer version's installer is downloaded and checked; installing it restarts the app.</summary>
    Ready,
    Failed,
}

/// <param name="Detail">One sentence for the app to show.</param>
/// <param name="Available">The newer version, while there is one.</param>
/// <param name="Page">Its release page, for installing by hand.</param>
/// <param name="CheckedAt">When GitHub last answered.</param>
public sealed record UpdateStatus(
    UpdateStage Stage,
    string Detail,
    Version? Available = null,
    string? Page = null,
    DateTimeOffset? CheckedAt = null);

/// <summary>
/// Keeps OpenCfDrive up to date from the repository's GitHub releases, the way
/// Nextcloud's desktop client updates itself on Windows (src/gui/updater/
/// ocupdater.cpp): it checks 3 s after the app starts and then every 10 hours
/// (unless automatic checks are off), downloads a newer version's installer
/// in the background, and says it is ready; installing it runs the installer,
/// which replaces the app, and opens the app again. On the next start it tells
/// whether the install worked and removes the installer.
/// </summary>
/// <remarks>
/// GitHub lists a SHA-256 for every file attached to a release, and the
/// download must match it. That comes over HTTPS from GitHub too, so it
/// catches a damaged or swapped download, not a compromised repository.
/// </remarks>
public sealed class Updater : IDisposable
{
    /// <summary>Nextcloud's UpdaterScheduler checks 3 s after starting...</summary>
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(3);

    /// <summary>...and then every updateCheckInterval, 10 hours by default.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(10);

    /// <summary>Nextcloud's OCUpdater gives the update server 30 s to answer.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string? _repository;
    private readonly string _folder;
    private readonly Func<bool> _automatic;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _stopping = new();
    private UpdateStatus _status;
    private Task? _check;

    /// <param name="current">The running version.</param>
    /// <param name="repository">owner/name on GitHub, or null for a build that does not update itself.</param>
    /// <param name="folder">Where the installer and the state of an update are kept.</param>
    /// <param name="automatic">Whether to check on the schedule (the user's setting, read at each check).</param>
    /// <param name="unavailable">Why this copy cannot update itself even with a repository, or null.</param>
    public Updater(
        Version current,
        string? repository,
        string folder,
        Func<bool> automatic,
        string? unavailable = null,
        HttpMessageHandler? handler = null,
        TimeProvider? time = null)
    {
        Current = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        _repository = string.IsNullOrWhiteSpace(repository) ? null : repository.Trim();
        _folder = folder;
        _automatic = automatic;
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenCfDrive/{GitHubReleases.Format(Current)}");
        _timer = _time.CreateTimer(_ => Check(manual: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _status = _repository is null
            ? new UpdateStatus(UpdateStage.Unavailable, "This build does not update itself: only release builds know where their releases are.")
            : unavailable is not null
                ? new UpdateStatus(UpdateStage.Unavailable, unavailable)
                : new UpdateStatus(UpdateStage.NotChecked, "Not checked yet.");
    }

    /// <summary>%LOCALAPPDATA%\OpenCfDrive\updates.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "updates");

    /// <summary>The running version (three numbers).</summary>
    public Version Current { get; }

    /// <summary>The installer's log of the last install.</summary>
    public string InstallLog => Path.Combine(_folder, "install.log");

    public UpdateStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Raised on a thread pool thread whenever <see cref="Status"/> changes.</summary>
    public event Action<UpdateStatus>? Changed;

    private string StatePath => Path.Combine(_folder, "update.json");

    /// <summary>
    /// What a previous run left (Nextcloud's handleStartup): an installer
    /// that is now installed, by this updater or by hand, is removed; one
    /// that is not yet installed is ready again, with a note if installing it
    /// failed. Then the checks start.
    /// </summary>
    public void Start()
    {
        if (Status.Stage == UpdateStage.Unavailable)
        {
            return;
        }

        var pending = LoadPending();
        if (pending is not null)
        {
            var target = GitHubReleases.ParseTag(pending.Version);
            if (target is null || target <= Current || !File.Exists(pending.Installer))
            {
                RemoveDownloads(keep: null);
                if (pending.Attempted && target == Current)
                {
                    Set(new UpdateStatus(UpdateStage.UpToDate, $"Updated to OpenCfDrive {GitHubReleases.Format(Current)}."));
                }
            }
            else
            {
                if (pending.Attempted)
                {
                    SavePending(pending with { Attempted = false });
                }

                Set(new UpdateStatus(
                    UpdateStage.Ready,
                    pending.Attempted
                        ? $"OpenCfDrive {pending.Version} did not install (the installer's log: {InstallLog}). Try again, or install it from its release page."
                        : ReadyText(target),
                    target,
                    pending.Page));
            }
        }

        _timer.Change(FirstCheckAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Asks GitHub now, whatever the setting, and downloads a newer version.</summary>
    public Task CheckNowAsync() => Check(manual: true);

    /// <summary>
    /// The downloaded installer to run, marked as tried so the next start can
    /// tell whether it worked; null if none is ready.
    /// </summary>
    public string? BeginInstall()
    {
        var pending = LoadPending();
        if (pending is null || !File.Exists(pending.Installer) ||
            GitHubReleases.ParseTag(pending.Version) is not { } target || target <= Current)
        {
            return null;
        }

        SavePending(pending with { Attempted = true });
        return pending.Installer;
    }

    /// <summary>
    /// Installs and then starts <paramref name="relaunch"/> again, from a
    /// PowerShell that outlives the app (Nextcloud's slotStartInstaller):
    /// the installer closes the running app and replaces its files, and a
    /// setup run without its full interface does not open the app afterwards.
    /// /passive shows only a progress bar; | Out-Null makes PowerShell wait
    /// for the setup, a Windows program, to finish.
    /// </summary>
    public static ProcessStartInfo InstallCommand(string installer, string log, string relaunch)
    {
        static string Quoted(string path) => "'" + path.Replace("'", "''") + "'";

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command" })
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(
            $"& {{ & {Quoted(installer)} /passive /norestart /log {Quoted(log)} | Out-Null; & {Quoted(relaunch)} }}");
        return start;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _stopping.Cancel();
        }

        _timer.Dispose();
        _http.Dispose();
    }

    private Task Check(bool manual)
    {
        // On the schedule: only if the user wants it, and not while a
        // download waits to be installed (as Nextcloud's backgroundCheckForUpdate).
        var scheduled = !manual && (Status.Stage == UpdateStage.Ready || !Automatic());
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested || _status.Stage == UpdateStage.Unavailable)
            {
                return Task.CompletedTask;
            }

            if (_check is { IsCompleted: false })
            {
                return _check;
            }

            if (scheduled)
            {
                _timer.Change(CheckInterval, Timeout.InfiniteTimeSpan);
                return Task.CompletedTask;
            }

            _check = Task.Run(RunCheckAsync);
            return _check;
        }
    }

    private bool Automatic()
    {
        try
        {
            return _automatic();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return true; // the default, as for a missing setting
        }
    }

    private async Task RunCheckAsync()
    {
        var before = Status;
        Version? found = null;
        string? page = null;
        try
        {
            Set(before with { Stage = UpdateStage.Checking, Detail = "Asking GitHub for the latest version..." });

            AppRelease? release;
            using (var answer = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                answer.CancelAfter(AnswerTimeout);
                try
                {
                    release = await GitHubReleases.LatestAsync(_http, _repository!, answer.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
                {
                    throw new TimeoutException($"GitHub did not answer within {AnswerTimeout.TotalSeconds:0} s");
                }
            }

            var checkedAt = _time.GetUtcNow();
            if (release is null || release.Version <= Current)
            {
                // Nothing newer: an older download is of no use any more.
                RemoveDownloads(keep: null);
                Set(new UpdateStatus(
                    UpdateStage.UpToDate,
                    $"OpenCfDrive {GitHubReleases.Format(Current)} is the latest version.",
                    CheckedAt: checkedAt));
                return;
            }

            (found, page) = (release.Version, release.Page);
            var version = GitHubReleases.Format(found);
            if (release.Installer is not { } installer)
            {
                throw new InvalidDataException($"its release has no {GitHubReleases.InstallerName(found)}");
            }

            if (installer.Sha256 is null)
            {
                throw new InvalidDataException("GitHub lists no checksum for its installer");
            }

            var target = Path.Combine(_folder, installer.Name);
            if (LoadPending() is not { } pending || pending.Version != version || !File.Exists(pending.Installer))
            {
                await DownloadAsync(installer, target, found, page).ConfigureAwait(false);
                RemoveDownloads(keep: target);
                SavePending(new Pending(version, target, page, Attempted: false));
            }

            Set(new UpdateStatus(UpdateStage.Ready, ReadyText(found), found, page, checkedAt));
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException or InvalidDataException
                                       or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            if (before.Stage == UpdateStage.Ready && found is null)
            {
                Set(before); // the download is still there to install
            }
            else
            {
                var reason = ex.Message.TrimEnd('.');
                Set(found is null
                    ? new UpdateStatus(UpdateStage.Failed, $"Could not check for updates: {reason}.")
                    : new UpdateStatus(
                        UpdateStage.Failed,
                        $"OpenCfDrive {GitHubReleases.Format(found)} is out, but could not be downloaded: {reason}. Install it from its release page.",
                        found,
                        page));
            }
        }
        finally
        {
            lock (_gate)
            {
                if (!_stopping.IsCancellationRequested)
                {
                    _timer.Change(CheckInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>Downloads to a side file, checks size and SHA-256, then puts it in place.</summary>
    private async Task DownloadAsync(ReleaseAsset installer, string target, Version version, string page)
    {
        Directory.CreateDirectory(_folder);
        var partial = target + ".download";
        var name = $"OpenCfDrive {GitHubReleases.Format(version)}";
        var totalMb = installer.Size / (1024 * 1024);
        try
        {
            Set(new UpdateStatus(UpdateStage.Downloading, $"Downloading {name}...", version, page));
            using var response = await _http.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead, _stopping.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(_stopping.Token).ConfigureAwait(false))
            await using (var file = File.Create(partial))
            {
                var buffer = new byte[81920];
                var reported = -1L;
                int read;
                while ((read = await source.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), _stopping.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (done / (1024 * 1024) is var megabytes && megabytes != reported)
                    {
                        reported = megabytes;
                        Set(new UpdateStatus(UpdateStage.Downloading, $"Downloading {name}: {megabytes} of {totalMb} MB...", version, page));
                    }
                }
            }

            if (done != installer.Size)
            {
                throw new InvalidDataException($"the download has {done:N0} bytes, not the {installer.Size:N0} GitHub lists");
            }

            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), installer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("the download does not match the checksum GitHub lists for it");
            }

            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private static string ReadyText(Version version) =>
        $"OpenCfDrive {GitHubReleases.Format(version)} is downloaded. Restart OpenCfDrive to install it.";

    private void Set(UpdateStatus status)
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested || _status == status)
            {
                return;
            }

            _status = status;
        }

        Changed?.Invoke(status);
    }

    /// <summary>Removes downloaded installers (and the state of an update) except <paramref name="keep"/>.</summary>
    private void RemoveDownloads(string? keep)
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_folder))
        {
            if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use; it goes with the next clean-up.
            }
        }
    }

    /// <summary>A downloaded update, kept across restarts (Nextcloud keeps the same in its config).</summary>
    /// <param name="Attempted">Its installer was started; the next start tells whether it worked.</param>
    private sealed record Pending(string Version, string Installer, string Page, bool Attempted);

    private Pending? LoadPending()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<Pending>(File.ReadAllText(StatePath), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SavePending(Pending pending)
    {
        Directory.CreateDirectory(_folder);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(pending, Json));
        File.Move(temp, StatePath, overwrite: true);
    }
}
