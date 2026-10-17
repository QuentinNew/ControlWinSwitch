using ControlWin.Actions;
using ControlWin.Config;
using ControlWin.Input;
using ControlWin.Setup;
using ControlWin.UI;
using static ControlWin.Native.Win32;

namespace ControlWin.App;

/// <summary>
/// The brain: decides for every controller report whether it is consumed by the app (menu, settings or
/// mouse mode open) or forwarded to the virtual pad (idle). Also owns the config and the mouse-driven UI events.
/// </summary>
internal sealed class AppController : IDisposable, ISettingsHost
{
    private const nuint RepeatTimerId = 1, WatchTimerId = 4;
    // One timer per controller slot: the settings rumble test (10..13) and the game rumble refresh (20..23).
    private const nuint RumbleTimerBase = 10, GameRumbleTimerBase = 20;
    private const uint RumbleMs = 400, GameRumbleRefreshMs = 80;
    private const uint WatchPollMs = 5000, WatchFastPollMs = 1000, WatchFastWindowMs = 15000;
    private const uint RepeatFirstMs = 400, RepeatNextMs = 110;
    private const float StickEngage = 0.6f, StickRelease = 0.4f;

    private enum Mode { Idle, Menu, Mouse, Settings, Editor }

    private enum Dir { None, Up, Down, Left, Right }

    private readonly SendInputMouse _mouseOutput = new();
    private readonly MouseMode _mouse;
    private char _mouseGlyph;
    private readonly ControllerSet _set;

    /// <summary>What belongs to one controller: its state, its virtual pad and what a game asks of its motors.</summary>
    private sealed class Slot
    {
        public readonly ControllerState State = new();
        public readonly VirtualPad Pad = new();
        public Buttons IgnoreMask; // buttons still held from the menu: hidden from the game until released
        public bool IgnoreSticks;  // sticks used in the menu: hidden until back near center
        public byte GameLarge, GameSmall;
    }

    private readonly Slot[] _slots = new Slot[ControllerSet.Slots];
    private int _owner; // the controller that has the menu / mouse mode / settings (the last one heard while idle)
    private Slot Owner => _slots[_owner];
    private ControllerState _state => Owner.State;
    private VirtualPad _pad => Owner.Pad;
    private Buttons _ignoreMask { get => Owner.IgnoreMask; set => Owner.IgnoreMask = value; }
    private bool _ignoreSticks { get => Owner.IgnoreSticks; set => Owner.IgnoreSticks = value; }
    private readonly MainWindow _window;
    private readonly MenuModel _menu = new();
    private readonly MenuOverlay _overlay = new();
    private readonly RadialModel _radial = new();
    private readonly RadialOverlay _radialOverlay = new();
    private readonly RadialEditorWindow _radialEditor = new();
    private readonly RadialStick _radialStick = new(100);
    private bool _radialShown;   // the open menu is the radial one (layout is read when the menu opens)
    private readonly SettingsWindow _settingsWindow = new();
    private readonly SettingsModel _settings;
    private readonly Toast _toast = new();
    private readonly ConfigStore _store = new();
    private readonly PadGate _gate = new();

    private nint _watchHandle;        // the watched process we wait on (0 = none, polling instead)
    private bool _watchRunning;
    private string _watchName = "";   // list entry of the process that matched
    private long _fastPollUntil;      // Environment.TickCount64 until which the poll is fast (after a shortcut launch)

    private bool _pairing;
    private bool _padEnabled;
    private bool _padFailed;
    private bool _shutDown;
    private HeadsetState _headsetState;
    private bool _headsetBusy;

    private Mode _mode;
    private Dir _heldDirection;
    private bool _repeatTimerArmed;

    public AppController(ControllerSet set, MainWindow window)
    {
        _set = set;
        _window = window;
        _mouse = new MouseMode(() => _state, _mouseOutput, () => _store.Current.Mouse);
        _settings = SettingsPages.Create(_store, this);
        for (int i = 0; i < _slots.Length; i++)
        {
            int slot = i;
            _slots[slot] = new Slot();
            _slots[slot].Pad.Feedback += (large, small) => OnPadFeedback(slot, large, small);
        }

        var error = _store.Load();
        if (error is not null) _toast.Show('', error);

        // Start-up: a watched process may already be running.
        _watchRunning = ProbeProcesses();
        _padEnabled = _gate.Apply(Config.VirtualPad.Enabled, _watchRunning);
        UpdateWatchTimer();

        _overlay.RowHovered += OnMenuRowHovered;
        _overlay.RowClicked += OnMenuRowClicked;
        _overlay.BackRequested += OnMenuBackRequested;
        _overlay.CloseRequested += () => { if (_mode == Mode.Menu) CloseMenu(); };
        _overlay.Scrolled += OnMenuScrolled;
        _overlay.ConfirmAnswered += OnMenuConfirmAnswered;
        _radialOverlay.NodeHovered += OnRadialNodeHovered;
        _radialOverlay.NodeClicked += OnRadialNodeClicked;
        _radialOverlay.BackRequested += () => { if (_mode == Mode.Menu && _radialShown) BackMenu(); };
        _settingsWindow.Tile = TileOf;
        _settingsWindow.PictureEditRequested += slot =>
        {
            ((ISettingsHost)this).ChooseControllerPicture(slot);
            _settingsWindow.Refresh();
        };
        _settingsWindow.PictureRemoveRequested += slot =>
        {
            ((ISettingsHost)this).RemoveControllerPicture(slot);
            _settingsWindow.Refresh();
        };
        _settingsWindow.CloseRequested += () => { if (_mode == Mode.Settings) CloseSettings(); };
        _radialEditor.Saved += OnRadialEditorSaved;
        _radialEditor.Cancelled += () => { if (_mode == Mode.Editor) CloseRadialEditor(); };
    }

    private AppConfig Config => _store.Current;

    // ---- loop hooks --------------------------------------------------------------------------

    /// <summary>Waitable timer handle for the message loop; signalled only while a stick moves the mouse.</summary>
    public nint PadNotifyHandle(int slot) => _slots[slot].Pad.NotifyHandle;

    public void OnPadNotify(int slot) => _slots[slot].Pad.OnNotifySignaled();

    public ControllerState StateOf(int slot) => _slots[slot].State;

    public nint MouseTimerHandle => _mouseOutput.TimerHandle;

    /// <summary>The mouse timer fired.</summary>
    public void OnMouseTick()
    {
        if (_mode == Mode.Mouse) _mouse.Tick();
        else _mouse.Deactivate(); // stray tick: make sure the timer is stopped
    }

