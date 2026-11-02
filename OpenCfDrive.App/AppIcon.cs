namespace OpenCfDrive.App;

/// <summary>The OpenCfDrive icon (assets\opencfdrive.ico), at the sizes Windows asks for.</summary>
internal static class AppIcon
{
    /// <summary>For the notification area and title bar.</summary>
    public static Icon Small { get; } = Load(SystemInformation.SmallIconSize);

    /// <summary>For Alt+Tab and the window's header.</summary>
    public static Icon Large { get; } = Load(SystemInformation.IconSize);

    private static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("OpenCfDrive.ico")!;
        return new Icon(stream, size);
    }
}
