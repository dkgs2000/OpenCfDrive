using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.Control;

/// <summary>
/// How the app reaches a running service: a named pipe per drive that only
/// the same user can open. A request is one line ("status", "stop", "sync",
/// "sync-scheduled"),
/// the answer one line of JSON; "watch" is answered with a line at every
/// change, as long as the connection stays open.
/// </summary>
public static class ServiceControl
{
    public const string StatusRequest = "status";
    public const string StopRequest = "stop";

    /// <summary>Look at the server now; answered once that sync run has finished.</summary>
    public const string SyncRequest = "sync";

    /// <summary>
    /// A sync run as the interval starts one: only the folders that are due
    /// are asked about (see FolderPollSchedule), where "sync" asks about all.
    /// For scripts and tests; answered once the run has finished.
    /// </summary>
    public const string ScheduledSyncRequest = "sync-scheduled";

    /// <summary>A <see cref="ServicePulse"/> line now and at every change, until either side leaves.</summary>
    public const string WatchRequest = "watch";

    /// <summary>
    /// How long to give <see cref="RequestSyncAsync"/>: a little more than the
    /// service lets a sync run take before it answers anyway, so that its own
    /// answer comes first.
    /// </summary>
    public static readonly TimeSpan SyncRequestTimeout = ServiceControlServer.SyncTimeout + TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a sync request waits for the service to take its connection.
    /// A running service takes it at once, so no service is known long before
    /// the time a sync run is given.
    /// </summary>
    private static readonly TimeSpan SyncConnectTimeout = TimeSpan.FromSeconds(5);

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The drive's pipe: "opencfdrive-&lt;hash of the folder&gt;", like its state database.</summary>
    public static string PipeName(string syncRoot) =>
        Path.GetFileNameWithoutExtension(SyncStatePaths.DatabasePath(syncRoot));

    /// <summary>The single-instance mutex the service holds for this drive while it runs.</summary>
    public static string InstanceMutexName(string syncRoot) => "Local\\" + PipeName(syncRoot);

    /// <summary>Whether a service runs for this drive (owns its mutex); asks it nothing, so it is instant.</summary>
    public static bool IsRunning(string syncRoot)
    {
        if (!Mutex.TryOpenExisting(InstanceMutexName(syncRoot), out var mutex))
        {
            return false;
        }

        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.Zero))
                {
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                // Its owner ended without releasing it; now this thread owns it.
            }

            mutex.ReleaseMutex();
            return false;
        }
    }

    /// <summary>The service's status, or null when none answers in time.</summary>
    public static async Task<ServiceStatus?> GetStatusAsync(
        string syncRoot, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var answer = await SendAsync(PipeName(syncRoot), StatusRequest, timeout, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ServiceStatus>(answer, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Asks the service to stop cleanly; true if it agreed.</summary>
    public static async Task<bool> RequestStopAsync(
        string syncRoot, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        await SendAsync(PipeName(syncRoot), StopRequest, timeout, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Asks the service to look at the server now, and waits for that sync
    /// run; true once it has finished. False when no service answers, the run
    /// failed, the service stopped meanwhile, or the run took longer than
    /// <paramref name="timeout"/> (within <see cref="SyncRequestTimeout"/>
    /// the service answers either way). A run that finished says nothing
    /// about the server: a drive that could not reach it is offline, as its
    /// status says.
    /// </summary>
    public static async Task<bool> RequestSyncAsync(
        string syncRoot, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var answer = await SendAsync(PipeName(syncRoot), SyncRequest, timeout, cancellationToken, SyncConnectTimeout).ConfigureAwait(false);
        if (answer is null)
        {
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(answer);
            return parsed.RootElement.ValueKind == JsonValueKind.Object &&
                   parsed.RootElement.TryGetProperty("synced", out var synced) &&
                   synced.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Watches the service: <paramref name="pulse"/> gets what it says at
    /// once, then each change, on the thread that read it. Returns once the
    /// service stops or is gone, or <paramref name="cancellationToken"/> is
    /// cancelled; true if any pulse came, false if none answered, or one too
    /// old to know the request.
    /// </summary>
    public static async Task<bool> WatchAsync(
        string syncRoot, Action<ServicePulse> pulse, TimeSpan connectTimeout, CancellationToken cancellationToken = default)
    {
        var any = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", PipeName(syncRoot), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connecting.CancelAfter(connectTimeout);
                await pipe.ConnectAsync(connecting.Token).ConfigureAwait(false);
            }

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(WatchRequest.AsMemory(), cancellationToken).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                ServicePulse? next;
                try
                {
                    next = JsonSerializer.Deserialize<ServicePulse>(line, Json);
                }
                catch (JsonException)
                {
                    next = null;
                }

                // A service that does not know the request answers with an
                // error, which has no revision.
                if (next is not { Revision: > 0 })
                {
                    break;
                }

                any = true;
                pulse(next);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            // Gone, or never there.
        }

        return any;
    }

    /// <param name="timeout">For all of it: connecting, asking and the answer.</param>
    /// <param name="connectTimeout">
    /// For connecting alone, when the answer may take long: without a
    /// service, connecting goes on trying for as long as it is given.
    /// </param>
    internal static async Task<string?> SendAsync(
        string pipeName, string request, TimeSpan timeout, CancellationToken cancellationToken, TimeSpan? connectTimeout = null)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(timer.Token))
            {
                connecting.CancelAfter(connectTimeout ?? timeout);
                await pipe.ConnectAsync(connecting.Token).ConfigureAwait(false);
            }

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(request.AsMemory(), timer.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            return await reader.ReadLineAsync(timer.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }
}
