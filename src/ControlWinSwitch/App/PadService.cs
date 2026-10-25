using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using ControlWinSwitch.UI;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.App;

/// <summary>
/// The virtual pads and what decides whether they are on: one ViGEm pad per controller slot, HidHide hiding the physical
/// controllers that drive one, the process watcher that turns the pads off while a listed program runs, and the game's
/// rumble sent back to the controllers.
/// </summary>
internal sealed class PadService : IDisposable
{
    private const uint GameRumbleRefreshMs = 80;
    private const uint WatchPollMs = 5000, WatchFastPollMs = 1000, WatchFastWindowMs = 15000;
    private const char ErrorGlyph = '';

    private readonly ConfigStore _store;
    private readonly ControllerSet _set;
    private readonly Toast _toast;
    private readonly VirtualPad[] _pads = new VirtualPad[ControllerSet.Slots];
    private readonly byte[] _gameLarge = new byte[ControllerSet.Slots], _gameSmall = new byte[ControllerSet.Slots];
    private readonly UiTimer[] _gameRumbleTimers = new UiTimer[ControllerSet.Slots]; // the controller rumbles only while it gets reports
    private readonly UiTimer _watchTimer;
    private readonly PadGate _gate = new();

    private bool _enabled;
    private bool _failed;
    private bool _shutDown;

    private nint _watchHandle;        // the watched process we wait on (0 = none, polling instead)
    private bool _watchRunning;
    private string _watchName = "";   // list entry of the process that matched
    private long _fastPollUntil;      // Environment.TickCount64 until which the poll is fast (after a shortcut launch)

    private string _hiddenKey = "";   // the set of paths last applied (to skip identical requests)
    private bool _hideFailed;

    /// <summary>A watched process started or closed, which may have switched the pads: the menu and settings show it.</summary>
    public event Action? WatchChanged;

    /// <summary>Call once the config is loaded: a watched process may already be running.</summary>
    public PadService(ConfigStore store, ControllerSet set, TimerService timers, Toast toast)
    {
        _store = store;
        _set = set;
        _toast = toast;
        _watchTimer = timers.Create(CheckProcesses);
        for (int i = 0; i < _pads.Length; i++)
        {
            int slot = i;
            _pads[slot] = new VirtualPad();
            _pads[slot].Feedback += (large, small) => OnFeedback(slot, large, small);
            _gameRumbleTimers[slot] = timers.Create(() => ApplyGameRumble(slot, restartTimer: false));
        }

        _watchRunning = ProbeProcesses();
        _enabled = _gate.Apply(Config.VirtualPad.Enabled, _watchRunning);
        UpdateWatchTimer();
    }

    private AppConfig Config => _store.Current;

    public bool Enabled => _enabled;

    /// <summary>ViGEmBus could not plug a pad in.</summary>
    public bool Failed => _failed;

    /// <summary>Note shown next to the pad in the menu and tray while a watched process holds it off.</summary>
    public string? Note => _gate.HeldOff ? ProcessList.DisplayName(_watchName) : null;

    /// <summary>The status shown on the pad's menu node.</summary>
    public string? Detail() => !_enabled ? (_gate.HeldOff ? $"Off ({ProcessList.DisplayName(_watchName)})" : "Off") : _failed ? "Unavailable" : "On";

    /// <summary>The Settings choice: 0 off, 1 Xbox 360, 2 DualShock 4.</summary>
    public int Mode => !_enabled ? 0 : Config.VirtualPad.Type == PadTypes.Ps4 ? 2 : 1;

    private PadKind ConfiguredKind => Config.VirtualPad.Type == PadTypes.Ps4 ? PadKind.Ps4 : PadKind.Xbox;

    // ---- message loop hooks ------------------------------------------------------------------

    /// <summary>Signalled when a game sent the slot's pad a rumble request (0 when unplugged).</summary>
    public nint NotifyHandle(int slot) => _pads[slot].NotifyHandle;

    public void OnNotify(int slot) => _pads[slot].OnNotifySignaled();

