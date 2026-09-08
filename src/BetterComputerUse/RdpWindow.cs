using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterComputerUse;

internal sealed class RdpWindow : Form
{
    internal readonly RdpControl Rdp = new() { Dock = DockStyle.Fill, TabStop = false };
    private readonly ToolStrip toolbar = new();
    private readonly ToolStripLabel status = new("View — automation may run");
    internal event Action<bool>? ControlRequested;
    internal bool HumanControl { get; private set; }
    internal bool ViewerVisible { get; private set; }
    private bool closing;
    protected override bool ShowWithoutActivation => !ViewerVisible;

    internal RdpWindow()
    {
        Text = "Computer Use — child session";
        ClientSize = new Size(1100, 700);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
        ShowInTaskbar = false;
        var view = new ToolStripButton("View");
        var take = new ToolStripButton("Take control");
        view.Click += (_, _) => { SetHumanControl(false); ControlRequested?.Invoke(false); };
        take.Click += (_, _) => ControlRequested?.Invoke(true);
        toolbar.Items.AddRange([view, take, new ToolStripSeparator(), status]);
        Controls.Add(Rdp); Controls.Add(toolbar);
        FormClosing += (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; SetHumanControl(false); SetViewer(false); ControlRequested?.Invoke(false);
        };
    }

    internal void SetHumanControl(bool enabled)
    {
        // Disable the native ActiveX subtree as well as tab navigation. Never rely on an overlay alone.
        Native.EnableWindow(Rdp.Handle, enabled);
        Rdp.Enabled = enabled; Rdp.TabStop = enabled;
        HumanControl = enabled;
        status.Text = enabled ? "You have control — automation paused" : "View — automation may run";
        if (enabled) Rdp.Focus(); else toolbar.Focus();
    }

    internal void SetViewer(bool show)
    {
        ViewerVisible = show;
        if (!show) SetHumanControl(false);
        ShowInTaskbar = show;
        Location = show ? new Point(80, 80) : new Point(-20000, -20000);
        if (show) { WindowState = FormWindowState.Normal; Activate(); }
        // Keep the RDP host realized and non-minimized so the child desktop keeps rendering.
    }

    internal void CloseHost(bool disconnect = true) { closing = true; try { if (disconnect) Rdp.Disconnect(); } finally { Close(); } }
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
