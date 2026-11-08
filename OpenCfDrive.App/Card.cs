using System.Drawing.Drawing2D;

namespace OpenCfDrive.App;

/// <summary>
/// A white card with a thin border and round corners, laid out as a table:
/// a section of the window, as Windows 11's Settings groups its settings.
/// It sizes itself to what it holds, at the width its parent gives it.
/// </summary>
internal sealed class Card : TableLayoutPanel
{
    /// <param name="columns">How many columns it has.</param>
    /// <param name="stretch">
    /// The column that takes the width left over; by default the second of
    /// two or three (after the captions), or the only one.
    /// </param>
    public Card(int columns = 1, int? stretch = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Theme.HoldWhileBuilding(this);
        BackColor = Theme.Card;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Padding = Theme.CardPadding;
        Margin = Theme.CardMargin;
        ColumnCount = columns;
        var stretching = stretch ?? (columns == 1 ? 0 : 1);
        for (var column = 0; column < columns; column++)
        {
            ColumnStyles.Add(column == stretching ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        }
    }

    /// <summary>Another row: <paramref name="cells"/> from the first column on; the last one spans what is left.</summary>
    public void AddRow(params Control?[] cells)
    {
        var row = RowCount;
        RowCount = row + 1;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var column = 0; column < cells.Length; column++)
        {
            if (cells[column] is not { } cell)
            {
                continue;
            }

            Controls.Add(cell, column, row);
            if (column == cells.Length - 1 && column < ColumnCount - 1)
            {
                SetColumnSpan(cell, ColumnCount - column);
            }
        }
    }

    /// <summary>The buttons beside the inputs, one under the other, as wide as the widest.</summary>
    public void SameWidthButtons()
    {
        foreach (Control control in Controls)
        {
            if (control is Button && GetColumn(control) == ColumnCount - 1 && ColumnCount > 1)
            {
                control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            }
        }
    }

    /// <summary>A row that leaves the caption column empty.</summary>
    public void AddIndented(Control cell) => AddRow(null, cell);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The corners outside the curve show what is behind the card.
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Page);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var radius = LogicalToDeviceUnits(6);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = RoundRectangle(bounds, radius);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(Theme.CardBorder, 1);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }

    public static GraphicsPath RoundRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
