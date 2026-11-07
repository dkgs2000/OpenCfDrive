using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCfDrive.Core.Control;

namespace OpenCfDrive.App;

/// <summary>
/// The notification area icon for each <see cref="DriveIndicator"/>, as
/// OneDrive and Nextcloud show theirs: the plain icon when all is synced,
/// otherwise a small badge in its corner. Blue arrows while syncing, an
/// amber "!" when files could not be synced, a red cross when nothing
/// syncs; greyed out with a line through when offline and with a pause sign
/// when the service is stopped (and plain grey before a remote is chosen).
/// Each is drawn once, at the size Windows asks for.
/// </summary>
internal sealed class TrayIcons(Size size) : IDisposable
{
    // Drawn this many times larger, then averaged down: smooth edges at 16 px.
    private const int Oversample = 4;

    private static readonly Color SyncBlue = Color.FromArgb(0x00, 0x78, 0xD4);
    private static readonly Color WarningAmber = Color.FromArgb(0xF5, 0x9E, 0x0B);
    private static readonly Color ProblemRed = Color.FromArgb(0xC4, 0x2B, 0x1C);
    private static readonly Color InactiveGrey = Color.FromArgb(0x6B, 0x72, 0x80);
    private static readonly Color Glyph = Color.White;
    private static readonly Color DarkGlyph = Color.FromArgb(0x1F, 0x1F, 0x1F);

    private readonly Dictionary<DriveIndicator, Icon> _icons = [];

    public Icon For(DriveIndicator indicator)
    {
        if (!_icons.TryGetValue(indicator, out var icon))
        {
            using var bitmap = Draw(indicator, size);
            icon = ToIcon(bitmap);
            _icons[indicator] = icon;
        }

        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }

