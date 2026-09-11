using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterComputerUse;

internal sealed class RdpWindow : Form
{
    internal readonly RdpControl Rdp = new() { Dock = DockStyle.Fill, TabStop = false };
    private readonly Panel surface = new() { BackColor = Color.FromArgb(22, 25, 29) };
    private readonly Panel header = new() { Dock = DockStyle.Top, Height = 38, BackColor = Color.FromArgb(29, 34, 40) };
    private readonly FlowLayoutPanel actions = new RoundedActionPanel() { Height = 48, Visible = false,
        BackColor = Color.FromArgb(29, 34, 40), Padding = new Padding(8), WrapContents = false };
    private readonly Label title = new() { Text = "Computer Use", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(236, 241, 245), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight,
        ForeColor = Color.FromArgb(127, 205, 188), Font = new Font("Segoe UI", 8) };
    private readonly Button collapse = new ViewerGlyphButton(ViewerGlyph.Collapse, "-", "Collapse to floating view");
    private readonly Button maximize = new ViewerGlyphButton(ViewerGlyph.Maximize, "Maximize", "Maximize or restore large view");
    private readonly Button close = new ViewerGlyphButton(ViewerGlyph.Close, "Close", "Close viewer");
    private readonly Button settingsButton = new ViewerGlyphButton(ViewerGlyph.Menu, "...", "Resume automation settings");
    private readonly ConnectionDot connectionDot = new();
    private readonly ViewerSettings settingsMenu = new();
    private bool pipHeaderRevealed;
    private readonly ToolTip tips = new();
    private readonly Size desktopSize;
    private int ExpandedHeaderUnits => desktopSize.Width < desktopSize.Height ? 124 : 38;
    private Point? previewDragStart;
    private Point previewWindowStart;
    private bool previewDragged;
    private Rectangle? restoredBounds;
    private readonly Button take = MakeButton("Take over", 110);
    private readonly Button expand = MakeButton("Expand view", 112);
    private readonly Button returnToAgent = MakeButton("Return to agent", 140);
    private readonly System.Windows.Forms.Timer outsideClick = new() { Interval = 25 };
    private readonly Icon appIcon = ViewerBranding.LoadIcon();
    private readonly NotifyIcon tray = new() { Text = "Better Computer Use" };
    private readonly ViewerPreferences preferences;
    internal event Action<bool>? ControlRequested;
    internal bool HumanControl { get; private set; }
    internal bool ViewerVisible { get; private set; }
    internal string ViewMode => !ViewerVisible ? "hidden" : expanded ? "expanded" : "pip";
    internal bool ControlsRevealed => actions.Visible;
    internal string ControlButtonText => take.Text;
    internal bool ControlButtonEnabled => take.Enabled;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal ResumeMode ResumeMode { get => preferences.Resume; set { preferences.Resume = value; preferences.Save(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool CollapseOnClickOutside { get => preferences.CollapseOnClickOutside; set { preferences.CollapseOnClickOutside = value; preferences.Save(); } }
    private bool closing, expanded, agentConnected, mouseWasDown, changingLayout, takeoverPending;
    private string? connectionStatus;
    protected override bool ShowWithoutActivation => !expanded;

    internal RdpWindow(ViewerPreferences? settings = null, Size? desktopSize = null)
    {
        preferences = settings ?? ViewerPreferences.Load();
        this.desktopSize = desktopSize ?? new Size(1920, 1080);
        Text = "Computer Use"; Icon = appIcon; tray.Icon = appIcon; tray.Visible = true;
        Font = new Font("Segoe UI", 9);
        BackColor = Color.FromArgb(62, 69, 77);
        Padding = new Padding(1);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(384, 256);
        MinimumSize = new Size(1, 1); // Aspect-aware sizing applies the minimum on one shared scale.
        Location = new Point(-20000, -20000);
        ShowInTaskbar = false;
        surface.Controls.Add(Rdp);
        Controls.Add(surface); Controls.Add(actions); Controls.Add(header); Controls.Add(connectionDot);
        header.Controls.AddRange([title, status, collapse, maximize, close, settingsButton, returnToAgent]);
        collapse.Click += (_, _) => { if (expanded) Collapse(); else SetViewer(false); };
        maximize.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) => SetViewer(false);
        tips.SetToolTip(collapse, "Return to floating view");
        tips.SetToolTip(maximize, "Maximize / restore");
        tips.SetToolTip(close, "Close view");
        tips.SetToolTip(settingsButton, "Resume automation settings");
        settingsMenu.CollapseChanged = value => CollapseOnClickOutside = value;
        settingsMenu.ResumeChanged = value => ResumeMode = value;
        settingsButton.Click += (_, _) => settingsMenu.Toggle(settingsButton, CollapseOnClickOutside, ResumeMode);
        connectionDot.Click += (_, _) => ToggleControls();
        returnToAgent.Visible = false;
        returnToAgent.Click += (_, _) => ReturnToAgent();
        take.BackColor = Color.FromArgb(19, 125, 112);
        expand.Margin = Padding.Empty;
        take.Click += (_, _) => RequestTakeover(); actions.Controls.Add(take);
        expand.Click += (_, _) => { if (expanded) Collapse(); else Expand(); }; actions.Controls.Add(expand);
        surface.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && !expanded)
            { previewDragStart = Cursor.Position; previewWindowStart = Location; previewDragged = false; }
        };
        surface.MouseMove += (_, e) =>
        {
            if (previewDragStart is not Point start || e.Button != MouseButtons.Left) return;
            var delta = new Size(Cursor.Position.X - start.X, Cursor.Position.Y - start.Y);
            if (!previewDragged && Math.Abs(delta.Width) + Math.Abs(delta.Height) < 5) return;
            previewDragged = true; surface.Capture = true; Location = previewWindowStart + delta;
        };
        surface.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            previewDragStart = null; surface.Capture = false;
            if (previewDragged) SavePosition(); else ToggleControls();
            previewDragged = false;
        };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ToggleControls(); };
        foreach (Control handle in new Control[] { header, title, status })
        {
            handle.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var start = Location;
                Native.ReleaseCapture(); Native.SendMessage(Handle, 0xa1, 2, 0);
                SavePosition();
                if (Math.Abs(Location.X - start.X) < 4 && Math.Abs(Location.Y - start.Y) < 4) ToggleControls();
            };
            handle.DoubleClick += (_, _) => { if (expanded) ToggleMaximize(); };
        }
        Resize += (_, _) => LayoutChrome();
        LayoutChrome();
        ResizeEnd += (_, _) => SavePosition();
        outsideClick.Tick += (_, _) =>
        {
            UpdateHover(Cursor.Position);
            var down = (Native.GetAsyncKeyState(1) & 0x8000) != 0;
            var foreground = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(foreground, out var pid);
            ObservePointer(down, !Bounds.Contains(Cursor.Position), foreground != 0 && pid != 0 && pid != Environment.ProcessId);
        };
        outsideClick.Start();
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) SetViewer(true); };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show floating view", null, (_, _) => SetViewer(true));
        trayMenu.Items.Add("Disconnect desktop and exit", null, (_, _) =>
        {
            // Deliberate local shutdown preserves the Windows session and applications.
            closing = true; Application.Exit();
        });
        tray.ContextMenuStrip = trayMenu;
        FormClosing += (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            SetViewer(false);
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Standard resizable app window semantics, with one custom-drawn title bar.
            // TOOLWINDOW excluded the PiP from accessibility / window discovery.
            parameters.Style |= 0x00cf0000;
            parameters.ExStyle = (parameters.ExStyle & ~0x40080) | (ViewerVisible ? 0x40000 : 0x80);
            return parameters;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int preference = 2;
        _ = Native.DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x83 && message.WParam != 0) { message.Result = 0; return; }
        if (message.Msg == 0x112 && ((long)message.WParam & 0xfff0) == 0xf030)
        {
            if (expanded) ToggleMaximize(); else Expand();
            return;
        }
        if (message.Msg == 0x112 && ((long)message.WParam & 0xfff0) == 0xf020)
        {
            if (expanded) Collapse(); else SetViewer(false);
            return;
        }
        if (message.Msg == 0x214)
        {
            var rect = Marshal.PtrToStructure<ViewerGeometry.NativeRect>(message.LParam);
            var fitted = ViewerGeometry.Resize(rect.ToRectangle(), desktopSize, expanded ? ExpandedHeaderUnits * DeviceDpi / 96 : 0,
                (int)message.WParam, Screen.FromControl(this).WorkingArea.Size,
                (expanded && ExpandedHeaderUnits == 38 ? 498 : 358) * DeviceDpi / 96, 84 * DeviceDpi / 96);
            Marshal.StructureToPtr(new ViewerGeometry.NativeRect(fitted), message.LParam, false);
            message.Result = 1; return;
        }
        base.WndProc(ref message);
        if (message.Msg == 0x84 && WindowState == FormWindowState.Normal && restoredBounds is null)
        {
            var point = PointToClient(new Point(unchecked((short)(long)message.LParam), unchecked((short)((long)message.LParam >> 16))));
            int edge = Math.Max(5, DeviceDpi * 5 / 96);
            bool left = point.X < edge, right = point.X >= ClientSize.Width - edge;
            bool top = point.Y < edge, bottom = point.Y >= ClientSize.Height - edge;
            message.Result = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 :
                left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : message.Result;
        }
    }
    private void ToggleMaximize()
    {
        if (!expanded) return;
        if (restoredBounds is Rectangle bounds) { restoredBounds = null; Bounds = bounds; }
        else { restoredBounds = Bounds; Bounds = ViewerGeometry.Fit(desktopSize, Screen.FromControl(this).WorkingArea, ExpandedHeaderUnits * DeviceDpi / 96, 1); }
    }
    private void LayoutChrome()
    {
        if (header is null) return;
        int U(int value) => value * DeviceDpi / 96;
        // Explicit bounds avoid Dock/BringToFront ordering putting the header over RDP.
        // Only PiP hover chrome is an overlay; expanded chrome reserves its own row.
        header.Dock = DockStyle.None;
        header.Visible = expanded || pipHeaderRevealed;
        bool portraitHeader = expanded && ExpandedHeaderUnits > 38;
        header.SetBounds(1, 1, ClientSize.Width - 2, U(expanded ? ExpandedHeaderUnits : 38));
        int previewTop = expanded ? header.Height + 1 : 1;
        surface.SetBounds(1, previewTop, Math.Max(1, ClientSize.Width - 2), Math.Max(1, ClientSize.Height - previewTop - 1));
        connectionDot.Visible = !expanded && !pipHeaderRevealed;
        connectionDot.SetBounds(ClientSize.Width - U(30), U(10), U(18), U(18));
        expand.Text = expanded ? "Collapse" : "Expand view";
        expand.AccessibleName = expand.Text;
        int right = header.ClientSize.Width - U(4);
        void Place(Control control, bool visible, int width)
        {
            control.Visible = visible;
            if (!visible) return;
            right -= U(width); control.SetBounds(right, U(4), U(width), U(30));
        }
        Place(close, true, 30);
        Place(maximize, expanded && !portraitHeader, 30);
        Place(collapse, expanded, 30);
        Place(settingsButton, true, 30);
        if (HumanControl) right -= U(8);
        Place(returnToAgent, HumanControl, expanded ? 138 : 115);
        if (portraitHeader && HumanControl) returnToAgent.SetBounds(U(8), U(42), Math.Max(1, ClientSize.Width - U(16)), U(32));
        title.Visible = ClientSize.Width >= U(268);
        status.Visible = !portraitHeader;
        title.SetBounds(U(12), 0, U(108), header.Height);
        int statusStart = expanded && !HumanControl ? U(388) : U(122);
        status.SetBounds(statusStart, 0, Math.Max(0, right - statusStart - U(8)), header.Height);
        bool compactActions = ClientSize.Width < U(268);
        actions.FlowDirection = compactActions ? FlowDirection.TopDown : FlowDirection.LeftToRight;
        actions.Padding = expanded && !portraitHeader ? new Padding(U(8), U(3), U(8), U(3)) : new Padding(U(8));
        actions.SetBounds(expanded && !portraitHeader ? U(128) : U(12), expanded ? (portraitHeader ? U(38) : 1) : ClientSize.Height - U(60),
            Math.Max(1, Math.Min(U(244), ClientSize.Width - U(24))), U(compactActions ? 86 : expanded ? 38 : 48));
        if (compactActions && !expanded) actions.Top = ClientSize.Height - actions.Height - U(12);
        take.Size = new Size(compactActions ? Math.Max(1, actions.Width - U(16)) : U(110), U(32));
        expand.Size = new Size(compactActions ? Math.Max(1, actions.Width - U(16)) : U(112), U(32));
        take.Margin = compactActions ? new Padding(0, 0, 0, U(6)) : new Padding(0, 0, U(6), 0);
        header.BringToFront(); actions.BringToFront(); connectionDot.BringToFront();
    }

    internal void UpdateHover(Point screenPoint)
    {
        bool reveal = !expanded && ViewerVisible && (settingsMenu.Visible ||
            Bounds.Contains(screenPoint) && PointToClient(screenPoint).Y < 42 * DeviceDpi / 96);
        if (pipHeaderRevealed == reveal) return;
        pipHeaderRevealed = reveal; LayoutChrome();
    }

    internal void ObservePointer(bool down, bool outside, bool externalForeground)
    {
        if (down && !mouseWasDown && outside && externalForeground && expanded && !changingLayout)
        {
            if (takeoverPending && CollapseOnClickOutside) ReturnToAgent();
            if (HumanControl && ResumeMode == ResumeMode.OnClickOutside)
            {
                SetHumanControl(false); ControlRequested?.Invoke(false);
            }
            if (CollapseOnClickOutside) { SetPip(); UpdateStatus(); }
        }
        if (down && !mouseWasDown && outside && !expanded) actions.Visible = false;
        mouseWasDown = down;
    }

    private static Button MakeButton(string text, int width) => new ViewerActionButton {
        Text = text, Width = width, Height = 32, BackColor = Color.FromArgb(48, 56, 64),
        ForeColor = Color.White, Font = new Font("Segoe UI", 9), Margin = new Padding(0, 0, 6, 0),
        Cursor = Cursors.Hand, TabStop = true, AccessibleName = text };

    internal void ToggleControls()
    {
        if (expanded && HumanControl) return;
        bool show = !actions.Visible;
        LayoutChrome(); actions.Visible = show;
        if (show) actions.BringToFront();
    }
    internal void RevealControls() { if (!expanded || !HumanControl) { LayoutChrome(); actions.Visible = true; actions.BringToFront(); } }
    internal void RequestTakeover()
    {
        if (!actions.Visible || !take.Enabled) return;
        takeoverPending = true; take.Enabled = false; status.Text = "Taking over...";
        ControlRequested?.Invoke(true);
    }
    internal void Expand()
    {
        SavePosition();
        changingLayout = true;
        try
        {
            expanded = true; restoredBounds = null; TopMost = true; ShowInTaskbar = true;
            var area = Screen.FromControl(this).WorkingArea;
            Bounds = ViewerGeometry.Fit(desktopSize, area, ExpandedHeaderUnits * DeviceDpi / 96, 5d / 6);
            LayoutChrome(); actions.Visible = !HumanControl;
            Native.EnableWindow(Rdp.Handle, HumanControl); Rdp.Enabled = HumanControl; Rdp.TabStop = HumanControl;
            if (ViewerVisible) Native.ShowWindow(Handle, 5);
            Activate();
            if (HumanControl) Rdp.Focus();
        }
        finally { changingLayout = false; }
    }
    internal void Collapse()
    {
        if (takeoverPending || HumanControl && ResumeMode != ResumeMode.Manual) { ReturnToAgent(); return; }
        SetPip(); UpdateStatus();
    }
    internal void ReturnToAgent()
    {
        SetHumanControl(false); SetPip(); ControlRequested?.Invoke(false);
    }
    private void SetPip()
    {
        changingLayout = true;
        try
        {
            expanded = false; pipHeaderRevealed = false; restoredBounds = null; WindowState = FormWindowState.Normal; TopMost = true; ShowInTaskbar = true;
            var area = Screen.FromPoint(preferences.Position ?? Cursor.Position).WorkingArea;
            var size = preferences.Size ?? new Size(416, 236);
            size = ViewerGeometry.Resize(new Rectangle(Point.Empty, size), desktopSize, 0, 2, area.Size,
                358 * DeviceDpi / 96, 84 * DeviceDpi / 96).Size;
            var point = preferences.Position ?? new Point(area.Right - size.Width - 24, area.Bottom - size.Height - 24);
            Bounds = new Rectangle(Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
                Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)), size.Width, size.Height);
            LayoutChrome(); actions.Visible = false;
            // Manual pause can survive collapse, but PiP itself never forwards desktop input.
            Native.EnableWindow(Rdp.Handle, false); Rdp.Enabled = false; Rdp.TabStop = false;
            if (ViewerVisible) Native.ShowWindow(Handle, 4); // Style/taskbar changes can recreate the form HWND.
        }
        finally { changingLayout = false; }
    }
    private void SavePosition()
    {
        if (expanded || changingLayout || !ViewerVisible) return;
        preferences.Position = Location; preferences.Size = Size; preferences.Save();
    }
    internal void SetAgentConnected(bool connected) { agentConnected = connected; UpdateStatus(); }
    internal void SetConnectionStatus(string? value) { connectionStatus = value; UpdateStatus(); }
    private void UpdateStatus()
    {
        status.Text = connectionStatus ?? (HumanControl ? (expanded ? "You have control - Agent paused" : "Paused") : agentConnected ? "Agent connected" : "Waiting for agent");
        connectionDot.Connected = agentConnected && connectionStatus is null;
        connectionDot.Paused = HumanControl;
        tips.SetToolTip(connectionDot, status.Text);
        take.Text = HumanControl ? "Taken control" : "Take over";
        take.AccessibleName = take.Text;
        take.BackColor = HumanControl ? Color.FromArgb(62, 68, 74) : Color.FromArgb(19, 125, 112);
        LayoutChrome();
        take.Enabled = !HumanControl && connectionStatus is null && !takeoverPending;
    }
    internal void SetHumanControl(bool enabled)
    {
        takeoverPending = false; HumanControl = enabled;
        if (enabled) Expand();
        // Disable the native ActiveX subtree; an overlay is never the input boundary.
        Native.EnableWindow(Rdp.Handle, enabled);
        Rdp.Enabled = enabled; Rdp.TabStop = enabled;
        actions.Visible = false;
        UpdateStatus();
        if (enabled) Rdp.Focus();
    }
    internal void SetViewer(bool show)
    {
        if (!show) settingsMenu.Hide();
        if (show && ViewerVisible) return;
        if (!show) SavePosition();
        if (!show && (takeoverPending || HumanControl && ResumeMode != ResumeMode.Manual)) ReturnToAgent();
        ViewerVisible = show;
        if (show) SetPip();
        else { ShowInTaskbar = false; Location = new Point(-20000, -20000); WindowState = FormWindowState.Normal; }
        // Keep the RDP host realized and non-minimized so the child desktop keeps rendering.
    }
    internal void CloseHost(bool disconnect = true)
    {
        closing = true;
        try { if (disconnect) Rdp.Disconnect(); }
        finally { Close(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { outsideClick.Dispose(); tray.Dispose(); tips.Dispose(); settingsMenu.Dispose(); appIcon.Dispose(); }
        base.Dispose(disposing);
    }
}

internal enum ResumeMode { OnClose, OnClickOutside, Manual }
internal sealed class ViewerPreferences
{
    public ResumeMode Resume { get; set; } = ResumeMode.OnClose;
    public bool CollapseOnClickOutside { get; set; } = true;
    public Point? Position { get; set; }
    public Size? Size { get; set; }
    internal bool Persist { get; init; } = true;
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterComputerUse", "viewer.json");
    internal static ViewerPreferences Load()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<ViewerPreferences>(File.ReadAllText(PathName)) ?? new(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal void Save()
    {
        if (!Persist) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(PathName)!); File.WriteAllText(PathName, System.Text.Json.JsonSerializer.Serialize(this)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    void set_Property([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    [return: MarshalAs(UnmanagedType.Struct)] object get_Property([MarshalAs(UnmanagedType.BStr)] string name);
}

[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IRdpEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int reason);
    [DispId(10)] void OnFatalError(int code);
    [DispId(22)] void OnLogonError(int code);
}

internal sealed class RdpControl : AxHost
{
    private ConnectionPointCookie? cookie;
    private RdpEvents? sink;
    internal TaskCompletionSource Login { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal event Action<string>? Lost;
    private object Ocx => GetOcx() ?? throw new InvalidOperationException("RDP ActiveX is not initialized.");
    internal bool Connected => IsHandleCreated && Convert.ToInt32(Get(Ocx, "Connected")) == 1;
    internal RdpControl() : base("A0C63C30-F08D-4AB4-907C-34905D770C7D") { }
    protected override void CreateSink()
    {
        sink = new RdpEvents(this);
        cookie = new ConnectionPointCookie(Ocx, sink, typeof(IRdpEvents));
    }
    protected override void DetachSink() { cookie?.Disconnect(); cookie = null; sink = null; }
    private static object Get(object obj, string name) => obj.GetType().InvokeMember(name, BindingFlags.GetProperty, null, obj, null)!;
    private static void Set(object obj, string name, object value) => obj.GetType().InvokeMember(name, BindingFlags.SetProperty, null, obj, [value]);
    private static void Call(object obj, string name) => obj.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, obj, null);
    internal void Connect(int width, int height)
    {
        var ocx = Ocx;
        Set(ocx, "Server", "localhost"); Set(ocx, "DesktopWidth", width); Set(ocx, "DesktopHeight", height); Set(ocx, "ColorDepth", 32);
        var advanced = Get(ocx, "AdvancedSettings7");
        Set(advanced, "EnableCredSspSupport", true); Set(advanced, "SmartSizing", true);
        Set(advanced, "RedirectClipboard", false); Set(advanced, "RedirectDrives", false);
        Set(advanced, "RedirectPrinters", false); Set(advanced, "RedirectSmartCards", false);
        Set(advanced, "EnableAutoReconnect", false); Set(advanced, "allowBackgroundInput", 0);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
        if (key?.GetValue("PortNumber") is int port and > 0 and <= 65535) Set(advanced, "RDPPort", port);
        var secured = Get(ocx, "SecuredSettings2");
        Set(secured, "KeyboardHookMode", 0); Set(secured, "AudioRedirectionMode", 2);
        object child = true;
        ((IMsRdpExtendedSettings)ocx).set_Property("ConnectToChildSession", ref child);
        Call(ocx, "Connect");
    }
    internal void Disconnect() { if (IsHandleCreated && Convert.ToInt32(Get(Ocx, "Connected")) != 0) Call(Ocx, "Disconnect"); }
    internal void Fail(string reason) { Login.TrySetException(new InvalidOperationException(reason)); Lost?.Invoke(reason); }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEvents : IRdpEvents
{
    private readonly RdpControl owner;
    internal RdpEvents(RdpControl owner) => this.owner = owner;
    public void OnConnecting() { }
    public void OnConnected() { }
    public void OnLoginComplete() => owner.Login.TrySetResult();
    public void OnDisconnected(int reason) => owner.Fail($"RDP disconnected ({reason}).");
    public void OnFatalError(int code) => owner.Fail($"RDP fatal error ({code}).");
    public void OnLogonError(int code)
    {
        // OnLogonError also reports continuing logon and interactive notices, not just failures.
        if (code is -5 or -4 or -2 or 3) return;
        owner.Fail($"RDP logon error ({code}).");
    }
}
