using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using ControlWinSwitch.Setup;
using ControlWinSwitch.UI;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.App;

/// <summary>
/// The brain: decides for every controller report whether it is consumed by the app (menu, settings or
/// mouse mode open) or forwarded to the virtual pad (idle). Also owns the config and the mouse-driven UI events.
/// </summary>
internal sealed class AppController : IDisposable, ISettingsHost
{
    private const uint SliderTickMs = 15, SliderRefreshMs = 250, SliderIdleMs = 5000, SliderQuietMs = 500;
    private const uint HideMenuDelayMs = 10; // the menu stays under the settings window this long, so no frame shows neither
    private const uint RumbleMs = 400;
    private const uint RepeatFirstMs = 400, RepeatNextMs = 110;
    private const float StickEngage = 0.6f, StickRelease = 0.4f;

    private enum Mode { Idle, Menu, Mouse, Settings, Editor, Slider }

    private enum Dir { None, Up, Down, Left, Right }

    private readonly SendInputMouse _mouseOutput = new();
    private readonly MouseMode _mouse;
    private char _mouseGlyph;
    private readonly ControllerSet _set;

    /// <summary>What belongs to one controller: its state and what of it is hidden from its virtual pad.</summary>
    private sealed class Slot
    {
        public readonly ControllerState State = new();
        public Buttons IgnoreMask; // buttons still held from the menu: hidden from the game until released
        public bool IgnoreSticks;  // sticks used in the menu: hidden until back near center
    }

    private readonly Slot[] _slots = new Slot[ControllerSet.Slots];
    private int _owner; // the controller that has the menu / mouse mode / settings (the last one heard while idle)
    private Slot Owner => _slots[_owner];
    private ControllerState _state => Owner.State;
    private readonly MainWindow _window;
    private readonly MenuModel _menu = new();
    private readonly MenuOverlay _overlay = new();
    private readonly RadialModel _radial = new();
    private readonly RadialOverlay _radialOverlay = new();
    private readonly MenuEditorWindow _menuEditor = new();
    private readonly RadialStick _radialStick = new(100);
    private bool _radialShown;   // the open menu is the radial one (layout is read when the menu opens)
    private readonly SettingsWindow _settingsWindow = new();
    private readonly SettingsModel _settings;
    private readonly Toast _toast = new();
    private readonly SliderModel _slider = new();
    private readonly SliderOverlay _sliderOverlay = new();
    private Volume? _volume;         // open while the Volume slider is shown
    private Brightness? _brightness; // open while the Brightness slider is shown
    private char _sliderGlyph;
    private bool _sliderChanged;     // the value was changed since the slider opened
    private long _sliderLastTick, _sliderLastInput;
    private readonly ConfigStore _store = new();
    private readonly ControllerRegistry _registry;
    private readonly PadService _pads;

    private bool _pairing;
    private bool _shutDown;
    private HeadsetState _headsetState;
    private bool _headsetBusy;
    private bool _screenshotBusy;
    private char _screenshotGlyph;
    private volatile string? _screenshotResult; // written by the screenshot thread, read after ScreenshotDone
    private volatile IReadOnlyList<string> _headsetDevices = [];

    private Mode _mode;
    private Dir _heldDirection;

    private readonly TimerService _timers;
    private readonly UiTimer _repeatTimer;        // key-repeat of the held direction (menu, settings, slider)
    private readonly UiTimer _hideMenuTimer;      // hides the menu once the settings window is up
    private readonly UiTimer _sliderTickTimer, _sliderRefreshTimer, _sliderIdleTimer;
    private readonly UiTimer[] _rumbleTestTimers = new UiTimer[ControllerSet.Slots]; // ends the settings rumble test

