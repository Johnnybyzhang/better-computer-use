"""Shared-host regression. --live preserves any existing desktop and its applications."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading
from concurrent.futures import ThreadPoolExecutor

class Client:
    def __init__(self, exe):
        self.process = subprocess.Popen([str(pathlib.Path(exe).resolve()), "mcp", "--auto-helper"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding="utf-8", text=True)
        self.inbox, self.errors, self.seq = queue.Queue(), [], 0
        def read():
            for line in self.process.stdout:
                self.inbox.put(line)
            self.inbox.put(None)
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr.readlines()), daemon=True).start()
        self.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "shared-desktop-test", "version": "1"}})
        self.send("notifications/initialized", notification=True)
    def send(self, method, params=None, notification=False):
        self.seq += 1
        request = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        if not notification:
            request["id"] = self.seq
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()
        if notification:
            return
        while True:
            line = self.inbox.get(timeout=240)
            assert line is not None, "MCP closed: " + "".join(self.errors)
            result = json.loads(line)
            if "id" not in result:
                continue
            assert result["id"] == self.seq and "result" in result, result
            return result["result"]
    def call(self, name, arguments=None, error=False):
        result = self.send("tools/call", {"name": name, "arguments": arguments or {}})
        assert bool(result.get("isError")) == error, result
        return result["content"][0]["text"] if error else json.loads(result["content"][0]["text"])
    def close(self):
        if self.process.poll() is not None:
            return
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)

def binding(result):
    return {key: result[key] for key in ("sessionId", "generation")}

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default="src/BetterComputerUse/bin/Release/net10.0-windows/BetterComputerUse.exe")
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--mode", choices=["admin", "user"], default="user")
    options = parser.parse_args()
    clients = []
    try:
        for _ in range(3):
            client = Client(options.exe); clients.append(client)
            assert {"session_start", "session_take_control", "list_windows"} <= {t["name"] for t in client.send("tools/list")["tools"]}
        a, b, c = clients
        with ThreadPoolExecutor(max_workers=3) as pool:
            statuses = list(pool.map(lambda client: client.call("session_status"), clients))
        assert len({s["hostPid"] for s in statuses}) == 1
        print("PASS three independent MCP clients share one authenticated desktop host", flush=True)
        if statuses[0].get("state") == "ready" and statuses[0].get("mode") == "user":
            fresh = Client(options.exe)
            try:
                advertised = {tool["name"] for tool in fresh.send("tools/list")["tools"]}
                assert "launch_process_as_admin" not in advertised and "computer_use_elevated" not in advertised
                print("PASS fresh client discovery reflects the existing user-mode host", flush=True)
            finally:
                fresh.close()
        if not options.live:
            return
        started = a.call("session_start", {"mode": options.mode})
        first = binding(started)
        print("STARTED", json.dumps(started), flush=True)
        first_windows = a.call("list_windows", first)
        assert first_windows["ok"], first_windows
        observer = binding(b.call("session_start", {"takeControl": False}))
        assert b.call("list_windows", observer)["ok"]
        assert b.call("end_turn", observer, error=True)
        second = binding(b.call("session_take_control"))
        assert second["sessionId"] == first["sessionId"]
        assert a.call("end_turn", first, error=True)
        assert b.call("list_windows", second)["ok"]
        first = binding(a.call("session_take_control"))
        assert b.call("end_turn", second, error=True)
        assert b.call("session_stop", second)["detached"]
        assert a.call("list_windows", first)["ok"]
        host = a.call("session_status")["hostPid"]
        a.call("session_stop", first)
        for client in clients:
            client.close()
        clients.clear()
        d = Client(options.exe); clients.append(d)
        resumed = d.call("session_start", {"mode": options.mode})
        assert resumed["hostPid"] == host and resumed["sessionId"] == first["sessionId"]
        assert resumed["workerPid"] == started["workerPid"]
        assert d.call("list_windows", binding(resumed))["ok"]
        assert d.call("session_status")["viewerMode"] == "pip"
        d.call("session_stop", binding(resumed))
        print("PASS observation, bidirectional handoff, stale input rejection, all-client exit, same worker/session reconnect and default PiP", flush=True)
    finally:
        for client in clients:
            client.close()

if __name__ == "__main__":
    main()
