using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace BetterComputerUse;

internal enum ViewerGlyph { Close, Collapse, Maximize, Menu }

internal sealed class ViewerGlyphButton : Button
{
    private readonly ViewerGlyph glyph;
    private bool hovered;
    internal ViewerGlyphButton(ViewerGlyph glyph, string text, string accessibleName)
    {
        this.glyph = glyph; Text = text; AccessibleName = accessibleName;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = Color.FromArgb(29, 34, 40); ForeColor = Color.FromArgb(181, 192, 202);
        Cursor = Cursors.Hand; TabStop = true; Size = new Size(30, 30);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(hovered ? Color.FromArgb(49, 58, 67) : BackColor);
        float x = Width / 2f, y = Height / 2f, r = 4 * DeviceDpi / 96f;
        using var pen = new Pen(ForeColor, 1.2f * DeviceDpi / 96f);
        using var brush = new SolidBrush(ForeColor);
        switch (glyph)
        {
            case ViewerGlyph.Close:
                e.Graphics.DrawLine(pen, x-r, y-r, x+r, y+r); e.Graphics.DrawLine(pen, x+r, y-r, x-r, y+r); break;
            case ViewerGlyph.Collapse: e.Graphics.DrawLine(pen, x-r, y+2, x+r, y+2); break;
            case ViewerGlyph.Maximize: e.Graphics.DrawRectangle(pen, x-r, y-r, 2*r, 2*r); break;
            case ViewerGlyph.Menu:
                for (int i = -1; i <= 1; i++) e.Graphics.FillEllipse(brush, x + i*r - 1, y - 1, 2, 2);
                break;
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
}

internal sealed class ViewerActionButton : Button
{
    private bool hovered;
    internal ViewerActionButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(29, 34, 40));
        var rect = new RectangleF(0, 0, Width - 1, Height - 1);
        float d = 12 * DeviceDpi / 96f;
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right-d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right-d, rect.Bottom-d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom-d, d, d, 90, 90); path.CloseFigure();
        using var brush = new SolidBrush(hovered && Enabled ? ControlPaint.Light(BackColor, .12f) : BackColor);
        e.Graphics.FillPath(brush, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class RoundedActionPanel : FlowLayoutPanel
{
    private bool renderQueued;
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80000; return parameters; }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RenderBackground(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RenderBackground(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RenderBackground(); }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); RenderBackground(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); QueueRender(); }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.Invalidated += ChildInvalidated;
        QueueRender();
    }
    protected override void OnControlRemoved(ControlEventArgs e)
    {
        e.Control!.Invalidated -= ChildInvalidated;
        base.OnControlRemoved(e); QueueRender();
    }
    private void ChildInvalidated(object? sender, InvalidateEventArgs e) => QueueRender();
    private void QueueRender()
    {
        if (renderQueued || !IsHandleCreated || IsDisposed) return;
        renderQueued = true;
        BeginInvoke(() => { renderQueued = false; if (!IsDisposed) RenderBackground(); });
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    private void RenderBackground()
    {
        if (!IsHandleCreated || !Visible || Width < 2 || Height < 2) return;
        using var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float d = Math.Min(20 * DeviceDpi / 96f, Math.Min(Width, Height));
        using var path = new GraphicsPath();
        path.AddArc(.5f, .5f, d, d, 180, 90); path.AddArc(Width-d-1.5f, .5f, d, d, 270, 90);
        path.AddArc(Width-d-1.5f, Height-d-1.5f, d, d, 0, 90); path.AddArc(.5f, Height-d-1.5f, d, d, 90, 90); path.CloseFigure();
        using var brush = new SolidBrush(BackColor);
        graphics.FillPath(brush, path);
        // UpdateLayeredWindow publishes the whole surface, including button paint.
        foreach (Control child in Controls)
            if (child.Visible && child.Width > 0 && child.Height > 0)
                child.DrawToBitmap(bitmap, child.Bounds);
        LayeredSurface.Update(Handle, bitmap);
    }
}

// A per-pixel-alpha child surface blends into the live RDP pixels. WinForms'
// transparent BackColor only paints the parent background and leaves a dark badge.
internal sealed class ConnectionDot : Control
{
    private bool connected, paused;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Connected { get => connected; set { if (connected != value) { connected = value; RenderDot(); } } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Paused { get => paused; set { if (paused != value) { paused = value; RenderDot(); } } }
    internal ConnectionDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        AccessibleName = "Show or hide viewer controls"; AccessibleRole = AccessibleRole.PushButton;
        Cursor = Cursors.Hand; TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        { OnClick(EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80000; return parameters; }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RenderDot(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RenderDot(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RenderDot(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    private void RenderDot()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0 || !Visible) return;
        using var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var color = paused ? Color.FromArgb(246, 199, 114) : connected ? Color.FromArgb(107, 224, 174) : Color.FromArgb(172, 186, 198);
        double radius = Math.Min(Width, Height) / 2d;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double dx = (x + .5 - Width / 2d) / radius, dy = (y + .5 - Height / 2d) / radius;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double edge = Math.Clamp((.58 - distance) / .2, 0, 1);
            double core = edge * edge * (3 - 2 * edge);
            double halo = .18 * Math.Exp(-5 * distance * distance) * Math.Clamp((1 - distance) * 4, 0, 1);
            int alpha = (int)Math.Round(255 * (.92 * core + (1 - core) * halo));
            bitmap.SetPixel(x, y, Color.FromArgb(alpha, color));
        }
        LayeredSurface.Update(Handle, bitmap);
    }
}

internal static class LayeredSurface
{
    internal static void Update(nint handle, Bitmap bitmap)
    {
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), image = 0, previous = 0;
        try
        {
            image = bitmap.GetHbitmap(Color.FromArgb(0)); previous = SelectObject(memory, image);
            var size = bitmap.Size; var origin = Point.Empty;
            var blend = new AlphaBlend { ConstantAlpha = 255, Format = 1 };
            Native.Check(UpdateLayeredWindow(handle, screen, 0, ref size, memory, ref origin, 0, ref blend, 2));
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (image != 0) DeleteObject(image);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AlphaBlend { internal byte Operation, Flags, ConstantAlpha, Format; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, nint destination,
        ref Size size, nint sourceDc, ref Point source, uint colorKey, ref AlphaBlend blend, uint flags);
}

internal static class ViewerBranding
{
    internal static Icon LoadIcon()
    {
        using var stream = typeof(ViewerBranding).Assembly.GetManifestResourceStream("BetterComputerUse.app.ico")
            ?? throw new InvalidOperationException("Missing application icon.");
        using var icon = new Icon(stream); return (Icon)icon.Clone();
    }
}
