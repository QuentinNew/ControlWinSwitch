using ControlWin.Config;
using ControlWin.Input;

namespace ControlWin.App;

/// <summary>Timing constants of mouse mode (the tunable values live in <see cref="ControlWin.Config.MouseConfig"/>).</summary>
internal static class MouseDefaults
{
    public const double DpadDelay = 0.3;       // s: a short tap moves exactly 1 px
    public const int TickMs = 8;
}

/// <summary>What mouse mode produces. The Win32 implementation is <c>SendInputMouse</c>; tests use a fake.</summary>
internal interface IMouseOutput
{
    void Move(int dx, int dy);
    void Button(bool right, bool down);
    void Wheel(int amount);   // 120 = one notch, positive = away from the user (scroll up)
    void HWheel(int amount);  // positive = right
    void SetTimer(bool running);
}

/// <summary>Left stick moves, right stick scrolls, A / B click, D-pad nudges precisely (1 px per tap, slow glide when held).</summary>
internal sealed class MouseMode
{
    private const int WheelChunk = 40;

    private readonly Func<ControllerState> _stateOf;

    private ControllerState _state => _stateOf();
    private readonly IMouseOutput _output;
    private readonly Func<long> _clock;
    private readonly Func<MouseConfig> _config;

    private bool _active;
    private Buttons _ignoreMask;
    private bool _leftDown, _rightDown;
    private bool _timerRunning;
    private long _lastTick;
    private double _dpadHeld;
    private float _fracX, _fracY, _wheel, _hwheel;

    public MouseMode(ControllerState state, IMouseOutput output, Func<MouseConfig>? config = null, Func<long>? clock = null)
        : this(() => state, output, config, clock)
    {
    }

    /// <summary>The state is looked up on every use: with several controllers, the one that opened the mouse mode drives it.</summary>
    public MouseMode(Func<ControllerState> stateOf, IMouseOutput output, Func<MouseConfig>? config = null, Func<long>? clock = null)
    {
        _stateOf = stateOf;
        _output = output;
        var fallback = new MouseConfig();
        _config = config ?? (() => fallback);
        _clock = clock ?? System.Diagnostics.Stopwatch.GetTimestamp;
    }

    public bool IsActive => _active;

    /// <summary>Buttons held on entry are ignored until released (the A press that selected the entry).</summary>
    public void Activate(Buttons currentlyHeld)
    {
        _active = true;
        _ignoreMask = currentlyHeld;
        _fracX = _fracY = _wheel = _hwheel = 0;
        OnReport();
    }

    /// <summary>Releases every button the mode holds and stops the timer. Safe to call twice.</summary>
    public void Deactivate()
    {
        if (_leftDown) _output.Button(false, false);
        if (_rightDown) _output.Button(true, false);
        _leftDown = _rightDown = false;
        StopTimer();
        _active = false;
        _ignoreMask = Buttons.None;
    }

    /// <summary>Call after <see cref="ControllerState.Update"/>: clicks, keyboard toggle, timer arming.</summary>
    public void OnReport()
    {
        if (!_active) return;

        var held = _state.Current;
        _ignoreMask &= held;
        var live = held & ~_ignoreMask;

        SetButton(false, (live & Buttons.A) != 0, ref _leftDown);
        SetButton(true, (live & Buttons.B) != 0, ref _rightDown);

        var (px, py) = DpadVector(live);
        if (px != 0 || py != 0)
        {
            var edge = live & (Buttons.Up | Buttons.Down | Buttons.Left | Buttons.Right) & ~_state.Previous;
            if (edge != 0) _output.Move(px, py); // 1 px per tap
        }
        else _dpadHeld = 0;

        if (!_timerRunning && SticksActive())
        {
            _timerRunning = true;
            _lastTick = _clock();
            _output.SetTimer(true);
        }
    }

    /// <summary>Timer tick: moves and scrolls according to the sticks.</summary>
    public void Tick()
    {
        long now = _clock();
        double dt = (now - _lastTick) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTick = now;
        Tick(dt);
    }

