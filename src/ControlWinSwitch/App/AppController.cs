using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>
/// The brain: routes every controller report to the open mode (menu, settings, mouse mode, slider, menu editor) or, when
/// none is open, to the controller's virtual pad. It decides which controller owns the open mode, switches modes, and
/// wires the services (pads, registry, headset, pairing, commands) to the window's messages.
/// </summary>
internal sealed class AppController : IDisposable, IModeHost, ICommandHost, ISettingsApp
{
    private const uint HideMenuDelayMs = 10; // the menu stays under the settings window this long, so no frame shows neither
    private const float StickEngage = 0.6f;
    private const char ErrorGlyph = '';

    /// <summary>What belongs to one controller: its state and what of it is hidden from its virtual pad.</summary>
    private sealed class Slot
    {
        public readonly ControllerState State = new();
        public Buttons IgnoreMask; // buttons still held from the menu: hidden from the game until released
        public bool IgnoreSticks;  // sticks used in the menu: hidden until back near center
    }

    private readonly ControllerSet _set;
    private readonly MainWindow _window;
    private readonly Slot[] _slots = new Slot[ControllerSet.Slots];
    private int _owner; // the controller that has the open mode (the last one heard while idle)
    private readonly ConfigStore _store = new();
    private readonly Toast _toast = new();
    private readonly TimerService _timers;
    private readonly DirectionRepeat _direction;
    private readonly ControllerRegistry _registry;
    private readonly PadService _pads;
    private readonly HeadsetService _headset;
    private readonly PairingService _pairing;
    private readonly CommandRunner _commands;

    private readonly MenuMode _menu;
    private readonly SettingsMode _settings;
    private readonly MouseControlMode _mouse;
    private readonly SliderMode _slider;
    private readonly EditorMode _editor;
    private IMode? _current; // null = idle: the reports drive the virtual pads
    private readonly UiTimer _hideMenuTimer; // hides the menu once the settings window is up
    private bool _shutDown;

    public AppController(ControllerSet set, MainWindow window)
    {
        _set = set;
        _window = window;
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot();
        _timers = new TimerService(window.Handle);
        _direction = new DirectionRepeat(_timers);
        _registry = new ControllerRegistry(_store, set, StateOf);

        var error = _store.Load();
        if (error is not null) _toast.Show(ErrorGlyph, error);

        _pads = new PadService(_store, set, _timers, _toast); // after Load: it reads the pad settings
        _pads.WatchChanged += RefreshOpenScreen;
        _headset = new HeadsetService(_store, window.Handle, _toast, IsShutDown, OpenBluetoothSettings);
        _headset.Changed += RefreshOpenScreen;
        _pairing = new PairingService(set, _registry, window.Handle, _toast, IsShutDown);
        _pairing.Changed += RefreshSettings;
        _commands = new CommandRunner(this, window.Handle, _toast, _pads, _headset, _pairing, IsShutDown);

        _menu = new MenuMode(this, _direction, _commands);
        _mouse = new MouseControlMode(this, _toast, MouseConfig);
        _slider = new SliderMode(this, _direction, _timers, _toast);
        _editor = new EditorMode(this, _store, _toast, SaveConfig);
        _settings = new SettingsMode(this, _direction, _store, _registry, CreateSettingsHost);
        _hideMenuTimer = _timers.Create(OnHideMenuTimer);
        ApplyMenuHotkey();
    }

    private AppConfig Config => _store.Current;

    private ControllerState StateOf(int slot) => _slots[slot].State;

    private bool IsShutDown() => _shutDown;

    private MouseConfig MouseConfig() => _store.Current.Mouse;

    private SettingsHost CreateSettingsHost(SettingsMode screen) =>
        new(screen, this, _store, _set, StateOf, _registry, _pads, _headset, _pairing, _timers, _toast);

    // ---- message loop hooks ------------------------------------------------------------------

    /// <summary>Signalled when a game sent the slot's pad a rumble request.</summary>
    public nint PadNotifyHandle(int slot) => _pads.NotifyHandle(slot);

    public void OnPadNotify(int slot) => _pads.OnNotify(slot);

