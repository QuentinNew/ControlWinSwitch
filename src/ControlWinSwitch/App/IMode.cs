using ControlWinSwitch.Input;

namespace ControlWinSwitch.App;

/// <summary>
/// A screen or mode that takes the input of one controller (menu, settings, mouse mode, slider, menu editor). The
/// controller opens one at a time; closing it is <see cref="IModeHost.ToIdle"/>, in one place.
/// </summary>
internal interface IMode
{
    /// <summary>A report of the controller that owns the mode (Capture is handled by the controller).</summary>
    void OnReport();

    /// <summary>
    /// Hides what the mode shows and lets go of what it holds. Called by <see cref="IModeHost.ToIdle"/>, which has already
    /// stopped the key repeat and hidden the held input from the game.
    /// </summary>
    void Exit(bool announce);
}

/// <summary>What the modes need from the controller.</summary>
internal interface IModeHost
{
    /// <summary>The state of the controller that owns the open mode.</summary>
    ControllerState State { get; }

    bool IsOpen(IMode mode);

    /// <summary>Closes the open mode (<paramref name="announce"/>: with a toast, for mouse mode).</summary>
    void ToIdle(bool announce = false);

    /// <summary>Nothing pressed on the owner's virtual pad while a mode takes its input.</summary>
    void NeutralPad();

    /// <summary>Straight from the menu to the settings, without closing the menu first (no flicker).</summary>
    void OpenSettings();
}

internal enum Dir { None, Up, Down, Left, Right }

/// <summary>
/// The direction held on the D-pad or the left stick in the menu, settings and slider, run once when pressed and then
/// repeated while held.
/// </summary>
internal sealed class DirectionRepeat
{
    private const uint FirstMs = 400, NextMs = 110;
    private const float StickEngage = 0.6f, StickRelease = 0.4f;

    private readonly UiTimer _timer;
    private Func<Dir, bool> _repeat = _ => false;

    public DirectionRepeat(TimerService timers) => _timer = timers.Create(Tick);

    /// <summary>The direction held now. A mode sets it when it opens, so a direction already held must be released first.</summary>
    public Dir Held { get; set; }

    /// <summary>
    /// Direction from the D-pad or, with <paramref name="useStick"/>, the left stick (with hysteresis so a wobbling stick
    /// doesn't flicker).
    /// </summary>
    public Dir Read(ControllerState state, bool horizontal, bool useStick = true)
    {
        var held = state.Current;
        if ((held & Buttons.Up) != 0) return Dir.Up;
        if ((held & Buttons.Down) != 0) return Dir.Down;
        if (horizontal)
        {
            if ((held & Buttons.Left) != 0) return Dir.Left;
            if ((held & Buttons.Right) != 0) return Dir.Right;
        }
        if (!useStick) return Dir.None;

        float threshold = Held != Dir.None ? StickRelease : StickEngage;
        var stick = state.Left; // +Y = down
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

    /// <summary>
    /// A newly held direction runs <paramref name="apply"/> once, then <paramref name="repeat"/> on every repeat tick
    /// until it is released or <paramref name="repeat"/> returns false.
    /// </summary>
    public void Track(Dir direction, Action<Dir> apply, Func<Dir, bool> repeat)
    {
        if (direction == Held) return;
        Held = direction;
        Stop();
        if (direction == Dir.None) return;

        apply(direction);
        _repeat = repeat;
        _timer.Start(FirstMs);
    }

    public void Stop()
    {
        if (_timer.Running) _timer.Stop();
    }

    /// <summary>Stops repeating and forgets the held direction.</summary>
    public void Reset()
    {
        Stop();
        Held = Dir.None;
    }

    private void Tick()
    {
        if (Held == Dir.None || !_repeat(Held))
        {
            Stop();
            return;
        }
        _timer.Start(NextMs); // restarts it with the faster interval
    }
}
