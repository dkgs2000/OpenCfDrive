using System.Net.Http.Headers;
using System.Text;

namespace OpenCfDrive.Core.Services;

/// <summary>
/// Where an rclone remote control listens, and the login it wants. The rclone
/// OpenCfDrive runs itself has a random login for each run; one the user runs
/// with --rc-no-auth has none.
/// </summary>
public sealed record RcloneEndpoint(string Url, string? User = null, string? Password = null)
{
    public string BaseUrl => Url.TrimEnd('/');

    /// <summary>An HTTP client that sends the login with every request.</summary>
    public HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        if (User is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")));
        }

        return client;
    }

    /// <summary>Never the password, even in a log line.</summary>
    public override string ToString() => User is null ? BaseUrl : $"{BaseUrl} (with a login)";
}