    /// <summary>PC going to sleep or resuming: nothing may stay pressed, and the old state is stale.</summary>
    public void OnPowerChange()
    {
        foreach (var slot in _slots)
        {
            slot.State.Reset();
            slot.IgnoreMask = Buttons.None;
            slot.IgnoreSticks = false;
        }
        if (_shutDown) return;
        if (_mode == Mode.Mouse) ExitMouse(announce: false);
    }

    /// <summary>Report handler: updates the shared state first, so later subscribers (the debug printer) see it.</summary>
    public void OnReport(int slot, ProReport report, ReadOnlyMemory<byte> raw)
    {
        _slots[slot].State.Update(report);
        if (_shutDown) return; // reports still queued while the window closes must not re-plug the pad

        if (_mode == Mode.Settings && _settings.Tab.Calibration is { } calibration && calibration.Slot == slot)
        {
            calibration.Feed();
            long now = Environment.TickCount64;
            if (now - _lastCalibrationPaint >= 30) // the sticks report faster than the screen needs
            {
                _lastCalibrationPaint = now;
                _settingsWindow.Refresh();
            }
        }

        // The menu, settings and mouse mode belong to one controller; the others keep playing.
        if (_mode == Mode.Idle) _owner = slot;
        else if (slot != _owner)
        {
            Forward(slot);
            return;
        }

        // Capture is reserved for the menu toggle and is never forwarded.
        if (_state.Pressed(Buttons.Capture))
        {
            switch (_mode)
            {
                case Mode.Menu: CloseMenu(); break;
                case Mode.Mouse: ExitMouse(announce: true); break;
                case Mode.Settings: CloseSettings(); break;
                case Mode.Editor: break; // the editor is mouse only
                default: OpenMenu(); break;
            }
            return;
        }

        switch (_mode)
        {
            case Mode.Menu:
                HandleMenuInput();
                break;
            case Mode.Settings:
                HandleSettingsInput();
                break;
            case Mode.Editor:
                break;
            case Mode.Mouse:
                _mouse.OnReport();
                break;
            default:
                Forward(slot);
                break;
        }
    }

