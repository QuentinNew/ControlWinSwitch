using ControlWin.Actions;
using ControlWin.Config;
using ControlWin.Input;
using ControlWin.UI;
using static ControlWin.Native.Win32;

namespace ControlWin.App;

/// <summary>
/// The brain: decides for every controller report whether it is consumed by the app (menu, settings or
/// mouse mode open) or forwarded to the virtual pad (idle). Also owns the config and the mouse-driven UI events.
/// </summary>
internal sealed class AppController : IDisposable, ISettingsHost
{
    private const nuint RepeatTimerId = 1;
    private const uint RepeatFirstMs = 400, RepeatNextMs = 110;
    private const float StickEngage = 0.6f, StickRelease = 0.4f;

    private enum Mode { Idle, Menu, Mouse, Settings }

    private enum Dir { None, Up, Down, Left, Right }

    private readonly SendInputMouse _mouseOutput = new();
    private readonly MouseMode _mouse;
    private char _mouseGlyph;
    private readonly ControllerState _state;
    private readonly MainWindow _window;
    private readonly MenuModel _menu = new();
    private readonly MenuOverlay _overlay = new();
    private readonly SettingsWindow _settingsWindow = new();
    private readonly SettingsModel _settings;
    private readonly Toast _toast = new();
    private readonly VirtualPad _pad = new();
    private readonly ConfigStore _store = new();

    private bool _padEnabled;
    private bool _padFailed;
    private bool _shutDown;
    private HeadsetState _headsetState;
    private bool _headsetBusy;
    private Buttons _ignoreMask; // buttons still held from the menu: hidden from the game until released
    private bool _ignoreSticks;  // sticks used in the menu: hidden until back near center

    private Mode _mode;
    private Dir _heldDirection;
    private bool _repeatTimerArmed;

    public AppController(ControllerState state, MainWindow window)
    {
        _state = state;
        _window = window;
        _mouse = new MouseMode(state, _mouseOutput, () => _store.Current.Mouse);
        _settings = SettingsPages.Create(_store, this);

        var error = _store.Load();
        _padEnabled = _store.Current.VirtualPad.Enabled;
        if (error is not null) _toast.Show('', error);

        _overlay.RowHovered += OnMenuRowHovered;
        _overlay.RowClicked += OnMenuRowClicked;
        _overlay.BackRequested += OnMenuBackRequested;
        _overlay.Scrolled += OnMenuScrolled;
        _overlay.ConfirmAnswered += OnMenuConfirmAnswered;
        _settingsWindow.CloseRequested += () => { if (_mode == Mode.Settings) CloseSettings(); };
    }

    private AppConfig Config => _store.Current;

    // ---- loop hooks --------------------------------------------------------------------------

