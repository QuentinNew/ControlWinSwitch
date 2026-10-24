using ControlWinSwitch.Input;

namespace ControlWinSwitch.Tests;

public class StickTests
{
    [Fact]
    public void Inside_deadzone_is_zero()
    {
        Assert.Equal(default, new Stick(0.1f, 0.05f).WithDeadzone(0.15f));
    }

    [Fact]
    public void Exactly_at_deadzone_is_zero()
    {
        Assert.Equal(default, new Stick(0.15f, 0f).WithDeadzone(0.15f));
    }

    [Fact]
    public void Full_deflection_is_one()
    {
        var s = new Stick(1f, 0f).WithDeadzone(0.15f);
        Assert.Equal(1f, s.X, 3);
        Assert.Equal(0f, s.Y, 3);
    }

    [Fact]
    public void Travel_past_deadzone_is_rescaled()
    {
        var s = new Stick(0f, -0.575f).WithDeadzone(0.15f); // (0.575 - 0.15) / 0.85 = 0.5
        Assert.Equal(-0.5f, s.Y, 3);
    }

    [Fact]
    public void Diagonal_overshoot_is_clamped_to_unit_circle()
    {
        var s = new Stick(1f, 1f).WithDeadzone(0.15f);
        Assert.Equal(1f, s.Magnitude, 3);
        Assert.Equal(s.X, s.Y, 3);
    }
}