    /// <summary>Handle of the watched process for the message loop (0 = none).</summary>
    public nint WatchHandle => _watchHandle;

    /// <summary>The watched process exited (its handle was signalled).</summary>
    public void OnWatchedProcessExited()
    {
        CloseWatchHandle();
        CheckProcesses();
    }

    // ---- on / off ----------------------------------------------------------------------------

    /// <summary>
    /// The user's choice (menu, tray, settings). It wins over the process watcher; it is only saved to the
    /// config while no watched process runs, so a choice made during Steam stays runtime-only.
    /// </summary>
    public void UserSet(bool enabled)
    {
        _gate.ManualOverride();
        SetEnabled(enabled);
        if (!_watchRunning)
        {
            Config.VirtualPad.Enabled = enabled;
            _store.Save();
        }
    }

    /// <summary>The Settings choice (see <see cref="Mode"/>).</summary>
    public void SetMode(int mode)
    {
        if (mode == Mode) return;
        if (mode > 0)
        {
            string type = mode == 2 ? PadTypes.Ps4 : PadTypes.Xbox;
            if (Config.VirtualPad.Type != type)
            {
                Config.VirtualPad.Type = type;
                if (!_watchRunning) _store.Save();
            }
        }
        UserSet(mode > 0); // also re-plugs the pads already in when the kind changed
    }