    /// <summary>The picture for <paramref name="indicator"/>, at <paramref name="size"/>.</summary>
    internal static Bitmap Draw(DriveIndicator indicator, Size size)
    {
        // Through a bitmap: drawing an icon straight onto one loses its alpha.
        var picture = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var icon = AppIcon.Load(size))
        using (var original = icon.ToBitmap())
        using (var graphics = Graphics.FromImage(picture))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(original, new Rectangle(Point.Empty, size));
        }

        if (indicator is DriveIndicator.Offline or DriveIndicator.Paused or DriveIndicator.NotSetUp)
        {
            Grey(picture);
        }

        if (Badge(indicator) is not { } badge)
        {
            return picture;
        }

        // The badge sits in the bottom right corner, a little over half the
        // icon, with a gap cut around it so it stands apart from the cloud.
        // A whole number of pixels across, so its middle falls on the grid.
        var diameter = (float)Math.Max(8, (int)Math.Round(size.Width * 9 / 16.0));
        var gap = Math.Max(1f, size.Width / 16f);
        var center = new PointF(size.Width - diameter / 2f, size.Height - diameter / 2f);
        CutOut(picture, center, diameter / 2f + gap);

        using var layer = DrawBadge(badge, size, center, diameter / 2f);
        using (var graphics = Graphics.FromImage(picture))
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.DrawImageUnscaled(layer, 0, 0);
        }

        return picture;
    }

    private enum BadgeKind
    {
        Sync,
        Offline,
        Pause,
        Warning,
        Problem,
    }

    private static BadgeKind? Badge(DriveIndicator indicator) => indicator switch
    {
        DriveIndicator.Syncing => BadgeKind.Sync,
        DriveIndicator.Offline => BadgeKind.Offline,
        DriveIndicator.Paused => BadgeKind.Pause,
        DriveIndicator.Warning => BadgeKind.Warning,
        DriveIndicator.Problem => BadgeKind.Problem,
        _ => null,
    };

    /// <summary>The badge alone on a transparent picture of the icon's size.</summary>
    private static Bitmap DrawBadge(BadgeKind kind, Size size, PointF center, float radius)
    {
        using var big = new Bitmap(size.Width * Oversample, size.Height * Oversample, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(big))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.ScaleTransform(Oversample, Oversample);
            graphics.TranslateTransform(center.X, center.Y);

            var fill = kind switch
            {
                BadgeKind.Sync => SyncBlue,
                BadgeKind.Warning => WarningAmber,
                BadgeKind.Problem => ProblemRed,
                _ => InactiveGrey,
            };
            using (var brush = new SolidBrush(fill))
            {
                graphics.FillEllipse(brush, -radius, -radius, 2 * radius, 2 * radius);
            }

            var r = radius;
            switch (kind)
            {
                case BadgeKind.Sync:
                    DrawSyncArrows(graphics, r);
                    break;
                case BadgeKind.Offline:
                    using (var pen = RoundPen(Glyph, 0.26f * r))
                    {
                        graphics.DrawLine(pen, -0.45f * r, 0.45f * r, 0.45f * r, -0.45f * r);
                    }

                    break;
                case BadgeKind.Pause:
                    using (var brush = new SolidBrush(Glyph))
                    {
                        // On whole pixels, so the bars stay sharp at 16 px.
                        float Snap(float offset) => MathF.Round(center.X + offset) - center.X;
                        var width = MathF.Max(1f, MathF.Round(0.26f * r));
                        var apart = MathF.Max(0.5f, 0.2f * r);
                        var (left, right) = (Snap(-apart - width), Snap(apart));
                        var (top, bottom) = (Snap(-0.5f * r), Snap(0.5f * r));
                        graphics.FillRectangle(brush, left, top, width, bottom - top);
                        graphics.FillRectangle(brush, right, top, width, bottom - top);
                    }

                    break;
                case BadgeKind.Warning:
                    using (var pen = RoundPen(DarkGlyph, 0.28f * r))
                    using (var brush = new SolidBrush(DarkGlyph))
                    {
                        graphics.DrawLine(pen, 0, -0.52f * r, 0, 0.1f * r);
                        var dot = 0.17f * r;
                        graphics.FillEllipse(brush, -dot, 0.48f * r - dot, 2 * dot, 2 * dot);
                    }

                    break;
                case BadgeKind.Problem:
                    using (var pen = RoundPen(Glyph, 0.26f * r))
                    {
                        var arm = 0.38f * r;
                        graphics.DrawLine(pen, -arm, -arm, arm, arm);
                        graphics.DrawLine(pen, -arm, arm, arm, -arm);
                    }

                    break;
            }
        }

        return Downsample(big, Oversample);
    }

    /// <summary>
    /// An arrow going round, three quarters of a circle: at 16 px two
    /// arrows run together into a blob.
    /// </summary>
    private static void DrawSyncArrows(Graphics graphics, float r)
    {
        var arc = 0.48f * r;
        using var pen = new Pen(Glyph, 0.28f * r) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        using var brush = new SolidBrush(Glyph);

        // Angles run clockwise from the right, as GDI+ draws them: from the
        // top right, round the bottom, to the top left; open at the top.
        const float start = 305f, sweep = 265f;
        graphics.DrawArc(pen, new RectangleF(-arc, -arc, 2 * arc, 2 * arc), start, sweep);

        // The head at the arc's end, pointing on round it.
        var end = (start + sweep) * MathF.PI / 180f;
        var tip = new PointF(arc * MathF.Cos(end), arc * MathF.Sin(end));
        var along = new PointF(-MathF.Sin(end), MathF.Cos(end)); // clockwise
        var outward = new PointF(MathF.Cos(end), MathF.Sin(end));
        var (forward, backward, half) = (0.42f * r, 0.04f * r, 0.42f * r);
        graphics.FillPolygon(brush,
        [
            new PointF(tip.X + along.X * forward, tip.Y + along.Y * forward),
            new PointF(tip.X - along.X * backward + outward.X * half, tip.Y - along.Y * backward + outward.Y * half),
            new PointF(tip.X - along.X * backward - outward.X * half, tip.Y - along.Y * backward - outward.Y * half),
        ]);
    }

    private static Pen RoundPen(Color color, float width) =>
        new(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };

    /// <summary>Greys the picture out and fades it a little, keeping its shape.</summary>
    private static void Grey(Bitmap picture) => EachPixel(picture, (_, _, pixel) =>
    {
        var (a, r, g, b) = (pixel >> 24 & 0xFF, pixel >> 16 & 0xFF, pixel >> 8 & 0xFF, pixel & 0xFF);
        var luma = (uint)(0.299 * r + 0.587 * g + 0.114 * b);
        var grey = Math.Min(255u, 60 + luma * 3 / 4);
        return a * 85 / 100 << 24 | grey << 16 | grey << 8 | grey;
    });

    /// <summary>Makes a disc of the picture transparent, with a soft edge.</summary>
    private static void CutOut(Bitmap picture, PointF center, float radius) => EachPixel(picture, (x, y, pixel) =>
    {
        var distance = MathF.Sqrt(MathF.Pow(x + 0.5f - center.X, 2) + MathF.Pow(y + 0.5f - center.Y, 2));
        var keep = Math.Clamp(distance - radius + 0.5f, 0f, 1f);
        var alpha = (uint)MathF.Round((pixel >> 24) * keep);
        return alpha << 24 | (pixel & 0x00FFFFFF);
    });

    private static void EachPixel(Bitmap picture, Func<int, int, uint, uint> change)
    {
        var area = new Rectangle(0, 0, picture.Width, picture.Height);
        var data = picture.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[data.Stride / 4 * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (var y = 0; y < data.Height; y++)
            {
                for (var x = 0; x < data.Width; x++)
                {
                    var i = y * data.Stride / 4 + x;
                    pixels[i] = unchecked((int)change(x, y, unchecked((uint)pixels[i])));
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            picture.UnlockBits(data);
        }
    }

    /// <summary>Averages each <paramref name="factor"/>² block into one pixel, weighting colour by alpha.</summary>
    private static Bitmap Downsample(Bitmap big, int factor)
    {
        var small = new Bitmap(big.Width / factor, big.Height / factor, PixelFormat.Format32bppArgb);
        var source = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new int[source.Stride / 4 * source.Height];
        Marshal.Copy(source.Scan0, pixels, 0, pixels.Length);
        var stride = source.Stride / 4;
        big.UnlockBits(source);

        EachPixel(small, (x, y, _) =>
        {
            long a = 0, r = 0, g = 0, b = 0;
            for (var dy = 0; dy < factor; dy++)
            {
                for (var dx = 0; dx < factor; dx++)
                {
                    var pixel = unchecked((uint)pixels[(y * factor + dy) * stride + x * factor + dx]);
                    var alpha = pixel >> 24;
                    a += alpha;
                    r += (pixel >> 16 & 0xFF) * alpha;
                    g += (pixel >> 8 & 0xFF) * alpha;
                    b += (pixel & 0xFF) * alpha;
                }
            }

            if (a == 0)
            {
                return 0;
            }

            var count = factor * factor;
            return (uint)(a / count) << 24 | (uint)(r / a) << 16 | (uint)(g / a) << 8 | (uint)(b / a);
        });
        return small;
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
