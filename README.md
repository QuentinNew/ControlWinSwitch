# ControlWin

A tiny background app that lets you control a Windows PC from a **Nintendo Switch Pro Controller** (including Pro-style pads such as the 8BitDo in Switch mode): open a menu with the **Capture** button, drive the mouse with the sticks, launch Steam or games, start and stop BetterJoy, connect a Bluetooth headset, sleep or shut down the PC, all from the couch.

It is written in C# / .NET 10, compiled with **NativeAOT** to a single small exe (about 1 MB), and uses plain Win32 and GDI with no WinForms or WPF. Goal: a few MB of RAM and no CPU use while idle.

> **Status: work in progress.** Only the controller input layer is finished (see [Status](#status)). Everything under [Planned features](#planned-features) is not built yet.

## Status

| Feature | State |
|---|---|
| Controller reading (Bluetooth and USB) + `--debug-input` | **Done** |
| Menu overlay, Capture toggle, navigation | Planned |
| Mouse mode (sticks = mouse, A/B = clicks, right stick = scroll) | Planned |
| Config file + launching shortcuts without admin rights | Planned |
| BetterJoy start/stop | Planned |
| Power: sleep, restart, shut down | Planned |
| Bluetooth headset connect/disconnect | Planned |
| Tray icon, single instance, installer, start at logon | Planned |
| Virtual Xbox 360 pad (a BetterJoy replacement) + factory stick calibration | Planned (phase 2) |

## Requirements

- Windows 10/11, x64
- A Switch Pro Controller (VID `057E`, PID `2009`) paired over Bluetooth, or plugged in over USB
- To build: .NET SDK 10 and the **C++ build tools** (NativeAOT links with the MSVC linker)

## Quick start

### Build

```powershell
# vswhere must be on PATH, otherwise the NativeAOT link step fails with VS 2019 Build Tools
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish src/ControlWin -c Release -o dist
```

The result is `dist\ControlWin.exe`.

### Try it: `--debug-input`

```powershell
dist\ControlWin.exe --debug-input
```

A console opens and prints the decoded state **only when it changes**:

```
[Full  ] Capture | raw L(2048,2047) R(2048,2048) | norm L(+0.00,+0.00) R(+0.00,+0.00) | 30-D2-40-00-20-00-00-F8-7F-00-08-80
```

That is: report format, held buttons, raw stick values, normalized sticks (-1..1, +Y = down) and the first bytes of the report. Lines tagged `[raw changed]` or `[unknown report]` show input the decoder does not understand, which is useful for unusual pads. Everything is also written to `%APPDATA%\ControlWin\log.txt`.

Without `--debug-input` the app runs invisibly; until the tray icon exists, close it from Task Manager.

## How it reads the controller

ControlWin opens the controller's HID interface **directly** and reads it with an overlapped read driven by the message loop, so nothing polls and the app sleeps between reports.

- **Why not Raw Input?** Raw Input is served by Windows itself, so [HidHide](https://github.com/nefarius/HidHide) hides the controller from it even for allowed apps. A direct open honours HidHide's application allow-list.
- **Full mode.** In its default "simple" mode the controller does not send Capture (and, on some pads, Plus). ControlWin therefore sends the standard *set input report mode 0x30* command when it opens the device, the same one BetterJoy and Steam send, so they keep working alongside. The mode resets when the controller reconnects, and ControlWin re-sends it automatically.
- **Hot-plug.** Connect and disconnect are detected through device notifications, so you can turn the controller on and off at any time.

## HidHide

If you use HidHide (for example with BetterJoy), the cloak hides the controller from every app that is not allow-listed. Add ControlWin in the **HidHide Configuration Client** under **Applications**:

- built from source: `<repo>\dist\ControlWin.exe`
- once installed: `C:\Program Files\ControlWin\ControlWin.exe`

Without it, the log says *"Pro Controller found but access denied (error 5)"*. HidHide matches on the exe path, so re-add it if you move the exe.

## Planned features

### Menu (Capture button)

Capture opens or closes an overlay that you navigate with the D-pad or left stick; **A** selects, **B** goes back.

- Mouse mode
- Shortcuts (from the config file: Steam Big Picture, games, any exe, URI or shortcut)
- BetterJoy start/stop
- Bluetooth headset connect/disconnect
- Power: Sleep / Restart / Shut down (each with a confirmation)
- Quit

### Mouse mode

Left stick moves the cursor (ZR = fast, ZL = precise), right stick scrolls, **A** = left click (hold to drag), **B** = right click, **X** = on-screen keyboard, **Capture** = back to the menu.

### Configuration

`%APPDATA%\ControlWin\config.json` (created with defaults on first run, comments and trailing commas allowed). Shortcuts launch with normal user rights even though ControlWin itself runs elevated. Example of what is planned:

```jsonc
{
  "betterJoyPath": "C:\\Users\\you\\Downloads\\BetterJoy_v7.1\\BetterJoyForCemu.exe",
  "headsetName": "WH-CH700N",
  "mouse": { "speed": 1400, "fastMultiplier": 2.5, "slowMultiplier": 0.3, "deadzone": 0.15, "scrollSpeed": 12 },
  "shortcuts": [
    { "name": "Steam Big Picture", "target": "steam://open/bigpicture" },
    { "name": "My game", "target": "steam://rungameid/<AppID>" }
  ]
}
```

### Install

`ControlWin.exe --install` copies the exe to `C:\Program Files\ControlWin\` and registers a scheduled task that starts it at logon with administrator rights (so it can control elevated windows), no UAC prompt. `--uninstall` removes it and keeps your config.

### Phase 2: replace BetterJoy

ControlWin would expose a virtual Xbox 360 controller through [ViGEmBus](https://github.com/nefarius/ViGEmBus), so games see a standard pad with no extra tool running. While the menu or mouse mode is active, the game would receive a neutral pad, which removes the "inputs still reach the game" limitation below. Rumble, LEDs, battery display, HidHide automation and gyro are deliberately out of scope.

## Known limitations

- While the menu or mouse mode is active, the controller's inputs **still reach the game or BetterJoy's virtual pad** until the phase 2 virtual pad exists.
- UAC prompts and the Ctrl+Alt+Del screen (secure desktop) cannot be controlled.
- Games in exclusive fullscreen can hide the overlay (borderless is fine).
- If BetterJoy or Steam maps Capture to a screenshot, set it to "none" there.
- Over USB, the controller needs a handshake that ControlWin does not send: it only works if BetterJoy or Steam is running.
- Some pads do not expose their extra buttons to the PC at all (for example the 8BitDo star / L4 / R4 in Switch mode). Remap them to a standard button in the pad's own software.
- In full mode the controller streams about 100 reports per second, so CPU use is very small but not exactly zero while the controller is connected.

## Project layout

```
src/ControlWin/
  Program.cs                 arguments and the message loop
  Log.cs                     %APPDATA%\ControlWin\log.txt
  App/MainWindow.cs          hidden top-level window, device notifications
  Native/Win32.cs            all P/Invoke (hand-written, AOT friendly)
  Native/NativeWindow.cs     window base class
  Input/HidControllerReader.cs   direct HID read, full-mode request, hot-plug
  Input/ProReportParser.cs   report decoding (pure, tested)
  Input/ControllerState.cs   edges, deadzone, calibration (pure, tested)
  Input/ProCommands.cs       output reports (pure, tested)
  Input/DebugInputPrinter.cs --debug-input output
tests/ControlWin.Tests/      xUnit
```

Run the tests with `dotnet test ControlWin.slnx`.

## Credits

- [BetterJoy](https://github.com/Davidobot/BetterJoy) (MIT), the reference for what this app wants to replace.
- [dekuNukem's Nintendo Switch reverse engineering notes](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for the HID report formats.
- [HidHide](https://github.com/nefarius/HidHide) and [ViGEmBus](https://github.com/nefarius/ViGEmBus) by Nefarius.

## License

Not chosen yet.
