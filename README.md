# ControlWin

A tiny background app that lets you control a Windows PC from a **Nintendo Switch Pro Controller** (including Pro-style pads such as the 8BitDo in Switch mode): open a menu with the **Capture** button, drive the mouse with the sticks, launch Steam or games, expose a virtual Xbox 360 pad, connect a Bluetooth headset, sleep or shut down the PC, all from the couch.

It is written in C# / .NET 10, compiled with **NativeAOT** to a single small exe (about 1 MB), and uses plain Win32 and GDI with no WinForms or WPF. Goal: a few MB of RAM and no CPU use while idle.

> **Status: work in progress.** Controller input, the menu overlay, the virtual pad and the tray icon are built. Mouse mode, config, power, headset and the installer are not (see [Status](#status)).

## Status

| Feature | State |
|---|---|
| Controller reading (Bluetooth and USB) + `--debug-input` | **Done** |
| Menu overlay, Capture toggle, navigation (only Virtual pad and Quit are wired, other entries are stubs) | **Done** |
| Virtual Xbox 360 pad via ViGEmBus (needs ViGEmBus; stop BetterJoy first) | **Done** (needs real-pad validation) |
| Tray icon, single instance, `--quit`, full close | **Done** (tray look and explorer restart not yet checked by hand) |
| Mouse mode (sticks = mouse, A/B = clicks, right stick = scroll) | Planned |
| Config file + launching shortcuts without admin rights | Planned |
| Power: sleep, restart, shut down | Planned |
| Bluetooth headset connect/disconnect (name hardcoded to `WH-CH700N` until the config file) | **Done** |
| Installer (scheduled task at logon, Start menu shortcut) | Planned |
| Factory stick calibration | Planned (phase 2) |

## Requirements

- Windows 10/11, x64
- A Switch Pro Controller (VID `057E`, PID `2009`) paired over Bluetooth, or plugged in over USB
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) for the virtual pad (the rest works without it)
- To build: .NET SDK 10 and the **C++ build tools** (NativeAOT links with the MSVC linker)

## Quick start

### Build

```powershell
# vswhere must be on PATH, otherwise the NativeAOT link step fails with VS 2019 Build Tools
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish src/ControlWin -c Release -o dist
```

The result is `dist\ControlWin.exe`. Stop any running instance first (`dist\ControlWin.exe --quit`), or the exe is locked.

### Run

Start `dist\ControlWin.exe`. The app runs invisibly apart from its [tray icon](#tray-icon). Press **Capture** to open the [menu](#menu-capture-button).

## Command line

| Argument | What it does |
|---|---|
| *(none)* | Starts the app. If it is already running, opens the running instance's menu and exits. |
| `--quit` | Asks the running instance to exit and waits up to 5 s for it to go. Works from a normal (non-elevated) prompt even when the app runs elevated. Exit code 0 when nothing is running or it exited, 1 on timeout. |
| `--debug-input` | Opens a console that prints the decoded controller state (see below). |

### `--debug-input`

```powershell
dist\ControlWin.exe --debug-input
```

The console prints the decoded state **only when it changes**:

```
[Full  ] Capture | raw L(2048,2047) R(2048,2048) | norm L(+0.00,+0.00) R(+0.00,+0.00) | 30-D2-40-00-20-00-00-F8-7F-00-08-80
```

That is: report format, held buttons, raw stick values, normalized sticks (-1..1, +Y = down) and the first bytes of the report. Lines tagged `[raw changed]` or `[unknown report]` show input the decoder does not understand, which is useful for unusual pads. Everything is also written to `%APPDATA%\ControlWin\log.txt`. Press Ctrl+C to quit.

## Menu (Capture button)

Capture opens or closes an overlay that you navigate with the D-pad or left stick up/down (hold to repeat); **A** selects, **B** goes back or closes. Sleep / Restart / Shut down / Quit ask for a confirmation first. The overlay is topmost, never takes focus, ignores the mouse, and appears on the monitor under the cursor.

| Entry | State |
|---|---|
| Mouse mode | stub (toast "not implemented yet") |
| Shortcuts | stub (empty submenu) |
| Virtual pad (shows On / Off / Unavailable) | **works** |
| Headset | stub |
| Power ▸ Sleep / Restart / Shut down | stub (confirmation works, no action yet) |
| Quit ControlWin | **works** (same full close as the tray) |

While the menu is open the game receives a neutral pad, and **Capture is never forwarded** to the game.

### Radial layout

Settings → Layout → **Main menu** switches between the list and a radial menu (`"menuLayout": "list" | "radial"` in `config.json`, applied the next time the menu opens). In the radial menu, tilt the left stick towards a node to highlight it (its name appears under it) and **let go to run it**; moving the stick slowly back to the center only clears the highlight. D-pad left / right steps around the ring and **A** runs the highlighted node; **B** goes back (or closes at the root); the mouse hovers, left-clicks and right-clicks as in the list. A submenu opens a smaller ring around the node you picked, with a Back node on the way you came; confirmations need the same node selected twice. `radialReleaseMs` (default 100) is how fast the stick must return to the center to count as "let go".

## Tray icon

A controller glyph appears in the taskbar notification area while ControlWin runs. Windows 11 puts new tray icons in the overflow (the `^` menu): drag it onto the taskbar to keep it visible.

- **Tooltip:** "ControlWin - controller connected" or "controller not found", kept up to date as the pad connects and disconnects.
- **Left click:** opens the menu, or closes it if it is already open.
- **Right click:** *Open menu*, *Virtual pad* (checked when on), *Open log* (in Notepad), *Quit ControlWin*.
- The icon is re-added automatically if explorer restarts.

### Opening and closing the app

- **Single instance.** Launching the exe a second time does not start a second copy: it opens the menu of the running one. This also works when the running copy is elevated and the new one is not.
- **Quit** (tray, menu, `--quit`, or Windows logoff / shutdown) is a full exit: the menu and toast are closed, the **virtual pad is unplugged** (it disappears from games), the controller handle is closed, the tray icon is deleted and the process ends. Quitting does not disable autostart (not built yet): the app comes back at the next logon.

## Virtual Xbox 360 pad

ControlWin plugs a virtual **Xbox 360 controller** into ViGEmBus, so XInput games see a standard pad with no BetterJoy running. It is plugged in when the first report arrives and unplugged when the controller disconnects, when you switch it off, or when the app quits. If the process is killed, Windows removes the pad on its own (closing the bus handle unplugs it).

**Mapping** (by label, not by position: Switch **A** = Xbox A, B = B, X = X, Y = Y):

| Pro Controller | Xbox 360 |
|---|---|
| A / B / X / Y | A / B / X / Y (by label) |
| D-pad | D-pad |
| L / R | LB / RB |
| ZL / ZR | LT / RT (digital: 0 or 255) |
| Left / right stick (+ click) | Left / right stick (+ LS / RS) |
| Plus / Minus | Start / Back |
| Home | Guide |
| Capture | never forwarded (opens the menu) |

**Behaviour**

- A button still held (or a stick still tilted) when you close the menu stays hidden from the game until you release it, so closing the menu with B does not press B in the game.
- Switch it on or off from the menu or the tray. If ViGEmBus is missing the menu shows "Unavailable" and a toast tells you; the rest of the app keeps working.
- **Do not run BetterJoy at the same time**: games would see two pads. Stop it by hand.
- Implementation note: no `ViGEmClient.dll` is shipped. The app talks to the bus driver directly with IOCTLs.

## How it reads the controller

ControlWin opens the controller's HID interface **directly** and reads it with an overlapped read driven by the message loop, so nothing polls and the app sleeps between reports.

- **Why not Raw Input?** Raw Input is served by Windows itself, so [HidHide](https://github.com/nefarius/HidHide) hides the controller from it even for allowed apps. A direct open honours HidHide's application allow-list.
- **Full mode.** In its default "simple" mode the controller does not send Capture (and, on some pads, Plus). ControlWin therefore sends the standard *set input report mode 0x30* command when it opens the device, the same one BetterJoy and Steam send, so they keep working alongside. The mode resets when the controller reconnects, and ControlWin re-sends it automatically.
- **Hot-plug.** Connect and disconnect are detected through device notifications, so you can turn the controller on and off at any time.

## HidHide

While the virtual pad is on, ControlWin hides each Pro Controller that drives a pad from every other app, so games only see the Xbox pad. It drives the installed [HidHide](https://github.com/nefarius/HidHide) driver itself: the controller goes on the blacklist, ControlWin's own exe on the application allow-list, and the cloak is switched on. When the pad turns off (or a controller disconnects) it is shown again. Controllers without a virtual pad are left alone.

- Needs HidHide installed. Its control device is exclusive, so close the HidHide Configuration Client while the pad is on, or hiding fails (it is logged and a toast appears; it retries on the next change). Without HidHide the pad still works.
- Switch it off with `VirtualPad.HidePhysical` in the config file.
- ControlWin rewrites HidHide's blacklist and cloak state, so entries you added in the HidHide client (for BetterJoy for example) are replaced while it is hiding.
- HidHide only blocks new opens: an app that already had the controller open (Steam, BetterJoy) keeps receiving it until it reconnects. Quit it first.
- If ControlWin crashes while hiding, the controller stays hidden until you start ControlWin again (or turn the cloak off in the HidHide client).

## Controllers

The **Controller** tab of the settings shows the four controller slots. Each connected controller has a tile with its picture (or a gamepad icon), its name and a small battery badge: four bars (green when full or medium, amber when low, red when critical or empty) and a plus while it charges. The Pro Controller only reports four levels, not a percentage.

Click a tile (or press **A** on it) to open that controller's page. The four slots stay on top, empty ones included, with a ring around the controller being edited; click another tile, or move up to the tiles and press left / right, to edit another connected controller. Below: **Name**, **Choose picture...** (a standard Open dialog; PNG, JPG or BMP; the whole picture is kept, scaled down to 128 pixels, and shown with rounded corners; the row shows the file name and a cross, or left on the pad, to remove it), **Test rumble** and **Back**. **B** or a right click goes back.

Controllers are told apart by their Bluetooth MAC address, which ControlWin reads from the controller itself, so a profile follows the controller to any slot and over USB or Bluetooth. Until the MAC is known the Windows device id is used instead, and the profile moves to the MAC once it arrives. Names are stored in `config.json` under `controllers`; pictures are copied to `%APPDATA%\ControlWin\controllers\`, so the original file can move or vanish. A profile is kept while the controller is away; to forget one, delete its entry (and picture file) by hand.

## Log

`%APPDATA%\ControlWin\log.txt` (also reachable from the tray). Truncated when it grows past 256 KB. First place to look when "nothing happens": it says whether the controller was opened, whether the menu opened, and why the virtual pad failed.

## Planned features

### Mouse mode

Left stick moves the cursor (ZR = fast, ZL = precise), right stick scrolls, **A** = left click (hold to drag), **B** = right click, **X** = on-screen keyboard, **Capture** = back to the menu. The virtual pad gets a neutral report while it is active.

### Configuration

`%APPDATA%\ControlWin\config.json` (created with defaults on first run, comments and trailing commas allowed). Shortcuts launch with normal user rights even though ControlWin itself runs elevated; a shortcut with `"runAsAdmin": true` (the "Run as administrator" switch in the radial editor) starts elevated instead, without a UAC prompt as long as ControlWin was started elevated (scheduled task / its shortcut). Steam games are started by Steam: for those, Steam itself has to run as administrator. Example of what is planned:

```jsonc
{
  "headsetName": "WH-CH700N",
  "virtualPad": { "enabled": true, "swapAB": true, "swapXY": true },
  "mouse": { "speed": 1400, "fastMultiplier": 2.5, "slowMultiplier": 0.3, "deadzone": 0.15, "scrollSpeed": 12 },
  "shortcuts": [
    { "name": "Steam Big Picture", "target": "steam://open/bigpicture" },
    { "name": "My game", "target": "steam://rungameid/<AppID>" }
  ]
}
```

Until then the virtual pad settings are constants in the code (`enabled`, label-based face buttons).

### Install

`ControlWin.exe --install` copies the exe to `C:\Program Files\ControlWin\` and registers a scheduled task that starts it at logon with administrator rights (so it can control elevated windows), no UAC prompt, plus a Start menu shortcut (pinnable to the taskbar) that starts it without a UAC prompt. `--uninstall` removes both and keeps your config.

Battery display and gyro are deliberately out of scope.

## Known limitations

- While the menu is open the virtual pad is neutral, so XInput games see nothing. The physical controller is hidden from games by HidHide (see above), so they only see the pad.
- UAC prompts and the Ctrl+Alt+Del screen (secure desktop) cannot be controlled.
- Games in exclusive fullscreen can hide the overlay (borderless is fine).
- If Steam (Steam Input) or BetterJoy maps Capture to a screenshot, set it to "none" there.
- Over USB, the controller needs a handshake that ControlWin does not send: it only works if BetterJoy or Steam is running.
- Some pads do not expose their extra buttons to the PC at all (for example the 8BitDo star / L4 / R4 in Switch mode). Remap them to a standard button in the pad's own software.
- In full mode the controller streams about 100 reports per second, so CPU use is very small but not exactly zero while the controller is connected.
- The virtual pad needs ViGEmBus (retired by its author, but working).

## Project layout

```
src/ControlWin/
  Program.cs                 arguments, single-instance mutex, message loop
  Log.cs                     %APPDATA%\ControlWin\log.txt
  App/MainWindow.cs          hidden top-level window: device notifications, tray, app messages, session end
  App/AppMessages.cs         registered messages (ShowMenu, Quit, TaskbarCreated), --quit
  App/AppController.cs       Idle / Menu modes: consumes or forwards input, owns the virtual pad, Shutdown()
  App/MenuModel.cs           menu tree and state machine (pure, tested)
  Native/Win32.cs            all P/Invoke (hand-written, AOT friendly)
  Native/NativeWindow.cs     window base class
  Input/HidControllerReader.cs   direct HID read, full-mode request, hot-plug
  Input/ProReportParser.cs   report decoding (pure, tested)
  Input/ControllerState.cs   edges, deadzone, calibration (pure, tested)
  Input/ProCommands.cs       output reports (pure, tested)
  Input/XInputMapper.cs      Pro Controller state -> Xbox 360 report (pure, tested)
  Input/DebugInputPrinter.cs --debug-input output
  Actions/VirtualPad.cs      ViGEmBus client (direct IOCTLs)
  UI/MenuOverlay.cs          the menu window (GDI, double buffered)
  UI/Toast.cs                short messages
  UI/TrayIcon.cs             tray icon, tooltip, context menu
  UI/Gdi.cs                  palette, fonts per DPI, paint buffer
tests/ControlWin.Tests/      xUnit
```

Run the tests with `dotnet test ControlWin.slnx`.

## Credits

- [BetterJoy](https://github.com/Davidobot/BetterJoy) (MIT), the reference for what this app replaces.
- [dekuNukem's Nintendo Switch reverse engineering notes](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for the HID report formats.
- [HidHide](https://github.com/nefarius/HidHide) and [ViGEmBus](https://github.com/nefarius/ViGEmBus) by Nefarius.

## License

Not chosen yet.
