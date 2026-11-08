namespace OpenCfDrive.App;

/// <summary>
/// The window's look, after Windows 11's Settings and OneDrive's settings
/// window: a navigation pane and pages on the window colour, sections of
/// white cards with a thin border, a semibold type ramp. Colours come from
/// the system's where they can, so high contrast still works; every size
/// is in 96 DPI units, scaled by the window, or follows the font.
/// </summary>
internal static class Theme
{
    // Windows' own text colours for success, caution and critical.
    public static readonly Color Good = Color.FromArgb(0x0F, 0x7B, 0x0F);
    public static readonly Color Busy = Color.FromArgb(0x9D, 0x5D, 0x00);
    public static readonly Color Problem = Color.FromArgb(0xC4, 0x2B, 0x1C);

    public static Color Page => SystemColors.Control;

    public static Color Card => SystemColors.Window;

    public static Color CardBorder => SystemInformation.HighContrast ? SystemColors.WindowText : Blend(Card, SystemColors.ControlDark, 0.3f);

    public static Color Subtle => SystemColors.GrayText;

    public static Color Hover => Blend(Page, SystemColors.ControlDark, 0.12f);

    public static Color Selected => SystemInformation.HighContrast ? SystemColors.Highlight : Blend(Page, SystemColors.ControlDark, 0.2f);

    public static Color Accent => SystemColors.Highlight;

    /// <summary>The space between sections, between a heading and its card, and inside a card.</summary>
    public static readonly Padding PagePadding = new(24, 16, 24, 16);
    public static readonly Padding CardPadding = new(16, 12, 16, 12);
    public static readonly Padding HeadingMargin = new(2, 14, 0, 6);
    public static readonly Padding CardMargin = new(0, 0, 0, 4);

    public static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.R + ((to.R - from.R) * amount)),
        (int)Math.Round(from.G + ((to.G - from.G) * amount)),
        (int)Math.Round(from.B + ((to.B - from.B) * amount)));

    private static readonly string? SemiboldFamily = Installed("Segoe UI Semibold");

    /// <summary>Segoe Fluent Icons on Windows 11, its predecessor on Windows 10; both have the same code points.</summary>
    private static readonly string? IconFamily = Installed("Segoe Fluent Icons") ?? Installed("Segoe MDL2 Assets");

    private static string? Installed(string family) =>
        FontFamily.Families.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) ? family : null;

    /// <summary>A page's or a card's title: 15 pt over the usual 9.</summary>
    public static Font Title(Font body) => Semibold(body, 5f / 3);

    /// <summary>A section's heading: 10.5 pt over the usual 9.</summary>
    public static Font Heading(Font body) => Semibold(body, 7f / 6);

    private static Font Semibold(Font body, float scale) => SemiboldFamily is { } family
        ? new Font(family, body.SizeInPoints * scale, FontStyle.Regular, GraphicsUnit.Point)
        : new Font(body.FontFamily, body.SizeInPoints * scale, FontStyle.Bold, GraphicsUnit.Point);

    /// <summary>The icon font at <paramref name="scale"/> times the body size, or null without one.</summary>
    public static Font? Icons(Font body, float scale = 4f / 3) =>
        IconFamily is { } family ? new Font(family, body.SizeInPoints * scale, FontStyle.Regular, GraphicsUnit.Point) : null;

    // Glyphs of the icon font.
    public const string SyncGlyph = "";
    public const string SettingsGlyph = "";
    public const string InfoGlyph = "";

    public static Label Heading(string text, Font font) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font,
        Margin = HeadingMargin,
        UseMnemonic = false,
    };

    /// <summary>A label that wraps within its table column.</summary>
    public static Label Text(string text = "", Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        ForeColor = color ?? SystemColors.ControlText,
        Margin = new Padding(0, 3, 0, 3),
        UseMnemonic = false,
    };

    /// <summary>Grey text under a field that says what it is for.</summary>
    public static Label Hint(string text = "") => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        ForeColor = Subtle,
        Margin = new Padding(0, 2, 0, 8),
        UseMnemonic = false,
    };

    /// <summary>A field's caption, in the first column of a card.</summary>
    public static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Top,
        ForeColor = Subtle,
        Margin = new Padding(0, 3, 16, 3),
        UseMnemonic = false,
        Tag = CaptionTag,
    };

    /// <summary>The caption of a field with an input box: lined up with the box's text.</summary>
    public static Label FieldCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 4, 16, 4),
        UseMnemonic = false,
        Tag = CaptionTag,
    };

    /// <summary>Marks the captions, so a page can line up the columns of its cards.</summary>
    public static readonly object CaptionTag = new();

    /// <summary>
    /// The captions of all the cards on <paramref name="page"/> as wide as the
    /// widest, so the values and inputs of every card start at the same place.
    /// </summary>
    public static void LineUpCaptions(Control page)
    {
        var captions = Descendants(page).OfType<Label>().Where(l => l.Tag == CaptionTag).ToList();
        var widest = captions.Count == 0
            ? 0
            : captions.Max(c => TextRenderer.MeasureText(c.Text, c.Font, Size.Empty, TextFormatFlags.NoPrefix).Width + c.Padding.Horizontal);
        foreach (var caption in captions)
        {
            if (caption.MinimumSize.Width != widest)
            {
                caption.MinimumSize = new Size(widest, 0);
            }
        }
    }

    /// <summary>
    /// Holds off laying out <paramref name="root"/> and everything in it
    /// until disposed, then lays each out once, inside first: many changes
    /// at once (a new screen scale) cost one layout, not one each.
    /// </summary>
    public static IDisposable OneLayout(Control root)
    {
        var containers = Descendants(root).Prepend(root).Where(c => c.HasChildren).ToList();
        foreach (var container in containers)
        {
            container.SuspendLayout();
        }

        return new Resumer(root, containers);
    }

    /// <summary>
    /// Containers made while layout is held off (see <see cref="OneLayout"/>)
    /// hold off theirs too, until the same resume.
    /// </summary>
    [ThreadStatic]
    private static Resumer? _building;

    public static void HoldWhileBuilding(Control container)
    {
        if (_building is { } building)
        {
            container.SuspendLayout();
            building.Add(container);
        }
    }

    private sealed class Resumer : IDisposable
    {
        private readonly List<Control> _containers;
        private readonly Resumer? _outer;
        private bool _done;

        public Resumer(Control root, List<Control> containers)
        {
            _containers = containers;
            if (!root.IsHandleCreated)
            {
                _outer = _building;
                _building = this;
            }
            else
            {
                _outer = _building;
            }
        }

        public void Add(Control container) => _containers.Add(container);

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            if (_building == this)
            {
                _building = _outer;
            }

            // Made later, held inside: last in the list, resumed first.
            for (var i = _containers.Count - 1; i >= 0; i--)
            {
                _containers[i].ResumeLayout(performLayout: true);
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => Descendants(c).Prepend(c));

    public static Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(88, 0),
        Padding = new Padding(10, 3, 10, 3),
        Margin = new Padding(0, 4, 8, 4),
        UseVisualStyleBackColor = true,
    };

    /// <summary>A row of buttons that wraps onto a second line when the window is narrow.</summary>
    public static WrapRow ButtonRow(params Control[] buttons)
    {
        var row = new WrapRow { Margin = new Padding(0, 4, 0, 0) };
        row.Controls.AddRange(buttons);
        return row;
    }
}
