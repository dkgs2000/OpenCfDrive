using System.Security.Cryptography;
using System.Text;
using OpenCfDrive.Core.Configuration;

namespace OpenCfDrive.App;

/// <summary>
/// One app per settings file (so per drive) in a user session: a second start
/// asks the running copy to show its window; the installer asks it to close.
/// A copy with its own settings file (OPENCFDRIVE_SETTINGS, for a test) runs
/// beside the user's.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly string Name = "Local\\OpenCfDrive.App." + Hash(DriveSettingsFile.Path);
    private static readonly string MutexName = Name;
    private static readonly string ShowEventName = Name + ".Show";
    private static readonly string QuitEventName = Name + ".Quit";

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))[..16];

    private readonly Mutex _mutex;
    private readonly List<RegisteredWaitHandle> _waits = [];

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        ShowRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        QuitRequested = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
    }

    public EventWaitHandle ShowRequested { get; }

    public EventWaitHandle QuitRequested { get; }

    /// <summary>This process's claim, or null when another copy runs.</summary>
    public static SingleInstance? TryClaim()
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
            {
                return new SingleInstance(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstance(mutex); // the last copy ended without releasing it
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Calls <paramref name="action"/> on a pool thread each time the event is set.</summary>
    public void OnSignal(EventWaitHandle signal, Action action) =>
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false));

    public static void ShowRunningCopy()
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
        {
            using (show)
            {
                show.Set();
            }
        }
    }

    /// <summary>Asks the running copy to close and waits until it has.</summary>
    public static void CloseRunningCopy(TimeSpan timeout)
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
        {
            return;
        }

        using (quit)
        {
            quit.Set();
        }

        using var mutex = new Mutex(false, MutexName);
        try
        {
            if (mutex.WaitOne(timeout))
            {
                mutex.ReleaseMutex();
            }
        }
        catch (AbandonedMutexException)
        {
            mutex.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        foreach (var wait in _waits)
        {
            wait.Unregister(null);
        }

        ShowRequested.Dispose();
        QuitRequested.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
