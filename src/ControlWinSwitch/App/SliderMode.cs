using ControlWinSwitch.Actions;
using ControlWinSwitch.Input;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.App;

/// <summary>
/// The volume / brightness slider: the left stick moves it smoothly (on a timer, so the speed doesn't depend on how often
/// the controller reports), the D-pad by one percent, Y mutes, A / B or 5 s without input close it.
/// </summary>
internal sealed class SliderMode : IMode
{
    private const uint TickMs = 15, RefreshMs = 250, IdleMs = 5000, QuietMs = 500;
    private const int WheelStep = 2; // percent per wheel notch, like the keyboard volume keys

    private readonly IModeHost _host;
    private readonly DirectionRepeat _direction;
    private readonly Toast _toast;
    private readonly SliderModel _slider = new();
    private readonly SliderOverlay _overlay = new();
    private readonly UiTimer _tickTimer, _refreshTimer, _idleTimer;
    private Volume? _volume;         // open while the Volume slider is shown
    private Brightness? _brightness; // open while the Brightness slider is shown
    private char _glyph;
    private bool _changed;           // the value was changed since the slider opened
    private long _lastTick, _lastInput;

    public SliderMode(IModeHost host, DirectionRepeat direction, TimerService timers, Toast toast)
    {
        _host = host;
        _direction = direction;
        _toast = toast;
        _tickTimer = timers.Create(OnTick);
        _refreshTimer = timers.Create(OnRefresh);
        _idleTimer = timers.Create(OnIdle);
        _overlay.ValueSet += percent =>
        {
            if (_slider.Set(percent)) Apply();
            else Touch();
        };
        _overlay.Scrolled += notches =>
        {
            if (_slider.Step(notches * WheelStep)) Apply();
            else Touch();
        };
    }

    private ControllerState State => _host.State;

    /// <summary>
    /// Opens the devices and reads the current value. False, with a toast, when there is nothing to control: the slider
    /// is then not shown.
    /// </summary>
    public bool TryOpen(MenuEntry entry, bool volume)
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
            CloseDevices();
            string error = volume ? "No audio output device" : "This screen's brightness can't be changed";
            Log.Info($"Slider: {error}");
            _toast.Show(entry.Glyph, error);
            return false;
        }

        _glyph = entry.Glyph;
        _slider.Reset(value, muted);
        _changed = false;
        _host.NeutralPad(); // neutral pad while the slider owns the input
        _toast.Hide();
        _direction.Held = ReadDpad(); // a D-pad already held must be released first
        _overlay.Show(Glyph, value, muted);
        _refreshTimer.Start(RefreshMs);
        Touch();
        Log.Info($"Slider opened ({(volume ? "volume" : "brightness")} {value})");
        return true;
    }

    public void Exit(bool announce)
    {
        StopTimers();
        _overlay.Hide();
        if (_brightness is { SavesToPowerPlan: true } && _changed)
        {
            // Into the power plan too, or Windows puts its own value back later. Slow: on a worker thread.
            int percent = _slider.Percent;
            ErrorBoundary.StartWorker("brightness", () =>
            {
                if (!Brightness.Save(percent)) Log.Error("Brightness: saving to the power plan failed");
            });
        }
        CloseDevices();
        Log.Info($"Slider closed ({_slider.Percent})");
    }

    private void StopTimers()
    {
        _tickTimer.Stop();
        _refreshTimer.Stop();
        _idleTimer.Stop();
    }

    private void CloseDevices()
    {
        _volume?.Dispose();
        _volume = null;
        _brightness?.Dispose();
        _brightness = null;
    }

    /// <summary>The muted speaker while the volume is muted, the node's own glyph otherwise.</summary>
    private char Glyph => _volume is not null && _slider.Muted ? MenuPages.Mute : _glyph;

    public void OnReport()
    {
        if (State.Pressed(Buttons.A) || State.Pressed(Buttons.B))
        {
            _host.ToIdle();
            return;
        }

        if (_volume is not null && State.Pressed(Buttons.Y))
        {
            _slider.Muted = !_slider.Muted;
            _volume.SetMute(_slider.Muted);
            Touch();
            Refresh();
        }

        // D-pad: one percent per press, repeated while held.
        _direction.Track(ReadDpad(), Step, direction =>
        {
            Step(direction);
            return true;
        });

        // Left stick: moved by a timer while tilted, so the speed doesn't depend on how often the controller reports.
        if (!_tickTimer.Running && _slider.StickActive(State.Left.X))
        {
            _lastTick = Environment.TickCount64;
            _tickTimer.Start(TickMs);
        }
    }

    private Dir ReadDpad()
    {
        var held = State.Current;
        if ((held & Buttons.Left) != 0) return Dir.Left;
        if ((held & Buttons.Right) != 0) return Dir.Right;
        return Dir.None;
    }

    /// <summary>A slider timer that ticks after the slider closed stops itself.</summary>
    private bool Gone(UiTimer timer)
    {
        if (_host.IsOpen(this)) return false;
        timer.Stop();
        return true;
    }

    private void OnTick()
    {
        if (Gone(_tickTimer)) return;
        long now = Environment.TickCount64;
        float seconds = Math.Min(0.1f, (now - _lastTick) / 1000f);
        _lastTick = now;
        float x = State.Left.X;
        if (!_slider.StickActive(x))
        {
            _tickTimer.Stop();
            return;
        }
        if (_slider.Move(x, seconds, boost: State.Held(Buttons.ZR), slow: State.Held(Buttons.ZL))) Apply();
        else Touch();
    }

    private void OnRefresh()
    {
        if (Gone(_refreshTimer)) return;
        // Changes made elsewhere (keyboard keys, another default device), only while the user isn't moving it.
        if (Environment.TickCount64 - _lastInput < QuietMs) return;
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
        Refresh();
    }

    private void OnIdle()
    {
        if (!Gone(_idleTimer)) _host.ToIdle();
    }

    private void Step(Dir direction)
    {
        if (_slider.Step(direction == Dir.Right ? 1 : -1)) Apply();
        else Touch();
    }

    /// <summary>Sends the shown value to the system. Changing the volume unmutes it, like the keyboard keys.</summary>
    private void Apply()
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
        _changed = true;
        Touch();
        Refresh();
    }

    private void Refresh() => _overlay.Update(Glyph, _slider.Percent, _slider.Muted);

    /// <summary>Input on the slider: it stays open another 5 s, and outside changes wait a little.</summary>
    private void Touch()
    {
        _lastInput = Environment.TickCount64;
        _idleTimer.Start(IdleMs); // restarts the countdown
    }
}
