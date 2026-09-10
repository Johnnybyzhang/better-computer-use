using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterComputerUse;

internal sealed class RdpWindow : Form
{
    internal readonly RdpControl Rdp = new() { Dock = DockStyle.Fill, TabStop = false };
    private readonly Panel surface = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 25, 29) };
    private readonly Panel header = new() { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(31, 35, 41) };
    private readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 46, Visible = false,
        BackColor = Color.FromArgb(31, 35, 41), Padding = new Padding(6), WrapContents = false };
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(170, 223, 213), Padding = new Padding(12, 0, 0, 0) };
    private readonly Button take = MakeButton("Take over", 110);
    private readonly Button returnToAgent = MakeButton("Return to agent", 140);
    private readonly System.Windows.Forms.Timer outsideClick = new() { Interval = 25 };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "Computer Use", Visible = true };
    private readonly ViewerPreferences preferences;
    internal event Action<bool>? ControlRequested;
    internal bool HumanControl { get; private set; }
    internal bool ViewerVisible { get; private set; }
    internal string ViewMode => !ViewerVisible ? "hidden" : expanded ? "expanded" : "pip";
    internal bool ControlsRevealed => actions.Visible;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal ResumeMode ResumeMode { get => preferences.Resume; set { preferences.Resume = value; preferences.Save(); } }
    private bool closing, expanded, agentConnected, mouseWasDown, changingLayout, takeoverPending;
    private string? connectionStatus;
    private Point? dragStart;
    protected override bool ShowWithoutActivation => !expanded;

    internal RdpWindow(ViewerPreferences? settings = null)
    {
        preferences = settings ?? ViewerPreferences.Load();
        Text = "Computer Use";
        Font = new Font("Segoe UI", 9);
        BackColor = header.BackColor;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(384, 256);
        MinimumSize = new Size(280, 190);
        Location = new Point(-20000, -20000);
        ShowInTaskbar = false;
        surface.Controls.Add(Rdp);
        Controls.Add(surface); Controls.Add(actions); Controls.Add(header);
        header.Controls.Add(status);
        var collapse = MakeButton("-", 36); collapse.Dock = DockStyle.Right;
        collapse.AccessibleName = "Collapse to floating view";
        collapse.Click += (_, _) => Collapse(); header.Controls.Add(collapse);
        var settingsButton = MakeButton("...", 36); settingsButton.Dock = DockStyle.Right;
        settingsButton.AccessibleName = "Resume automation settings";
        var menu = new ContextMenuStrip();
        foreach (var (mode, label) in new[] { (ResumeMode.OnClose, "Resume when I close the large view"),
            (ResumeMode.OnClickOutside, "Also resume when I click outside"), (ResumeMode.Manual, "Resume only when I choose Return to agent") })
        {
            var item = new ToolStripMenuItem(label) { Tag = mode };
            item.Click += (_, _) => ResumeMode = mode;
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) => { foreach (ToolStripMenuItem item in menu.Items) item.Checked = (ResumeMode)item.Tag! == ResumeMode; };
        settingsButton.Click += (_, _) => menu.Show(settingsButton, new Point(0, settingsButton.Height));
        header.Controls.Add(settingsButton);
        returnToAgent.Dock = DockStyle.Right; returnToAgent.Visible = false;
        returnToAgent.Click += (_, _) => ReturnToAgent(); header.Controls.Add(returnToAgent);
        take.Click += (_, _) => RequestTakeover(); actions.Controls.Add(take);
        var expand = MakeButton("Expand view", 112);
        expand.Click += (_, _) => Expand(); actions.Controls.Add(expand);
        surface.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) RevealControls(); };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) RevealControls(); };
        foreach (Control handle in new Control[] { header, status })
        {
            handle.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && !expanded) dragStart = e.Location; };
            handle.MouseMove += (_, e) => { if (dragStart is Point start && e.Button == MouseButtons.Left) Location = new(Location.X + e.X - start.X, Location.Y + e.Y - start.Y); };
            handle.MouseUp += (_, _) => { dragStart = null; SavePosition(); RevealControls(); };
        }
        ResizeEnd += (_, _) => SavePosition();
        outsideClick.Tick += (_, _) =>
        {
            var down = (Native.GetAsyncKeyState(1) & 0x8000) != 0;
            var foreground = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(foreground, out var pid);
            // Menus/dialogs owned by this viewer and the secure desktop are not click-out.
            ObservePointer(down, !Bounds.Contains(Cursor.Position), foreground != 0 && pid != 0 && pid != Environment.ProcessId);
        };
        outsideClick.Start();
        tray.DoubleClick += (_, _) => SetViewer(true);
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
            if (expanded) Collapse(); else SetViewer(false);
        };
    }

    protected override void WndProc(ref Message message)
    {
        // Treat the standard title-bar minimize action like collapsing the viewer.
        // Never minimize the native RDP host: doing so can suspend remote rendering.
        if (message.Msg == 0x112 && ((long)message.WParam & 0xfff0) == 0xf020)
        {
            if (expanded) Collapse(); else SetViewer(false);
            return;
        }
        base.WndProc(ref message);
    }

    internal void ObservePointer(bool down, bool outside, bool externalForeground)
    {
        if (down && !mouseWasDown && outside && externalForeground && expanded && HumanControl &&
            ResumeMode == ResumeMode.OnClickOutside && !changingLayout) ReturnToAgent();
        mouseWasDown = down;
    }

    private static Button MakeButton(string text, int width) => new() { Text = text, Width = width, Height = 32,
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(42, 49, 57), ForeColor = Color.White,
        Cursor = Cursors.Hand, TabStop = true, AccessibleName = text };

    internal void RevealControls() { if (!expanded || !HumanControl) actions.Visible = true; }
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
            expanded = true; FormBorderStyle = FormBorderStyle.Sizable; TopMost = false; ShowInTaskbar = true;
            var area = Screen.FromControl(this).WorkingArea;
            Bounds = new Rectangle(area.X + area.Width / 12, area.Y + area.Height / 12, area.Width * 5 / 6, area.Height * 5 / 6);
            actions.Visible = !HumanControl;
            if (ViewerVisible) Native.ShowWindow(Handle, 5);
            Activate();
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
            expanded = false; FormBorderStyle = FormBorderStyle.SizableToolWindow; WindowState = FormWindowState.Normal; TopMost = true; ShowInTaskbar = false;
            var area = Screen.FromPoint(preferences.Position ?? Cursor.Position).WorkingArea;
            var size = preferences.Size ?? new Size(400, 270);
            size = new Size(Math.Clamp(size.Width, 280, Math.Max(280, area.Width)), Math.Clamp(size.Height, 190, Math.Max(190, area.Height)));
            var point = preferences.Position ?? new Point(area.Right - size.Width - 24, area.Bottom - size.Height - 24);
            Bounds = new Rectangle(Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
                Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)), size.Width, size.Height);
            actions.Visible = false;
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
        status.Text = connectionStatus ?? (HumanControl ? "You have control - Agent paused" : agentConnected ? "Agent connected" : "Waiting for agent");
        returnToAgent.Visible = HumanControl;
        take.Enabled = connectionStatus is null && !takeoverPending;
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
        if (disposing) { outsideClick.Dispose(); tray.Dispose(); }
        base.Dispose(disposing);
    }
}

internal enum ResumeMode { OnClose, OnClickOutside, Manual }
internal sealed class ViewerPreferences
{
    public ResumeMode Resume { get; set; } = ResumeMode.OnClose;
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
