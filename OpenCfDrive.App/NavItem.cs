using System.Drawing.Drawing2D;

namespace OpenCfDrive.App;

/// <summary>
/// A page in the window's navigation pane, drawn as Windows 11's Settings
/// draws its own: an icon and a name, a soft highlight under the pointer and
/// on the chosen page, and an accent bar beside the chosen one. It is a radio
/// button underneath, so the arrow keys move between pages and screen
/// readers announce it as a tab.
/// </summary>
internal sealed class NavItem : RadioButton
{
    private readonly string _glyph;
    private bool _hover;

    public NavItem(string text, string glyph)
    {
        _glyph = glyph;
        Text = text;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.PageTab;
        AutoSize = true;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 2);
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);
    }

    /// <summary>The font of the icons, set by the window (it follows the window's DPI).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Font? GlyphFont { get; set; }

    /// <summary>One line of text, with room around it to point at.</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        var icon = Font.Height * 4 / 3;
        return new Size((Font.Height * 2) + icon + text.Width, Font.Height * 5 / 2);
    }

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(eventargs);
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(eventargs);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckedChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Page);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var radius = LogicalToDeviceUnits(4);
        if (Checked || _hover)
        {
            using var highlight = new SolidBrush(Checked ? Theme.Selected : Theme.Hover);
            using var path = Card.RoundRectangle(bounds, radius);
            g.FillPath(highlight, path);
        }

        if (Checked)
        {
            var bar = new RectangleF(0, Height * 0.3f, LogicalToDeviceUnits(3), Height * 0.4f);
            using var accent = new SolidBrush(SystemInformation.HighContrast ? SystemColors.HighlightText : Theme.Accent);
            using var pill = Card.RoundRectangle(bar, bar.Width / 2);
            g.FillPath(accent, pill);
        }

        var color = SystemInformation.HighContrast && Checked ? SystemColors.HighlightText : SystemColors.ControlText;
        var icon = new Rectangle(Font.Height * 3 / 4, 0, Font.Height * 4 / 3, Height);
        if (GlyphFont is { } glyphs)
        {
            TextRenderer.DrawText(g, _glyph, glyphs, icon, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        var text = new Rectangle(icon.Right + (Font.Height * 3 / 4), 0, Math.Max(0, Width - icon.Right - Font.Height), Height);
        TextRenderer.DrawText(g, Text, Font, text, color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(SystemColors.ControlText, LogicalToDeviceUnits(2)) { Alignment = PenAlignment.Inset };
            using var outline = Card.RoundRectangle(new RectangleF(1, 1, Width - 2, Height - 2), radius);
            g.DrawPath(focus, outline);
        }
    }
}
