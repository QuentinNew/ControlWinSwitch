namespace ControlWin.Input;

/// <summary>Normalized stick position, -1..1 on each axis, screen convention (+Y = down).</summary>
internal readonly record struct Stick(float X, float Y)
{
    public float Magnitude => MathF.Sqrt(X * X + Y * Y);

    /// <summary>Radial deadzone; the remaining travel is rescaled to 0..1.</summary>
    public Stick WithDeadzone(float deadzone)
    {
        float m = Magnitude;
        if (m <= deadzone) return default;
        float scaled = MathF.Min(1f, (m - deadzone) / (1f - deadzone));
        return new Stick(X / m * scaled, Y / m * scaled);
    }
}

/// <summary>
/// Turns raw axis values into -1..1. The range starts from a conservative guess and widens
/// as the stick reaches further, so no factory calibration read (and no device write) is needed.
/// </summary>
internal sealed class AxisCalibrator
{
    private readonly int _center;
    private int _neg, _pos;

    public AxisCalibrator(int center, int initialRange)
    {
        _center = center;
        _neg = _pos = initialRange;
    }

    public float Normalize(int raw)
    {
        int d = raw - _center;
        if (d < 0)
        {
            if (-d > _neg) _neg = -d;
            return d / (float)_neg;
        }
        if (d > _pos) _pos = d;
        return d / (float)_pos;
    }
}

/// <summary>Current and previous controller state, with button edge detection.</summary>
internal sealed class ControllerState
{
    private readonly AxisCalibrator[] _simple =
        [new(0x8000, 16000), new(0x8000, 16000), new(0x8000, 16000), new(0x8000, 16000)];
    private readonly AxisCalibrator[] _full =
        [new(2048, 1300), new(2048, 1300), new(2048, 1300), new(2048, 1300)];

    public Buttons Current { get; private set; }
    public Buttons Previous { get; private set; }
    public Stick Left { get; private set; }
    public Stick Right { get; private set; }

    public bool Pressed(Buttons b) => (Current & b) != 0 && (Previous & b) == 0;
    public bool Released(Buttons b) => (Current & b) == 0 && (Previous & b) != 0;
    public bool Held(Buttons b) => (Current & b) != 0;

    public void Update(in ProReport r)
    {
        Previous = Current;
        Current = r.Buttons;
        if (r.Format == ReportFormat.Simple)
        {
            Left = new Stick(_simple[0].Normalize(r.LX), _simple[1].Normalize(r.LY));
            Right = new Stick(_simple[2].Normalize(r.RX), _simple[3].Normalize(r.RY));
        }
        else
        {
            // Full reports grow upward: flip Y into screen convention.
            Left = new Stick(_full[0].Normalize(r.LX), -_full[1].Normalize(r.LY));
            Right = new Stick(_full[2].Normalize(r.RX), -_full[3].Normalize(r.RY));
        }
    }

    /// <summary>Forget everything (controller gone, PC resumed).</summary>
    public void Reset()
    {
        Previous = Current = Buttons.None;
        Left = Right = default;
    }
}
