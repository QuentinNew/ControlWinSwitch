using ControlWinSwitch.Input;

namespace ControlWinSwitch.Tests;

public class XInputMapperTests
{
    private static XusbReport Map(Buttons b, Stick l = default, Stick r = default, bool ab = false, bool xy = false) =>
        XInputMapper.Map(b, l, r, ab, xy);

    [Theory]
    [InlineData((uint)Buttons.B, XInputMapper.A)]
    [InlineData((uint)Buttons.A, XInputMapper.B)]
    [InlineData((uint)Buttons.Y, XInputMapper.X)]
    [InlineData((uint)Buttons.X, XInputMapper.Y)]
    [InlineData((uint)Buttons.Plus, XInputMapper.Start)]
    [InlineData((uint)Buttons.Minus, XInputMapper.Back)]
    [InlineData((uint)Buttons.Home, XInputMapper.Guide)]
    [InlineData((uint)Buttons.L, XInputMapper.LeftShoulder)]
    [InlineData((uint)Buttons.R, XInputMapper.RightShoulder)]
    [InlineData((uint)Buttons.LStick, XInputMapper.LeftThumb)]
    [InlineData((uint)Buttons.RStick, XInputMapper.RightThumb)]
    [InlineData((uint)Buttons.Up, XInputMapper.DpadUp)]
    [InlineData((uint)Buttons.Down, XInputMapper.DpadDown)]
    [InlineData((uint)Buttons.Left, XInputMapper.DpadLeft)]
    [InlineData((uint)Buttons.Right, XInputMapper.DpadRight)]
    public void Buttons_map_by_position(uint held, int expected) =>
        Assert.Equal((ushort)expected, Map((Buttons)held).Buttons);

    [Fact]
    public void Capture_is_never_mapped() =>
        Assert.Equal(default, Map(Buttons.Capture));

    [Fact]
    public void Triggers_are_digital()
    {
        var r = Map(Buttons.ZL | Buttons.ZR);
        Assert.Equal(255, r.LeftTrigger);
        Assert.Equal(255, r.RightTrigger);
        Assert.Equal(0, Map(Buttons.None).LeftTrigger);
    }

    [Fact]
    public void Swaps_exchange_face_buttons()
    {
        Assert.Equal(XInputMapper.B, Map(Buttons.B, ab: true).Buttons);
        Assert.Equal(XInputMapper.A, Map(Buttons.A, ab: true).Buttons);
        Assert.Equal(XInputMapper.Y, Map(Buttons.Y, xy: true).Buttons);
        Assert.Equal(XInputMapper.X, Map(Buttons.X, xy: true).Buttons);
    }

    [Fact]
    public void Sticks_scale_and_invert_y()
    {
        var r = Map(Buttons.None, new Stick(1f, 1f), new Stick(-1f, -1f));
        Assert.Equal(32767, r.ThumbLX);
        Assert.Equal(-32767, r.ThumbLY); // screen down -> XInput negative
        Assert.Equal(-32767, r.ThumbRX);
        Assert.Equal(32767, r.ThumbRY);
        Assert.Equal(0, Map(Buttons.None).ThumbLX);
        Assert.Equal(32767, XInputMapper.Axis(5f)); // clamped
    }
}
