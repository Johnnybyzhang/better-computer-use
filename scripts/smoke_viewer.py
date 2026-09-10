"""Live viewer event test, targeting only this build's host window (not the remote desktop)."""
import argparse
import ctypes
from ctypes import wintypes as w
import time
from smoke_multi_task import Client, binding

parser = argparse.ArgumentParser()
parser.add_argument("--exe", default="src/BetterComputerUse/bin/Release/net10.0-windows/BetterComputerUse.exe")
options = parser.parse_args()
u = ctypes.WinDLL("user32")
callback = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
u.GetParent.argtypes = [w.HWND]; u.GetParent.restype = w.HWND
u.SendMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]; u.SendMessageW.restype = w.LPARAM

def windows(host):
    roots, children = [], []
    @callback
    def collect(h, _):
        title = ctypes.create_unicode_buffer(256); kind = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(h, title, 256); u.GetClassNameW(h, kind, 256)
        rect = w.RECT(); u.GetWindowRect(h, ctypes.byref(rect))
        children.append((h, title.value, kind.value, (rect.right-rect.left)*(rect.bottom-rect.top)))
        return True
    @callback
    def root(h, _):
        pid = w.DWORD(); u.GetWindowThreadProcessId(h, ctypes.byref(pid))
        title = ctypes.create_unicode_buffer(256); u.GetWindowTextW(h, title, 256)
        if pid.value == host and title.value == "Computer Use":
            roots.append(h); u.EnumChildWindows(h, collect, 0)
        return True
    u.EnumWindows(root, 0)
    assert len(roots) == 1, roots
    assert u.IsWindowVisible(roots[0]), "PiP/expanded host must remain visible after changing window styles"
    return roots[0], children

def button(host, text):
    _, children = windows(host)
    handle = next(h for h, title, kind, _ in children if title == text and "Button" in kind)
    u.SendMessageW(handle, 0xF5, 0, 0)  # BM_CLICK on a verified host button

def reveal(host):
    root, children = windows(host)
    surface = max((item for item in children if u.GetParent(item[0]) == root and not item[1] and "WindowsForms10.Window" in item[2]), key=lambda item: item[3])[0]
    u.SendMessageW(surface, 0x201, 1, 10 | (10 << 16))
    u.SendMessageW(surface, 0x202, 0, 10 | (10 << 16))

def wait_state(client, human, mode):
    deadline = time.monotonic() + 5
    while True:
        state = client.call("session_status")
        if state["humanControl"] == human and state["viewerMode"] == mode:
            return state
        assert time.monotonic() < deadline, state
        time.sleep(.05)

a, b = Client(options.exe), Client(options.exe)
try:
    started = a.call("session_start", {"mode": "user"}); host = started["hostPid"]
    wait_state(a, False, "pip")
    reveal(host)
    assert not a.call("session_status")["humanControl"]
    button(host, "Take over")
    wait_state(a, True, "expanded")
    a.call("end_turn", binding(started), error=True)
    selected = b.call("session_take_control")
    assert selected["sessionId"] == started["sessionId"]
    b.call("end_turn", binding(selected), error=True)
    button(host, "-")
    wait_state(b, False, "pip")
    assert b.call("end_turn", binding(selected))["ok"]
    a.call("end_turn", binding(started), error=True)
    reveal(host); button(host, "Expand view")
    wait_state(b, False, "expanded")
    assert b.call("end_turn", binding(selected))["ok"]
    button(host, "-"); wait_state(b, False, "pip")
    b.call("session_stop", binding(selected))
    print("PASS two-click viewer handlers, human input pause, agent handoff while human controls, collapse-to-resume and read-only expansion", flush=True)
finally:
    a.close(); b.close()
