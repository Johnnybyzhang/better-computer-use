"""Verify installed plugin tool inventory in three real Codex app-server tasks, without model calls."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading
import time
import tomllib

parser = argparse.ArgumentParser()
parser.add_argument("--codex", required=True)
options = parser.parse_args()
config = tomllib.loads((pathlib.Path.home() / ".codex/config.toml").read_text(encoding="utf-8-sig"))
plugins = {key: False for key in config.get("plugins", {})}
plugins["better-computer-use@personal"] = True
inline = "{" + ",".join(json.dumps(key) + "={enabled=" + str(enabled).lower() + "}" for key, enabled in plugins.items()) + "}"
command = [options.codex, "app-server", "-c", "plugins=" + inline, "-c", "mcp_servers={}", "-c", "features.apps=false"]
process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
inbox, errors = queue.Queue(), []
def read():
    for line in process.stdout:
        inbox.put(line)
    inbox.put(None)
threading.Thread(target=read, daemon=True).start()
threading.Thread(target=lambda: errors.extend(process.stderr.readlines()), daemon=True).start()
seq = 0
def call(method, params):
    global seq
    seq += 1
    process.stdin.write(json.dumps({"id": seq, "method": method, "params": params}) + "\n")
    process.stdin.flush()
    deadline = time.monotonic() + 60
    while True:
        line = inbox.get(timeout=max(0.1, deadline - time.monotonic()))
        assert line is not None, "App server closed: " + "".join(errors[-5:])
        value = json.loads(line)
        if value.get("id") != seq:
            continue
        assert "result" in value, value
        return value["result"]
try:
    call("initialize", {"clientInfo": {"name": "bcu-validation", "version": "1"}, "capabilities": {"experimentalApi": True}})
    threads = []
    for index in range(3):
        started = call("thread/start", {"cwd": str(pathlib.Path.cwd()), "ephemeral": True, "approvalPolicy": "never", "sandbox": "read-only"})
        threads.append(started["thread"]["id"])
    for index, thread_id in enumerate(threads):
        deadline = time.monotonic() + 40
        while True:
            inventory = call("mcpServerStatus/list", {"threadId": thread_id, "detail": "toolsAndAuthOnly"})
            server = next((item for item in inventory["data"] if item["name"] == "child-computer-use"), None)
            if server and {"session_start", "computer_use", "list_windows", "launch_process_as_admin"} <= set(server["tools"]):
                break
            assert time.monotonic() < deadline, {"task": index, "server": server}
            time.sleep(0.2)
        status = call("mcpServer/tool/call", {"threadId": thread_id, "server": "child-computer-use", "tool": "session_status", "arguments": {}})
        assert not status.get("isError"), status
        print(f"PASS app-server task {index + 1}: {len(server['tools'])} tools loaded and session_status executed", flush=True)
finally:
    process.stdin.close()
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
