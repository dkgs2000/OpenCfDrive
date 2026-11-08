namespace OpenCfDrive.App;

/// <summary>
/// Controls side by side, going on to the next line when the width runs out:
/// a row of buttons that still fits a narrow window. It says how tall it is
/// at the width it is offered, every time it is asked (a FlowLayoutPanel in
/// a table keeps the height it had at its narrowest).
/// </summary>
internal sealed class WrapRow : Panel
{
    public WrapRow()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        Arrange(proposedSize.Width > 1 ? proposedSize.Width : int.MaxValue, place: false);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange(ClientSize.Width, place: true);
    }

    private Size Arrange(int width, bool place)
    {
        var inner = width == int.MaxValue ? int.MaxValue : Math.Max(1, width - Padding.Horizontal);
        int x = 0, y = 0, line = 0, widest = 0;
        foreach (Control control in Controls)
        {
            if (!control.Visible)
            {
                continue;
            }

            var size = control.AutoSize ? control.GetPreferredSize(Size.Empty) : control.Size;
            size = new Size(Math.Max(size.Width, control.MinimumSize.Width), Math.Max(size.Height, control.MinimumSize.Height));
            var across = size.Width + control.Margin.Horizontal;
            if (x > 0 && x + across > inner)
            {
                y += line;
                x = 0;
                line = 0;
            }

            if (place)
            {
                control.Bounds = new Rectangle(Padding.Left + x + control.Margin.Left, Padding.Top + y + control.Margin.Top, size.Width, size.Height);
            }

            x += across;
            line = Math.Max(line, size.Height + control.Margin.Vertical);
            widest = Math.Max(widest, x);
        }

        return new Size(widest + Padding.Horizontal, y + line + Padding.Vertical);
    }
}
