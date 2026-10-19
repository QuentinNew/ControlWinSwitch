# Changelog

## Unreleased

- **All controllers control menu** (Layout tab): any connected controller can drive the open menu and the settings, not only the one that opened them.
- **Paired controllers** on the Controller tab: the controllers that are not connected, last connected first, with their page (name, picture, Re-pair, Forget). **Add new controller** no longer forgets every Pro Controller that is not connected: it only pairs again one that is in sync mode.
- **Switch 2 Joy-Cons** over Bluetooth Low Energy (no pairing in Windows): a left and a right Joy-Con act as one controller, a lone one is held sideways (hold SL / SR while it connects to keep it alone). Paired from **Add new controller**, then reconnected with any button. C and Capture open the menu. Stick calibration, player lights, rumble and battery badge; a lone Joy-Con scrolls in mouse mode with SL / SR held.
- The exe now uses the Windows SDK projection (`net10.0-windows10.0.22621.0`, still running on Windows 10) for the Bluetooth LE APIs, and is about 2 MB larger.

## 1.0.0 - 2026-10-17

First release. A portable exe that lets you control a Windows PC from a Nintendo Switch Pro Controller.

- **Menu on Capture**, list or radial, with an editor for nodes, folders, icons, colors and app shortcuts
- **Mouse mode**: sticks move and scroll, A / B click, ZR / ZL change the speed, D-pad nudges.
- **Virtual Xbox 360 or DualShock 4 pad** per controller through ViGEmBus, with rumble forwarded to the controller, label- or position-based face buttons, and automatic pause while chosen processes (Steam by default) run.
- **HidHide integration**: the physical controller is hidden from games while its virtual pad is on.
- **Up to four controllers** with names, pictures, battery badges, player lights, flash stick calibration, per-stick deadzones and Bluetooth pairing from the app.
- **Power** (sleep, restart, shut down) and **Bluetooth headset** connect / disconnect.
- Tray icon, single instance, `--quit`, Launch on startup (elevated scheduled task or Run key), settings window driven by the controller or the mouse, `config.json` in `%APPDATA%\ControlWin`.

Requires Windows 10/11 x64 and ViGEmBus for the virtual pad; HidHide is optional.
