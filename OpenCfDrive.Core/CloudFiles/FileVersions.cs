using System.Globalization;

namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// Whether two versions of a file are the same, by rclone's own rule: the
/// size first, then the content hash where both have one, else the time. A
/// hash is kept as "type:value" (md5:5d41...), as Nextcloud keeps its
/// checksums with their type.
/// </summary>
public static class FileVersions
{
    /// <summary>Whether two hashes say the same content; null when either is unknown or they are of different types.</summary>
    public static bool? SameHash(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return null;
        }

        var colonA = a.IndexOf(':');
        var colonB = b.IndexOf(':');
        if (colonA <= 0 || colonB <= 0 ||
            !string.Equals(a[..colonA], b[..colonB], StringComparison.OrdinalIgnoreCase) ||
            colonA == a.Length - 1 || colonB == b.Length - 1)
        {
            return null;
        }

        return string.Equals(a[(colonA + 1)..], b[(colonB + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the server's version is not the one recorded. The hash wins
    /// over the time: a server that rewrote the same content (a re-upload, a
    /// copy, a touch) has not changed it, and an edit that kept the size and
    /// time has. Times come from the same server on both sides, so they are
    /// compared to within a second (the store keeps them to 100 ns).
    /// </summary>
    /// <param name="knownModifiedUtc">The recorded time, as the store keeps it (ISO 8601).</param>
    public static bool Differs(long knownSize, string? knownModifiedUtc, string? knownHash, long size, DateTimeOffset? modified, string? hash)
    {
        if (knownSize != size)
        {
            return true;
        }

        if (SameHash(knownHash, hash) is { } same)
        {
            return !same;
        }

        return modified is { } serverTime &&
               DateTimeOffset.TryParse(knownModifiedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var knownTime) &&
               Math.Abs((serverTime - knownTime).TotalSeconds) >= 1;
    }
}