    /// <summary>Waitable timer handle for the message loop; signalled only while a stick moves the mouse.</summary>
    public nint MouseTimerHandle => _mouse.TimerHandle;

    public void OnMouseTick() => _mouse.OnTick();

    /// <summary>Handle of the watched process for the message loop (0 = none).</summary>
    public nint WatchHandle => _pads.WatchHandle;

    /// <summary>The watched process exited (its handle was signalled).</summary>
    public void OnWatchedProcessExited() => _pads.OnWatchedProcessExited();

    /// <summary>WM_TIMER of the main window.</summary>
    public void OnTimer(nuint id) => _timers.Dispatch(id);

    // ---- controller reports ------------------------------------------------------------------

    /// <summary>Report handler: updates the slot's state, then drives the open mode or the virtual pad.</summary>
    public void OnReport(int slot, ProReport report)
    {
        _slots[slot].State.Update(report);
        if (_shutDown) return; // reports still queued while the window closes must not re-plug the pad
        if (_registry.KnownSyncDue) SyncKnown(); // a MAC read late, half of a Joy-Con pair gone
        _settings.OnAnyReport(slot);

        // The open mode belongs to one controller; the others keep playing.
        if (_current is null) _owner = slot;
        else if (slot != _owner && !TakeOver(slot))
        {
            Forward(slot);
            return;
        }

        // Capture is reserved for the menu toggle and is never forwarded. It closes any open mode (the menu editor
        // without saving).
        if (State.Pressed(Buttons.Capture))
        {
            if (_current is null) OpenMenu();
            else ToIdle(announce: true);
            return;
        }

        if (_current is null) Forward(slot);
        else _current.OnReport();
    }

    /// <summary>
    /// With "All controllers control menu", or once the controller that had them disconnected, another controller takes
    /// the open menu or settings over as soon as it presses a button or tilts a stick. The one that had them goes back to
    /// its game, its held buttons and sticks hidden until released. Mouse mode stays with the controller that started it.
    /// </summary>
    private bool TakeOver(int slot)
    {
        if (_current != _menu && _current != _settings) return false;
        // Without "All controllers control menu", only once the controller that had them is gone.
        if (!Config.MenuAnyController && _set.Source(_owner).IsOpen) return false;
        var state = _slots[slot].State;
        bool active = (state.Current & ~state.Previous) != 0
            || state.Left.Magnitude > StickEngage || state.Right.Magnitude > StickEngage;
        if (!active) return false;

        HideHeldInput();
        _direction.Reset();
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
        _settings.OnControllerRemoved(slot);
        int heir = Config.MenuAnyController && (_current == _menu || _current == _settings)
            ? Enumerable.Range(0, _slots.Length).FirstOrDefault(i => i != slot && _set.Source(i).IsOpen, -1) : -1;
        if (slot == _owner && heir >= 0)
        {
            // Every controller drives the menu: another one keeps it open.
            _direction.Reset();
            _owner = heir;
            _pads.Neutral(_owner);
        }
        else if (slot == _owner)
        {
            // The settings stay open: the mouse still works, and the next controller that presses a button takes them.
            // The menu editor is a mouse window.
            if (_current == _settings) _direction.Reset();
            else if (_current != _editor) ToIdle();
        }
        gone.IgnoreMask = Buttons.None;
        gone.IgnoreSticks = false;
        SyncKnown();
        _settings.Refresh();
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
        if (_current == _mouse || _current == _slider) ToIdle();
    }

    /// <summary>The stick calibration of a controller was read from its flash.</summary>
    public void OnCalibration(int slot, StickCalibrations calibration)
    {
        _slots[slot].State.SetCalibration(calibration);
        _settings.Refresh();
    }

    /// <summary>Joy-Cons joined a slot: a new controller, or the partner of a lone Joy-Con.</summary>
    public void OnJoyConsChanged(int slot)
    {
        _window.UpdateTray();
        if (!_shutDown) _settings.Refresh();
    }

