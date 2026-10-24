# AGENTS.md

Notes for coding agents working on ControlWinSwitch. User-facing docs are in [README.md](README.md), release notes in [CHANGELOG.md](CHANGELOG.md).

## What it is

A background Windows app (C# / .NET 10, NativeAOT, plain Win32 + GDI, no UI framework) that drives the PC from a Switch Pro Controller or Switch 2 Joy-Cons: menu overlay on Capture, mouse mode, virtual Xbox 360 / DS4 pad through ViGEmBus, HidHide, Bluetooth pairing, power and headset actions.

## Where things are on disk

| What | Path |
|---|---|
| **Log** | `%APPDATA%\ControlWinSwitch\log.txt` (written by [Log.cs](src/ControlWinSwitch/Log.cs)) |
| Config | `%APPDATA%\ControlWinSwitch\config.json` |
| Controller pictures | `%APPDATA%\ControlWinSwitch\controllers\<MAC>.png` |
| Built exe | `dist\ControlWinSwitch.exe` (git-ignored) |

The log is the first place to look when the user reports a bug or a crash. Unhandled exceptions are logged as `ERROR Fatal` ([Program.cs](src/ControlWinSwitch/Program.cs)); each launch logs `Started <version> (elevated=..., hwnd=...)`. If the log shows no fatal error, check the Windows Event Log (Application, `.NET Runtime` / `Application Error`) before assuming a crash.

## Source layout

- `src/ControlWinSwitch/App`: `AppController` (the central state machine: modes Menu / Mouse / Settings / Editor), menu, radial, settings and editor models.
- `src/ControlWinSwitch/Input`: HID reading, Pro Controller reports and subcommands, Joy-Con 2 (BLE) protocol, stick calibration, XInput / DS4 mappers.
- `src/ControlWinSwitch/Actions`: side effects (virtual pad, HidHide, Bluetooth pairing / headset, power, SendInput mouse, Steam library, window actions).
- `src/ControlWinSwitch/UI`: GDI windows (menu overlay, radial overlay, settings, editor, toast, tray icon).
- `src/ControlWinSwitch/Config`: `AppConfig` (config.json), hotkeys, menu repair.
- `src/ControlWinSwitch/Native`: Win32 interop, message window, high-resolution timer.
- `src/ControlWinSwitch/Setup`: Launch on startup (scheduled task or Run key).
- `tests/ControlWinSwitch.Tests`: xUnit tests for the pure models and parsers.

## Build, run, test

Git Bash (vswhere must be on PATH, or the NativeAOT link step fails):

```bash
export PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer"
dist/ControlWinSwitch.exe --quit                       # the running exe locks dist
dotnet publish src/ControlWinSwitch -c Release -o dist
schtasks //Run //TN ControlWinSwitch                   # relaunch elevated, no UAC
grep "Started" "$APPDATA/ControlWinSwitch/log.txt" | tail -1   # expect elevated=True
```

- Rebuild `dist` and relaunch after every code change.
- Always run from the main checkout's `dist\ControlWinSwitch.exe`: it is the only path on HidHide's allow-list. Any other path cannot read the pad (`access denied (error 5)` in the log). Don't use git worktrees.
- Never start the exe directly from the shell: use the `ControlWinSwitch` scheduled task so it runs elevated.
- Tests: `dotnet test ControlWinSwitch.slnx`, only before a commit or when a test was added or changed.

## Conventions

- Sources use **CRLF**. Edit with an editor tool, not `sed -i` (Git Bash strips the CRs and the whole file shows as changed).
- Work on `master`. Commit only when asked, with plain messages and no Claude / AI attribution trailers.
- If the user edited a file, keep their changes: never re-add text they removed.
- It is a background app: keep it at a few MB of RAM and no CPU while idle.
