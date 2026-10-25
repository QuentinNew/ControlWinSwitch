namespace ControlWinSwitch.App;

/// <summary>
/// The value of the Volume / Brightness slider (0..100). The left stick moves it smoothly, faster with ZR held, slower with ZL;
/// the D-pad moves it by 1. Kept fractional so slow stick moves add up; shown and applied as whole percents.
/// </summary>
internal sealed class SliderModel
{
    public const float StickRate = 40f;  // percent per second at full tilt
    public const float BoostFactor = 3f; // ZR held
    public const float SlowFactor = 1f / 3f; // ZL held
    public const float Deadzone = 0.2f;

    private float _value;
    private bool _stickArmed;

    public int Percent => (int)MathF.Round(_value);
    public bool Muted { get; set; }

    /// <summary>A new value read from the system. The stick must come back to the center before it moves the value.</summary>
    public void Reset(int percent, bool muted)
    {
        _value = Math.Clamp(percent, 0, 100);
        Muted = muted;
        _stickArmed = false;
    }

    /// <summary>A value read from the system while the slider is open (changed by the keyboard keys, say).</summary>
    public void Sync(int percent, bool muted)
    {
        if (percent != Percent) _value = Math.Clamp(percent, 0, 100);
        Muted = muted;
    }

    /// <summary>D-pad: one percent up or down from the shown value. True when the shown value changed.</summary>
    public bool Step(int delta)
    {
        int before = Percent;
        _value = Math.Clamp(before + delta, 0, 100);
        return Percent != before;
    }

    /// <summary>True when the stick is tilted enough to move the value (it came back to the center since opening).</summary>
    public bool StickActive(float x)
    {
        if (MathF.Abs(x) < Deadzone)
        {
            _stickArmed = true;
            return false;
        }
        return _stickArmed;
    }

    /// <summary>Left stick X over <paramref name="seconds"/>; ZR (<paramref name="boost"/>) faster, ZL (<paramref name="slow"/>) slower. True when the shown value changed.</summary>
    public bool Move(float x, float seconds, bool boost, bool slow = false)
    {
        if (!StickActive(x)) return false;
        float tilt = Math.Min(1f, (MathF.Abs(x) - Deadzone) / (1f - Deadzone));
        float factor = boost == slow ? 1f : boost ? BoostFactor : SlowFactor; // both held: they cancel out
        float rate = StickRate * tilt * factor * MathF.Sign(x);
        int before = Percent;
        _value = Math.Clamp(_value + rate * seconds, 0f, 100f);
        return Percent != before;
    }
}