    public AppController(ControllerSet set, MainWindow window)
    {
        _set = set;
        _window = window;
        _mouse = new MouseMode(() => _state, _mouseOutput, () => _store.Current.Mouse);
        _settings = SettingsPages.Create(_store, this);
        _registry = new ControllerRegistry(_store, set, slot => _slots[slot].State);

        _timers = new TimerService(window.Handle);
        _repeatTimer = _timers.Create(OnRepeatTimer);
        _hideMenuTimer = _timers.Create(OnHideMenuTimer);
        _sliderTickTimer = _timers.Create(OnSliderTick);
        _sliderRefreshTimer = _timers.Create(OnSliderRefresh);
        _sliderIdleTimer = _timers.Create(OnSliderIdle);

        for (int i = 0; i < _slots.Length; i++)
        {
            int slot = i;
            _slots[slot] = new Slot();
            _rumbleTestTimers[slot] = _timers.Create(() =>
            {
                _rumbleTestTimers[slot].Stop();
                _window.SetRumble(slot, false);
            });
        }

        var error = _store.Load();
        if (error is not null) _toast.Show('', error);

        _pads = new PadService(_store, set, _timers, _toast); // after Load: it reads the pad settings
        _pads.WatchChanged += OnWatchChanged;
        ApplyMenuHotkey();

        _overlay.RowHovered += OnMenuRowHovered;
        _overlay.RowClicked += OnMenuRowClicked;
        _overlay.BackRequested += OnMenuBackRequested;
        _overlay.CloseRequested += () => { if (_mode == Mode.Menu) CloseMenu(); };
        _overlay.Scrolled += OnMenuScrolled;
        _overlay.ConfirmAnswered += OnMenuConfirmAnswered;
        _radialOverlay.NodeHovered += OnRadialNodeHovered;
        _radialOverlay.NodeClicked += OnRadialNodeClicked;
        _radialOverlay.BackRequested += () => { if (_mode == Mode.Menu && _radialShown) BackMenu(); };
        _settingsWindow.Tile = _registry.TileOf;
        _settingsWindow.PictureEditRequested += (slot, second) =>
        {
            ((ISettingsHost)this).ChooseControllerPicture(slot, second);
            _settingsWindow.Refresh();
        };
        _settingsWindow.PictureRemoveRequested += (slot, second) =>
        {
            ((ISettingsHost)this).RemoveControllerPicture(slot, second);
            _settingsWindow.Refresh();
        };
        _settingsWindow.PairedTile = _registry.PairedTileOf;
        _settingsWindow.PairedPictureEditRequested += key =>
        {
            ChoosePicture(key);
            _settingsWindow.Refresh();
        };
        _settingsWindow.PairedPictureRemoveRequested += key =>
        {
            ControllerPictures.Remove(key);
            _settingsWindow.Refresh();
        };
        _settingsWindow.CloseRequested += () => { if (_mode == Mode.Settings) CloseSettings(); };
        _menuEditor.Saved += OnMenuEditorSaved;
        _menuEditor.Cancelled += () => { if (_mode == Mode.Editor) CloseMenuEditor(); };
    }

    private AppConfig Config => _store.Current;

    // ---- loop hooks --------------------------------------------------------------------------

    /// <summary>Waitable timer handle for the message loop; signalled only while a stick moves the mouse.</summary>
    public nint PadNotifyHandle(int slot) => _pads.NotifyHandle(slot);

