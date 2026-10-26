# AGENTS.md

Notes for coding agents (but also humans!) working on ControlWinSwitch.

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

## Conventions

- Sources use **CRLF**.
- Do not edit the README.md, CHANGELOG.md, or AGENTS.md without specific instructions from the user.
- It is a background app: keep it at a few MB of RAM and no CPU while idle.