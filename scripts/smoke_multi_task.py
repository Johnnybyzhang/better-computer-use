"""Regression: ChatGPT creates one stdio MCP connection per task, not per desktop."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading

parser = argparse.ArgumentParser()
parser.add_argument("--exe", default="artifacts/app/BetterComputerUse.exe")
parser.add_argument("--live", action="store_true")
args = parser.parse_args()

class Client:
    def __init__(self):
        self.process = subprocess.Popen([str(pathlib.Path(args.exe).resolve()), "mcp", "--auto-helper"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding="utf-8", text=True)
        self.inbox, self.errors, self.seq = queue.Queue(), [], 0
        def read():
            for line in self.process.stdout:
                self.inbox.put(line)
            self.inbox.put(None)
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr.readlines()), daemon=True).start()
    def send(self, method, params=None, notification=False):
        self.seq += 1
        request = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        if not notification:
            request["id"] = self.seq
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()
        if notification:
            return
        line = self.inbox.get(timeout=80)
        assert line is not None, "MCP closed during handshake/request: " + "".join(self.errors)
        result = json.loads(line)
        assert result["id"] == self.seq and "result" in result, result
        return result["result"]
    def initialize(self):
        self.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "multi-task-regression", "version": "1"}})
        self.send("notifications/initialized", notification=True)
        tools = self.send("tools/list")["tools"]
        assert {"session_start", "computer_use", "list_windows", "launch_process_as_admin", "start_audio_recording"} <= {t["name"] for t in tools}
    def call(self, name, arguments=None, error=False):
        result = self.send("tools/call", {"name": name, "arguments": arguments or {}})
        assert bool(result.get("isError")) == error, result
        return result["content"][0]["text"] if error else json.loads(result["content"][0]["text"])
    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)

clients, owners = [], {}
try:
    for i in range(3):
        client = Client()
        clients.append(client)
        client.initialize()
        assert client.call("session_status")["state"] == "stopped"
    print("PASS three simultaneous task transports initialize, list tools and answer status", flush=True)
    if args.live:
        a, b, c = clients
        started = a.call("session_start")
        binding = {k: started[k] for k in ("sessionId", "generation")}
        owners[a] = binding
        assert "Another task owns" in b.call("session_start", error=True)
        assert not b.call("session_status")["existingChildSession"]["canLogoff"]
        assert "Another task owns" in b.call("session_logoff", {"sessionId": binding["sessionId"]}, error=True)
        assert b.send("tools/list")["tools"]
        assert c.call("session_status")["state"] == "stopped"
        assert a.call("computer_use", {**binding, "method": "list_windows"})["ok"]
        assert b.call("computer_use", {**binding, "method": "list_windows"}, error=True)
        a.call("session_stop", {**binding, "logoff": True})
        del owners[a]
        started = b.call("session_start")
        binding = {k: started[k] for k in ("sessionId", "generation")}
        owners[b] = binding
        assert b.call("computer_use", {**binding, "method": "list_windows"})["ok"]
        b.call("session_stop", {**binding, "logoff": True})
        del owners[b]
        print("PASS desktop contention returns tool error, keeps other tasks connected, prevents cross-task access, and releases ownership", flush=True)
finally:
    for client in clients:
        try:
            status = client.call("session_status")
            if status.get("sessionId") is not None:
                client.call("session_stop", {"sessionId": status["sessionId"], "generation": status["generation"], "logoff": True})
        finally:
            client.close()
