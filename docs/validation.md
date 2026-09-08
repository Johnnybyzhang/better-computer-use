# Validation and known limitations

## Checked locally

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

The workflow passed local actionlint validation and five release-policy tests, but
GitHub-hosted execution has not yet occurred. Both architectures cross-build and
pass extracted-bundle checksum/PE architecture checks locally; the x64 binary also
passes native execution checks.

Hosted build success does not establish end-to-end Windows on ARM compatibility.
The local OpenAI helper, RDP ActiveX registration, session behavior and elevation
must be checked together on a real Windows on ARM installation.

## Known limitations

- **Session stop cleanup:** a repeated-session stress test exposed an intermittent
  `session_stop` cleanup hang after Windows had logged off the desktop. This remains
  unresolved. Do not describe repeated teardown as fully validated.
- **UI:** the viewer is functional but basic. Layout, DPI behavior, clearer connection
  and control states, and general visual polish are deferred to a later version.
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
it is intentionally opt-in. `scripts/smoke_multi_task.py --live` stresses ownership
and handoff. `scripts/smoke_app_server.py --codex <CLI>` verifies actual app-server
plugin loading without model calls.