    public void OnPadNotify(int slot) => _pads.OnNotify(slot);

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
        else if (_mode == Mode.Slider) ExitSlider();
    }

    /// <summary>Report handler: updates the slot's state, then drives the current mode.</summary>
    public void OnReport(int slot, ProReport report)
    {
        _slots[slot].State.Update(report);
        if (_shutDown) return; // reports still queued while the window closes must not re-plug the pad
        if (_registry.KnownSyncDue) SyncKnown(); // a MAC read late, half of a Joy-Con pair gone

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
        else if (slot != _owner && !TakeOverMenu(slot))
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
                case Mode.Editor: CloseMenuEditor(); break; // Capture closes the editor without saving
                case Mode.Slider: ExitSlider(); break;
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
            case Mode.Slider:
                HandleSliderInput();
                break;
            default:
                Forward(slot);
                break;
        }
    }

    /// <summary>
    /// With "All controllers control menu", or once the controller that had them disconnected, another controller takes
    /// the open menu or settings over as soon as it presses a button or tilts a stick. The one that had them goes back to
    /// its game, its held buttons and sticks hidden until released. Mouse mode stays with the controller that started it.
    /// </summary>
    private bool TakeOverMenu(int slot)
    {
        if (_mode is not (Mode.Menu or Mode.Settings)) return false;
        // Without "All controllers control menu", only once the controller that had them is gone.
        if (!Config.MenuAnyController && _window.ControllerConnected(_owner)) return false;
        var state = _slots[slot].State;
        bool active = (state.Current & ~state.Previous) != 0
            || state.Left.Magnitude > StickEngage || state.Right.Magnitude > StickEngage;
        if (!active) return false;

        HideHeldInput();
        StopRepeat();
        _heldDirection = Dir.None;
        _owner = slot;
        _pads.Neutral(_owner); // neutral pad while the menu owns the input
        return true;
    }

    public void OnControllerRemoved(int slot)
    {
        var gone = _slots[slot];
        gone.State.Reset();
        gone.State.ClearCalibration();
        _window.UpdateTray();
        if (_shutDown) return;
        _pads.OnControllerRemoved(slot); // re-plugged on the next report
        if (_settings.InSubPage && _pageStrip is { Kind: SettingKind.Strip } strip && strip.Slot == slot) CloseControllerPage();
        int heir = Config.MenuAnyController && _mode is Mode.Menu or Mode.Settings
            ? Enumerable.Range(0, _slots.Length).FirstOrDefault(i => i != slot && _window.ControllerConnected(i), -1) : -1;
        if (slot == _owner && heir >= 0)
        {
            // Every controller drives the menu: another one keeps it open.
            StopRepeat();
            _heldDirection = Dir.None;
            _owner = heir;
            _pads.Neutral(_owner);
        }
        else if (slot == _owner)
        {
            if (_mode == Mode.Menu) CloseMenu();
            else if (_mode == Mode.Settings)
            {
                // The settings stay open: the mouse still works, and the next controller that presses a button takes them.
                StopRepeat();
                _heldDirection = Dir.None;
            }
            else if (_mode == Mode.Mouse) ExitMouse(announce: false);
            else if (_mode == Mode.Slider) ExitSlider();
        }
        gone.IgnoreMask = Buttons.None;
        gone.IgnoreSticks = false;
        SyncKnown();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    /// <summary>WM_TIMER of the main window.</summary>
    public void OnTimer(nuint id) => _timers.Dispatch(id);

    /// <summary>Key-repeat of the held direction.</summary>
    private void OnRepeatTimer()
    {
        if (_mode == Mode.Slider)
        {
            OnSliderRepeat();
            return;
        }
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
        _repeatTimer.Start(RepeatNextMs); // restarts it with the faster interval
    }

    // ---- menu --------------------------------------------------------------------------------

    private void OpenMenu()
    {
        if (_mode == Mode.Mouse) _mouse.Deactivate(); // tray / second launch while the mouse is on
        if (_mode == Mode.Settings) CloseSettings();
        if (_mode == Mode.Slider) ExitSlider();

        ReloadConfigIfChanged();
        var statuses = new NodeStatuses(_pads.Detail, HeadsetDetail);
        _radialShown = Config.MenuLayout == MenuLayouts.Radial;
        _pads.Neutral(_owner); // neutral pad while the menu owns the input
        _mode = Mode.Menu;
        if (_radialShown)
        {
            _radial.Reset(MenuTree.ToPage("ControlWinSwitch", Config.RadialMenu ?? [], statuses));
            _radialStick.ReleaseMs = Config.RadialReleaseMs;
            _radialStick.CardinalBias = Config.RadialCardinalBias * MathF.PI / 180f;
            _radialStick.Reset(); // a stick already tilted must come back to the center first
            _heldDirection = Dir.None;
            _radialOverlay.Show(_radial, Config.MenuShowControllers ? _registry.ConnectedTiles() : null);
        }
        else
        {
            _menu.Reset(MenuTree.ToPage("ControlWinSwitch", Config.ListMenu ?? [], statuses, slotted: false));
            _heldDirection = ReadDirection(horizontal: false); // a stick already tilted must be released first, not scroll on open
            _overlay.Show(_menu, Config.MenuShowControllers ? _registry.ConnectedTiles() : null);
        }
        Log.Info(_radialShown ? "Menu opened (radial)" : "Menu opened");
        StartHeadsetWork(toggle: false); // refresh the status shown in the menu
    }

    private void CloseMenu()
    {
        StopRepeat();
        _heldDirection = Dir.None;
        _mode = Mode.Idle;
        HideHeldInput();
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
        if (_mode == Mode.Slider) ExitSlider();
        bool fromMenu = _mode == Mode.Menu;
        if (fromMenu) StopRepeat();

        ReloadConfigIfChanged();
        SyncKnown();
        _registry.RefreshRemembered();
        _pads.Neutral(_owner);
        _heldDirection = ReadDirection(horizontal: true);
        _mode = Mode.Settings;
        _settingsWindow.Show(_settings);
        if (fromMenu) _hideMenuTimer.Start(HideMenuDelayMs); // hidden shortly after the settings window is up
        Log.Info("Settings opened");
        StartHeadsetWork(toggle: false); // refresh the status shown on the Headset tab
    }

    private void OnHideMenuTimer()
    {
        _hideMenuTimer.Stop();
        if (_mode == Mode.Settings) HideMenu();
    }

    private void CloseSettings()
    {
        StopRepeat();
        _heldDirection = Dir.None;
        _mode = Mode.Idle;
        HideHeldInput();
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
        else if (_state.Pressed(Buttons.X))
        {
            _settings.Alternate();
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
        _repeatTimer.Start(RepeatFirstMs);
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
        if (_repeatTimer.Running) _repeatTimer.Stop();
    }

    /// <summary>Hides the owner's held buttons and sticks from the game until they are released.</summary>
    private void HideHeldInput()
    {
        Owner.IgnoreMask = _state.Current;
        Owner.IgnoreSticks = true;
    }

    // ---- mouse mode --------------------------------------------------------------------------

    private void EnterMouse(char glyph)
    {
        _mouseGlyph = glyph;
        _pads.Neutral(_owner); // neutral pad while the mouse owns the input
        _mode = Mode.Mouse;
        _mouse.Activate(_state.Current);
        _toast.Show(glyph, "Mouse mode - Capture to exit");
        Log.Info("Mouse mode on");
    }

    private void ExitMouse(bool announce)
    {
        _mouse.Deactivate(); // releases left / right buttons
        _mode = Mode.Idle;
        HideHeldInput();
        if (announce) _toast.Show(_mouseGlyph, "Mouse mode: off");
        Log.Info("Mouse mode off");
    }

    // ---- volume / brightness slider ----------------------------------------------------------

    /// <summary>The menu is already closed. Opens the slider on the current value; a toast when there is nothing to control.</summary>
    private void EnterSlider(MenuEntry entry, bool volume)
    {
        int value;
        bool muted = false;
        if (volume)
        {
            _volume = Volume.Open();
            value = _volume?.Get(out muted) ?? -1;
        }
        else
        {
            _brightness = Brightness.Open();
            value = _brightness?.Get() ?? -1;
        }
        if (value < 0)
        {
            CloseSliderDevices();
            string error = volume ? "No audio output device" : "This screen's brightness can't be changed";
            Log.Info($"Slider: {error}");
            _toast.Show(entry.Glyph, error);
            return;
        }

        _sliderGlyph = entry.Glyph;
        _slider.Reset(value, muted);
        _sliderChanged = false;
        _pads.Neutral(_owner); // neutral pad while the slider owns the input
        _toast.Hide();
        _mode = Mode.Slider;
        _heldDirection = ReadSliderDpad(); // a D-pad already held must be released first
        _sliderOverlay.Show(SliderGlyph, value, muted);
        _sliderRefreshTimer.Start(SliderRefreshMs);
        TouchSlider();
        Log.Info($"Slider opened ({(volume ? "volume" : "brightness")} {value})");
    }

    private void ExitSlider()
    {
        StopSliderTimers();
        StopRepeat();
        _heldDirection = Dir.None;
        _mode = Mode.Idle;
        HideHeldInput();
        _sliderOverlay.Hide();
        if (_brightness is { SavesToPowerPlan: true } && _sliderChanged)
        {
            // Into the power plan too, or Windows puts its own value back later. Slow: on a worker thread.
            int percent = _slider.Percent;
            ErrorBoundary.StartWorker("brightness", () =>
            {
                if (!Brightness.Save(percent)) Log.Error("Brightness: saving to the power plan failed");
            });
        }
        CloseSliderDevices();
        Log.Info($"Slider closed ({_slider.Percent})");
    }

    private void StopSliderTimers()
    {
        _sliderTickTimer.Stop();
        _sliderRefreshTimer.Stop();
        _sliderIdleTimer.Stop();
    }

    private void CloseSliderDevices()
    {
        _volume?.Dispose();
        _volume = null;
        _brightness?.Dispose();
        _brightness = null;
    }

    /// <summary>The muted speaker while the volume is muted, the node's own glyph otherwise.</summary>
    private char SliderGlyph => _volume is not null && _slider.Muted ? MenuPages.Mute : _sliderGlyph;

    private void HandleSliderInput()
    {
        if (_state.Pressed(Buttons.A) || _state.Pressed(Buttons.B))
        {
            ExitSlider();
            return;
        }

        if (_volume is not null && _state.Pressed(Buttons.Y))
        {
            _slider.Muted = !_slider.Muted;
            _volume.SetMute(_slider.Muted);
            TouchSlider();
            RefreshSlider();
        }

        // D-pad: one percent per press, repeated while held.
        var direction = ReadSliderDpad();
        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            StopRepeat();
            if (direction != Dir.None)
            {
                StepSlider(direction);
                _repeatTimer.Start(RepeatFirstMs);
            }
        }

        // Left stick: moved by a timer while tilted, so the speed doesn't depend on how often the controller reports.
        if (!_sliderTickTimer.Running && _slider.StickActive(_state.Left.X))
        {
            _sliderLastTick = Environment.TickCount64;
            _sliderTickTimer.Start(SliderTickMs);
        }
    }

    private Dir ReadSliderDpad()
    {
        var held = _state.Current;
        if ((held & Buttons.Left) != 0) return Dir.Left;
        if ((held & Buttons.Right) != 0) return Dir.Right;
        return Dir.None;
    }

    /// <summary>Key-repeat while the slider is open (the repeat timer is shared with the menu and settings).</summary>
    private void OnSliderRepeat()
    {
        if (_heldDirection == Dir.None)
        {
            StopRepeat();
            return;
        }
        StepSlider(_heldDirection);
        _repeatTimer.Start(RepeatNextMs); // the faster interval
    }

    /// <summary>A slider timer that ticks after the slider closed stops itself.</summary>
    private bool SliderGone(UiTimer timer)
    {
        if (_mode == Mode.Slider) return false;
        timer.Stop();
        return true;
    }

    private void OnSliderTick()
    {
        if (SliderGone(_sliderTickTimer)) return;
        long now = Environment.TickCount64;
        float seconds = Math.Min(0.1f, (now - _sliderLastTick) / 1000f);
        _sliderLastTick = now;
        float x = _state.Left.X;
        if (!_slider.StickActive(x))
        {
            _sliderTickTimer.Stop();
            return;
        }
        if (_slider.Move(x, seconds, boost: _state.Held(Buttons.ZR), slow: _state.Held(Buttons.ZL))) ApplySlider();
        else TouchSlider();
    }

    private void OnSliderRefresh()
    {
        if (SliderGone(_sliderRefreshTimer)) return;
        // Changes made elsewhere (keyboard keys, another default device), only while the user isn't moving it.
        if (Environment.TickCount64 - _sliderLastInput < SliderQuietMs) return;
        int value = -1;
        bool muted = false;
        if (_volume is not null)
        {
            _volume.Reacquire();
            value = _volume.Get(out muted);
        }
        else if (_brightness is not null) value = _brightness.Get();
        if (value < 0) return;
        _slider.Sync(value, muted);
        RefreshSlider();
    }

    private void OnSliderIdle()
    {
        if (!SliderGone(_sliderIdleTimer)) ExitSlider();
    }

    private void StepSlider(Dir direction)
    {
        if (_slider.Step(direction == Dir.Right ? 1 : -1)) ApplySlider();
        else TouchSlider();
    }

    /// <summary>Sends the shown value to the system. Changing the volume unmutes it, like the keyboard keys.</summary>
    private void ApplySlider()
    {
        int percent = _slider.Percent;
        if (_volume is not null)
        {
            if (_slider.Muted)
            {
                _slider.Muted = false;
                _volume.SetMute(false);
            }
            _volume.Set(percent);
        }
        else _brightness?.Set(percent);
        _sliderChanged = true;
        TouchSlider();
        RefreshSlider();
    }

    private void RefreshSlider() => _sliderOverlay.Update(SliderGlyph, _slider.Percent, _slider.Muted);

    /// <summary>Input on the slider: it stays open another 5 s, and outside changes wait a little.</summary>
    private void TouchSlider()
    {
        _sliderLastInput = Environment.TickCount64;
        _sliderIdleTimer.Start(SliderIdleMs); // restarts the countdown
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

            case CommandKind.AddController:
                ((ISettingsHost)this).AddController();
                break;

            case CommandKind.Launch:
                if (entry.Launch is { } launch) LaunchTarget(launch);
                break;

            case CommandKind.CloseMenu:
                break; // the menu is already closed

            case CommandKind.CloseWindow:
                var error = WindowActions.CloseForeground(out string title);
                if (error is not null) Log.Info($"Close window: {error}");
                _toast.Show(entry.Glyph, error ?? $"Closing {title}");
                break;

            case CommandKind.Screenshot:
                TakeScreenshot(entry);
                break;

            case CommandKind.Volume:
            case CommandKind.Brightness:
                EnterSlider(entry, volume: command.Kind == CommandKind.Volume);
                break;

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
                Log.Error($"Unhandled menu command {command.Kind} ({entry.Label})");
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
        nint hwnd = _window.Handle;
        ErrorBoundary.StartWorker("power", () =>
        {
            var error = PowerActions.Run(action);
            if (error is not null)
            {
                Log.Error($"Power {action}: {error}");
                PostMessageW(hwnd, AppMessages.PowerFailed, 0, 0);
            }
        }, onFailure: () => PostMessageW(hwnd, AppMessages.PowerFailed, 0, 0));
    }

    /// <summary>AppMessages.PowerFailed, on the UI thread.</summary>
    public void OnPowerFailed()
    {
        if (!_shutDown) _toast.Show(MenuPages.Power, "Power action failed - see log");
    }

    /// <summary>The menu is already hidden: capture now, encode and write on a worker thread.</summary>
    private void TakeScreenshot(MenuEntry entry)
    {
        if (_screenshotBusy)
        {
            _toast.Show(entry.Glyph, "Still saving the last screenshot");
            return;
        }
        _toast.Hide(); // a toast still on screen would end up in the picture

        var shot = Screenshot.Capture(out var error);
        if (shot is null)
        {
            Log.Error($"Screenshot: {error}");
            _toast.Show(entry.Glyph, error ?? "Screenshot failed");
            return;
        }

        _screenshotBusy = true;
        _screenshotGlyph = entry.Glyph;
        nint hwnd = _window.Handle;
        string? folder = entry.SaveFolder;
        bool copy = entry.CopyToClipboard;
        ErrorBoundary.StartWorker("screenshot", () =>
        {
            _screenshotResult = Screenshot.Save(shot, folder, copy);
            PostMessageW(hwnd, AppMessages.ScreenshotDone, 0, 0);
        }, onFailure: () => PostMessageW(hwnd, AppMessages.ScreenshotDone, 0, 0)); // clears the busy flag
    }

    /// <summary>AppMessages.ScreenshotDone, on the UI thread.</summary>
    public void OnScreenshotDone()
    {
        _screenshotBusy = false;
        if (!_shutDown && _screenshotResult is { } text) _toast.Show(_screenshotGlyph, text);
        _screenshotResult = null;
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

        _pads.WatchSoon(); // the launched program may be a watched one
    }

    // ---- virtual pad (kept by PadService) ----------------------------------------------------

    public bool PadEnabled => _pads.Enabled;

    public void TogglePad(char glyph)
    {
        if (_shutDown) return;
        _pads.UserSet(!_pads.Enabled);
        _toast.Show(glyph, _pads.Failed ? "Virtual pad unavailable (ViGEmBus?)"
            : _pads.Enabled ? "Virtual pad: on" : "Virtual pad: off");
        if (_mode == Mode.Menu) RefreshMenu(); // the pad row shows its state
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    /// <summary>Note shown next to the pad in the menu and tray while a watched process holds it off.</summary>
    public string? PadNote => _pads.Note;

    /// <summary>Handle of the watched process for the message loop (0 = none).</summary>
    public nint WatchHandle => _pads.WatchHandle;

    /// <summary>The watched process exited (its handle was signalled).</summary>
    public void OnWatchedProcessExited() => _pads.OnWatchedProcessExited();

    /// <summary>A watched process started or closed: the pad's state shows in the menu and settings.</summary>
    private void OnWatchChanged()
    {
        if (_mode == Mode.Menu) RefreshMenu();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    /// <summary>The state is not consumed: it drives the virtual pad.</summary>
    private void Forward(int index)
    {
        if (!_pads.Prepare(index)) return;

        var slot = _slots[index];
        var state = slot.State;
        slot.IgnoreMask &= state.Current;
        var left = state.Left;
        var right = state.Right;
        if (slot.IgnoreSticks)
        {
            if (left.Magnitude < 0.3f && right.Magnitude < 0.3f) slot.IgnoreSticks = false;
            else left = right = default;
        }

        left = left.WithDeadzone(_registry.StickDeadzone(index, right: false));
        right = right.WithDeadzone(_registry.StickDeadzone(index, right: true));
        _pads.Submit(index, state.Current & ~slot.IgnoreMask, left, right);
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
        _pads.ApplyConfig();
        ApplyMenuHotkey();
    }

    private string? _menuHotkey; // the shortcut registered last (null = not yet)

    /// <summary>(Re-)registers the keyboard shortcut of the menu when it changed.</summary>
    private void ApplyMenuHotkey()
    {
        string text = Config.MenuHotkey;
        if (text == _menuHotkey) return;
        _menuHotkey = text;
        Hotkey? hotkey = Hotkey.TryParse(text, out var parsed) ? parsed : null;
        if (_window.SetMenuHotkey(hotkey)) Log.Info($"Menu shortcut: {(text.Length == 0 ? "none" : text)}");
        else _toast.Show(MenuPages.Gear, $"{text} is already used by another app");
    }

    // ISettingsHost
    string ISettingsHost.ConfigPath => _store.Path;

    IReadOnlyList<string> ISettingsHost.HeadsetDevices => _headsetDevices;

    string ISettingsHost.HeadsetStatus =>_headsetBusy ? "Working..." : HeadsetDetail() ?? "Unknown";

    bool ISettingsHost.ControllerConnected(int slot) => _window.ControllerConnected(slot);

    void ISettingsHost.RumbleController(int slot)
    {
        if (!_window.SetRumble(slot, true)) return;
        _rumbleTestTimers[slot].Start(RumbleMs);
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

    // ---- controllers (profiles and the known list are kept by ControllerRegistry) -------------

    private long _lastCalibrationPaint;
    private SettingItem? _pageStrip; // the strip row of the open controller page: its Slot is the controller being edited

    float ISettingsHost.ControllerDeadzone(int slot, bool right) => _registry.StickDeadzone(slot, right);

    void ISettingsHost.SetControllerDeadzone(int slot, bool right, float value) => _registry.SetStickDeadzone(slot, right, value);

    string ISettingsHost.ControllerName(int slot) => _registry.Key(slot) is { } key ? _registry.ProfileName(key) : "";

    void ISettingsHost.SetControllerName(int slot, string name)
    {
        if (_registry.Key(slot) is { } key) _registry.SetProfileName(key, name);
    }

    string ISettingsHost.ProfileName(string key) => _registry.ProfileName(key);

    void ISettingsHost.SetProfileName(string key, string name) => _registry.SetProfileName(key, name);

    void ISettingsHost.ChooseControllerPicture(int slot, bool second)
    {
        if (_registry.PictureKey(slot, second) is { } key) ChoosePicture(key);
    }

    private void ChoosePicture(string key)
    {
        var file = FilePicker.Pick(_settingsWindow.Handle, "Choose a picture for this controller", FilePicker.PictureFilter);
        if (file is null) return;
        var error = ControllerPictures.Import(file, key);
        if (error is not null) _toast.Show(MenuPages.Gear, error);
    }

    void ISettingsHost.RemoveControllerPicture(int slot, bool second)
    {
        if (_registry.PictureKey(slot, second) is { } key) ControllerPictures.Remove(key);
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

    void ISettingsHost.AddController() => StartPairing(only: null);

    /// <summary>
    /// Searches for a controller in sync mode: a Pro Controller or an original Joy-Con (<paramref name="only"/>: that one
    /// only, by MAC) and, when any controller is wanted, a Joy-Con 2.
    /// </summary>
    private void StartPairing(string? only)
    {
        if (_pairing || _shutDown) return;
        _pairing = true;
        _pairedJoyCons = 0;
        _toast.Show(MenuPages.Game, "Searching... Please hold sync button");
        if (only is null) _set.Hub.StartPairing(PairWindowMs); // a Joy-Con 2 in sync mode is claimed meanwhile
        nint hwnd = _window.Handle;
        ErrorBoundary.StartWorker("pairing", () =>
        {
            var result = BluetoothPairing.PairController(only);
            PostMessageW(hwnd, AppMessages.PairDone, (nuint)(int)result, 0);
        }, onFailure: () => PostMessageW(hwnd, AppMessages.PairDone, (nuint)(int)PairResult.Failed, 0));
    }

    /// <summary>AppMessages.PairDone, on the UI thread.</summary>
    public void OnPairDone(PairResult result)
    {
        _pairing = false;
        _set.Hub.StopPairing();
        if (_shutDown) return;
        Log.Info($"Pairing: {result}");
        _registry.RefreshRemembered();
        if (result == PairResult.NotFound && _pairedJoyCons > 0)
        {
            if (_mode == Mode.Settings) _settingsWindow.Refresh();
            return; // the Joy-Con toast said it already
        }
        _toast.Show(MenuPages.Game, result switch
        {
            PairResult.Paired => "Controller paired",
            PairResult.NotFound => "No new controller found",
            _ => "Pairing failed - try Windows Bluetooth settings",
        });
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    private const int PairWindowMs = BluetoothPairing.SearchMs + 20_000; // longer than the Pro Controller search: it ends the window
    private int _pairedJoyCons;

    /// <summary>A Joy-Con 2 in sync mode was paired with this PC (UI thread).</summary>
    public void OnJoyConPaired(JoyCon2Device device)
    {
        _pairedJoyCons++;
        if (!_shutDown) _toast.Show(MenuPages.Game, $"{device.Name} paired");
    }

    /// <summary>Joy-Cons joined a slot: a new controller, or the partner of a lone Joy-Con.</summary>
    public void OnJoyConsChanged(int slot)
    {
        _window.UpdateTray();
        if (!_shutDown && _mode == Mode.Settings) _settingsWindow.Refresh();
    }

    // ---- known controllers (the paired list of the Controller tab) ----------------------------

    /// <summary>Records the controllers that connected or went away, and keeps the open controller page in line.</summary>
    private void SyncKnown()
    {
        if (_registry.SyncKnown() is not { } joined) return;

        // The paired controller being edited connected: edit it as a connected controller.
        if (_settings.InSubPage && _pageStrip is { Kind: SettingKind.Paired } page && page.Key is { } key && joined.Contains(key))
        {
            int slot = _registry.SlotOf(key);
            CloseControllerPage();
            if (slot >= 0) ((ISettingsHost)this).OpenControllerPage(slot);
        }
        else ClosePairedPageIfEmpty();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    IReadOnlyList<string> ISettingsHost.PairedControllers => _registry.PairedControllers;

    string ISettingsHost.PairedDisplayName(string key) => _registry.PairedDisplayName(key);

    void ISettingsHost.ForgetController(string key)
    {
        if (!_registry.Forget(key)) _toast.Show(MenuPages.Game, "Could not forget the controller");
        // Forgotten from its own page: back to the Controller tab.
        if (_settings.InSubPage && _pageStrip is { Kind: SettingKind.Paired } page && page.Key == key) CloseControllerPage();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    void ISettingsHost.RepairController(string key)
    {
        StartPairing(only: _registry.UsesWindowsPairing(key) ? key : null); // a Joy-Con 2 in sync mode is claimed by the hub
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    void ISettingsHost.OpenPairedPage(string key)
    {
        if (!_registry.PairedControllers.Contains(key)) return;
        _settings.OpenSub(SettingsPages.PairedPage(this, key, out _pageStrip));
    }

    /// <summary>The paired page has nothing left to show: back to the Controller tab.</summary>
    private void ClosePairedPageIfEmpty()
    {
        if (_settings.InSubPage && _pageStrip is { Kind: SettingKind.Paired } && _registry.PairedControllers.Count == 0)
            CloseControllerPage();
    }


    void ISettingsHost.EditMenu() => OpenMenuEditor(Config.MenuLayout);

    void ISettingsHost.SetMenuHotkey(string text)
    {
        if (text.Trim().Length == 0) Config.MenuHotkey = "";
        else if (Hotkey.TryParse(text, out var hotkey)) Config.MenuHotkey = hotkey.ToString();
        else
        {
            _toast.Show(MenuPages.Gear, $"Unknown shortcut \"{text.Trim()}\" - try Ctrl+Alt+M");
            return;
        }
        ((ISettingsHost)this).ConfigChanged();
    }

    bool ISettingsHost.StartOnLogon => Startup.IsEnabled;

    void ISettingsHost.SetStartOnLogon(bool enabled)
    {
        string exe = Environment.ProcessPath ?? "";
        var mode = Startup.Set(enabled, exe, out var error);
        Log.Info($"Launch on startup: {(enabled ? "on" : "off")} -> {mode}{(error is null ? "" : " (" + error + ")")}");

        string message = error ?? (enabled
            ? mode == Startup.Mode.Task ? "ControlWinSwitch will start with Windows" : "ControlWinSwitch will start with Windows (without administrator rights)"
            : "ControlWinSwitch will no longer start with Windows");
        _toast.Show(MenuPages.Gear, message);
    }

    private void OpenMenuEditor(string layout)
    {
        if (_shutDown || _mode == Mode.Editor) return;
        if (_mode == Mode.Mouse) _mouse.Deactivate();
        if (_mode == Mode.Menu) CloseMenu();
        if (_mode == Mode.Settings) CloseSettings();
        if (_mode == Mode.Slider) ExitSlider();

        _pads.Neutral(_owner); // neutral pad while the editor is open
        _heldDirection = Dir.None;
        _mode = Mode.Editor;
        _menuEditor.Show(layout, Config.RadialMenu ?? [], Config.ListMenu ?? []);
        Log.Info($"Menu editor opened ({layout})");
    }

    private void CloseMenuEditor()
    {
        _menuEditor.Hide();
        _mode = Mode.Idle;
        HideHeldInput();
        Log.Info("Menu editor closed");
    }

    private void OnMenuEditorSaved(string layout, List<MenuNodeConfig> tree)
    {
        if (_mode != Mode.Editor) return;
        if (layout == MenuLayouts.Radial) Config.RadialMenu = tree;
        else Config.ListMenu = tree;
        Config.Sanitize();
        ((ISettingsHost)this).ConfigChanged();
        CloseMenuEditor();
        _toast.Show(MenuPages.Gear, layout == MenuLayouts.Radial ? "Radial menu saved" : "List menu saved");
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

    bool ISettingsHost.PadEnabled => _pads.Enabled;

    int ISettingsHost.PadMode => _pads.Mode;

    void ISettingsHost.SetPadMode(int mode) => _pads.SetMode(mode);

    void ISettingsHost.SetPad(bool enabled)
    {
        if (enabled == _pads.Enabled) return;
        _pads.UserSet(enabled);
    }

    void ISettingsHost.WatchListChanged() => _pads.WatchListChanged();

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
        ErrorBoundary.StartWorker("headset", () =>
        {
            if (!toggle) _headsetDevices = BluetoothHeadset.ListDevices();
            var state = toggle ? BluetoothHeadset.Toggle(name) : BluetoothHeadset.GetState(name);
            PostMessageW(hwnd, AppMessages.HeadsetDone, (nuint)(int)state, toggle ? 1 : 0);
        }, onFailure: () => PostMessageW(hwnd, AppMessages.HeadsetDone, (nuint)(int)HeadsetState.Failed, toggle ? 1 : 0));
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
                case HeadsetState.NotFound:
                    _toast.Show(glyph, string.IsNullOrWhiteSpace(Config.HeadsetName) ? "Set the headset name in Settings" : "Headset not paired");
                    break;
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
        // Step by step: one that throws must not keep the controllers hidden or the pads plugged in.
        TryStep("save the last-seen times", _registry.SaveLastSeen);
        TryStep("close the open screen", () =>
        {
            StopRepeat();
            if (_mode == Mode.Menu) CloseMenu();
            if (_mode == Mode.Settings) CloseSettings();
            if (_mode == Mode.Editor) CloseMenuEditor();
            if (_mode == Mode.Slider) ExitSlider();
        });
        _mode = Mode.Idle;
        TryStep("release the mouse buttons", _mouse.Deactivate);
        TryStep("hide the toast", _toast.Hide);
        TryStep("unplug the virtual pads", _pads.Shutdown); // they disappear from the games; the controllers show again
        TryStep("remove the tray icon", _window.RemoveTray);
    }

    /// <summary>
    /// After an unexpected error on the UI thread: closes whatever screen is open and lets go of everything held, so a
    /// half-done change can't leave an overlay up, a mouse button down or a button stuck on the virtual pad.
    /// </summary>
    public void RecoverToIdle(bool announce)
    {
        if (_shutDown) return;
        var mode = _mode;
        _mode = Mode.Idle;
        _heldDirection = Dir.None;
        TryStep("stop the key repeat", StopRepeat);
        TryStep("hide the menu", () =>
        {
            _overlay.Hide();
            _radialOverlay.Hide();
        });
        if (mode == Mode.Settings) TryStep("hide the settings", _settingsWindow.Hide);
        if (mode == Mode.Editor) TryStep("hide the menu editor", _menuEditor.Hide);
        TryStep("close the slider", () =>
        {
            StopSliderTimers();
            _sliderOverlay.Hide();
            CloseSliderDevices();
        });
        TryStep("release the mouse buttons", _mouse.Deactivate);
        foreach (var slot in _slots)
        {
            slot.IgnoreMask = slot.State.Current; // what is held reaches the game only once released
            slot.IgnoreSticks = true;
        }
        for (int i = 0; i < _slots.Length; i++)
        {
            int index = i;
            TryStep("neutralize a virtual pad", () => _pads.Neutral(index));
        }
        if (!announce) return;
        Log.Info($"Recovered from the error (was {mode})");
        TryStep("show the error toast", () => _toast.Show(MenuPages.Gear, "Something went wrong - see log"));
    }

    /// <summary>Last resort when the process is about to die, from any thread: shows the hidden controllers again.</summary>
    public void ShowControllersAgain()
    {
        _pads.ShowControllersAgain();
    }

    private static void TryStep(string step, Action action) => ErrorBoundary.TryStep(step, action);

    public void Dispose()
    {
        _mouse.Deactivate();
        _mouseOutput.Dispose();
        _pads.Dispose();
    }
}
