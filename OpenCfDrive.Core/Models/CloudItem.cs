namespace OpenCfDrive.Core.Models;

public sealed class CloudItem
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? ModifiedTime { get; init; }
    public string? MimeType { get; init; }
    public string? Hash { get; init; }

    /// <summary>Path relative to the sync root, always using '/' separators.</summary>
    public string RelativePath => Path.TrimStart('/');
}