    /// <summary>Waitable timer handle for the message loop; signalled only while a stick moves the mouse.</summary>
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
        _state.Reset();
        if (_shutDown) return;
        if (_mode == Mode.Mouse) ExitMouse(announce: false);
        _ignoreMask = Buttons.None;
        _ignoreSticks = false;
    }

    /// <summary>Report handler: updates the shared state first, so later subscribers (the debug printer) see it.</summary>
    public void OnReport(ProReport report, ReadOnlyMemory<byte> raw)
    {
        _state.Update(report);
        if (_shutDown) return; // reports still queued while the window closes must not re-plug the pad

        // Capture is reserved for the menu toggle and is never forwarded.
        if (_state.Pressed(Buttons.Capture))
        {
            switch (_mode)
            {
                case Mode.Menu: CloseMenu(); break;
                case Mode.Mouse: ExitMouse(announce: true); break;
                case Mode.Settings: CloseSettings(); break;
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
            case Mode.Mouse:
                _mouse.OnReport();
                break;
            default:
                Forward();
                break;
        }
    }

    public void OnControllerRemoved()
    {
        _state.Reset();
        _window.UpdateTray();
        if (_shutDown) return;
        _pad.Detach(); // re-plugged on the next report
        if (_mode == Mode.Menu) CloseMenu();
        else if (_mode == Mode.Settings) CloseSettings();
        else if (_mode == Mode.Mouse) ExitMouse(announce: false);
        _ignoreMask = Buttons.None;
        _ignoreSticks = false;
    }

    /// <summary>WM_TIMER of the main window: key-repeat of the held direction.</summary>
    public void OnTimer(nuint id)
    {
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
        _menu.Reset(MenuPages.Root(PadDetail, HeadsetDetail, BuildShortcutsPage));
        _pad.Submit(default); // neutral pad while the menu owns the input
        _heldDirection = ReadDirection(horizontal: false); // a stick already tilted must be released first, not scroll on open
        _mode = Mode.Menu;
        _overlay.Show(_menu);
        Log.Info("Menu opened");
        StartHeadsetWork(toggle: false); // refresh the status shown in the menu
    }

    private void CloseMenu()
    {
        StopRepeat();
        _heldDirection = Dir.None;
        _mode = Mode.Idle;
        _ignoreMask = _state.Current;
        _ignoreSticks = true;
        _overlay.Hide();
        Log.Info("Menu closed");
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
        TrackDirection(horizontal: false);

        if (_state.Pressed(Buttons.A)) AcceptMenu();
        else if (_state.Pressed(Buttons.B)) BackMenu();
    }

    private void AcceptMenu()
    {
        var entry = _menu.CurrentEntry;
        var command = _menu.Accept();
        if (command is { } c && entry is not null)
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
        if (_shutDown || _mode == Mode.Settings) return;
        OpenSettings();
    }

    private void OpenSettings()
    {
        if (_mode == Mode.Mouse) _mouse.Deactivate();
        if (_mode == Mode.Menu)
        {
            StopRepeat();
            _overlay.Hide();
        }

        ReloadConfigIfChanged();
        _pad.Submit(default);
        _heldDirection = ReadDirection(horizontal: true);
        _mode = Mode.Settings;
        _settingsWindow.Show(_settings);
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

        if (_state.Pressed(Buttons.A))
        {
            _settings.Activate();
            if (_mode == Mode.Settings) _settingsWindow.Refresh();
        }
        else if (_state.Pressed(Buttons.B))
        {
            CloseSettings();
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
        _pad.Submit(default); // neutral pad while the mouse owns the input
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

            case CommandKind.ToggleVirtualPad:
                TogglePad(entry.Glyph);
                break;

            case CommandKind.ToggleHeadset:
                if (_headsetBusy) break;
                _toast.Show(entry.Glyph, _headsetState == HeadsetState.Connected ? "Disconnecting headset..." : "Connecting headset...");
                StartHeadsetWork(toggle: true);
                break;

            default:
                // Implemented by F6.
                _toast.Show(entry.Glyph, $"{entry.Label}: not implemented yet");
                break;
        }
    }

    private void LaunchShortcut(int index)
    {
        var list = Config.Shortcuts;
        if (index < 0 || index >= list.Count) return;

        var shortcut = list[index];
        _toast.Show(MenuPages.Play, $"Launching {shortcut.Name}");
        var error = UnelevatedLauncher.Launch(shortcut.Target, shortcut.Args, shortcut.WorkingDir);
        if (error is not null)
        {
            Log.Error($"Shortcut '{shortcut.Name}': {error}");
            _toast.Show('', $"{shortcut.Name}: {error}");
        }
    }

    // ---- virtual pad -------------------------------------------------------------------------

    public bool PadEnabled => _padEnabled;

    public void TogglePad(char glyph)
    {
        if (_shutDown) return;
        SetPadEnabled(!_padEnabled);
        Config.VirtualPad.Enabled = _padEnabled;
        _store.Save();
        _toast.Show(glyph, _padFailed ? "Virtual pad unavailable (ViGEmBus?)"
            : _padEnabled ? "Virtual pad: on" : "Virtual pad: off");
        if (_mode == Mode.Menu) _overlay.Refresh(); // the pad row shows its state
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    private void SetPadEnabled(bool enabled)
    {
        _padEnabled = enabled;
        _padFailed = false;
        if (!enabled) _pad.Detach();
        else if (!_pad.Attach()) _padFailed = true;
    }

    private string? PadDetail() => !_padEnabled ? "Off" : _padFailed ? "Unavailable" : "On";

    /// <summary>The state is not consumed: it drives the virtual pad.</summary>
    private void Forward()
    {
        if (!_padEnabled || _padFailed) return;
        if (!_pad.IsAttached && !_pad.Attach())
        {
            _padFailed = true;
            _toast.Show(MenuPages.Game, "Virtual pad unavailable (ViGEmBus?)");
            return;
        }

        _ignoreMask &= _state.Current;
        var left = _state.Left;
        var right = _state.Right;
        if (_ignoreSticks)
        {
            if (left.Magnitude < 0.3f && right.Magnitude < 0.3f) _ignoreSticks = false;
            else left = right = default;
        }

        var held = _state.Current & ~_ignoreMask;
        var pad = Config.VirtualPad;
        _pad.Submit(XInputMapper.Map(held, left, right, pad.SwapAB, pad.SwapXY));
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
        if (Config.VirtualPad.Enabled != _padEnabled) SetPadEnabled(Config.VirtualPad.Enabled);
    }

    // ISettingsHost
    string ISettingsHost.ConfigPath => _store.Path;

    string ISettingsHost.HeadsetStatus => _headsetBusy ? "Working..." : HeadsetDetail() ?? "Unknown";

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
        if (_mode == Mode.Menu) _overlay.Refresh();
        if (_mode == Mode.Settings) _settingsWindow.Refresh();
    }

    // ---- window-level entry points -------------------------------------------------------------

    /// <summary>Opens the menu (tray, second launch); no-op when it is already open.</summary>
    public void ShowMenu()
    {
        if (_shutDown || _mode == Mode.Menu) return;
        OpenMenu();
    }

    /// <summary>Tray left click.</summary>
    public void ToggleMenu()
    {
        if (_shutDown) return;
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
        _mouse.Deactivate(); // releases held mouse buttons
        _mode = Mode.Idle;
        _toast.Hide();
        _pad.Dispose(); // unplugs the pad: it disappears from the games
        _window.RemoveTray();
    }

    public void Dispose()
    {
        _mouse.Deactivate();
        _mouseOutput.Dispose();
        _pad.Dispose();
    }
}
