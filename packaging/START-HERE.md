# Better Computer Use ? pre-built Windows bundle

1. Download the ZIP for your Windows architecture: **x64** or **ARM64**.
2. Extract the entire ZIP and run **Install.cmd** as a standard user.
3. Open a **new task** in the installed ChatGPT/Codex desktop app.
4. Ask the agent to start a separate desktop with Better Computer Use.

The installer checks every bundled file, preserves other plugins and backs up the
previous plugin version. No compiler, Python or separate .NET installation is needed.
This is a desktop-app plugin; it cannot be installed into the ChatGPT website.

OpenAI's Computer Use executable is not included. Initialize Computer Use in your
own compatible app installation so its extracted runtime exists. Windows RDP child
sessions, Task Scheduler and a compatible RDP ActiveX control are required. ARM64
builds still need full interactive compatibility validation on Windows on ARM.

Session startup defaults to elevated Computer Use, with UAC on the **main desktop**.
Declining UAC or selecting user mode uses an unelevated helper and removes admin
tools. Explicit admin application launch uses separate main-session UAC. No extra
per-app or per-click approval windows are added.

The skill prefers this backend on Windows unless requested otherwise. Windows Home
is excluded unless explicitly requested; macOS uses its built-in Computer Use.
Only one task owns the child desktop at a time, while other MCP tasks keep their tools.
View lets automation run; Take control pauses automation for manual input. Logoff
closes that child desktop's applications and unsaved work.

Verify an extracted bundle without installing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./Install.ps1 -VerifyOnly
```

If CLI discovery fails, run `Install.ps1 -CodexPath <absolute path to codex.exe>`.
Read `better-computer-use/README.md` and `docs/validation.md` for behavior and limits.