    public void OnControllerRemoved(int slot)
    {
        var gone = _slots[slot];
        gone.State.Reset();
        gone.State.ClearCalibration();
        _window.UpdateTray();
        if (_shutDown) return;
        gone.Pad.Detach(); // re-plugged on the next report
        SyncHiding();
        if (_settings.InSubPage && _pageStrip?.Slot == slot) CloseControllerPage();
        if (slot == _owner)
        {
            if (_mode == Mode.Menu) CloseMenu();
            else if (_mode == Mode.Settings) CloseSettings();
            else if (_mode == Mode.Mouse) ExitMouse(announce: false);
        }
        gone.IgnoreMask = Buttons.None;
        gone.IgnoreSticks = false;
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    /// <summary>WM_TIMER of the main window: key-repeat of the held direction.</summary>
    public void OnTimer(nuint id)
    {
        if (id == WatchTimerId)
        {
            CheckProcesses();
            return;
        }
        if (id >= GameRumbleTimerBase && id < GameRumbleTimerBase + (nuint)_slots.Length)
        {
            ApplyGameRumble((int)(id - GameRumbleTimerBase), restartTimer: false);
            return;
        }
        if (id >= RumbleTimerBase && id < RumbleTimerBase + (nuint)_slots.Length)
        {
            KillTimer(_window.Handle, id);
            _window.SetRumble((int)(id - RumbleTimerBase), false);
            return;
        }
        if (id != RepeatTimerId) return;
        bool menu = _mode == Mode.Menu, settings = _mode == Mode.Settings;
        if ((!menu && !settings) || _heldDirection == Dir.None)
        {
            StopRepeat();
            return;
        }

        // Holding left / right must not flip a toggle over and over: only numbers repeat.
        if (settings && _heldDirection is Dir.Left or Dir.Right && _settings.CurrentItem?.Kind != SettingKind.Number)
        {
            StopRepeat();
            return;
        }

        ApplyDirection(_heldDirection);
        SetTimer(_window.Handle, RepeatTimerId, RepeatNextMs, 0); // same id: replaces the timer with the faster interval
    }

    // ---- menu --------------------------------------------------------------------------------

    private void OpenMenu()
    {
        if (_mode == Mode.Mouse) _mouse.Deactivate(); // tray / second launch while the mouse is on
        if (_mode == Mode.Settings) CloseSettings();

        ReloadConfigIfChanged();
        var root = MenuPages.Root(PadDetail, HeadsetDetail, BuildShortcutsPage);
        _radialShown = Config.MenuLayout == MenuLayouts.Radial;
        _pad.SubmitNeutral(); // neutral pad while the menu owns the input
        _mode = Mode.Menu;
        if (_radialShown)
        {
            _radial.Reset(RadialTree.ToPage("ControlWin", Config.RadialMenu ?? [], PadDetail, HeadsetDetail));
            _radialStick.ReleaseMs = Config.RadialReleaseMs;
            _radialStick.CardinalBias = Config.RadialCardinalBias * MathF.PI / 180f;
            _radialStick.Reset(); // a stick already tilted must come back to the center first
            _heldDirection = Dir.None;
            _radialOverlay.Show(_radial);
        }
        else
        {
            _menu.Reset(root);
            _heldDirection = ReadDirection(horizontal: false); // a stick already tilted must be released first, not scroll on open
            _overlay.Show(_menu);
        }
        Log.Info(_radialShown ? "Menu opened (radial)" : "Menu opened");
        StartHeadsetWork(toggle: false); // refresh the status shown in the menu
    }

    private void CloseMenu()
    {
        StopRepeat();
        _heldDirection = Dir.None;
        _mode = Mode.Idle;
        _ignoreMask = _state.Current;
        _ignoreSticks = true;
        HideMenu();
        Log.Info("Menu closed");
    }

    private void HideMenu()
    {
        if (_radialShown) _radialOverlay.Hide();
        else _overlay.Hide();
    }

    /// <summary>Repaints whichever menu layout is open.</summary>
    private void RefreshMenu()
    {
        if (_radialShown) _radialOverlay.Refresh();
        else _overlay.Refresh();
    }

    private MenuPage BuildShortcutsPage()
    {
        var list = Config.Shortcuts;
        if (list.Count == 0)
            return new MenuPage("Shortcuts",
                [new MenuEntry { Label = "No shortcuts - edit config.json", Glyph = MenuPages.Play, Enabled = false }]);

        var entries = new List<MenuEntry>(list.Count);
        for (int i = 0; i < list.Count; i++)
            entries.Add(new MenuEntry { Label = list[i].Name, Glyph = MenuPages.Play, Command = new(CommandKind.Shortcut, i) });
        return new MenuPage("Shortcuts", entries);
    }

    private void HandleMenuInput()
    {
        if (_radialShown)
        {
            HandleRadialInput();
            return;
        }

        TrackDirection(horizontal: false);

        if (_state.Pressed(Buttons.A)) AcceptMenu();
        else if (_state.Pressed(Buttons.B)) BackMenu();
    }

    // ---- radial menu -------------------------------------------------------------------------

    private void HandleRadialInput()
    {
        // Left stick: tilt highlights, a quick release runs the highlighted node.
        int before = _radialStick.Selected;
        int released = _radialStick.Update(_state.Left, Environment.TickCount64, _radial.Ring);
        if (released >= 0)
        {
            _radial.Select(released);
            AcceptRadial();
            return;
        }

        // Only a change made by the stick touches the highlight, so a D-pad selection survives a resting stick.
        bool changed = _radialStick.Selected != before && _radial.Select(_radialStick.Selected);

        if (_state.Pressed(Buttons.Left)) { _radial.Step(-1); changed = true; }
        else if (_state.Pressed(Buttons.Right)) { _radial.Step(1); changed = true; }

        if (_state.Pressed(Buttons.A))
        {
            AcceptRadial();
            return;
        }
        if (_state.Pressed(Buttons.B))
        {
            BackMenu();
            return;
        }

        if (changed) _radialOverlay.Refresh();
    }

    private void AcceptRadial()
    {
        var node = _radial.CurrentNode;
        var result = _radial.Activate();
        if (result is { } run)
        {
            if (run.Command.Kind == CommandKind.OpenSettings)
            {
                // Straight from the menu to the settings: no close-then-open flicker.
                Log.Info($"Menu command: {CommandKind.OpenSettings} ({run.Entry.Label})");
                OpenSettings();
            }
            else
            {
                CloseMenu();
                Execute(run.Entry, run.Command);
            }
            return;
        }

        if (node is not null) _radialStick.Reset(); // the ring changed under the stick: start from the center again
        _radialOverlay.Refresh();
    }

    private void OnRadialNodeHovered(int index)
    {
        if (_mode == Mode.Menu && _radialShown && _radial.Select(index)) _radialOverlay.Refresh();
    }

    private void OnRadialNodeClicked(int index)
    {
        if (_mode != Mode.Menu || !_radialShown) return;
        _radial.Select(index);
        if (_radial.CurrentNode is not null) AcceptRadial();
    }

    private void AcceptMenu()
    {
        var entry = _menu.CurrentEntry;
        var command = _menu.Accept();
        if (command is { Kind: CommandKind.OpenSettings } && entry is not null)
        {
            // Straight from the menu to the settings: no close-then-open flicker.
            Log.Info($"Menu command: {CommandKind.OpenSettings} ({entry.Label})");
            OpenSettings();
        }
        else if (command is { } c && entry is not null)
        {
            CloseMenu();
            Execute(entry, c);
        }
        else
        {
            _overlay.Refresh();
        }
    }

    private void BackMenu()
    {
        if (_radialShown)
        {
            if (_radial.Back())
            {
                _radialStick.Reset();
                _radialOverlay.Refresh();
            }
            else CloseMenu();
            return;
        }

        if (_menu.Back()) _overlay.Refresh();
        else CloseMenu();
    }

    // Mouse on the menu.
    private void OnMenuRowHovered(int index)
    {
        if (_mode == Mode.Menu && _menu.Select(index)) _overlay.Refresh();
    }

    private void OnMenuRowClicked(int index)
    {
        if (_mode != Mode.Menu || _menu.IsConfirming) return;
        var entries = _menu.Page.Entries;
        if (index < 0 || index >= entries.Count || !entries[index].Enabled) return;
        _menu.Select(index);
        AcceptMenu();
    }

    private void OnMenuBackRequested()
    {
        if (_mode == Mode.Menu) BackMenu();
    }

    private void OnMenuScrolled(int direction)
    {
        if (_mode != Mode.Menu) return;
        _menu.Move(direction);
        _overlay.Refresh();
    }

    private void OnMenuConfirmAnswered(bool yes)
    {
        if (_mode != Mode.Menu || !_menu.IsConfirming) return;
        if (yes) AcceptMenu();
        else BackMenu();
    }

    // ---- settings ----------------------------------------------------------------------------

    /// <summary>Opens the settings window (menu entry, tray); no-op when it is already open.</summary>
    public void ShowSettings()
    {
        if (_shutDown || _mode is Mode.Settings or Mode.Editor) return;
        OpenSettings();
    }

    private void OpenSettings()
    {
        if (_mode == Mode.Mouse) _mouse.Deactivate();
        bool fromMenu = _mode == Mode.Menu;
        if (fromMenu) StopRepeat();

        ReloadConfigIfChanged();
        _pad.SubmitNeutral();
        _heldDirection = ReadDirection(horizontal: true);
        _mode = Mode.Settings;
        _settingsWindow.Show(_settings);
        if (fromMenu) HideMenu(); // hidden only once the settings window is up
        Log.Info("Settings opened");
        StartHeadsetWork(toggle: false); // refresh the status shown on the Headset tab
    }

    private void CloseSettings()
    {
        StopRepeat();
        _heldDirection = Dir.None;
        _mode = Mode.Idle;
        _ignoreMask = _state.Current;
        _ignoreSticks = true;
        _settingsWindow.Hide();
        Log.Info("Settings closed");
    }

    private void HandleSettingsInput()
    {
        if (_settingsWindow.IsEditing)
        {
            // Typing happens on the keyboard; the pad only confirms or cancels.
            if (_state.Pressed(Buttons.A)) _settingsWindow.EndEdit(commit: true);
            else if (_state.Pressed(Buttons.B)) _settingsWindow.EndEdit(commit: false);
            return;
        }

        TrackDirection(horizontal: true);

        // On a controller's page ZL / ZR edit the previous / next connected controller.
        if (_settings.InSubPage && _pageStrip is { } controllers)
        {
            int step = _state.Pressed(Buttons.ZR) ? 1 : _state.Pressed(Buttons.ZL) ? -1 : 0;
            if (step != 0)
            {
                controllers.Adjust?.Invoke(step);
                _settingsWindow.Refresh();
            }
        }

        if (_state.Pressed(Buttons.L))
        {
            _settings.MoveTab(-1);
            _settingsWindow.Refresh();
        }
        if (_state.Pressed(Buttons.R))
        {
            _settings.MoveTab(1);
            _settingsWindow.Refresh();
        }

        if (_settings.Tab.Calibration is { } calibration)
        {
            if (_state.Pressed(Buttons.X)) calibration.StartAuto();
            else if (_state.Pressed(Buttons.A) || _state.Pressed(Buttons.B)) _settings.Back();
            _settingsWindow.Refresh();
            return;
        }

        if (_state.Pressed(Buttons.A))
        {
            _settings.Activate();
            if (_mode == Mode.Settings) _settingsWindow.Refresh();
        }
        else if (_state.Pressed(Buttons.B))
        {
            if (_settings.InSubPage) CloseControllerPage();
            else CloseSettings();
        }
    }

    // ---- direction handling (D-pad / left stick, with key-repeat) ------------------------------

    private void TrackDirection(bool horizontal)
    {
        var direction = ReadDirection(horizontal);
        if (direction == _heldDirection) return;

        _heldDirection = direction;
        StopRepeat();
        if (direction == Dir.None) return;

        ApplyDirection(direction);
        _repeatTimerArmed = true;
        SetTimer(_window.Handle, RepeatTimerId, RepeatFirstMs, 0);
    }

    private void ApplyDirection(Dir direction)
    {
        if (_mode == Mode.Menu)
        {
            if (direction is Dir.Up or Dir.Down)
            {
                _menu.Move(direction == Dir.Up ? -1 : 1);
                _overlay.Refresh();
            }
        }
        else if (_mode == Mode.Settings)
        {
            if (_settings.Tab.Calibration is { } calibration)
            {
                switch (direction)
                {
                    case Dir.Up: calibration.Adjust(1); break;
                    case Dir.Down: calibration.Adjust(-1); break;
                    case Dir.Left: calibration.MoveStick(-1); break;
                    case Dir.Right: calibration.MoveStick(1); break;
                }
                _settingsWindow.Refresh();
                return;
            }
            switch (direction)
            {
                case Dir.Up: _settings.Move(-1); break;
                case Dir.Down: _settings.Move(1); break;
                case Dir.Left: _settings.Adjust(-1); break;
                case Dir.Right: _settings.Adjust(1); break;
            }
            if (_mode == Mode.Settings) _settingsWindow.Refresh();
        }
    }

    /// <summary>Direction from the D-pad or the left stick (with hysteresis so a wobbling stick doesn't flicker).</summary>
    private Dir ReadDirection(bool horizontal)
    {
        var held = _state.Current;
        if ((held & Buttons.Up) != 0) return Dir.Up;
        if ((held & Buttons.Down) != 0) return Dir.Down;
        if (horizontal)
        {
            if ((held & Buttons.Left) != 0) return Dir.Left;
            if ((held & Buttons.Right) != 0) return Dir.Right;
        }

        if (_mode == Mode.Settings && _settings.Tab.Calibration is not null) return Dir.None; // the sticks are being looked at, not used

        float threshold = _heldDirection != Dir.None ? StickRelease : StickEngage;
        var stick = _state.Left; // +Y = down
        if (horizontal && MathF.Abs(stick.X) > MathF.Abs(stick.Y))
        {
            if (stick.X <= -threshold) return Dir.Left;
            if (stick.X >= threshold) return Dir.Right;
            return Dir.None;
        }
        if (stick.Y <= -threshold) return Dir.Up;
        if (stick.Y >= threshold) return Dir.Down;
        return Dir.None;
    }

    private void StopRepeat()
    {
        if (!_repeatTimerArmed) return;
        KillTimer(_window.Handle, RepeatTimerId);
        _repeatTimerArmed = false;
    }

    // ---- mouse mode --------------------------------------------------------------------------

    private void EnterMouse(char glyph)
    {
        _mouseGlyph = glyph;
        _pad.SubmitNeutral(); // neutral pad while the mouse owns the input
        _mode = Mode.Mouse;
        _mouse.Activate(_state.Current);
        _toast.Show(glyph, "Mouse mode - Capture to exit");
        Log.Info("Mouse mode on");
    }

    private void ExitMouse(bool announce)
    {
        _mouse.Deactivate(); // releases left / right buttons
        _mode = Mode.Idle;
        _ignoreMask = _state.Current;
        _ignoreSticks = true;
        if (announce) _toast.Show(_mouseGlyph, "Mouse mode: off");
        Log.Info("Mouse mode off");
    }

    // ---- commands ----------------------------------------------------------------------------

    private void Execute(MenuEntry entry, MenuCommand command)
    {
        Log.Info($"Menu command: {command.Kind} ({entry.Label})");
        switch (command.Kind)
        {
            case CommandKind.Quit:
                PostMessageW(_window.Handle, WM_CLOSE, 0, 0);
                break;

            case CommandKind.MouseMode:
                EnterMouse(entry.Glyph);
                break;

            case CommandKind.OpenSettings:
                OpenSettings();
                break;

            case CommandKind.Shortcut:
                LaunchShortcut(command.Arg);
                break;

            case CommandKind.Launch:
                if (entry.Launch is { } launch) LaunchTarget(launch);
                break;

            case CommandKind.CloseMenu:
                break; // the menu is already closed

            case CommandKind.ToggleVirtualPad:
                TogglePad(entry.Glyph);
                break;

            case CommandKind.ToggleHeadset:
                if (_headsetBusy) break;
                _toast.Show(entry.Glyph, _headsetState == HeadsetState.Connected ? "Disconnecting headset..." : "Connecting headset...");
                StartHeadsetWork(toggle: true);
                break;

            case CommandKind.Sleep:
            case CommandKind.Restart:
            case CommandKind.Shutdown:
                RunPower(PowerActions.FromCommand(command.Kind)!.Value, entry);
                break;

            default:
                _toast.Show(entry.Glyph, $"{entry.Label}: not implemented");
                break;
        }
    }

    /// <summary>The menu is already closed. Mouse mode is off and the pad state cleared before the PC goes away.</summary>
    private void RunPower(PowerAction action, MenuEntry entry)
    {
        if (_mode == Mode.Mouse) ExitMouse(announce: false);
        _state.Reset();
        _toast.Show(entry.Glyph, action switch
        {
            PowerAction.Sleep => "Sleeping...",
            PowerAction.Restart => "Restarting...",
            _ => "Shutting down...",
        });

        // Sleep blocks until the PC wakes, so it never runs on the UI thread.
        new Thread(() =>
        {
            var error = PowerActions.Run(action);
            if (error is not null)
            {
                Log.Error($"Power {action}: {error}");
                PostMessageW(_window.Handle, AppMessages.PowerFailed, 0, 0);
            }
        }) { IsBackground = true, Name = "power" }.Start();
    }

    /// <summary>AppMessages.PowerFailed, on the UI thread.</summary>
    public void OnPowerFailed()
    {
        if (!_shutDown) _toast.Show(MenuPages.Power, "Power action failed - see log");
    }

    private void LaunchShortcut(int index)
    {
        var list = Config.Shortcuts;
        if (index < 0 || index >= list.Count) return;

        LaunchTarget(list[index]);
    }

    private void LaunchTarget(ShortcutConfig shortcut)
    {
        _toast.Show(MenuPages.Play, $"Launching {shortcut.Name}");
        var error = UnelevatedLauncher.Launch(shortcut.Target, shortcut.Args, shortcut.WorkingDir, shortcut.RunAsAdmin == true);
        if (error is not null)
        {
            Log.Error($"Shortcut '{shortcut.Name}': {error}");
            _toast.Show('', $"{shortcut.Name}: {error}");
            return;
        }

        // The launched program may be a watched one: look again right away and then every second for a while.
        _fastPollUntil = Environment.TickCount64 + WatchFastWindowMs;
        CheckProcesses();
    }

    // ---- virtual pad -------------------------------------------------------------------------

    public bool PadEnabled => _padEnabled;

    public void TogglePad(char glyph)
    {
        if (_shutDown) return;
        UserSetPad(!_padEnabled);
        _toast.Show(glyph, _padFailed ? "Virtual pad unavailable (ViGEmBus?)"
            : _padEnabled ? "Virtual pad: on" : "Virtual pad: off");
        if (_mode == Mode.Menu) RefreshMenu(); // the pad row shows its state
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    /// <summary>
    /// The user's choice (menu, tray, settings). It wins over the process watcher; it is only saved to the
    /// config while no watched process runs, so a choice made during Steam stays runtime-only.
    /// </summary>
    private void UserSetPad(bool enabled)
    {
        _gate.ManualOverride();
        SetPadEnabled(enabled);
        if (!_watchRunning)
        {
            Config.VirtualPad.Enabled = enabled;
            _store.Save();
        }
    }

    /// <summary>Note shown next to the pad in the menu and tray while a watched process holds it off.</summary>
    public string? PadNote => _gate.HeldOff ? ProcessList.DisplayName(_watchName) : null;

    // ---- process watcher ---------------------------------------------------------------------

    /// <summary>Handle of the watched process for the message loop (0 = none).</summary>
    public nint WatchHandle => _watchHandle;

    /// <summary>The watched process exited (its handle was signalled).</summary>
    public void OnWatchedProcessExited()
    {
        CloseWatchHandle();
        CheckProcesses();
    }

    private void CloseWatchHandle()
    {
        if (_watchHandle == 0) return;
        CloseHandle(_watchHandle);
        _watchHandle = 0;
    }

    /// <summary>Looks for a watched process and keeps the wait handle in line. Returns whether one runs.</summary>
    private bool ProbeProcesses()
    {
        var names = Config.VirtualPad.DisableWhileRunning;
        if (names.Count == 0)
        {
            CloseWatchHandle();
            return false;
        }

        int pid = ProcessList.FindRunning(names, out var matched);
        if (pid == 0)
        {
            CloseWatchHandle();
            return false;
        }

        _watchName = matched;
        if (_watchHandle == 0) _watchHandle = OpenProcess(SYNCHRONIZE, 0, (uint)pid); // 0 on failure: the poll covers it
        return true;
    }

    /// <summary>Poll tick, process exit or list change: apply the result to the pad.</summary>
    private void CheckProcesses()
    {
        if (_shutDown) return;
        bool running = ProbeProcesses();
        UpdateWatchTimer();
        if (running == _watchRunning) return;
        _watchRunning = running;

        string name = ProcessList.DisplayName(_watchName);
        Log.Info($"Watched process {name}: {(running ? "running" : "closed")}");
        switch (_gate.OnRunningChanged(running, _padEnabled))
        {
            case PadGate.Change.Disable:
                SetPadEnabled(false);
                break;
            case PadGate.Change.Enable:
                SetPadEnabled(true);
                break;
        }
        if (_mode == Mode.Menu) RefreshMenu();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    /// <summary>Poll only while there is something to wait for and no handle to wait on.</summary>
    private void UpdateWatchTimer()
    {
        bool poll = Config.VirtualPad.DisableWhileRunning.Count > 0 && _watchHandle == 0;
        if (!poll)
        {
            KillTimer(_window.Handle, WatchTimerId);
            return;
        }

        uint interval = Environment.TickCount64 < _fastPollUntil ? WatchFastPollMs : WatchPollMs;
        SetTimer(_window.Handle, WatchTimerId, interval, 0); // same id: replaces the interval
    }

    private PadKind ConfiguredKind => Config.VirtualPad.Type == PadTypes.Ps4 ? PadKind.Ps4 : PadKind.Xbox;

    private void SetPadEnabled(bool enabled)
    {
        _padEnabled = enabled;
        _padFailed = false;
        var kind = ConfiguredKind;
        for (int i = 0; i < _slots.Length; i++)
        {
            var pad = _slots[i].Pad;
            if (pad.IsAttached && pad.PluggedKind != kind) pad.Detach();
            pad.Kind = kind;
            if (!enabled) _slots[i].Pad.Detach();
            else if (_set.Readers[i].IsOpen && !_slots[i].Pad.Attach()) _padFailed = true;
        }
        SyncHiding();
    }

    // ---- hiding the physical controllers (HidHide) -----------------------------------------

    private string _hiddenKey = "";   // the set of paths last applied (to skip identical requests)
    private bool _hideFailed;

    /// <summary>Hides the controllers that currently drive a virtual pad, and shows every other one again.</summary>
    private void SyncHiding()
    {
        var wanted = new List<string>();
        if (_padEnabled && !_padFailed && !_shutDown && Config.VirtualPad.HidePhysical)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_set.Readers[i].Path is { } path && _slots[i].Pad.IsAttached) wanted.Add(path);
        }
        wanted.Sort(StringComparer.OrdinalIgnoreCase);
        string key = string.Join('|', wanted);
        if (key == _hiddenKey && !_hideFailed) return;

        var error = HidHide.Apply(wanted);
        _hiddenKey = key;
        bool failedBefore = _hideFailed;
        _hideFailed = error is not null;
        if (error is not null)
        {
            Log.Error($"Hiding the physical controller: {error}");
            if (!failedBefore) _toast.Show('', error);
        }
    }

    private string? PadDetail() => !_padEnabled ? (_gate.HeldOff ? $"Off ({ProcessList.DisplayName(_watchName)})" : "Off") : _padFailed ? "Unavailable" : "On";

    /// <summary>The state is not consumed: it drives the virtual pad.</summary>
    private void Forward(int index)
    {
        var slot = _slots[index];
        if (!_padEnabled || _padFailed) return;
        if (!slot.Pad.IsAttached && !slot.Pad.Attach())
        {
            _padFailed = true;
            _toast.Show(MenuPages.Game, "Virtual pad unavailable (ViGEmBus?)");
            return;
        }
        if (_set.Readers[index].Path is { } path && !_hiddenKey.Contains(path, StringComparison.OrdinalIgnoreCase)) SyncHiding(); // just attached

        var state = slot.State;
        slot.IgnoreMask &= state.Current;
        var left = state.Left;
        var right = state.Right;
        if (slot.IgnoreSticks)
        {
            if (left.Magnitude < 0.3f && right.Magnitude < 0.3f) slot.IgnoreSticks = false;
            else left = right = default;
        }

        if (_set.Readers[index].Id is { } id && Config.Controllers.TryGetValue(id, out var profile))
        {
            left = left.WithDeadzone(profile.LeftDeadzone);
            right = right.WithDeadzone(profile.RightDeadzone);
        }

        var held = state.Current & ~slot.IgnoreMask;
        var pad = Config.VirtualPad;
        if (slot.Pad.PluggedKind == PadKind.Ps4) slot.Pad.Submit(Ds4Mapper.Map(held, left, right, pad.SwapAB, pad.SwapXY));
        else slot.Pad.Submit(XInputMapper.Map(held, left, right, pad.SwapAB, pad.SwapXY));
    }

    // ---- config ------------------------------------------------------------------------------

    private void ReloadConfigIfChanged()
    {
        if (_store.ReloadIfChanged(out var error))
        {
            Log.Info("Config reloaded");
            ApplyConfig();
        }
        else if (error is not null)
        {
            _toast.Show('', error);
        }
    }

    /// <summary>Brings the running parts in line with the current config.</summary>
    private void ApplyConfig()
    {
        // A file edit / reload is a fresh start for the watcher too (the list may have changed).
        _watchRunning = ProbeProcesses();
        bool enabled = _gate.Apply(Config.VirtualPad.Enabled, _watchRunning);
        UpdateWatchTimer();
        if (enabled != _padEnabled) SetPadEnabled(enabled);
        else if (_padEnabled && _slots.Any(s => s.Pad.IsAttached && s.Pad.PluggedKind != ConfiguredKind)) SetPadEnabled(true); // the kind changed
        else SyncHiding(); // the Hide setting may have changed
        for (int i = 0; i < _slots.Length; i++) ApplyGameRumble(i, restartTimer: false); // the Rumble setting may have changed (0 stops it at once)
    }

    /// <summary>A game set a virtual pad's motors: forward them to its Pro Controller, scaled by the Rumble setting.</summary>
    private void OnPadFeedback(int slot, byte large, byte small)
    {
        _slots[slot].GameLarge = large;
        _slots[slot].GameSmall = small;
        ApplyGameRumble(slot, restartTimer: true);
    }

    /// <summary>
    /// The controller keeps vibrating only while it keeps receiving rumble reports: refresh them while the game
    /// asks for any, and send a neutral one when it stops.
    /// </summary>
    private void ApplyGameRumble(int index, bool restartTimer)
    {
        var slot = _slots[index];
        int percent = Config.VirtualPad.Rumble;
        bool active = percent > 0 && (slot.GameLarge > 0 || slot.GameSmall > 0);
        _window.SetRumbleMotors(index, active ? slot.GameLarge : (byte)0, active ? slot.GameSmall : (byte)0, percent);
        nuint timer = GameRumbleTimerBase + (nuint)index;
        if (!active) KillTimer(_window.Handle, timer);
        else if (restartTimer) SetTimer(_window.Handle, timer, GameRumbleRefreshMs, 0);
    }

    // ISettingsHost
    string ISettingsHost.ConfigPath => _store.Path;

    string ISettingsHost.HeadsetStatus => _headsetBusy ? "Working..." : HeadsetDetail() ?? "Unknown";

    bool ISettingsHost.ControllerConnected(int slot) => _window.ControllerConnected(slot);

    void ISettingsHost.RumbleController(int slot)
    {
        if (!_window.SetRumble(slot, true)) return;
        SetTimer(_window.Handle, RumbleTimerBase + (nuint)slot, RumbleMs, 0);
    }

    void ISettingsHost.OpenDeadzoneCalibration()
    {
        if (_pageStrip is { } strip) _settings.OpenSub(SettingsPages.DeadzonePage(this, strip));
    }

    (Stick Left, Stick Right) ISettingsHost.ControllerSticks(int slot) => (_slots[slot].State.Left, _slots[slot].State.Right);

    /// <summary>The stick calibration of a controller was read from its flash.</summary>
    public void OnCalibration(int slot, StickCalibrations calibration)
    {
        _slots[slot].State.SetCalibration(calibration);
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    string ISettingsHost.PairingStatus => _pairing ? "Searching..." : "";

    // ---- per-controller profile (name, picture, battery) -----------------------------------

    private long _lastCalibrationPaint;
    private SettingItem? _pageStrip; // the strip row of the open controller page: its Slot is the controller being edited

    /// <summary>
    /// The id a controller's profile is stored under: its MAC address, or the temporary instance id until the MAC is
    /// known. A profile (and picture) saved under the temporary id moves to the MAC once that is known.
    /// </summary>
    private string? ControllerKey(int slot)
    {
        var reader = _set.Readers[slot];
        var id = reader.Id;
        if (id is null) return null;
        if (reader.HasMac && reader.FallbackId is { } old && old != id)
        {
            bool moved = false;
            if (Config.Controllers.Remove(old, out var profile) && !Config.Controllers.ContainsKey(id))
            {
                Config.Controllers[id] = profile;
                moved = true;
            }
            if (ControllerPictures.Has(old) && !ControllerPictures.Has(id)) ControllerPictures.Move(old, id);
            if (moved) _store.Save();
        }
        return id;
    }

    private SlotTile TileOf(int slot)
    {
        bool connected = ((ISettingsHost)this).ControllerConnected(slot);
        if (!connected) return default;
        var key = ControllerKey(slot);
        string name = key is not null && Config.Controllers.TryGetValue(key, out var profile) ? profile.Name : "";
        var state = _slots[slot].State;
        return new SlotTile(true, name.Length > 0 ? name : $"Controller {slot + 1}", state.Battery, state.Charging,
            key is null ? 0 : ControllerPictures.Get(key));
    }

    /// <summary>The profile of a controller (created when missing).</summary>
    private ControllerProfile ProfileOf(string key)
    {
        if (!Config.Controllers.TryGetValue(key, out var profile)) Config.Controllers[key] = profile = new ControllerProfile();
        return profile;
    }

    /// <summary>Forgets a profile that holds nothing.</summary>
    private void PruneProfile(string key)
    {
        if (Config.Controllers.TryGetValue(key, out var profile) && profile.IsDefault)
            Config.Controllers.Remove(key);
    }

    float ISettingsHost.ControllerDeadzone(int slot, bool right) =>
        ControllerKey(slot) is { } key && Config.Controllers.TryGetValue(key, out var profile)
            ? (right ? profile.RightDeadzone : profile.LeftDeadzone) : 0f;

    void ISettingsHost.SetControllerDeadzone(int slot, bool right, float value)
    {
        if (ControllerKey(slot) is not { } key) return;
        var profile = ProfileOf(key);
        value = Math.Clamp(value, 0f, 0.5f);
        if (right) profile.RightDeadzone = value; else profile.LeftDeadzone = value;
        PruneProfile(key);
        _store.Save();
    }

    string ISettingsHost.ControllerName(int slot) =>
        ControllerKey(slot) is { } key && Config.Controllers.TryGetValue(key, out var profile) ? profile.Name : "";

    void ISettingsHost.SetControllerName(int slot, string name)
    {
        if (ControllerKey(slot) is not { } key) return;
        name = name.Trim();
        if (name.Length > 24) name = name[..24];
        ProfileOf(key).Name = name;
        PruneProfile(key);
        _store.Save();
    }

    void ISettingsHost.ChooseControllerPicture(int slot)
    {
        if (ControllerKey(slot) is not { } key) return;
        var file = FilePicker.Pick(_settingsWindow.Handle, "Choose a picture for this controller", FilePicker.PictureFilter);
        if (file is null) return;
        var error = ControllerPictures.Import(file, key);
        if (error is not null) _toast.Show(MenuPages.Gear, error);
    }

    void ISettingsHost.RemoveControllerPicture(int slot)
    {
        if (ControllerKey(slot) is { } key) ControllerPictures.Remove(key);
    }

    void ISettingsHost.OpenControllerPage(int slot)
    {
        if (!((ISettingsHost)this).ControllerConnected(slot)) return;
        _settings.OpenSub(SettingsPages.ControllerPage(this, slot, out _pageStrip));
    }

    private void CloseControllerPage()
    {
        _pageStrip = null;
        _settings.Back();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    void ISettingsHost.CloseControllerPage() => CloseControllerPage();

    void ISettingsHost.AddProController()
    {
        if (_pairing || _shutDown) return;
        _pairing = true;
        _toast.Show(MenuPages.Gear, "Searching - hold the Pro Controller's sync button");
        nint hwnd = _window.Handle;
        new Thread(() =>
        {
            var result = BluetoothPairing.PairNewProController();
            PostMessageW(hwnd, AppMessages.PairDone, (nuint)(int)result, 0);
        }) { IsBackground = true, Name = "pairing" }.Start();
    }

    /// <summary>AppMessages.PairDone, on the UI thread.</summary>
    public void OnPairDone(PairResult result)
    {
        _pairing = false;
        if (_shutDown) return;
        Log.Info($"Pairing: {result}");
        _toast.Show(MenuPages.Gear, result switch
        {
            PairResult.Paired => "Pro Controller paired",
            PairResult.NotFound => "No new Pro Controller found - hold its sync button and try again",
            _ => "Pairing failed - try Windows Bluetooth settings",
        });
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    void ISettingsHost.EditRadialMenu() => OpenRadialEditor();

    bool ISettingsHost.StartOnLogon => Startup.IsEnabled;

    void ISettingsHost.SetStartOnLogon(bool enabled)
    {
        string exe = Environment.ProcessPath ?? "";
        var mode = Startup.Set(enabled, exe, out var error);
        Log.Info($"Launch on startup: {(enabled ? "on" : "off")} -> {mode}{(error is null ? "" : " (" + error + ")")}");

        string message = error ?? (enabled
            ? mode == Startup.Mode.Task ? "ControlWin will start with Windows" : "ControlWin will start with Windows (without administrator rights)"
            : "ControlWin will no longer start with Windows");
        _toast.Show(MenuPages.Gear, message);
    }

    private void OpenRadialEditor()
    {
        if (_shutDown || _mode == Mode.Editor) return;
        if (_mode == Mode.Mouse) _mouse.Deactivate();
        if (_mode == Mode.Menu) CloseMenu();
        if (_mode == Mode.Settings) CloseSettings();

        _pad.SubmitNeutral(); // neutral pad while the editor is open
        _heldDirection = Dir.None;
        _mode = Mode.Editor;
        _radialEditor.Show(Config.RadialMenu ?? [], Config.Shortcuts);
        Log.Info("Radial editor opened");
    }

    private void CloseRadialEditor()
    {
        _radialEditor.Hide();
        _mode = Mode.Idle;
        _ignoreMask = _state.Current;
        _ignoreSticks = true;
        Log.Info("Radial editor closed");
    }

    private void OnRadialEditorSaved(List<RadialNodeConfig> tree)
    {
        if (_mode != Mode.Editor) return;
        Config.RadialMenu = tree;
        Config.Sanitize();
        ((ISettingsHost)this).ConfigChanged();
        CloseRadialEditor();
        _toast.Show(MenuPages.Gear, "Radial menu saved");
    }

    void ISettingsHost.ConfigChanged()
    {
        var error = _store.Save();
        if (error is not null) _toast.Show('', error);
        ApplyConfig();
    }

    void ISettingsHost.ReloadConfig()
    {
        var error = _store.Load();
        if (error is null)
        {
            ApplyConfig();
            _toast.Show(MenuPages.Gear, "Config reloaded");
        }
        else
        {
            _toast.Show('', error);
        }
    }

    void ISettingsHost.EditConfig() => EditConfig();

    void ISettingsHost.OpenLog() => OpenLog();

    void ISettingsHost.ToggleHeadset()
    {
        if (_headsetBusy) return;
        _toast.Show(MenuPages.Headphone, _headsetState == HeadsetState.Connected ? "Disconnecting headset..." : "Connecting headset...");
        StartHeadsetWork(toggle: true);
    }

    bool ISettingsHost.PadEnabled => _padEnabled;

    int ISettingsHost.PadMode => !_padEnabled ? 0 : Config.VirtualPad.Type == PadTypes.Ps4 ? 2 : 1;

    void ISettingsHost.SetPadMode(int mode)
    {
        if (mode == ((ISettingsHost)this).PadMode) return;
        if (mode > 0)
        {
            string type = mode == 2 ? PadTypes.Ps4 : PadTypes.Xbox;
            if (Config.VirtualPad.Type != type)
            {
                Config.VirtualPad.Type = type;
                if (!_watchRunning) _store.Save();
            }
        }
        UserSetPad(mode > 0); // also re-plugs the pads already in when the kind changed
    }

    void ISettingsHost.SetPad(bool enabled)
    {
        if (enabled == _padEnabled) return;
        UserSetPad(enabled);
    }

    void ISettingsHost.WatchListChanged()
    {
        // Re-evaluate against the new list: an emptied list gives the pad back, a new entry may take it.
        CheckProcesses();
    }

    void ISettingsHost.EditText(string current, Action<string> commit) => _settingsWindow.BeginEdit(current, commit);

    void ISettingsHost.OpenBluetoothSettings()
    {
        if (_mode == Mode.Settings) CloseSettings(); // the window is topmost: it would hide Windows Settings
        BluetoothHeadset.OpenBluetoothSettings();
    }

    /// <summary>Opens config.json in Notepad with the user's own rights.</summary>
    public void EditConfig()
    {
        if (_shutDown) return;
        if (_mode == Mode.Settings) CloseSettings();
        if (!File.Exists(_store.Path)) _store.Save();
        LaunchNotepad(_store.Path);
    }

    /// <summary>Opens log.txt in Notepad with the user's own rights.</summary>
    public void OpenLog()
    {
        if (_shutDown) return;
        var path = Log.FilePath;
        if (path is null) return;
        if (_mode == Mode.Settings) CloseSettings();
        LaunchNotepad(path);
    }

    private void LaunchNotepad(string path)
    {
        var error = UnelevatedLauncher.Launch("notepad.exe", $"\"{path}\"");
        if (error is not null)
        {
            Log.Error($"Notepad: {error}");
            _toast.Show('', error);
        }
    }

    // ---- headset -----------------------------------------------------------------------------

    private string? HeadsetDetail() => _headsetBusy ? "..." : _headsetState switch
    {
        HeadsetState.Connected => "Connected",
        HeadsetState.Disconnected => "Disconnected",
        HeadsetState.NotFound => "Not paired",
        HeadsetState.Failed => "Error",
        HeadsetState.Connecting => "Connecting...",
        _ => null,
    };

    /// <summary>Runs the (blocking) headset query or toggle on a worker thread and posts the outcome back.</summary>
    private void StartHeadsetWork(bool toggle)
    {
        if (_headsetBusy || _shutDown) return;
        _headsetBusy = true;
        nint hwnd = _window.Handle;
        string name = Config.HeadsetName;
        new Thread(() =>
        {
            var state = toggle ? BluetoothHeadset.Toggle(name) : BluetoothHeadset.GetState(name);
            PostMessageW(hwnd, AppMessages.HeadsetDone, (nuint)(int)state, toggle ? 1 : 0);
        }) { IsBackground = true, Name = "headset" }.Start();
    }

    /// <summary>AppMessages.HeadsetDone, on the UI thread.</summary>
    public void OnHeadsetDone(HeadsetState state, bool wasToggle)
    {
        _headsetBusy = false;
        if (_shutDown) return;
        _headsetState = state;
        Log.Info($"Headset: {state}");

        if (wasToggle)
        {
            const char glyph = MenuPages.Headphone;
            switch (state)
            {
                case HeadsetState.Connected: _toast.Show(glyph, "Headset connected"); break;
                case HeadsetState.Disconnected: _toast.Show(glyph, "Headset disconnected"); break;
                case HeadsetState.Connecting: break; // no toast: the menu row shows it
                case HeadsetState.NotFound: _toast.Show(glyph, "Headset not paired"); break;
                default:
                    _toast.Show(glyph, "Headset failed - opening Bluetooth settings");
                    if (_mode == Mode.Settings) CloseSettings();
                    BluetoothHeadset.OpenBluetoothSettings();
                    break;
            }
        }
        if (_mode == Mode.Menu) RefreshMenu();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    // ---- window-level entry points -------------------------------------------------------------

    /// <summary>Opens the menu (tray, second launch); no-op when it is already open.</summary>
    public void ShowMenu()
    {
        if (_shutDown || _mode is Mode.Menu or Mode.Editor) return;
        OpenMenu();
    }

    /// <summary>Tray left click.</summary>
    public void ToggleMenu()
    {
        if (_shutDown || _mode == Mode.Editor) return;
        if (_mode == Mode.Menu) CloseMenu();
        else OpenMenu();
    }

    /// <summary>
    /// Releases everything the app holds towards the system: menu and toast hidden, virtual pad unplugged,
    /// tray icon deleted. Idempotent; the reader is closed by the caller once the loop ends.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        Log.Info("Shutting down");

        StopRepeat();
        if (_mode == Mode.Menu) CloseMenu();
        if (_mode == Mode.Settings) CloseSettings();
        if (_mode == Mode.Editor) CloseRadialEditor();
        _mouse.Deactivate(); // releases held mouse buttons
        _mode = Mode.Idle;
        _toast.Hide();
        foreach (var slot in _slots) slot.Pad.Dispose(); // unplugs the pads: they disappear from the games
        SyncHiding(); // shutting down: shows the controllers again
        _window.RemoveTray();
    }

    public void Dispose()
    {
        _mouse.Deactivate();
        _mouseOutput.Dispose();
        foreach (var slot in _slots) slot.Pad.Dispose();
    }
}
