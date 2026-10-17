using ControlWin.Input;

namespace ControlWin.App;

internal enum AutoDeadzone { Idle, Sampling, Done, Pushed }

/// <summary>
/// The deadzone calibration screen of a controller: two sticks, one selected, each with its deadzone, plus the automatic
/// measurement (let go of the sticks, 2 s: the deadzone becomes the furthest a stick strayed from center, plus a margin).
/// Values live in the controller's profile (through the host); this only holds the screen's state.
/// </summary>
internal sealed class DeadzoneCalibration(Func<int> slot, ISettingsHost host, Func<long>? clock = null)
{
    public const float Step = 0.02f, Max = 0.5f, Margin = 0.02f, PushedLimit = 0.5f;
    public const long SampleMs = 2000;

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private long _started;
    private int _autoSlot = -1;
    private readonly float[] _peak = new float[2];

    /// <summary>The controller being calibrated (follows the controller strip).</summary>
    public int Slot => slot();

    /// <summary>0 = left stick, 1 = right stick.</summary>
    public int Selected { get; private set; }

    public AutoDeadzone Auto { get; private set; }

    public float Get(int stick) => host.ControllerDeadzone(Slot, stick == 1);

    public (Stick Left, Stick Right) Sticks => host.ControllerSticks(Slot);

    public void SelectStick(int stick) => Selected = Math.Clamp(stick, 0, 1);

    public void MoveStick(int delta) => Selected = delta < 0 ? 0 : 1;

    /// <summary>Raises / lowers the deadzone of a stick by one step (the selected one by default).</summary>
    public void Adjust(int delta, int? stick = null)
    {
        int s = stick ?? Selected;
        float v = MathF.Round(Get(s) / Step) * Step + Math.Sign(delta) * Step;
        host.SetControllerDeadzone(Slot, s == 1, MathF.Round(Math.Clamp(v, 0f, Max) / Step) * Step);
        Auto = AutoDeadzone.Idle;
    }

    public void StartAuto()
    {
        _autoSlot = Slot;
        _started = _clock();
        Array.Clear(_peak);
        Auto = AutoDeadzone.Sampling;
    }

    /// <summary>Seconds left of the measurement (0 when not measuring).</summary>
    public float SecondsLeft => Auto == AutoDeadzone.Sampling ? Math.Max(0, (SampleMs - (_clock() - _started)) / 1000f) : 0f;

    /// <summary>Call on every report of the calibrated controller: advances the automatic measurement.</summary>
    public void Feed()
    {
        if (Auto != AutoDeadzone.Sampling) return;
        if (Slot != _autoSlot) // another controller took the screen: start over
        {
            Auto = AutoDeadzone.Idle;
            return;
        }
        var (left, right) = Sticks;
        if (left.Magnitude > PushedLimit || right.Magnitude > PushedLimit)
        {
            Auto = AutoDeadzone.Pushed;
            return;
        }
        _peak[0] = MathF.Max(_peak[0], left.Magnitude);
        _peak[1] = MathF.Max(_peak[1], right.Magnitude);
        if (_clock() - _started < SampleMs) return;

        host.SetControllerDeadzone(Slot, false, Fit(_peak[0]));
        host.SetControllerDeadzone(Slot, true, Fit(_peak[1]));
        Auto = AutoDeadzone.Done;
    }

    /// <summary>The deadzone for a stick that strayed up to <paramref name="peak"/>: a bit above it, rounded up to 0.01.</summary>
    public static float Fit(float peak) => peak <= 0f ? 0f : Math.Min(Max, MathF.Ceiling((peak + Margin) * 100f - 0.001f) / 100f);

    public string Status => Auto switch
    {
        AutoDeadzone.Sampling => $"Hands off the sticks... {SecondsLeft:0.0}s",
        AutoDeadzone.Done => "Deadzones found",
        AutoDeadzone.Pushed => "Release the sticks, then retry",
        _ => "Let go of the sticks, then Auto",
    };
}
