using ControlWinSwitch.Actions;
using ControlWinSwitch.App;

namespace ControlWinSwitch.Tests;

public class SliderModelTests
{
    private static SliderModel Armed(int percent)
    {
        var slider = new SliderModel();
        slider.Reset(percent, muted: false);
        slider.StickActive(0f); // the stick came back to the center
        return slider;
    }

    [Fact]
    public void Full_tilt_moves_40_percent_per_second()
    {
        var slider = Armed(50);
        for (int i = 0; i < 10; i++) slider.Move(1f, 0.1f, boost: false);
        Assert.Equal(90, slider.Percent);
    }

    [Fact]
    public void ZR_moves_three_times_faster()
    {
        var slider = Armed(50);
        slider.Move(-1f, 0.25f, boost: true);
        Assert.Equal(20, slider.Percent);
    }

    [Fact]
    public void ZL_moves_three_times_slower()
    {
        var slider = Armed(50);
        slider.Move(1f, 1.5f, boost: false, slow: true);
        Assert.Equal(70, slider.Percent);
    }

    [Fact]
    public void Small_moves_add_up()
    {
        var slider = Armed(50);
        bool changed = slider.Move(1f, 0.01f, boost: false); // 0.4 %
        Assert.False(changed);
        Assert.True(slider.Move(1f, 0.01f, boost: false)); // 0.8 %: rounds to 51
        Assert.Equal(51, slider.Percent);
    }

    [Fact]
    public void A_stick_tilted_on_open_does_nothing_until_centered()
    {
        var slider = new SliderModel();
        slider.Reset(50, muted: false);
        Assert.False(slider.Move(1f, 1f, boost: false));
        slider.Move(0f, 0.1f, boost: false);
        Assert.True(slider.Move(1f, 0.1f, boost: false));
    }

    [Fact]
    public void Deadzone_ignores_a_resting_stick()
    {
        var slider = Armed(50);
        Assert.False(slider.Move(0.15f, 1f, boost: false));
        Assert.Equal(50, slider.Percent);
    }

    [Fact]
    public void Value_stays_between_0_and_100()
    {
        var slider = Armed(98);
        slider.Move(1f, 1f, boost: true);
        Assert.Equal(100, slider.Percent);
        Assert.False(slider.Step(1));
        slider.Reset(1, muted: false);
        slider.Step(-1);
        Assert.False(slider.Step(-1));
        Assert.Equal(0, slider.Percent);
    }

    [Fact]
    public void Dpad_steps_from_the_shown_value()
    {
        var slider = Armed(50);
        slider.Move(1f, 0.03f, boost: false); // 51.2, shown 51
        slider.Step(1);
        Assert.Equal(52, slider.Percent);
    }

    [Theory]
    [InlineData(0u, 100u, 50, 50u)]
    [InlineData(10u, 60u, 50, 35u)]
    [InlineData(0u, 255u, 100, 255u)]
    public void DDC_values_scale_to_the_monitor_range(uint min, uint max, int percent, uint expected)
    {
        Assert.Equal(expected, DdcBrightness.FromPercent(percent, min, max));
        Assert.Equal(percent, DdcBrightness.ToPercent(expected, min, max));
    }

    [Theory]
    [InlineData(11u, true)]         // embedded DisplayPort (laptop panel)
    [InlineData(0x80000000u, true)] // internal
    [InlineData(5u, false)]         // HDMI
    [InlineData(10u, false)]        // DisplayPort cable
    public void Built_in_screens_are_told_by_their_connection(uint technology, bool builtIn)
    {
        Assert.Equal(builtIn, Brightness.IsBuiltIn(technology));
    }

    [Theory]
    [InlineData(47, 50)]
    [InlineData(43, 40)]
    [InlineData(100, 100)]
    public void Brightness_uses_the_closest_supported_level(int percent, byte expected)
    {
        byte[] levels = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.Equal(expected, Brightness.Nearest(levels, percent));
    }
}