    internal void Tick(double dt)
    {
        if (!_active)
        {
            StopTimer();
            return;
        }
        dt = Math.Clamp(dt, 0, 0.05); // a stalled loop must not teleport the cursor

        var cfg = _config();
        var live = _state.Current & ~_ignoreMask;
        var move = _state.Left.WithDeadzone(cfg.Deadzone);
        var scroll = _state.Right.WithDeadzone(cfg.Deadzone);
        // A lone Joy-Con has one stick: it scrolls while L or R (SL / SR) is held.
        if (_state.SingleStick && (live & (Buttons.L | Buttons.R)) != 0) (move, scroll) = (default, move);
        var (dpx, dpy) = DpadVector(live);
        bool dpad = dpx != 0 || dpy != 0;
        if (move == default && scroll == default && !dpad)
        {
            StopTimer();
            return;
        }

        float multiplier = 1f;
        if ((live & Buttons.ZR) != 0) multiplier *= cfg.FastMultiplier;
        if ((live & Buttons.ZL) != 0) multiplier *= cfg.SlowMultiplier;

        if (move != default)
        {
            float k = MouseMath.Speed(move, cfg.Speed, multiplier) / move.Magnitude * (float)dt;
            _fracX += move.X * k;
            _fracY += move.Y * k;
            int dx = (int)_fracX, dy = (int)_fracY;
            _fracX -= dx;
            _fracY -= dy;
            if (dx != 0 || dy != 0) _output.Move(dx, dy);
        }

        if (dpad)
        {
            _dpadHeld += dt;
            if (_dpadHeld > MouseDefaults.DpadDelay)
            {
                _fracX += dpx * cfg.DpadSpeed * (float)dt;
                _fracY += dpy * cfg.DpadSpeed * (float)dt;
                int dx = (int)_fracX, dy = (int)_fracY;
                _fracX -= dx;
                _fracY -= dy;
                if (dx != 0 || dy != 0) _output.Move(dx, dy);
            }
        }
        else _dpadHeld = 0;

        if (scroll != default)
        {
            // ZR / ZL speed up or slow down scrolling as well as the cursor.
            float k = cfg.ScrollSpeed * 120f * multiplier * (float)dt;
            _wheel += -scroll.Y * k;
            _hwheel += scroll.X * k;
            int w = TakeChunks(ref _wheel);
            if (w != 0) _output.Wheel(w);
            int h = TakeChunks(ref _hwheel);
            if (h != 0) _output.HWheel(h);
        }
    }

    private static int TakeChunks(ref float acc)
    {
        int chunks = (int)(acc / WheelChunk);
        acc -= chunks * WheelChunk;
        return chunks * WheelChunk;
    }

    private bool SticksActive() =>
        _state.Left.WithDeadzone(_config().Deadzone) != default ||
        _state.Right.WithDeadzone(_config().Deadzone) != default ||
        DpadVector(_state.Current & ~_ignoreMask) != (0, 0);

    private static (int X, int Y) DpadVector(Buttons b) =>
        (((b & Buttons.Right) != 0 ? 1 : 0) - ((b & Buttons.Left) != 0 ? 1 : 0),
         ((b & Buttons.Down) != 0 ? 1 : 0) - ((b & Buttons.Up) != 0 ? 1 : 0));

    private void SetButton(bool right, bool want, ref bool isDown)
    {
        if (want == isDown) return;
        isDown = want;
        _output.Button(right, want);
    }

    private void StopTimer()
    {
        if (!_timerRunning) return;
        _timerRunning = false;
        _output.SetTimer(false);
    }
}

internal static class MouseMath
{
    /// <summary>Cursor speed in px/s for a deadzoned stick: quadratic curve, so small tilts stay precise.</summary>
    public static float Speed(Stick deadzoned, float maxSpeed, float multiplier)
    {
        float m = MathF.Min(1f, deadzoned.Magnitude);
        return m * m * maxSpeed * multiplier;
    }
}
