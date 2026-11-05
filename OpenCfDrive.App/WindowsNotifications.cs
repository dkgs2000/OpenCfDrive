using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace OpenCfDrive.App;

/// <summary>
/// Windows notifications: shown at the corner of the screen and kept in the
/// notification centre, under OpenCfDrive's name and icon. An app that is not
/// packaged has no identity of its own for them, so it registers one for this
/// user (an AppUserModelID under HKCU\Software\Classes\AppUserModelId, the way
/// the Windows Community Toolkit does) and runs under it. Clicking one runs
/// what was given with it while the app runs.
/// </summary>
internal sealed class WindowsNotifications
{
    public const string AppId = "OpenCfDrive.App";
    private const string RegistryKey = @"Software\Classes\AppUserModelId\" + AppId;

    private readonly ToastNotifier? _notifier;

    // Kept so that a click still finds its handler.
    private readonly Queue<ToastNotification> _shown = new();

    private WindowsNotifications(ToastNotifier? notifier) => _notifier = notifier;

    /// <summary>Registers the app's identity and runs under it; unavailable (Show returns false) if Windows refuses.</summary>
    public static WindowsNotifications Create()
    {
        try
        {
            var icon = WriteIcon();
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
            {
                key.SetValue("DisplayName", "OpenCfDrive");
                key.SetValue("IconUri", icon);
            }

            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
            return new WindowsNotifications(ToastNotificationManager.CreateToastNotifier(AppId));
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return new WindowsNotifications(null);
        }
    }

    /// <summary>What the uninstaller removes: the identity registered for notifications.</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Harmless if it stays: nothing uses it without the app.
        }
    }

    /// <summary>Shows a notification; false if Windows notifications are unavailable.</summary>
    public bool Show(string title, string text, Action? clicked)
    {
        if (_notifier is null)
        {
            return false;
        }

        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(text)}</text>" +
                "</binding></visual></toast>");
            var toast = new ToastNotification(xml);
            if (clicked is not null)
            {
                toast.Activated += (_, _) => clicked();
            }

            _notifier.Show(toast);
            _shown.Enqueue(toast);
            while (_shown.Count > 20)
            {
                _shown.Dequeue();
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    /// <summary>The app's icon as a picture file the notification can show.</summary>
    private static string WriteIcon()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenCfDrive");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "notification-icon.png");
        try
        {
            using var bitmap = AppIcon.Large.ToBitmap();
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) && File.Exists(path))
        {
            // Being read by Windows for a notification: the one there will do.
        }

        return path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
