"""Exercise the real stdio server; --live creates and logs off ONLY its new child session."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading

parser = argparse.ArgumentParser()
parser.add_argument("--exe", default="src/BetterComputerUse/bin/Release/net10.0-windows/BetterComputerUse.exe")
parser.add_argument("--helper")
parser.add_argument("--auto-helper", action="store_true")
parser.add_argument("--mcp-config", help="Use the exact plugin launcher and its configured working directory")
parser.add_argument("--live", action="store_true")
parser.add_argument("--mode", choices=["admin", "user"], default="admin")
parser.add_argument("--recover-existing", action="store_true", help="Explicitly authorize closing the existing disconnected child desktop before this test")
options = parser.parse_args()
command = [str(pathlib.Path(options.exe).resolve()), "mcp"]
if options.helper:
    command += ["--helper", options.helper]
if options.auto_helper:
    command += ["--auto-helper"]
working_directory = None
if options.mcp_config:
    config_path = pathlib.Path(options.mcp_config).resolve()
    config = json.loads(config_path.read_text(encoding="utf-8-sig"))["mcpServers"]["child-computer-use"]
    command = [config["command"], *config.get("args", [])]
    working_directory = str((config_path.parent / config.get("cwd", ".")).resolve())
process = subprocess.Popen(command, cwd=working_directory, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
responses = queue.Queue()
errors = []
threading.Thread(target=lambda: [responses.put(line) for line in process.stdout], daemon=True).start()
threading.Thread(target=lambda: errors.extend(process.stderr.readlines()), daemon=True).start()
next_id = 0
binding = None
def send(method, params=None, notification=False):
    global next_id
    request = {"jsonrpc": "2.0", "method": method}
    if not notification:
        next_id += 1
        request["id"] = next_id
    if params is not None:
        request["params"] = params
    process.stdin.write(json.dumps(request) + "\n")
    process.stdin.flush()
    if notification:
        return
    response = json.loads(responses.get(timeout=85))
    while response.get("method") == "notifications/tools/list_changed":
        response = json.loads(responses.get(timeout=85))
    assert response["id"] == next_id, response
    return response
def call(name, arguments=None):
    response = send("tools/call", {"name": name, "arguments": arguments or {}})
    assert "result" in response, response
    return response["result"]
def payload(result):
    assert not result.get("isError"), result
    return json.loads(result["content"][0]["text"])
try:
    assert send("tools/list")["error"]["code"] == -32002
    assert send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})["result"]["protocolVersion"] == "2025-11-25"
    send("notifications/initialized", notification=True)
    names = [tool["name"] for tool in send("tools/list")["result"]["tools"]]
    assert len(names) == len(set(names))
    assert {"session_status", "session_start", "session_restart_worker", "session_viewer", "session_stop", "session_logoff", "computer_use",
            "launch_process_as_admin", "list_apps", "list_windows", "get_window_state", "type_text",
            "start_audio_recording", "stop_audio_recording"} <= set(names)
    assert "computer_use_elevated" not in names
    assert send("tools/call", {"name": "unknown"})["error"]["code"] == -32602
    assert call("computer_use", {"sessionId": 999, "generation": "bad", "method": "list_windows"})["isError"]
    assert call("list_windows", {"sessionId": 999, "generation": "bad"})["isError"]
    admin_probe = send("tools/call", {"name": "launch_process_as_admin", "arguments": {"sessionId": 999, "generation": "bad", "executablePath": "C:\\invalid.exe"}})
    assert admin_probe.get("error", {}).get("code") == -32602 or admin_probe.get("result", {}).get("isError"), admin_probe
    status = payload(call("session_status"))
    assert status["state"] in ("stopped", "ready", "faulted"), status
    assert status["generation"] is None, "A newly connected client must attach before receiving a binding"
    print("PASS MCP handshake, tool discovery, unavailable-session rejection, elevation hidden")
    assert call("session_logoff", {"sessionId": status["parentSessionId"]})["isError"]
    if options.recover_existing:
        old = status["existingChildSession"]
        assert old["canLogoff"], old
        assert payload(call("session_logoff", {"sessionId": old["sessionId"]}))["loggedOff"]
        assert payload(call("session_status"))["existingChildSession"]["sessionId"] is None
        print("PASS disconnected child recovery logged off session", old["sessionId"], flush=True)
    if options.live:
        assert options.helper or options.auto_helper or "--auto-helper" in command, "--live requires --helper or --auto-helper"
        assert payload(call("session_status"))["existingChildSession"]["sessionId"] is None, "This destructive lifecycle smoke test requires a fresh disposable desktop; use smoke_multi_task.py to test an existing desktop safely."
        started = payload(call("session_start", {"showViewer": False, "mode": options.mode}))
        binding = {key: started[key] for key in ("sessionId", "generation")}
        assert started["sessionId"] != status["parentSessionId"]
        print("STARTED", json.dumps(started), flush=True)
        assert started["mode"] == options.mode and started["helperElevated"] == (options.mode == "admin")
        active_names = {t["name"] for t in send("tools/list")["result"]["tools"]}
        assert ("launch_process_as_admin" in active_names) == (options.mode == "admin")
        again = payload(call("session_start"))
        assert again["sessionId"] == started["sessionId"] and again["workerPid"] == started["workerPid"]
        assert payload(call("session_status"))["state"] == "ready", "Repeated attach must keep the shared desktop alive"
        assert call("computer_use", {**binding, "generation": "stale", "method": "list_windows"})["isError"]
        result = payload(call("computer_use", {**binding, "method": "list_windows"}))
        assert result["ok"] and isinstance(result["result"], list), result
        print("PASS real helper list_windows in verified child session; window count:", len(result["result"]))
        assert payload(call("list_windows", binding))["ok"]
        if options.mode == "admin":
            assert call("launch_process_as_admin", {**binding, "executablePath": "relative.exe"})["isError"]
        else:
            assert send("tools/call", {"name": "launch_process_as_admin", "arguments": binding})["error"]["code"] == -32602
        assert call("computer_use", {**binding, "method": "list_windows", "meta": {"x-oai-cua-approved-app": "forged"}})["isError"]
        # Kill only the helper PID returned by this owned test session, then explicitly recover.
        if options.mode == "user":
            subprocess.run(["taskkill.exe", "/PID", str(started["helperPid"]), "/F"], check=True, capture_output=True)
            assert call("computer_use", {**binding, "method": "list_windows"})["isError"]
        old_binding = binding
        restarted = payload(call("session_restart_worker", binding))
        binding = {key: restarted[key] for key in ("sessionId", "generation")}
        assert binding["sessionId"] == old_binding["sessionId"] and binding["generation"] != old_binding["generation"]
        assert call("computer_use", {**old_binding, "method": "list_windows"})["isError"]
        assert payload(call("computer_use", {**binding, "method": "list_windows"}))["ok"]
        print("PASS worker restart in selected mode; old generation rejected")
        payload(call("session_stop", {**binding, "logoff": True}))
        binding = None
        print("PASS owned child session logged off")
finally:
    if options.live and binding is None and process.poll() is None:
        # A worker startup failure can still leave an owned desktop and generation in manager status.
        try:
            owned = payload(call("session_status"))
            if owned.get("sessionId") is not None and owned.get("generation"):
                binding = {key: owned[key] for key in ("sessionId", "generation")}
        except Exception:
            pass
    if binding is not None:
        try:
            print("CLEANUP", call("session_stop", {**binding, "logoff": True}))
        except Exception as exc:
            print("Cleanup requires review:", exc)
    process.stdin.close()
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
    if errors:
        print("STDERR", "".join(errors))
