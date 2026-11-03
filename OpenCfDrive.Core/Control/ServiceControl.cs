using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.Control;

/// <summary>
/// How the app reaches a running service: a named pipe per drive that only
/// the same user can open. A request is one line ("status" or "stop"), the
/// answer one line of JSON.
/// </summary>
public static class ServiceControl
{
    public const string StatusRequest = "status";
    public const string StopRequest = "stop";

    /// <summary>Look at the server now; answered once that sync run has finished.</summary>
    public const string SyncRequest = "sync";

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

    internal static async Task<string?> SendAsync(
        string pipeName, string request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timer.Token).ConfigureAwait(false);

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