    /// <summary>Records the controllers that connected or went away, and keeps the open controller page in line.</summary>
    private void SyncKnown()
    {
        if (_registry.SyncKnown() is { } joined) _settings.OnKnownChanged(joined);
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

    /// <summary>Hides the owner's held buttons and sticks from the game until they are released.</summary>
    private void HideHeldInput()
    {
        var owner = _slots[_owner];
        owner.IgnoreMask = owner.State.Current;
        owner.IgnoreSticks = true;
    }

    // ---- modes -------------------------------------------------------------------------------

    /// <summary>The state of the controller that owns the open mode.</summary>
    public ControllerState State => _slots[_owner].State;

    public bool IsOpen(IMode mode) => _current == mode;

    /// <summary>Closes the open mode: the key repeat stops and what is still held is hidden from the game until released.</summary>
    public void ToIdle(bool announce = false)
    {
        if (_current is not { } mode) return;
        _direction.Reset();
        _current = null;
        HideHeldInput();
        mode.Exit(announce);
    }

    public void NeutralPad() => _pads.Neutral(_owner);

    void IModeHost.OpenSettings() => OpenSettings();

    private void OpenMenu()
    {
        ToIdle(); // tray / second launch while mouse mode, the settings or the slider is open
        ReloadConfigIfChanged();
        var statuses = new NodeStatuses(_pads.Detail, _headset.Detail);
        _pads.Neutral(_owner); // neutral pad while the menu owns the input
        _current = _menu;
        _menu.Open(Config, statuses, Config.MenuShowControllers ? _registry.ConnectedTiles() : null);
        _headset.Refresh(); // refresh the status shown in the menu
    }

    private void OpenSettings()
    {
        bool fromMenu = _current == _menu;
        if (fromMenu)
        {
            // Straight from the menu: it stays up under the settings window for a moment, so no frame shows neither.
            _direction.Stop();
            _current = null;
        }
        else ToIdle();

        ReloadConfigIfChanged();
        SyncKnown();
        _registry.RefreshRemembered();
        _pads.Neutral(_owner);
        _current = _settings;
        _settings.Open();
        if (fromMenu) _hideMenuTimer.Start(HideMenuDelayMs);
        _headset.Refresh(); // refresh the status shown on the Headset tab
    }

    private void OnHideMenuTimer()
    {
        _hideMenuTimer.Stop();
        if (_current == _settings) _menu.HideOverlay();
    }

    private void OpenMenuEditor(string layout)
    {
        if (_shutDown || _current == _editor) return;
        ToIdle();
        _pads.Neutral(_owner); // neutral pad while the editor is open
        _current = _editor;
        _editor.Open(layout);
    }

    // ---- commands (run by CommandRunner) -----------------------------------------------------

    void ICommandHost.OpenSettings() => OpenSettings();

    void ICommandHost.EnterMouse(char glyph)
    {
        ToIdle();
        _pads.Neutral(_owner); // neutral pad while the mouse owns the input
        _current = _mouse;
        _mouse.Enter(glyph);
    }

    void ICommandHost.EnterSlider(MenuEntry entry, bool volume)
    {
        ToIdle();
        if (_slider.TryOpen(entry, volume)) _current = _slider;
    }

    void ICommandHost.BeforePowerAction()
    {
        if (_current == _mouse) ToIdle();
        State.Reset();
    }

    /// <summary>AppMessages.PowerFailed, on the UI thread.</summary>
    public void OnPowerFailed() => _commands.OnPowerFailed();

    /// <summary>AppMessages.ScreenshotDone, on the UI thread.</summary>
    public void OnScreenshotDone() => _commands.OnScreenshotDone();

    /// <summary>AppMessages.HeadsetDone, on the UI thread.</summary>
    public void OnHeadsetDone(HeadsetState state, bool wasToggle) => _headset.OnDone(state, wasToggle);

    /// <summary>AppMessages.PairDone, on the UI thread.</summary>
    public void OnPairDone(PairResult result) => _pairing.OnDone(result);

    /// <summary>A Joy-Con 2 in sync mode was paired with this PC (UI thread).</summary>
    public void OnJoyConPaired(JoyCon2Device device) => _pairing.OnJoyConPaired(device);

    // ---- virtual pad -------------------------------------------------------------------------

    public bool PadEnabled => _pads.Enabled;

    /// <summary>Note shown next to the pad in the menu and tray while a watched process holds it off.</summary>
    public string? PadNote => _pads.Note;

    public void TogglePad(char glyph)
    {
        if (_shutDown) return;
        _pads.UserSet(!_pads.Enabled);
        _toast.Show(glyph, _pads.Failed ? "Virtual pad unavailable (ViGEmBus?)"
            : _pads.Enabled ? "Virtual pad: on" : "Virtual pad: off");
        RefreshOpenScreen(); // the pad row shows its state
    }

    /// <summary>Something the menu or settings show changed (pad, headset): repaints whichever is open.</summary>
    private void RefreshOpenScreen()
    {
        _menu.Refresh();
        _settings.Refresh();
    }

    private void RefreshSettings() => _settings.Refresh();

    private void OpenBluetoothSettings() => _settings.OpenBluetoothSettings();

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
            _toast.Show(ErrorGlyph, error);
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

    public void SaveConfig()
    {
        var error = _store.Save();
        if (error is not null) _toast.Show(ErrorGlyph, error);
        ApplyConfig();
    }

    public void ReloadConfig()
    {
        var error = _store.Load();
        if (error is null)
        {
            ApplyConfig();
            _toast.Show(MenuPages.Gear, "Config reloaded");
        }
        else
        {
            _toast.Show(ErrorGlyph, error);
        }
    }

    void ISettingsApp.EditMenu() => OpenMenuEditor(Config.MenuLayout);

    /// <summary>Opens config.json in Notepad with the user's own rights.</summary>
    public void EditConfig()
    {
        if (_shutDown) return;
        _settings.CloseIfOpen();
        if (!File.Exists(_store.Path)) _store.Save();
        LaunchNotepad(_store.Path);
    }

    /// <summary>Opens log.txt in Notepad with the user's own rights.</summary>
    public void OpenLog()
    {
        if (_shutDown) return;
        var path = Log.FilePath;
        if (path is null) return;
        _settings.CloseIfOpen();
        LaunchNotepad(path);
    }

    private void LaunchNotepad(string path)
    {
        var error = UnelevatedLauncher.Launch("notepad.exe", $"\"{path}\"");
        if (error is not null)
        {
            Log.Error($"Notepad: {error}");
            _toast.Show(ErrorGlyph, error);
        }
    }

    // ---- window-level entry points -------------------------------------------------------------

    /// <summary>Opens the menu (tray, second launch); no-op when it is already open.</summary>
    public void ShowMenu()
    {
        if (_shutDown || _current == _menu || _current == _editor) return;
        OpenMenu();
    }

    /// <summary>Tray left click.</summary>
    public void ToggleMenu()
    {
        if (_shutDown || _current == _editor) return;
        if (_current == _menu) ToIdle();
        else OpenMenu();
    }

    /// <summary>Opens the settings window (menu entry, tray); no-op when it is already open.</summary>
    public void ShowSettings()
    {
        if (_shutDown || _current == _settings || _current == _editor) return;
        OpenSettings();
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
        TryStep("close the open screen", () => ToIdle());
        _current = null;
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
        var mode = _current;
        _current = null;
        TryStep("stop the key repeat", _direction.Reset);
        if (mode is not null) TryStep("close the open screen", () => mode.Exit(announce: false));
        TryStep("hide the menu", _menu.HideAll);
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
        Log.Info($"Recovered from the error (was {mode?.GetType().Name ?? "idle"})");
        TryStep("show the error toast", () => _toast.Show(MenuPages.Gear, "Something went wrong - see log"));
    }

    /// <summary>Last resort when the process is about to die, from any thread: shows the hidden controllers again.</summary>
    public void ShowControllersAgain() => _pads.ShowControllersAgain();

    private static void TryStep(string step, Action action) => ErrorBoundary.TryStep(step, action);

    public void Dispose()
    {
        _mouse.Dispose();
        _pads.Dispose();
    }
}
