using ControlWinSwitch.Input;

namespace ControlWinSwitch.Tests;

public class ControllerStateTests
{
    private static ProReport Simple(Buttons b = Buttons.None, int lx = 0x8000, int ly = 0x8000) =>
        new(ReportFormat.Simple, b, lx, ly, 0x8000, 0x8000);

    private static ProReport Full(Buttons b = Buttons.None, int lx = 2048, int ly = 2048) =>
        new(ReportFormat.Full, b, lx, ly, 2048, 2048);

    [Fact]
    public void Pressed_is_true_only_on_the_press_edge()
    {
        var s = new ControllerState();

        s.Update(Simple(Buttons.A));
        Assert.True(s.Pressed(Buttons.A));
        Assert.True(s.Held(Buttons.A));
        Assert.False(s.Released(Buttons.A));

        s.Update(Simple(Buttons.A));
        Assert.False(s.Pressed(Buttons.A));
        Assert.True(s.Held(Buttons.A));
    }

    [Fact]
    public void Released_is_true_only_on_the_release_edge()
    {
        var s = new ControllerState();
        s.Update(Simple(Buttons.B));

        s.Update(Simple());
        Assert.True(s.Released(Buttons.B));
        Assert.False(s.Held(Buttons.B));

        s.Update(Simple());
        Assert.False(s.Released(Buttons.B));
    }

    [Fact]
    public void Simple_mode_keeps_Y_downward()
    {
        var s = new ControllerState();
        s.Update(Simple(ly: 0xFFFF));
        Assert.Equal(1f, s.Left.Y, 3);
        Assert.Equal(0f, s.Left.X, 3);
    }

    [Fact]
    public void Full_mode_flips_Y_to_screen_convention()
    {
        var s = new ControllerState();
        s.Update(Full(ly: 4095)); // stick pushed up on the controller
        Assert.True(s.Left.Y < 0f);
        Assert.Equal(-1f, s.Left.Y, 3);
    }

    [Fact]
    public void Axis_range_widens_adaptively()
    {
        var cal = new AxisCalibrator(2048, 1300);
        Assert.Equal(1f, cal.Normalize(2048 + 1300), 3);
        Assert.Equal(1f, cal.Normalize(2048 + 2000), 3); // widens the range to 2000
        Assert.Equal(0.5f, cal.Normalize(2048 + 1000), 3);
    }

    [Fact]
    public void Axis_sides_calibrate_independently()
    {
        var cal = new AxisCalibrator(2048, 1300);
        cal.Normalize(2048 + 2000);
        Assert.Equal(-1f, cal.Normalize(2048 - 1300), 3);
        Assert.Equal(-1f, cal.Normalize(2048 - 1500), 3);
    }

    [Fact]
    public void Reset_clears_buttons_and_sticks()
    {
        var s = new ControllerState();
        s.Update(Simple(Buttons.A, lx: 0xFFFF));
        s.Reset();

        Assert.Equal(Buttons.None, s.Current);
        Assert.Equal(Buttons.None, s.Previous);
        Assert.Equal(default, s.Left);
        Assert.Equal(default, s.Right);
    }
}
