using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Tests.TestSupport;
using Windows.Storage;
using Windows.Storage.Provider;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// Whether a per-file sync state can be shown in Explorer by an unpackaged
/// provider: the state is registered as a custom item property definition,
/// set with StorageProviderItemProperties.SetAsync, and the platform reports
/// it back (system property System.StorageProviderCustomStates). Pinned here:
/// it works for a placeholder and for a plain file, it survives reconnecting
/// the sync root, and setting an empty list clears it again.
/// </summary>
public class ItemStateContractTest(ITestOutputHelper output)
{
    private const string StateValue = "waiting to upload";

    private static readonly string Icon =
        Path.Combine(Environment.SystemDirectory, "imageres.dll") + ",0";

    [SkippableFact]
    public void AStateSetOnAPlaceholderAndAPlainFile_IsReportedByTheShell_SurvivesReconnect_AndClears()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-item-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "drive"));
        var syncRoot = LongPath(Path.Combine(root, "drive"));
        var folder = Path.Combine(syncRoot, "dir");
        var now = DateTimeOffset.UtcNow;
        var log = new ConcurrentQueue<string>();
        var listings = new ConcurrentQueue<string>();

        Task<SyncRootConnection.DirectoryListing?> ListDirectory(string directory, string localDirectory, CancellationToken cancellationToken)
        {
            var first = listings.Count(d => d == directory) == 0;
            listings.Enqueue(directory);
            IReadOnlyList<PlaceholderEntry> entries = directory switch
            {
                "" when first => [new PlaceholderEntry("dir", true, 0, now, "dir/")],
                "dir" when first => [new PlaceholderEntry("a.txt", false, 1, now, "dir/a.txt")],
                _ => [],
            };
            return Task.FromResult<SyncRootConnection.DirectoryListing?>(
                new SyncRootConnection.DirectoryListing(entries, Listed: true));
        }

        using var server = new OneByteServer(() => Task.CompletedTask);
        var store = new SqliteMetadataStore(Path.Combine(root, "state.db"));
        store.Initialize();
        using var serve = new RcloneServeClient(server.Url, "e2e:");
        var registrar = new SyncRootRegistrar();
        var placeholders = new CloudPlaceholderService();
        Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);
        using var connection = new SyncRootConnection(
            store,
            serve,
            log.Enqueue,
            listDirectory: ListDirectory,
            listingReuseWindow: TimeSpan.Zero);

        try
        {
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");
            Assert.True(placeholders.EnableOnDemandPopulation(syncRoot));

            Assert.Equal(["a.txt"], Names(Run($"dir /b \"{folder}\"").Output));
            var placeholder = Path.Combine(folder, "a.txt");
            Assert.True(placeholders.QueryState(placeholder).IsPlaceholder, "a.txt is not a placeholder.");

            // A plain file that was never a placeholder, written directly.
            var plain = Path.Combine(folder, "plain.txt");
            File.WriteAllText(plain, "plain");
            Assert.False(placeholders.QueryState(plain).IsPlaceholder);

            SetState(placeholder, StateValue);
            SetState(plain, "couldn't sync");

            var placeholderStates = CustomStates(placeholder);
            var plainStates = CustomStates(plain);
            output.WriteLine($"placeholder: {DescribeStates(placeholderStates)}; shell: {ShellReport(placeholder)}");
            output.WriteLine($"plain file:  {DescribeStates(plainStates)}; shell: {ShellReport(plain)}");

            Assert.True(Contains(placeholderStates, StateValue), "The placeholder's state is not in what the platform holds.");
            Assert.True(Contains(plainStates, "couldn't sync"), "The plain file's state is not in what the platform holds.");
            Assert.True(Contains(placeholderStates, Icon), "The placeholder's icon is not in what the platform holds.");

            // Reconnecting (a restart of the provider) must not lose it.
            connection.Disconnect();
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed after reconnecting.");
            var afterReconnect = CustomStates(placeholder);
            output.WriteLine($"after reconnect: {DescribeStates(afterReconnect)}; shell: {ShellReport(placeholder)}");
            Assert.True(Contains(afterReconnect, StateValue), "The state was lost when the sync root reconnected.");

            // An empty list removes the state. The platform keeps an envelope
            // naming the sync root, so "cleared" means no state entries rather
            // than no bytes at all.
            SetState(placeholder);
            var cleared = CustomStates(placeholder);
            output.WriteLine($"after clearing: {DescribeStates(cleared)}; shell: {ShellReport(placeholder)}");
            Assert.False(Contains(cleared, StateValue), "The state was not cleared.");
            Assert.False(Contains(cleared, Icon), "The state's icon was not cleared.");
        }
        finally
        {
            foreach (var line in log)
            {
                output.WriteLine(line);
            }

            connection.Disconnect();
            registrar.Unregister(syncRoot);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>Sets the one state property, or clears it when no value is given.</summary>
    private static void SetState(string path, string? value = null)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        IEnumerable<StorageProviderItemProperty> properties = value is null
            ? []
            : [new StorageProviderItemProperty
              {
                  Id = SyncRootRegistrar.StatePropertyId,
                  Value = value,
                  IconResource = Icon,
              }];
        StorageProviderItemProperties.SetAsync(file, properties).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The serialized custom states the platform holds for an item.</summary>
    private static byte[] CustomStates(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var properties = file.Properties
            .RetrievePropertiesAsync(["System.StorageProviderCustomStates"])
            .AsTask().GetAwaiter().GetResult();
        return properties.TryGetValue("System.StorageProviderCustomStates", out var value) && value is byte[] bytes
            ? bytes
            : [];
    }

    private static string DescribeStates(byte[] states) =>
        states.Length == 0 ? "no custom states" : $"{states.Length} bytes: {Convert.ToHexString(states)}";

    /// <summary>Whether a string is stored in the serialized property store (UTF-16LE, as PROPVARIANT strings are).</summary>
    private static bool Contains(byte[] states, string value)
    {
        var utf16 = Encoding.Unicode.GetBytes(value);
        var utf8 = Encoding.UTF8.GetBytes(value);
        return states.AsSpan().IndexOf(utf16) >= 0 || states.AsSpan().IndexOf(utf8) >= 0;
    }

    private static string[] Names(string listing) =>
        listing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).Order(StringComparer.Ordinal).ToArray();

    /// <summary>What Explorer is told about the item, through the same shell a user sees.</summary>
    private string ShellReport(string path)
    {
        var parent = Path.GetDirectoryName(path)!.Replace("'", "''");
        var name = Path.GetFileName(path).Replace("'", "''");
        var script =
            "$i = (New-Object -ComObject Shell.Application).Namespace('" + parent + "').ParseName('" + name + "'); " +
            "$v = $i.ExtendedProperty('System.StorageProviderCustomStates'); " +
            "if ($null -eq $v) { 'custom=null' } else { 'type=' + $v.GetType().FullName + ' len=' + @($v).Count + ' value=' + (@($v) -join ',') }";
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-Command", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "The shell query did not finish.");
        return (stdout.Result.Trim() + " " + stderr.Result.Trim()).Trim();
    }

    /// <summary>From another process: the provider's own I/O raises no callbacks.</summary>
    private (int ExitCode, string Output) Run(string command)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"{command}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(70)), $"'{command}' did not finish.");
        output.WriteLine($"{command}: exit {process.ExitCode} {stderr.Result.Trim()}");
        return (process.ExitCode, stdout.Result);
    }

    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);
}
