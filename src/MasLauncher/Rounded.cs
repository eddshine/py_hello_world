using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MasLauncher;

/// <summary>Geometry shared by the flat controls that have rounded corners.</summary>
internal static class RoundedGeometry
{
    /// <summary>A closed rectangle with its four corners replaced by arcs of <paramref name="radius"/>.</summary>
    public static GraphicsPath Path(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var r = Math.Min(Math.Max(radius, 0), Math.Min(bounds.Width, bounds.Height) / 2);
        if (r <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var d = r * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The first opaque colour behind a control, used to mask rounded corners.</summary>
    public static Color Behind(Control control, Color fallback)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent.BackColor != Color.Transparent)
                return parent.BackColor;
        return fallback;
    }

    /// <summary>Mixes <paramref name="b"/> into <paramref name="a"/>; 0 keeps a, 1 gives b.</summary>
    public static Color Mix(Color a, Color b, float amount)
    {
        var t = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }
}

/// <summary>
/// Flat button with rounded corners. WinForms paints square buttons, so the whole surface is
/// drawn here: rounded fill, a 1px border, then the (possibly two-line) text. The colours still
/// come from the normal button properties, so callers do not change.
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _hover;
    private bool _pressed;

    public RoundedButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw, true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
    }

    /// <summary>Corner radius in pixels.</summary>
    public int Radius { get; set; } = 4;

    private Color BorderColor => FlatAppearance.BorderColor.IsEmpty
        ? RoundedGeometry.Mix(BackColor, Color.White, 0.16f)
        : FlatAppearance.BorderColor;

    private Color HoverColor => FlatAppearance.MouseOverBackColor.IsEmpty
        ? RoundedGeometry.Mix(BackColor, Color.White, 0.09f)
        : FlatAppearance.MouseOverBackColor;

    private Color PressedColor => FlatAppearance.MouseDownBackColor.IsEmpty
        ? RoundedGeometry.Mix(BackColor, Color.Black, 0.14f)
        : FlatAppearance.MouseDownBackColor;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(RoundedGeometry.Behind(this, Color.FromArgb(23, 23, 26)));

        var bounds = new Rectangle(0, 0, Math.Max(Width - 1, 1), Math.Max(Height - 1, 1));
        var fill = !Enabled ? BackColor : _pressed ? PressedColor : _hover ? HoverColor : BackColor;
        var border = Enabled ? BorderColor : RoundedGeometry.Mix(BorderColor, BackColor, 0.5f);
        var fore = Enabled ? ForeColor : RoundedGeometry.Mix(ForeColor, BackColor, 0.55f);

        using (var path = RoundedGeometry.Path(bounds, Radius))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using (var pen = new Pen(border)) g.DrawPath(pen, path);
        }

        DrawText(g, fore);

        if (Focused && ShowFocusCues)
        {
            var inner = Rectangle.Inflate(bounds, -4, -4);
            if (inner.Width > 6 && inner.Height > 6)
            {
                using var path = RoundedGeometry.Path(inner, Math.Max(Radius - 2, 1));
                using var pen = new Pen(RoundedGeometry.Mix(fore, BackColor, 0.35f)) { DashStyle = DashStyle.Dot };
                g.DrawPath(pen, path);
            }
        }
    }

    private void DrawText(Graphics g, Color fore)
    {
        var lines = Text.Split('\n');
        if (lines.Length == 0) return;

        var heights = new int[lines.Length];
        var total = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            heights[i] = TextRenderer.MeasureText(g, lines[i], Font).Height;
            total += heights[i];
        }

        var left = Math.Max(Padding.Left, 8);
        var right = Math.Max(Padding.Right, 8);
        var area = new Rectangle(
            left,
            Math.Max((Height - total) / 2, 1),
            Math.Max(Width - left - right, 1),
            total);

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft =>
                TextFormatFlags.Left,
            ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight =>
                TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter,
        };

        for (var i = 0; i < lines.Length; i++)
        {
            TextRenderer.DrawText(g, lines[i], Font, new Rectangle(area.X, area.Y, area.Width, heights[i]), fore, flags);
            area.Y += heights[i];
        }
    }
}

/// <summary>
/// Panel with rounded corners. The panel itself stays square, because child controls with a
/// transparent background repaint the parent over their whole rectangle and would square the
/// corners off again. Instead the four corner wedges are repainted in the colour of the surface
/// behind the panel, which gives an antialiased radius without clipping anything.
/// </summary>
internal sealed class RoundedPanel : Panel
{
    public RoundedPanel()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>Corner radius in pixels.</summary>
    public int Radius { get; set; } = 6;

    /// <summary>The colour of the surface the panel sits on, painted into the corners.</summary>
    public Color CornerColor { get; set; } = Color.FromArgb(23, 23, 26);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var r = Math.Min(Radius, Math.Min(Width, Height) / 2);
        if (r <= 0) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var d = r * 2;
        MaskCorner(g, new PointF(0, 0), new RectangleF(0, 0, d, d), 270, -90, new PointF(r, 0));
        MaskCorner(g, new PointF(Width, 0), new RectangleF(Width - d, 0, d, d), 270, 90, new PointF(Width - r, 0));
        MaskCorner(g, new PointF(Width, Height), new RectangleF(Width - d, Height - d, d, d), 0, 90, new PointF(Width, Height - r));
        MaskCorner(g, new PointF(0, Height), new RectangleF(0, Height - d, d, d), 180, -90, new PointF(0, Height - r));
    }

    /// <summary>Fills the wedge between a corner and the arc tangent to both of its edges.</summary>
    private void MaskCorner(Graphics g, PointF corner, RectangleF ellipse, float startAngle, float sweep, PointF arcStart)
    {
        using var path = new GraphicsPath();
        path.AddLine(corner, arcStart);
        path.AddArc(ellipse, startAngle, sweep);
        path.CloseFigure();

        using var brush = new SolidBrush(CornerColor);
        g.FillPath(brush, path);
    }
}
