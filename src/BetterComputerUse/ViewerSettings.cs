using System.Drawing.Drawing2D;

namespace BetterComputerUse;

internal sealed class ViewerSettings : Form
{
    private readonly ViewerSwitch collapse = new() { Text = "Collapse on click outside" };
    private readonly List<(ResumeMode Mode, ViewerActionButton Button)> choices = [];
    internal Action<bool>? CollapseChanged;
    internal Action<ResumeMode>? ResumeChanged;

    internal ViewerSettings()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(29, 34, 40);
        ForeColor = Color.White; Font = new Font("Segoe UI", 9); ClientSize = new Size(350, 126);
        Padding = new Padding(12); KeyPreview = true;
        collapse.SetBounds(12, 8, 326, 36); Controls.Add(collapse);
        collapse.CheckedChanged += (_, _) => CollapseChanged?.Invoke(collapse.Checked);
        Controls.Add(new Label { Text = "Resume automation", Bounds = new Rectangle(12, 52, 326, 22),
            ForeColor = Color.FromArgb(181, 192, 202) });
        int x = 12;
        foreach (var (mode, label, width) in new[] { (ResumeMode.OnClose, "On close", 72),
            (ResumeMode.OnClickOutside, "On close or click outside", 166), (ResumeMode.Manual, "Manually", 76) })
        {
            var button = new ViewerActionButton { Text = label, AccessibleName = label,
                ForeColor = Color.White, Bounds = new Rectangle(x, 78, width, 34), Cursor = Cursors.Hand };
            button.Click += (_, _) => { SelectMode(mode); ResumeChanged?.Invoke(mode); };
            choices.Add((mode, button)); Controls.Add(button); x += width + 6;
        }
        Deactivate += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
    }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); int corner = 2;
        _ = Native.DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
    }
    private void SelectMode(ResumeMode mode)
    {
        foreach (var choice in choices)
        {
            choice.Button.BackColor = choice.Mode == mode ? Color.FromArgb(19, 125, 112) : Color.FromArgb(48, 56, 64);
            choice.Button.AccessibleDescription = choice.Mode == mode ? "Selected" : "Not selected";
        }
    }
    internal void Toggle(Control anchor, bool autoCollapse, ResumeMode mode)
    {
        if (Visible) { Hide(); return; }
        collapse.Checked = autoCollapse; SelectMode(mode);
        var point = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + 6));
        var work = Screen.FromControl(anchor).WorkingArea;
        Location = new Point(Math.Clamp(point.X - Width, work.Left, Math.Max(work.Left, work.Right - Width)),
            Math.Clamp(point.Y, work.Top, Math.Max(work.Top, work.Bottom - Height)));
        Show(anchor.FindForm());
    }
}

internal sealed class ViewerSwitch : CheckBox
{
    internal ViewerSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.CheckButton;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f, h = 22 * scale, w = 40 * scale;
        float x = Width - w - 1, y = (Height - h) / 2;
        using var path = new GraphicsPath();
        path.AddArc(x, y, h, h, 90, 180); path.AddArc(x + w - h, y, h, h, 270, 180); path.CloseFigure();
        using var track = new SolidBrush(Checked ? Color.FromArgb(19, 145, 126) : Color.FromArgb(79, 87, 96));
        using var thumb = new SolidBrush(Color.FromArgb(239, 245, 244));
        e.Graphics.FillPath(track, path);
        float inset = 3 * scale;
        e.Graphics.FillEllipse(thumb, Checked ? x + w - h + inset : x + inset, y + inset, h - 2 * inset, h - 2 * inset);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, (int)x - 8, Height), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
}
