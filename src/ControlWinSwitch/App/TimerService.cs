using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.App;

/// <summary>
/// Hands out the main window's WM_TIMER ids: each owner creates its timers with their callback, so nobody numbers ids by
/// hand or shares one big WM_TIMER switch.
/// </summary>
internal sealed class TimerService
{
    private readonly Action<nuint, uint> _set;
    private readonly Action<nuint> _kill;
    private readonly List<UiTimer> _timers = []; // id = index + 1

    /// <summary>Timers of a window (WM_TIMER is then passed to <see cref="Dispatch"/>).</summary>
    public TimerService(nint hwnd) : this((id, ms) => SetTimer(hwnd, id, ms, 0), id => KillTimer(hwnd, id)) { }

    /// <summary>For tests: what starting and stopping a timer does.</summary>
    internal TimerService(Action<nuint, uint> set, Action<nuint> kill)
    {
        _set = set;
        _kill = kill;
    }

    /// <summary>A new, stopped timer that calls <paramref name="onTick"/> on every tick until stopped.</summary>
    public UiTimer Create(Action onTick)
    {
        var timer = new UiTimer(this, (nuint)(_timers.Count + 1), onTick);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>WM_TIMER: runs the callback of the timer with this id.</summary>
    public void Dispatch(nuint id)
    {
        int index = (int)id - 1;
        if (index < 0 || index >= _timers.Count) return;
        var timer = _timers[index];
        if (timer.Running) timer.OnTick();
        else _kill(id); // a tick already queued when it was stopped
    }

    internal void Set(nuint id, uint ms) => _set(id, ms);

    internal void Kill(nuint id) => _kill(id);
}

/// <summary>A repeating WM_TIMER of the main window (UI thread only).</summary>
internal sealed class UiTimer
{
    private readonly TimerService _owner;
    private readonly nuint _id;
    internal readonly Action OnTick;

    internal UiTimer(TimerService owner, nuint id, Action onTick)
    {
        _owner = owner;
        _id = id;
        OnTick = onTick;
    }

    public bool Running { get; private set; }

    /// <summary>Starts the timer, or restarts it with this interval when it already runs.</summary>
    public void Start(uint ms)
    {
        _owner.Set(_id, ms);
        Running = true;
    }

    public void Stop()
    {
        _owner.Kill(_id);
        Running = false;
    }
}