    private void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _failed = false;
        var kind = ConfiguredKind;
        for (int i = 0; i < _pads.Length; i++)
        {
            var pad = _pads[i];
            if (pad.IsAttached && pad.PluggedKind != kind) pad.Detach();
            pad.Kind = kind;
            if (!enabled) pad.Detach();
            else if (_set.Source(i).IsOpen && !pad.Attach()) _failed = true;
        }
        SyncHiding();
    }

    /// <summary>Brings the pads in line with the current config (after a reload or a settings change).</summary>
    public void ApplyConfig()
    {
        // A file edit / reload is a fresh start for the watcher too (the list may have changed).
        _watchRunning = ProbeProcesses();
        bool enabled = _gate.Apply(Config.VirtualPad.Enabled, _watchRunning);
        UpdateWatchTimer();
        if (enabled != _enabled) SetEnabled(enabled);
        else if (_enabled && _pads.Any(p => p.IsAttached && p.PluggedKind != ConfiguredKind)) SetEnabled(true); // the kind changed
        else SyncHiding(); // the Hide setting may have changed
        for (int i = 0; i < _pads.Length; i++) ApplyGameRumble(i, restartTimer: false); // the Rumble setting may have changed (0 stops it at once)
    }

    // ---- forwarding --------------------------------------------------------------------------

    /// <summary>
    /// Whether the slot's input can go to its pad: on, plugged in (it is plugged on the first report) and its controller
    /// hidden. A pad that cannot be plugged in is reported once.
    /// </summary>
    public bool Prepare(int slot)
    {
        if (!_enabled || _failed) return false;
        var pad = _pads[slot];
        if (!pad.IsAttached && !pad.Attach())
        {
            _failed = true;
            _toast.Show(MenuPages.Game, "Virtual pad unavailable (ViGEmBus?)");
            return false;
        }
        var paths = _set.Source(slot).HidPaths;
        for (int i = 0; i < paths.Count; i++)
            if (!_hiddenKey.Contains(paths[i], StringComparison.OrdinalIgnoreCase))
            {
                SyncHiding(); // just attached
                break;
            }
        return true;
    }

    /// <summary>Sends the held buttons and sticks (deadzones applied) to the slot's pad, in its kind's layout.</summary>
    public void Submit(int slot, Buttons held, Stick left, Stick right)
    {
        var pad = _pads[slot];
        var config = Config.VirtualPad;
        if (pad.PluggedKind == PadKind.Ps4) pad.Submit(Ds4Mapper.Map(held, left, right, config.SwapAB, config.SwapXY));
        else pad.Submit(XInputMapper.Map(held, left, right, config.SwapAB, config.SwapXY));
    }

    /// <summary>Nothing pressed on the slot's pad (the app took the controller's input).</summary>
    public void Neutral(int slot) => _pads[slot].SubmitNeutral();

    /// <summary>The slot's controller went away: its pad is unplugged (and plugged in again on the next report).</summary>
    public void OnControllerRemoved(int slot)
    {
        _pads[slot].Detach();
        SyncHiding();
    }

    // ---- game rumble -------------------------------------------------------------------------

    /// <summary>A game set a virtual pad's motors: forward them to its controller, scaled by the Rumble setting.</summary>
    private void OnFeedback(int slot, byte large, byte small)
    {
        _gameLarge[slot] = large;
        _gameSmall[slot] = small;
        ApplyGameRumble(slot, restartTimer: true);
    }

    /// <summary>
    /// The controller keeps vibrating only while it keeps receiving rumble reports: refresh them while the game
    /// asks for any, and send a neutral one when it stops.
    /// </summary>
    private void ApplyGameRumble(int slot, bool restartTimer)
    {
        int percent = Config.VirtualPad.Rumble;
        bool active = percent > 0 && (_gameLarge[slot] > 0 || _gameSmall[slot] > 0);
        _set.Source(slot).SetRumbleMotors(active ? _gameLarge[slot] : (byte)0, active ? _gameSmall[slot] : (byte)0, percent);
        if (!active) _gameRumbleTimers[slot].Stop();
        else if (restartTimer) _gameRumbleTimers[slot].Start(GameRumbleRefreshMs);
    }

    // ---- process watcher ---------------------------------------------------------------------

    /// <summary>A program was just launched: it may be a watched one, so look now and then every second for a while.</summary>
    public void WatchSoon()
    {
        _fastPollUntil = Environment.TickCount64 + WatchFastWindowMs;
        CheckProcesses();
    }

    /// <summary>Re-evaluates against a changed list: an emptied list gives the pad back, a new entry may take it.</summary>
    public void WatchListChanged() => CheckProcesses();

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
        switch (_gate.OnRunningChanged(running, _enabled))
        {
            case PadGate.Change.Disable:
                SetEnabled(false);
                break;
            case PadGate.Change.Enable:
                SetEnabled(true);
                break;
        }
        WatchChanged?.Invoke();
    }

    /// <summary>Poll only while there is something to wait for and no handle to wait on.</summary>
    private void UpdateWatchTimer()
    {
        bool poll = Config.VirtualPad.DisableWhileRunning.Count > 0 && _watchHandle == 0;
        if (!poll)
        {
            _watchTimer.Stop();
            return;
        }

        _watchTimer.Start(Environment.TickCount64 < _fastPollUntil ? WatchFastPollMs : WatchPollMs); // replaces the interval
    }

    // ---- hiding the physical controllers (HidHide) -----------------------------------------

    /// <summary>Hides the controllers that currently drive a virtual pad, and shows every other one again.</summary>
    private void SyncHiding()
    {
        var wanted = new List<string>();
        if (_enabled && !_failed && !_shutDown && Config.VirtualPad.HidePhysical)
        {
            for (int i = 0; i < _pads.Length; i++)
                if (_pads[i].IsAttached) wanted.AddRange(_set.Source(i).HidPaths);
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
            if (!failedBefore) _toast.Show(ErrorGlyph, error);
        }
    }

    /// <summary>Last resort when the process is about to die, from any thread: shows the hidden controllers again.</summary>
    public void ShowControllersAgain()
    {
        if (_hiddenKey.Length > 0) HidHide.Apply([]);
    }

    // ---- lifetime ----------------------------------------------------------------------------

    /// <summary>Unplugs the pads (they disappear from the games) and shows the controllers again. Each step on its own.</summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        foreach (var pad in _pads) ErrorBoundary.TryStep("unplug a virtual pad", pad.Dispose);
        ErrorBoundary.TryStep("show the controllers again", SyncHiding); // shutting down: hides nothing
    }

    public void Dispose()
    {
        foreach (var pad in _pads) pad.Dispose();
    }
}
