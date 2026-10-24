using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.Input;
using static ControlWinSwitch.Input.JoyCon2Buttons;

namespace ControlWinSwitch.Tests;

public class JoyCon1Tests
{
    // 12-bit pair (x, y) packed in 3 bytes, as in the report's stick fields and the calibration blocks.
    private static byte[] Pack(int x, int y) => [(byte)x, (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)), (byte)(y >> 4)];

    private static byte[] Report(byte battery, byte right, byte shared, byte left, (int, int) leftStick, (int, int) rightStick)
    {
        var r = new byte[49];
        r[0] = 0x30;
        r[2] = battery;
        (r[3], r[4], r[5]) = (right, shared, left);
        Pack(leftStick.Item1, leftStick.Item2).CopyTo(r, 6);
        Pack(rightStick.Item1, rightStick.Item2).CopyTo(r, 9);
        return r;
    }

    [Fact]
    public void Left_joycon_report_uses_the_joycon2_bits()
    {
        // Left byte: Up 0x02, SL 0x20, ZL 0x80; shared: Minus 0x01, Capture 0x20.
        var r = Report(0x8E, 0, 0x21, 0xA2, (1000, 3000), (0, 0));

        Assert.True(JoyCon1Device.TryParseInput(r, out var input, out int battery, out bool charging));
        Assert.Equal(Up | SlL | ZL | Minus | Capture, input.Buttons);
        Assert.Equal((1000, 3000), (input.LX, input.LY));
        Assert.Equal(4, battery);
        Assert.False(charging);
    }

    [Fact]
    public void Right_joycon_report_fills_the_right_half()
    {
        // Right byte: A 0x08, SR 0x10, R 0x40; shared: Plus 0x02, Home 0x10, charging grip 0x80 (dropped).
        var r = Report(0x50, 0x58, 0x92, 0, (0, 0), (2100, 1900));

        Assert.True(JoyCon1Device.TryParseInput(r, out var input, out int battery, out bool charging));
        Assert.Equal(A | SrR | R | Plus | Home, input.Buttons);
        Assert.Equal((2100, 1900), (input.RX, input.RY));
        Assert.Equal(2, battery);
        Assert.True(charging);
    }

    [Fact]
    public void Simple_reports_are_not_parsed()
    {
        var r = new byte[12];
        r[0] = 0x3F;
        Assert.False(JoyCon1Device.TryParseInput(r, out _, out _, out _));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Each_joycon_reads_its_own_stick_block()
    {
        var blank = Enumerable.Repeat((byte)0xFF, 9).ToArray();
        var leftBlock = Concat(Pack(1500, 1600), Pack(2048, 2000), Pack(1300, 1400));
        var rightBlock = Concat(Pack(2040, 2010), Pack(1200, 1250), Pack(1450, 1550));
        var noUser = Enumerable.Repeat((byte)0xFF, StickCalibrationParser.UserLength).ToArray();

        var left = StickCalibrationParser.CombineStick(noUser, Concat(leftBlock, blank), right: false)!;
        var right = StickCalibrationParser.CombineStick(noUser, Concat(blank, rightBlock), right: true)!;

        Assert.Equal(new AxisRange(2048, 1300, 1500), left[0]);
        Assert.Equal(new AxisRange(2040, 1200, 1450), right[0]);
        Assert.Null(StickCalibrationParser.CombineStick(noUser, Concat(leftBlock, blank), right: true));
    }

    [Theory]
    [InlineData("Pro Controller", ControllerKinds.Pro)]
    [InlineData("Joy-Con (L)", ControllerKinds.JoyConLeft)]
    [InlineData("Joy-Con (R)", ControllerKinds.JoyConRight)]
    [InlineData("Xbox Wireless Controller", null)]
    public void Windows_device_names_give_the_kind(string name, string? kind) => Assert.Equal(kind, BluetoothPairing.KindOfName(name));
}
