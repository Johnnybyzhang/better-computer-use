# Validation and known limitations

## Connected-desktop follow-up

The Windows parent desktop was subsequently connected and the following checks ran:

- The bundled outer Computer Use runtime activated/captured a real parent window,
  then discovered and captured the redesigned viewer. Real injected clicks revealed
  the PiP controls and expanded the viewer; a fresh capture showed the button change
  from Expand view to Collapse and the live child-desktop pixels.
- The inner helper discovered a dedicated child-session GUI, typed a known string,
  and returned that exact string in a fresh UI Automation document-text read.
- A second client attached as an observer, queried the inner session, was refused
  controller-only work, and detached without transferring the first client's control.
- The isolated background build passed 45 noninteractive tests. The full suite then
  ran inside the child desktop: 66 tests passed with no interactive checks skipped.
  Geometry tests cover exact desktop aspect fitting and no upscaling beyond the RDP
  desktop. Added UI regressions cover hover overlays, read-only click-out, and hidden
  window styles/taskbar restoration, non-overlapping expanded chrome, control-button
  toggling, retained human-control labeling and independent auto-collapse/resume settings.

The user took over outer testing after canceling the outer Computer Use run. No further
outer automation was attempted. User feedback drove borderless PiP, a soft alpha-blended
connection dot, a hover title bar, two rounded action buttons, an Expand/Collapse toggle,
retained topmost behavior, aspect fitting, taskbar ghost removal and a dedicated tray/app
icon. The new development build was opened for manual validation with the existing child
desktop preserved. No screenshot-tool-specific click-out exception is implemented.

Physical screenshot-tool interactions, tray/taskbar behavior, multi-monitor/DPI and
manual UAC testing remain subject to the user's connected-desktop walkthrough.

## Shared desktop and PiP revision (2026-09-11)

Checked locally on Windows x64 before pushing the PR:

- 59 tests passed with no interactive checks skipped: existing identity/IPC/elevation
  checks plus two-click takeover, native RDP input disabling, manual/automatic return,
  click-out decision logic, minimize-to-PiP, pending takeover cancellation and agent
  binding invalidation.
- The CI-mode build passed 44 tests, explicitly skipping 15 interactive/unelevated
  checks, and published the local application output with no compiler warnings.
- `python scripts/smoke_multi_task.py --live --mode user`: three independent MCP
  processes shared a host, attached as observer/controller, handed control A/B/A,
  rejected stale input, exited, and reattached to the same session and worker.
- `python scripts/smoke_viewer.py`: exercised the actual host window's click handlers
  and checked native visibility through PiP/large transitions, input pause, selecting
  another agent during human control, collapse-to-resume and read-only expansion.
  These are synthetic window messages, not physical mouse tests.
- `python scripts/smoke_preservation.py --crash-host`: a child-bound test GUI kept
  unsaved TextBox content and the same PID through agent handoff, all-client exit,
  host termination and RDP reconnection. Fresh samples after recovery matched the
  in-memory document. It also verified that an existing MCP client reconnects a
  broken host pipe with one session_start. The test closes only its own GUI, not
  the existing desktop.
- `python scripts/smoke_mcp.py`: stdio protocol, discovery and invalid-binding checks.

The outer Windows session was disconnected. Physical mouse/focus behavior, secure
UAC desktop transitions, multi-monitor/DPI behavior and live remote-pixel rendering
still need an active-desktop walkthrough. PrintWindow verified window chrome but
cannot establish DirectX RDP preview rendering in that environment. Live shared-host
checks used explicit user mode; admin/UAC mode selection was covered by the automated
suite, not a fresh manual UAC interaction in this revision. A native Git CMD launch
reported no targetable window despite starting child-session processes; it was not
retried. The preservation test used a dedicated child-bound test GUI instead of
claiming that native app discovery/launch had passed.

For normal testing on an existing desktop, use the shared-host scripts above.
`smoke_preservation.py --crash-host` deliberately terminates this build's shared host;
run it only with other test clients disconnected. The original `smoke_mcp.py --live`
is destructive lifecycle validation and refuses an existing desktop unless its
explicit recovery option is used.

## Earlier release validation

- 51 automated checks cover protocol framing, binding validation, helper transport,
  request-level authorization, process identity, suspended launch, desktop locking,
  recovery eligibility and admin/user-mode selection.
- Live Windows x64 checks have exercised child-session startup, native discovery,
  helper recovery, separate main-session UAC and an elevated application independently
  confirming that it ran in the child session.
- Disconnected-session recovery has cleared an abandoned desktop, started a fresh
  desktop, called the installed helper and logged off the disposable test session.
- Real app-server checks load the plugin and call status from three independent tasks.

The publication review live-tested persistent admin mode and explicit user mode on
Windows x64 with the packaged launcher. Both supported multiple native helper calls,
worker restart with generation rotation, and successful logoff. User mode removed
the admin tool and rejected a stale call to it. Mode-selection logic was also tested
with injected UAC outcomes; a real user-declined UAC fallback remains an interactive
validation item. These successful lifecycle runs do not establish that the previously
observed intermittent teardown hang is fixed.

## Hosted CI

The configured native Windows x64 and ARM64 jobs compile, run the noninteractive test subset, build
self-contained bundles, extract and verify all file checksums, and execute the native
binary's help entrypoint. Tests requiring an interactive RDP desktop, an unelevated
host, or suspended GUI/console startup are explicitly skipped and counted under
`-Ci`. No CI job attempts to grant UAC or requires an OpenAI installation.

The workflow passed local actionlint validation and five release-policy tests. The
initial public GitHub Actions run passed native Windows x64 and ARM64 builds, the
noninteractive tests, bundle extraction/checksums, executable architecture validation
and native help-entrypoint execution, then published both snapshot bundles. Local
x64 live checks remain separate from these hosted checks.

Hosted build success does not establish end-to-end Windows on ARM compatibility.
The local OpenAI helper, RDP ActiveX registration, session behavior and elevation
must be checked together on a real Windows on ARM installation.

## Known limitations

- **Session stop cleanup:** a repeated-session stress test exposed an intermittent
  `session_stop` cleanup hang after Windows had logged off the desktop. This remains
  unresolved. Do not describe repeated teardown as fully validated.
- **UI:** PiP and shared handoff are implemented. Physical click-out, secure-desktop
  interactions, multi-monitor/DPI transitions and interactive ARM64 need validation.
- **Private protocol:** installed OpenAI runtime updates can change executable paths,
  native tool schemas or authorization behavior. Restart after runtime updates.
- **Platform coverage:** Windows Home is excluded by default; macOS uses built-in
  Computer Use. ARM64 builds are experimental until interactive testing is completed.
- **Distribution:** binaries are unsigned. Checksums verify integrity, not publisher identity.
- **Session semantics:** apps with single-instance activation may behave differently
  across sessions. A disconnected session can keep background work running.

## Interactive checks

Use a disposable desktop. Verify main-session UAC acceptance and decline, multiple
helper calls in each mode, admin-tool removal after decline, worker restart, viewer
View/Take control, physical Escape handling, explicit admin application launch and
logoff/recovery. Do not close another task's desktop merely to make a test pass.

`scripts/smoke_mcp.py --live --mode admin` or `--mode user` runs a live helper test.
`--recover-existing` additionally authorizes closing the existing abandoned desktop;
it is intentionally opt-in. `scripts/smoke_multi_task.py --live` tests shared attachment
and handoff without logging off the desktop. `scripts/smoke_app_server.py --codex <CLI>` verifies actual app-server
plugin loading without model calls.
