using ControlWinSwitch.Input;

namespace ControlWinSwitch.Tests;

public class StickCalibrationTests
{
    // 12-bit pair (x, y) packed in 3 bytes: x = b0 | (b1 & 0xF) << 8, y = b1 >> 4 | b2 << 4.
    private static byte[] Pack(int x, int y) => [(byte)x, (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)), (byte)(y >> 4)];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Left_block_is_above_center_below()
    {
        var r = StickCalibrationParser.ParseLeft(Concat(Pack(1500, 1600), Pack(2048, 2000), Pack(1300, 1400)))!;

        Assert.Equal(new AxisRange(2048, 1300, 1500), r[0]);
        Assert.Equal(new AxisRange(2000, 1400, 1600), r[1]);
    }

    [Fact]
    public void Right_block_is_center_below_above()
    {
        var r = StickCalibrationParser.ParseRight(Concat(Pack(2048, 2000), Pack(1300, 1400), Pack(1500, 1600)))!;

        Assert.Equal(new AxisRange(2048, 1300, 1500), r[0]);
        Assert.Equal(new AxisRange(2000, 1400, 1600), r[1]);
    }

    [Fact]
    public void Erased_flash_gives_no_calibration()
    {
        var blank = Enumerable.Repeat((byte)0xFF, 9).ToArray();
        Assert.Null(StickCalibrationParser.ParseLeft(blank));
        Assert.Null(StickCalibrationParser.ParseRight(blank));
    }

    private static byte[] FactoryBytes() => Concat(
        Concat(Pack(1500, 1600), Pack(2048, 2000), Pack(1300, 1400)),
        Concat(Pack(2040, 2010), Pack(1200, 1250), Pack(1450, 1550)));

    private static byte[] UserBytes(bool left, bool right)
    {
        var d = Enumerable.Repeat((byte)0xFF, 22).ToArray();
        if (left)
        {
            d[0] = 0xB2; d[1] = 0xA1;
            Concat(Pack(1000, 1000), Pack(2100, 2100), Pack(900, 900)).CopyTo(d, 2);
        }
        if (right)
        {
            d[11] = 0xB2; d[12] = 0xA1;
            Concat(Pack(2100, 2100), Pack(900, 900), Pack(1000, 1000)).CopyTo(d, 13);
        }
        return d;
    }

    [Fact]
    public void Factory_is_used_when_no_user_calibration_exists()
    {
        var c = StickCalibrationParser.Combine(UserBytes(false, false), FactoryBytes())!;

        Assert.Equal("Factory", c.Source);
        Assert.Equal(new AxisRange(2048, 1300, 1500), c.Left[0]);
        Assert.Equal(new AxisRange(2040, 1200, 1450), c.Right[0]);
    }

    [Fact]
    public void User_calibration_wins_per_stick()
    {
        var c = StickCalibrationParser.Combine(UserBytes(true, false), FactoryBytes())!;

        Assert.Equal("Factory + user", c.Source);
        Assert.Equal(new AxisRange(2100, 900, 1000), c.Left[0]);
        Assert.Equal(new AxisRange(2040, 1200, 1450), c.Right[0]);
        Assert.Equal("User", StickCalibrationParser.Combine(UserBytes(true, true), FactoryBytes())!.Source);
    }

    [Fact]
    public void Combine_fails_when_the_factory_block_is_blank_and_no_user_block()
    {
        var blank = Enumerable.Repeat((byte)0xFF, 18).ToArray();
        Assert.Null(StickCalibrationParser.Combine(UserBytes(false, false), blank));
    }

    [Fact]
    public void Fixed_range_reaches_one_without_warm_up_and_clamps()
    {
        var cal = new AxisCalibrator(new AxisRange(2048, 1300, 1500));

        Assert.Equal(0f, cal.Normalize(2048));
        Assert.Equal(1f, cal.Normalize(2048 + 1500), 3);
        Assert.Equal(-1f, cal.Normalize(2048 - 1300), 3);
        Assert.Equal(1f, cal.Normalize(2048 + 1800)); // clamped, the range never widens
        Assert.Equal(0.5f, cal.Normalize(2048 + 750), 3);
    }

    [Fact]
    public void State_applies_the_calibration_in_full_mode()
    {
        var s = new ControllerState();
        var cal = StickCalibrationParser.Combine(UserBytes(false, false), FactoryBytes())!;
        s.SetCalibration(cal);

        s.Update(new ProReport(ReportFormat.Full, Buttons.None, 2048 + 1500, 2000, 2040, 2010));

        Assert.Equal(1f, s.Left.X, 3);
        Assert.Equal(0f, s.Left.Y, 3);

        s.ClearCalibration();
        s.Update(new ProReport(ReportFormat.Full, Buttons.None, 2048 + 1500, 2000, 2040, 2010));
        Assert.True(s.Left.X > 1f - 0.001f); // adaptive again: the range widens to this reading
    }

    [Fact]
    public void ReadSpi_builds_the_request()
    {
        var r = ProCommands.ReadSpi(3, 0x603D, 18, 49);

        Assert.Equal(0x01, r[0]);
        Assert.Equal(0x10, r[10]);
        Assert.Equal([0x3D, 0x60, 0x00, 0x00], r[11..15]);
        Assert.Equal(18, r[15]);
    }

    [Fact]
    public void SpiRead_reply_is_parsed()
    {
        var r = new byte[64];
        r[0] = 0x21; r[13] = 0x90; r[14] = 0x10;
        BitConverter.GetBytes(0x8010).CopyTo(r, 15);
        r[19] = 3; r[20] = 7; r[21] = 8; r[22] = 9;

        Assert.True(ProCommands.TryParseSpiRead(r, out var address, out var data));
        Assert.Equal(0x8010, address);
        Assert.Equal([7, 8, 9], data);

        r[13] = 0x80; // an error ack
        Assert.False(ProCommands.TryParseSpiRead(r, out _, out _));
    }
}

public class ControllerDeadzoneTests
{
    [Fact]
    public void Profile_with_a_deadzone_is_not_default()
    {
        Assert.True(new ControlWinSwitch.Config.ControllerProfile().IsDefault);
        Assert.False(new ControlWinSwitch.Config.ControllerProfile { RightDeadzone = 0.1f }.IsDefault);
    }
}
