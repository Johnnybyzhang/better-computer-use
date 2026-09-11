"""Opt-in live test: preserve an unsaved GUI document through agent and host replacement."""
import argparse
import json
import pathlib
import subprocess
import tempfile
import time
import uuid
from smoke_multi_task import Client, binding

parser = argparse.ArgumentParser()
parser.add_argument("--exe", default="src/BetterComputerUse/bin/Release/net10.0-windows/BetterComputerUse.exe")
parser.add_argument("--probe-exe", default="tests/BetterComputerUse.Tests/bin/Release/net10.0-windows/BetterComputerUse.Tests.exe")
parser.add_argument("--crash-host", action="store_true", help="Explicitly crash this build's shared host; disconnect other test clients first")
options = parser.parse_args()
probe = pathlib.Path(tempfile.gettempdir()) / ("bcu-document-" + uuid.uuid4().hex + ".json")
nonce = "Unsaved BCU document " + uuid.uuid4().hex
clients = []
def sample():
    probe.unlink(missing_ok=True)
    pathlib.Path(str(probe) + ".query").write_text("sample")
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if probe.exists():
            try:
                return json.loads(probe.read_text())
            except json.JSONDecodeError:
                pass
        time.sleep(.1)
    raise RuntimeError("Document did not respond")
try:
    a = Client(options.exe); clients.append(a)
    started = a.call("session_start", {"mode": "user"})
    subprocess.run([str(pathlib.Path(options.probe_exe).resolve()), "--launch-desktop-document", str(probe), nonce], check=True)
    before = sample()
    assert before["sessionId"] == started["sessionId"] and before["text"] == nonce
    b = Client(options.exe); clients.append(b)
    second = b.call("session_take_control")
    assert second["sessionId"] == started["sessionId"] and sample()["text"] == nonce
    a.call("end_turn", binding(started), error=True)
    for client in clients:
        client.close()
    clients.clear()
    if options.crash_host:
        # PID comes from this authenticated build's host, never from a broad process search.
        subprocess.run(["taskkill.exe", "/PID", str(started["hostPid"]), "/F"], check=True, capture_output=True)
    c = Client(options.exe); clients.append(c)
    resumed = c.call("session_start", {"mode": "user"})
    after = sample()
    assert resumed["sessionId"] == started["sessionId"]
    if options.crash_host:
        assert resumed["hostPid"] != started["hostPid"]
    assert after["pid"] == before["pid"] and after["text"] == nonce and after["sampledAt"] != before["sampledAt"]
    print("PASS unsaved TextBox contents and process survive handoff, all-client exit and" + (" host crash/RDP reconnect" if options.crash_host else " reattach"), flush=True)
    print(json.dumps({"before": before, "after": after, "hostPid": resumed["hostPid"]}), flush=True)
    if options.crash_host:
        subprocess.run(["taskkill.exe", "/PID", str(resumed["hostPid"]), "/F"], check=True, capture_output=True)
        resumed = c.call("session_start", {"mode": "user"})
        assert sample()["text"] == nonce and sample()["pid"] == before["pid"]
        print("PASS existing MCP client reconnects its broken pipe in one session_start", flush=True)
    c.call("session_stop", binding(resumed))
finally:
    pathlib.Path(str(probe) + ".close").write_text("close only this test document")
    for client in clients:
        client.close()
