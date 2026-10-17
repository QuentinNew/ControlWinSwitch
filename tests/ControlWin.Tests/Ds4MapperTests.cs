using ControlWin.Input;

namespace ControlWin.Tests;

public class Ds4MapperTests
{
    [Fact]
    public void Neutral_has_centered_sticks_and_released_hat()
    {
        var r = Ds4Mapper.Map(Buttons.None, default, default);
        Assert.Equal(Ds4Mapper.Neutral, r);
        Assert.Equal(0x80, r.ThumbLX);
        Assert.Equal(8, r.Buttons & 0xF);
    }

    [Theory]
    [InlineData((uint)Buttons.Up, 0)]
    [InlineData((uint)(Buttons.Up | Buttons.Right), 1)]
    [InlineData((uint)Buttons.Right, 2)]
    [InlineData((uint)(Buttons.Down | Buttons.Right), 3)]
    [InlineData((uint)Buttons.Down, 4)]
    [InlineData((uint)(Buttons.Down | Buttons.Left), 5)]
    [InlineData((uint)Buttons.Left, 6)]
    [InlineData((uint)(Buttons.Up | Buttons.Left), 7)]
    [InlineData((uint)(Buttons.Up | Buttons.Down), 8)]
    public void Dpad_becomes_a_hat(uint held, int hat) =>
        Assert.Equal(hat, Ds4Mapper.Map((Buttons)held, default, default).Buttons & 0xF);

    [Fact]
    public void Face_buttons_map_by_position_and_swap_back_to_labels()
    {
        Assert.Equal(1 << 5, Ds4Mapper.Map(Buttons.B, default, default).Buttons & 0xFFF0); // bottom = Cross
        Assert.Equal(1 << 5, Ds4Mapper.Map(Buttons.A, default, default, swapAB: true).Buttons & 0xFFF0);
        Assert.Equal(1 << 7, Ds4Mapper.Map(Buttons.X, default, default).Buttons & 0xFFF0); // top = Triangle
    }

    [Fact]
    public void Triggers_are_analog_and_digital_and_home_is_ps()
    {
        var r = Ds4Mapper.Map(Buttons.ZL | Buttons.Home, default, default);
        Assert.Equal(255, r.LeftTrigger);
        Assert.Equal(0, r.RightTrigger);
        Assert.NotEqual(0, r.Buttons & (1 << 10));
        Assert.Equal(1, r.Special);
    }

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 128)]
    [InlineData(1f, 255)]
    public void Axis_spans_a_byte(float v, int expected) => Assert.Equal((byte)expected, Ds4Mapper.Axis(v));
}
