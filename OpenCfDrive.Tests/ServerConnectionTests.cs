using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Tests.TestSupport;

namespace OpenCfDrive.Tests;

/// <summary>
/// The connection state copied from Nextcloud's desktop client: offline after
/// a request that cannot reach the server, checked again on use and on its
/// schedule (62 s, then one more interval per failed check, at most eight),
/// online again when a check reaches the server. Time is driven by hand.
/// </summary>
public sealed class ServerConnectionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ManualTime _time = new();
    private readonly List<TaskCompletionSource> _checks = [];
    private readonly SemaphoreSlim _checkStarted = new(0);
    private readonly ServerConnection _server;

    public ServerConnectionTests()
    {
        _server = new ServerConnection(_ =>
        {
            var check = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_checks)
            {
                _checks.Add(check);
            }

            _checkStarted.Release();
            return check.Task;
        }, TimeSpan.FromSeconds(10), time: _time);
    }

    private async Task<TaskCompletionSource> NextCheckAsync()
    {
        Assert.True(await _checkStarted.WaitAsync(Timeout), "No check was started.");
        lock (_checks)
        {
            return _checks[^1];
        }
    }

    private Task<bool> NextChangeAsync()
    {
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<bool> handler = null!;
        handler = online =>
        {
            _server.Changed -= handler;
            changed.TrySetResult(online);
        };
        _server.Changed += handler;
        return changed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AFailedRequest_GoesOffline_AndACheckThatAnswers_GoesOnline()
    {
        Assert.True(_server.IsOnline);

        var offline = NextChangeAsync();
        _server.Failed("no answer in time");
        Assert.False(await offline);
        Assert.False(_server.IsOnline);
        Assert.Equal("no answer in time", _server.OfflineReason);

        var online = NextChangeAsync();
        _server.CheckNow();
        (await NextCheckAsync()).SetResult();
        Assert.True(await online);
        Assert.True(_server.IsOnline);
        Assert.Null(_server.OfflineReason);
    }

    [Fact]
    public async Task OnlyOneCheckRuns_AtATime()
    {
        _server.Failed("down");

        _server.CheckNow();
        var first = await NextCheckAsync();
        _server.CheckNow();
        _server.CheckNow();
        Assert.False(await _checkStarted.WaitAsync(TimeSpan.Zero), "A second check started while one was running.");

        // Once it has failed (it schedules the next one), another may start.
        var scheduled = _time.NextSchedule();
        first.SetException(new TimeoutException());
        await scheduled.WaitAsync(Timeout);
        var online = NextChangeAsync();
        _server.CheckNow();
        (await NextCheckAsync()).SetResult();
        Assert.True(await online);
    }

    [Fact]
    public async Task ChecksFollowNextcloudsSchedule()
    {
        _server.Failed("down");
        Assert.Equal<TimeSpan?>(TimeSpan.FromSeconds(62), _time.DueIn);

        for (var failed = 1; failed <= 10; failed++)
        {
            _time.Fire();
            var scheduled = _time.NextSchedule();
            (await NextCheckAsync()).SetException(new TimeoutException());
            await scheduled.WaitAsync(Timeout);
            Assert.Equal<TimeSpan?>(TimeSpan.FromSeconds(62 * Math.Min(failed, 8)), _time.DueIn);
        }

        var online = NextChangeAsync();
        _time.Fire();
        (await NextCheckAsync()).SetResult();
        Assert.True(await online);
        Assert.Null(_time.DueIn);
    }

    [Fact]
    public void Online_NothingIsChecked()
    {
        _server.CheckNow();
        _server.Succeeded();

        Assert.True(_server.IsOnline);
        Assert.Null(_time.DueIn);
        Assert.Equal(0, _checkStarted.CurrentCount);
    }

    [Theory]
    [InlineData("couldn't list files: Propfind \"http://127.0.0.1:5000/\": dial tcp 127.0.0.1:5000: connectex: No connection could be made because the target machine actively refused it.", true)]
    [InlineData("couldn't list files: Get \"https://example.invalid/\": dial tcp: lookup example.invalid: no such host", true)]
    [InlineData("operations/list failed (500): {\"error\":\"context deadline exceeded\"}", true)]
    [InlineData("operations/list failed (500): {\"error\":\"directory not found\"}", false)]
    [InlineData("operations/stat failed (500): {\"error\":\"401 Unauthorized\"}", false)]
    public void TellsAServerThatCannotBeReached_FromOneThatAnswered(string message, bool unreachable)
    {
        Assert.Equal(unreachable, ServerConnection.IsUnreachable(new InvalidOperationException(message)));
    }

    [Fact]
    public void TimeoutsAndNoConnectionToRclone_AreUnreachable()
    {
        Assert.True(ServerConnection.IsUnreachable(new TaskCanceledException()));
        Assert.True(ServerConnection.IsUnreachable(new HttpRequestException("No connection could be made")));
        Assert.False(ServerConnection.IsUnreachable(
            new HttpRequestException("Not found", null, System.Net.HttpStatusCode.NotFound)));
    }

    public void Dispose()
    {
        _server.Dispose();
        _checkStarted.Dispose();
    }
}
