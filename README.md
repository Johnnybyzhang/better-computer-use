# Better Computer Use

Keep steering your agent while it uses Windows applications on a separate desktop.

Computer use on Windows takes control of the user's mouse. That makes it awkward
to steer the model while automation is running. Existing tools mitigate this by
placing the agent and its harness in a separate runtime. That does not fully solve
the steering problem: you still have to take control back from computer use to
steer the agent, or rely on an awkward remote setup.

Better Computer Use keeps the agent, MCP connection and steering UI in your normal
Windows session. The installed OpenAI Computer Use executable and automated apps
run in a Windows child session, with their own desktop and input. You can continue
using the main desktop to talk to the agent. An optional **View / Take control**
window lets you inspect or interact with the child desktop.

This is an early independent integration with a private, version-dependent OpenAI
executable protocol. UI polish is deferred; see [validation and known limitations](docs/validation.md).

## Credits

- **[BetterGI](https://github.com/babalae/better-genshin-impact)** is the source of
  the child-session approach used here, particularly its `Service/ChildSession`
  implementation and session-targeted process launch pattern.
- **[ParaDesk](https://github.com/sinpoce/ParaDesk)** is related work: a separate
  Windows desktop with independent input while sharing the user's account and files.

Their reference checkouts are not redistributed. See [NOTICE.md](NOTICE.md) for
attribution and licensing boundaries.

## Install

Download the **Windows x64** or **Windows ARM64** ZIP from this repository's Releases
page, extract it, and run `Install.cmd`. Start a new ChatGPT/Codex task afterward.
The installer verifies bundle checksums and installs a per-user local plugin,
preserving other marketplace entries and backing up the previous plugin version.

Self-contained bundles include .NET. They **do not include OpenAI's executable**:
you need a compatible local ChatGPT/Codex installation with Computer Use initialized.
The wrapper discovers its extracted `cua_node` runtime under LocalAppData.

Windows child-session support, Task Scheduler and the RDP ActiveX control are required.
The skill prefers this backend on Windows, excludes Windows Home unless explicitly
requested, and uses built-in Computer Use on macOS. A Home override does not add
missing Windows capabilities. ARM64 builds are produced by CI; full native ARM64
RDP/helper compatibility still requires interactive verification on Windows on ARM.

## How it works

```text
Main Windows session (standard user)
  Agent / steering UI ? MCP adapter + child-session manager
                        ?? optional View / Take control viewer
                        ?? UAC broker for elevated startup
                               ? authenticated session-bound IPC
                               ?
Child Windows session
  Persistent worker ? locally installed codex-computer-use.exe
                       ?? applications being automated
```

There can be any number of MCP processes. Only child-desktop ownership is exclusive:
`session_start` takes the lock, and stop/exit releases it. Other tasks retain their
tool inventory and receive a busy result rather than losing their MCP connection.

Session initialization defaults to **admin mode**. A UAC request on the main desktop
starts an elevated child worker and elevated Computer Use executable. Ordinary
computer-use calls reuse that worker. `launch_process_as_admin` requests separate
main-session UAC for an explicit application launch.

Selecting `mode:"user"`, or declining initialization UAC, starts **user-space mode**.
Administrator tools are removed and stale calls to them are rejected. Other startup
errors remain errors. Mode and UAC-decline state are returned in status/start results.
There are no extra app, click or wrapper approval dialogs. Windows UAC remains in
the main session; the backend never clicks it on the user's behalf.

## Tools and lifecycle

| Tool | Behavior |
| --- | --- |
| `session_status` | Mode, worker/helper identity and existing-child recovery information. |
| `session_start` | Create a child desktop; `mode` is `admin` (default) or `user`. |
| `session_viewer` | Show or hide the viewer. Local Take control pauses automation. |
| `session_restart_worker` | Restart the helper and rotate generation without closing apps. Admin mode requests new UAC. |
| `session_stop` | Disconnect by default; `logoff:true` closes the owned desktop and its apps. |
| `session_logoff` | Clear an unowned, disconnected child desktop before starting fresh. |
| Native named tools / `computer_use` | Forward the bundled helper's public Windows capture, UI, input, app and audio operations. |
| `launch_process_as_admin` | Admin mode only; verify a suspended application in the child session before letting it execute. |

Every owned-session operation takes the returned `sessionId` and `generation`.
Discover window IDs inside that session. `end_turn` ends a helper turn, not desktop
ownership. Never replay ambiguous failed input or launch operations automatically.

For abandoned desktops, read `session_status.existingChildSession`. If `canLogoff`
is true and replacement is intended, call `session_logoff` with its `sessionId`, then
`session_start`. Logoff closes that desktop's apps and unsaved work. Busy or connected
desktops cannot be cleared this way. Host shell commands still run on the main desktop;
use child-bound tools for application launches, including diagnostic invocations.

## Develop

Requires Windows and the .NET 10 SDK. Run from a standard interactive user session:

```powershell
./scripts/build.ps1 -Test
./artifacts/app/BetterComputerUse.exe doctor --auto-helper
./artifacts/app/BetterComputerUse.exe mcp --auto-helper
./scripts/package.ps1 -Runtime win-x64
./scripts/package.ps1 -Runtime win-arm64
./scripts/verify-release.ps1
```

Cross-built ARM64 bundles can be checked on x64 with `verify-release.ps1 -SkipExecution`.
Run native execution checks on matching hardware. `mcp` reserves stdout for protocol
messages. Use `--helper <absolute installed executable path>` for an explicit helper.
The optional legacy `--allow-elevation` alias is unnecessary for normal admin-mode
computer use and remains unavailable in user mode.

[Contributing](CONTRIBUTING.md) ? [Release automation](docs/releases.md) ?
[Validation](docs/validation.md) ? [Security](SECURITY.md)

## Scope

Child sessions share the account and filesystem. They separate interactive desktops;
they are not a VM or a security boundary against code running as your user or as admin.
OpenAI runtime changes can break the adapter. Unsigned snapshot builds are intended
for testing. The wrapper is [MIT licensed](LICENSE); third-party components retain
their own licenses.
