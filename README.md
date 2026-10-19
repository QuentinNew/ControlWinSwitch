# ControlWin

A background app that lets you control a Windows PC from a **Nintendo Switch Pro Controller** (including Pro-style pads in Switch mode) or **Switch 2 Joy-Cons**: open a menu with the **Capture** button, drive the mouse with the sticks, launch games, expose a virtual Xbox 360 or DualShock 4 pad, connect a Bluetooth headset, sleep or shut down the PC, all keyboard and mouse free!

Written in C# / .NET 10, compiled with **NativeAOT** and uses plain Win32 and GDI. It uses a few MB of RAM and no CPU while idle.

## Features

- **Menu on Capture**: a list or radial menu overlay, fully editable (nodes, folders, icons, colors, app shortcuts).
- **Mouse mode**: sticks move the cursor and scroll, A / B click.
- **Joy-Con 2**: a left and a right Joy-Con 2 act as one controller, or one alone held sideways (see [Joy-Con 2](#joy-con-2)).
- **Virtual pad**: each controller drives its own virtual Xbox 360 or DualShock 4 pad through ViGEmBus, with rumble forwarded to the controller. Replaces alternatives like BetterJoy.
- **HidHide integration**: the physical controller is hidden from games while its virtual pad is on.
- **Up to four controllers**, each with a name, picture, battery badge, player lights, stick calibration and deadzone.
- **Shortcuts**: launch apps, URLs and Steam games (with a recently-played Steam picker).
- **Power**: sleep, restart, shut down. **Bluetooth headset** connect / disconnect. **Pair** a new Pro Controller or Joy-Con 2 from the menu.
- **Tray icon**, single instance, **Launch on startup**, settings window driven by the controller or the mouse.

## Requirements

- Windows 10/11, x64
- A Switch Pro Controller (VID `057E`, PID `2009`) paired over Bluetooth, or plugged in over USB (see [Known limitations](#known-limitations)), and / or Switch 2 Joy-Cons with a Bluetooth adapter that supports Bluetooth Low Energy
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) for the virtual pad (everything else works without it)
- Optional: [HidHide](https://github.com/nefarius/HidHide), so games see only the virtual pad

## Install

1. Download `ControlWin-<version>-win-x64.zip` from the [Releases](../../releases) page and unzip it to any folder (for example `C:\Tools\ControlWin`). There is no installer: the exe is the whole app.
2. Install ViGEmBus and HidHide.
3. Start `ControlWin.exe`. The app runs invisibly apart from its [tray icon](#tray-icon). Press **Capture** to open the [menu](#menu-capture-button).
4. To start it with Windows: Settings → **Other** → **Launch on startup**. When ControlWin runs as administrator this creates a scheduled task that starts it elevated at logon with no UAC prompt (needed to control elevated windows); otherwise it uses the per-user Run key and starts without administrator rights. To get the elevated task, run `ControlWin.exe` once as administrator and turn the setting on.

To uninstall: turn Launch on startup off, quit ControlWin and delete its folder. Settings live in `%APPDATA%\ControlWin\` (delete it too to remove everything).

## Build from source

Needs the .NET SDK 10 and the **C++ build tools** (NativeAOT links with the MSVC linker).

```powershell
# vswhere must be on PATH, otherwise the NativeAOT link step fails with VS 2019 Build Tools
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish src/ControlWin -c Release -o dist
```

The result is `dist\ControlWin.exe`. Stop any running instance first (`dist\ControlWin.exe --quit`), or the exe is locked. Run the tests with `dotnet test ControlWin.slnx`.

## Command line

| Argument | What it does |
|---|---|
| *(none)* | Starts the app. If it is already running, opens the running instance's menu and exits. |
| `--quit` | Asks the running instance to exit and waits up to 5 s for it to go. Exit code 0 when nothing is running or it exited, 1 on timeout. |

## Menu (Capture button)

Capture opens or closes the menu overlay. The overlay is topmost, never takes focus and appears on the monitor under the cursor. While it is open the game receives a neutral pad, and **Capture is never forwarded** to the game. Sleep / Restart / Shut down / Quit ask for a confirmation first.

Default entries (both layouts):

| Entry | What it does |
|---|---|
| Mouse mode | Turns the controller into a mouse (see [Mouse mode](#mouse-mode)) |
| Shortcuts ▸ | Steam Big Picture, Steam, and anything you add |
| Virtual pad (On / Off / Unavailable) | Plugs or unplugs the virtual pad |
| Headset (Connected / Disconnected) | Connects or disconnects the Bluetooth headset |
| Settings | Opens the [settings window](#settings) |
| Power ▸ Sleep / Restart / Shut down | Power actions |
| Quit ControlWin | Full exit (same as the tray) |
| Close menu | Closes the menu (in the radial menu by default; B also closes) |

The connected controllers (picture and battery) can be shown in the menu: Settings → Layout → **Show controller in menu**.

By default the menu and the settings answer only the controller that opened them. With Settings → Layout → **All controllers control menu**, any connected controller can drive them: the last one to press a button or tilt a stick has them (mouse mode stays with the controller that started it).

### List layout

Navigate with the D-pad or left stick up / down (hold to repeat); **A** selects, **B** goes back or closes. The mouse hovers and clicks the rows too.

### Radial layout

Settings → Layout → **Main menu** switches between List and Radial (applied the next time the menu opens). Tilt the left stick towards a node to highlight it (its name appears under it) and **let go to run it**; moving the stick slowly back to the center only clears the highlight. D-pad left / right steps around the ring and **A** runs the highlighted node; **B** goes back (or closes at the root); the mouse hovers, left-clicks and right-clicks. A folder opens a smaller ring around the node you picked, with a Back node on the way you came; confirmations need the same node selected twice. The root ring has 8 slots, a folder 7 (plus Back), and folders do not nest.

### Editing the menu

Settings → Layout → **Edit menu** opens the editor for the current layout (mouse driven). Drag nodes to move or swap them, **+** adds a node (app shortcut, folder, or any built-in action), double-click a folder to enter it. On the right: name, icon (default, a glyph from a grid, the program's own icon, or an image), color (HSV picker, hex, screen eyedropper), and for shortcuts the target, arguments, a recently-played **Steam games** picker and **Run as administrator**. **Save** / **Cancel** / **Reset to default**. A Settings node must stay somewhere in the menu (it is put back if it goes missing).

Shortcuts start with **normal user rights** even when ControlWin runs elevated. With Run as administrator they start elevated instead, without a UAC prompt as long as ControlWin itself runs elevated. Steam games are started by Steam: for those, Steam itself has to run as administrator.

## Mouse mode

| Input | Action |
|---|---|
| Left stick | Moves the cursor (ZR held = faster, ZL held = more precise) |
| Right stick | Scrolls (vertical and horizontal; ZR / ZL scale it too). A lone Joy-Con: its stick scrolls while L or R (SL / SR) is held |
| A | Left click (hold to drag) |
| B | Right click |
| D-pad | Nudges the cursor 1 px per tap, glides after 0.3 s |
| Capture | Leaves mouse mode |

The virtual pad gets a neutral report while mouse mode is on. Speeds and the deadzone are in Settings → Mouse.

## Settings

Opened from the menu's Settings node or the tray. Drive it with the controller (L / R switch tabs, D-pad moves, A changes, B goes back) or with the mouse.

| Tab | Contents |
|---|---|
| Controller | The four controller slots (see [Controllers](#controllers)), Add new controller (pairing), how to keep a Joy-Con alone, the paired controllers that are not connected |
| Layout | Main menu List / Radial, Edit menu, Show controller in menu, All controllers control menu, Keyboard shortcut |
| Virtual Pad | None / Xbox / PS4, Swap A / B, Swap X / Y, Rumble strength, Pad off while running |
| Mouse | Speed, fast / precise multipliers, stick deadzone, scroll speed, D-pad glide speed |
| Headset | Device name, status, connect / disconnect, Bluetooth settings |
| Other | Launch on startup, edit / reload the config file, open the log |

## Tray icon

A controller glyph appears in the taskbar notification area while ControlWin runs. Windows 11 puts new tray icons in the overflow (the `^` menu): drag it onto the taskbar to keep it visible.

- **Left click:** opens the menu, or closes it if it is already open.
- **Right click:** *Open menu*, *Virtual pad* (checked when on), *Settings*, *Edit config file*, *Open log*, *Quit ControlWin*.
- The icon is re-added automatically if explorer restarts.

### Opening and closing the app

- **Single instance.** Launching the exe a second time does not start a second copy: it opens the menu of the running one. This also works when the running copy is elevated and the new one is not.
- **Quit** (tray, menu, `--quit`, or Windows logoff / shutdown) is a full exit: the menu and toast are closed, mouse buttons are released, the **virtual pad is unplugged** (it disappears from games), the controller is shown again to other apps, the tray icon is deleted and the process ends. Quitting does not turn off Launch on startup.

## Virtual pad

ControlWin plugs a virtual **Xbox 360** or **DualShock 4** controller (Settings → Virtual Pad) into ViGEmBus for each connected Pro Controller, so games see a standard pad with no BetterJoy running. It is plugged in when the first report arrives and unplugged when the controller disconnects, when you switch it off, or when the app quits. If the process is killed, Windows removes the pad on its own. Rumble from the game is forwarded to the Pro Controller (strength 0-100 %), and each controller's player lights show its slot.

**Mapping** (by label by default: Switch **A** = Xbox A, B = B, X = X, Y = Y; turn off Swap A / B and Swap X / Y to map by position):

| Pro Controller | Xbox 360 | DualShock 4 |
|---|---|---|
| A / B / X / Y | A / B / X / Y | Cross / Circle / Square / Triangle |
| D-pad | D-pad | D-pad |
| L / R | LB / RB | L1 / R1 |
| ZL / ZR | LT / RT (digital: 0 or 255) | L2 / R2 |
| Left / right stick (+ click) | Left / right stick (+ LS / RS) | Left / right stick (+ L3 / R3) |
| Plus / Minus | Start / Back | Options / Share |
| Home | Guide | PS |
| Capture | never forwarded (opens the menu) | never forwarded |

**Behaviour**

- A button still held (or a stick still tilted) when you close the menu stays hidden from the game until you release it, so closing the menu with B does not press B in the game.
- Switch it on or off from the menu or the tray. If ViGEmBus is missing the menu shows "Unavailable" and a toast tells you; the rest of the app keeps working.
- **Pad off while running**: the pad is switched off while one of the listed processes runs (default `steam.exe`, since Steam Input reads the controller itself) and back on when it exits. A manual toggle wins.
- **Do not run BetterJoy at the same time**: games would see two pads.
- No `ViGEmClient.dll` is shipped: the app talks to the bus driver directly with IOCTLs.

## HidHide

While the virtual pad is on, ControlWin hides each Pro Controller that drives a pad from every other app, so games only see the virtual pad. It drives the installed [HidHide](https://github.com/nefarius/HidHide) driver itself: the controller goes on the blacklist, ControlWin's own exe (wherever it is) on the application allow-list, and the cloak is switched on. When the pad turns off (or a controller disconnects) it is shown again. Controllers without a virtual pad are left alone.

- Needs HidHide installed. Its control device is exclusive, so close the HidHide Configuration Client while the pad is on, or hiding fails (it is logged and a toast appears; it retries on the next change). Without HidHide the pad still works.
- Switch it off with `virtualPad.hidePhysical` in the config file.
- ControlWin rewrites HidHide's blacklist and cloak state, so entries you added in the HidHide client (for BetterJoy for example) are replaced while it is hiding.
- HidHide only blocks new opens: an app that already had the controller open (Steam, BetterJoy) keeps receiving it until it reconnects. Quit it first.

## Controllers

The **Controller** tab of the settings shows the four controller slots. Each connected controller has a tile with its picture (or a gamepad icon), its name and a small battery badge: four bars (green when full or medium, amber when low, red when critical or empty) and a lightning bolt while it charges. The Pro Controller only reports four levels, not a percentage.

Click a tile (or press **A** on it) to open that controller's page: **Name**, **Test rumble**, **Calibrate deadzone** and **Back**, with the connected controllers kept on top and a ring around the one being edited. Click another tile, or press **ZL** / **ZR** for the previous / next one, to edit another controller. Only on that page can a picture be changed: hover the picture of the controller being edited, and a pencil appears on it (click it to choose a PNG, JPG or BMP; the picture is scaled down to 128 pixels and shown with rounded corners) and, when it has a picture, a red cross at the top right that removes it. **B** or a right click goes back.

- **Stick calibration** is read from the controller's own flash (user calibration if present, otherwise factory), so the sticks reach their full range without a warm-up. **Calibrate deadzone** sets a per-stick deadzone for a stick that drifts.
- **Add new controller** (in the Controller tab or as a menu node) pairs a Pro Controller or a Joy-Con 2 over Bluetooth: hold its sync button while ControlWin searches. A Pro Controller Windows already knows is paired again when it is in sync mode (its old pairing is stale after it was used on a Switch); the others are left alone.
- **Paired controllers**, at the bottom of the Controller tab, lists the controllers that are not connected, last connected first (how long ago on each tile). Hover a tile: its picture opens its page, the cross on its top right forgets it; with the controller, **A** edits and **X** forgets. Its page edits the name and picture, **Re-pair controller** pairs it again (hold its sync button) and **Forget controller** forgets it. Forgetting unpairs a Pro Controller from Windows; a Joy-Con 2 is only dropped from the list and comes back the next time it connects. The name and picture are kept either way.
- Controllers are told apart by their Bluetooth MAC address, which ControlWin reads from the controller itself, so a profile follows the controller to any slot and over USB or Bluetooth. Names and deadzones are stored in `config.json` under `controllers`; pictures are copied to `%APPDATA%\ControlWin\controllers\`. A profile is kept while the controller is away; to forget one, delete its entry (and picture file) by hand.

## Joy-Con 2

Switch 2 Joy-Cons talk Bluetooth Low Energy, not HID, so ControlWin connects to them itself (protocol from [switch2-controllers](https://github.com/Nadeflore/switch2-controllers)). **Do not pair them in Windows' Bluetooth settings**; if you already did, remove them there first.

- **Pairing**: choose **Add new controller**, then hold the Joy-Con's sync button. ControlWin writes this PC's address into it, so from then on pressing any button on it reconnects it (no search needed). Attaching it to the Switch 2 pairs it back to the console.
- **A pair**: a left and a right Joy-Con join into one controller slot, mapped like a Pro Controller. Both **Capture** and **C** open the menu.
- **Alone, sideways**: a Joy-Con alone is held sideways (stick on the left, SL / SR on top). Its buttons are turned so A / B / X / Y sit where they do on a Pro Controller, SL / SR are L / R, its shoulder buttons are ZL / ZR, and its stick is the left stick. On the left one Minus is Plus; Capture (left) or C (right) opens the menu.
- Hold **SL / SR** while a Joy-Con connects to keep it alone even when its partner is around; otherwise a Joy-Con joins a lone Joy-Con of the other side. When half of a pair disconnects, the other carries on alone, sideways, in the same slot, and the partner rejoins when it comes back.
- Each Joy-Con keeps its own profile (by its address): its stick's deadzone stays with it whichever Joy-Con it is paired with; a pair's name is the left Joy-Con's.
- Stick calibration, player lights, rumble and the battery badge (the emptier Joy-Con of a pair; estimated from its voltage) work like on the Pro Controller. A pair's tile shows both Joy-Cons' pictures side by side, turned upright (the left one a quarter clockwise, the right one a quarter counterclockwise, for pictures of Joy-Cons held sideways); on its controller page, hover one half to change that Joy-Con's picture. They are not HID devices, so HidHide has nothing to hide.
- The low-latency connection is requested on Windows 11 only.

## Configuration file

`%APPDATA%\ControlWin\config.json`, created with defaults on first run. Comments and trailing commas are allowed, but saving from the settings rewrites the file without comments. Everything is editable from the settings; the file is reloaded when the menu opens if it changed (Settings → Other → Reload config file forces it).

## Known limitations

- While the menu or mouse mode is open the virtual pad is neutral, so games see nothing.
- Games in exclusive fullscreen can hide the overlay (borderless is fine).
- The virtual pad needs ViGEmBus.
- Gyro is not supported, nor the Joy-Con 2 mouse sensor.
- Pro Controller 2 and the GameCube controller for Switch 2 are not supported yet.

## Project layout

```
src/ControlWin/
  Program.cs                   arguments, single-instance mutex, message loop
  Log.cs                       %APPDATA%\ControlWin\log.txt
  App/                         MainWindow (hidden window, tray, device notifications), AppController (Idle / Menu /
                               Mouse / Settings modes), AppMessages, MenuModel, MenuTree, RadialModel, MenuEditorModel,
                               SettingsModel, MouseMode, ProcessWatch, DeadzoneCalibration (models are pure, tested)
  Config/                      AppConfig (config.json, source-generated JSON), MenuRepair (defaults and repair)
  Input/                       HidControllerReader (direct HID read), ControllerSet (four slots), ProReportParser,
                               ControllerState, StickCalibration, ProCommands, XInputMapper, Ds4Mapper,
                               JoyCon2Hub / JoyCon2Device (Bluetooth LE), JoyConSlot, JoyCon2Protocol, JoyCon2Mapper
  Actions/                     VirtualPad (ViGEmBus IOCTLs), HidHide, UnelevatedLauncher, PowerActions,
                               BluetoothHeadset, BluetoothPairing, SteamLibrary, SendInputMouse, ControllerPictures, FilePicker
  Setup/Startup.cs             Launch on startup (scheduled task or Run key)
  UI/                          MenuOverlay, RadialOverlay, MenuEditorWindow, SettingsWindow, ControllerTile, RadialIcons,
                               Toast, TrayIcon, Gdi
  Native/                      Win32 (all P/Invoke, hand-written, AOT friendly), NativeWindow, HighResTimer
tests/ControlWin.Tests/        xUnit
```

## Credits

- [BetterJoy](https://github.com/Davidobot/BetterJoy) (MIT), the reference for what this app replaces.
- [dekuNukem's Nintendo Switch reverse engineering notes](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for the HID report formats.
- [switch2-controllers](https://github.com/Nadeflore/switch2-controllers) by Nadeflore for the Joy-Con 2 Bluetooth LE protocol.
- [HidHide](https://github.com/nefarius/HidHide) and [ViGEmBus](https://github.com/nefarius/ViGEmBus) by Nefarius.

## License

MIT, see [LICENSE](LICENSE).
