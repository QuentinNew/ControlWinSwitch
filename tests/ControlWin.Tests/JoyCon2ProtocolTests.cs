using ControlWin.Input;
using static ControlWin.Input.JoyCon2Buttons;

namespace ControlWin.Tests;

public class JoyCon2ProtocolTests
{
    // 12-bit pair (x, y) packed in 3 bytes.
    private static byte[] Pack(int x, int y) => [(byte)x, (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)), (byte)(y >> 4)];

    private static byte[] Advert(ushort pid, ulong reconnect)
    {
        var d = new byte[24];
        d[3] = 0x7E; d[4] = 0x05;
        d[5] = (byte)pid; d[6] = (byte)(pid >> 8);
        for (int i = 0; i < 6; i++) d[10 + i] = (byte)(reconnect >> (8 * i));
        return d;
    }

    [Fact]
    public void Advertisement_gives_side_and_reconnect_address()
    {
        Assert.True(JoyCon2Protocol.TryParseAdvertisement(Advert(0x2067, 0xA4C138001122), out var side, out var host));
        Assert.Equal(JoyConSide.Left, side);
        Assert.Equal(0xA4C138001122UL, host);

        Assert.True(JoyCon2Protocol.TryParseAdvertisement(Advert(0x2066, 0), out side, out host));
        Assert.Equal(JoyConSide.Right, side);
        Assert.Equal(0UL, host);
    }

    [Fact]
    public void Advertisement_of_other_controllers_is_ignored()
    {
        Assert.False(JoyCon2Protocol.TryParseAdvertisement(Advert(0x2069, 0), out _, out _)); // Pro Controller 2: not yet
        Assert.False(JoyCon2Protocol.TryParseAdvertisement(Advert(0x2067, 0)[..10], out _, out _));
        var wrongVendor = Advert(0x2067, 0);
        wrongVendor[3] = 0;
        Assert.False(JoyCon2Protocol.TryParseAdvertisement(wrongVendor, out _, out _));
    }

    [Fact]
    public void Input_decodes_buttons_sticks_and_battery()
    {
        var r = new byte[60];
        BitConverter.GetBytes(A | ZL | C).CopyTo(r, 4);
        Pack(100, 4000).CopyTo(r, 10);
        Pack(2048, 1).CopyTo(r, 13);
        BitConverter.GetBytes((ushort)3912).CopyTo(r, 31);
        BitConverter.GetBytes((short)-25).CopyTo(r, 33);

        Assert.True(JoyCon2Protocol.TryParseInput(r, out var input));

        Assert.Equal(A | ZL | C, input.Buttons);
        Assert.Equal((100, 4000), (input.LX, input.LY));
        Assert.Equal((2048, 1), (input.RX, input.RY));
        Assert.Equal(3912, input.BatteryMv);
        Assert.Equal(-25, input.BatteryCurrent);
        Assert.False(JoyCon2Protocol.TryParseInput(new byte[20], out _));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(3660, 4)] // a full Joy-Con 2
    [InlineData(3450, 3)]
    [InlineData(3300, 2)]
    [InlineData(3130, 1)]
    [InlineData(3000, 0)]
    public void Battery_level_from_voltage(int mv, int level) => Assert.Equal(level, JoyCon2Protocol.BatteryLevel(mv));

    [Fact]
    public void Command_has_header_and_data()
    {
        Assert.Equal(new byte[] { 0x09, 0x91, 0x01, 0x07, 0x00, 0x04, 0x00, 0x00, 0x02, 0, 0, 0 }, JoyCon2Protocol.SetPlayerLeds(0x02));
    }

    [Fact]
    public void Memory_read_request_and_reply()
    {
        Assert.Equal(new byte[] { 0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00, 0x0B, 0x7E, 0, 0, 0x42, 0xC0, 0x1F, 0x00 },
            JoyCon2Protocol.ReadMemory(0x0B, 0x1FC042));

        byte[] reply = [0x02, 0x01, 0, 0, 0, 0, 0, 0, 0x03, 0, 0, 0, 0x42, 0xC0, 0x1F, 0x00, 0xAA, 0xBB, 0xCC];
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, JoyCon2Protocol.ParseMemoryReply(reply, 3, 0x1FC042));
        Assert.Null(JoyCon2Protocol.ParseMemoryReply(reply, 3, 0x0130A8)); // another address
        Assert.Null(JoyCon2Protocol.ParseMemoryReply(reply, 4, 0x1FC042)); // another length
        reply[0] = 0x09;
        Assert.Null(JoyCon2Protocol.ParseMemoryReply(reply, 3, 0x1FC042)); // another command
    }

    [Fact]
    public void Calibration_is_center_above_below()
    {
        var block = Pack(2000, 2100).Concat(Pack(1500, 1600)).Concat(Pack(1300, 1400)).Concat(new byte[2]).ToArray();

        var r = JoyCon2Protocol.ParseCalibration(block)!;

        Assert.Equal(new AxisRange(2000, 1300, 1500), r[0]);
        Assert.Equal(new AxisRange(2100, 1400, 1600), r[1]);
        Assert.Null(JoyCon2Protocol.ParseCalibration(Enumerable.Repeat((byte)0xFF, 11).ToArray()));
        Assert.Null(JoyCon2Protocol.ParseCalibration(new byte[11]));
    }

    [Fact]
    public void Pair_address_is_sent_twice_little_endian()
    {
        var c = JoyCon2Protocol.PairSetAddress(0xA4C138001122);

        Assert.Equal(new byte[] { 0x15, 0x91, 0x01, 0x01, 0x00, 14, 0, 0 }, c[..8]);
        Assert.Equal(new byte[] { 0x00, 0x02, 0x22, 0x11, 0x00, 0x38, 0xC1, 0xA4, 0x22, 0x11, 0x00, 0x38, 0xC1, 0xA4 }, c[8..]);
        Assert.Equal(17, JoyCon2Protocol.PairLtk1()[5]);
        Assert.Equal(new byte[] { 0x15, 0x91, 0x01, 0x03, 0x00, 1, 0, 0, 0 }, JoyCon2Protocol.PairFinish());
    }

    [Fact]
    public void Rumble_packet_carries_counter_and_amplitudes()
    {
        var p = JoyCon2Protocol.Rumble(0x13, 800, 400);

        Assert.Equal(17, p.Length);
        Assert.Equal(0, p[0]);
        Assert.Equal(0x53, p[1]);
        ulong frame = 0;
        for (int i = 4; i >= 0; i--) frame = (frame << 8) | p[2 + i];
        Assert.Equal(0x0E1UL, frame & 0x1FF);
        Assert.Equal(800UL, (frame >> 10) & 0x3FF);
        Assert.Equal(0x1E1UL, (frame >> 20) & 0x1FF);
        Assert.Equal(400UL, (frame >> 30) & 0x3FF);

        ulong silent = 0;
        for (int i = 4; i >= 0; i--) silent = (silent << 8) | p[7 + i];
        Assert.Equal(0UL, (silent >> 10) & 0x3FF);
        Assert.Equal(0UL, (silent >> 30) & 0x3FF);
    }

    [Fact]
    public void Amplitudes_scale_motor_strength_and_percent()
    {
        Assert.Equal((800, 401), JoyCon2Protocol.Amplitudes(255, 128, 100));
        Assert.Equal((400, 0), JoyCon2Protocol.Amplitudes(255, 0, 50));
        Assert.Equal((0, 0), JoyCon2Protocol.Amplitudes(255, 255, 0));
    }

    [Fact]
    public void Leds_reverse_for_the_other_end()
    {
        Assert.Equal(0x08, JoyCon2Protocol.ReverseLeds(0x01));
        Assert.Equal(0x0C, JoyCon2Protocol.ReverseLeds(0x03));
        Assert.Equal(0x0F, JoyCon2Protocol.ReverseLeds(0x0F));
    }

    [Fact]
    public void Address_formats_like_a_mac() => Assert.Equal("A4:C1:38:00:11:22", JoyCon2Protocol.FormatAddress(0xA4C138001122));
}

public class JoyCon2MapperTests
{
    private static JoyCon2Input In(uint buttons, int lx = 2048, int ly = 2048, int rx = 2048, int ry = 2048) =>
        new(buttons, lx, ly, rx, ry, 3900, 0);

    private static readonly (int, int) Center = (2048, 2048);

    [Fact]
    public void Combined_merges_both_halves()
    {
        var r = JoyCon2Mapper.Map(In(Up | L | ZL | Minus | Capture, lx: 100), In(A | ZR | Home | Plus, ry: 3000), Center, Center, 3, false);

        Assert.Equal(Buttons.Up | Buttons.L | Buttons.ZL | Buttons.Minus | Buttons.Capture | Buttons.A | Buttons.ZR | Buttons.Home | Buttons.Plus, r.Buttons);
        Assert.Equal((100, 2048, 2048, 3000), (r.LX, r.LY, r.RX, r.RY));
        Assert.Equal(ReportFormat.Full, r.Format);
        Assert.False(r.SingleStick);
        Assert.Equal(3, r.Battery);
    }

    [Fact]
    public void C_and_capture_both_open_the_menu()
    {
        Assert.Equal(Buttons.Capture, JoyCon2Mapper.MapButtons(C, JoyConLayout.Combined));
        Assert.Equal(Buttons.Capture, JoyCon2Mapper.MapButtons(Capture, JoyConLayout.Combined));
        Assert.Equal(Buttons.Capture, JoyCon2Mapper.MapButtons(Capture, JoyConLayout.SingleLeft));
        Assert.Equal(Buttons.Capture, JoyCon2Mapper.MapButtons(C, JoyConLayout.SingleRight));
    }

    [Fact]
    public void Sideways_left_rotates_dpad_into_face_buttons()
    {
        Assert.Equal(Buttons.B, JoyCon2Mapper.MapButtons(Left, JoyConLayout.SingleLeft));
        Assert.Equal(Buttons.A, JoyCon2Mapper.MapButtons(Down, JoyConLayout.SingleLeft));
        Assert.Equal(Buttons.Y, JoyCon2Mapper.MapButtons(Up, JoyConLayout.SingleLeft));
        Assert.Equal(Buttons.X, JoyCon2Mapper.MapButtons(Right, JoyConLayout.SingleLeft));
        Assert.Equal(Buttons.L | Buttons.R, JoyCon2Mapper.MapButtons(SlL | SrL, JoyConLayout.SingleLeft));
        Assert.Equal(Buttons.ZL | Buttons.ZR, JoyCon2Mapper.MapButtons(L | ZL, JoyConLayout.SingleLeft));
        Assert.Equal(Buttons.Plus, JoyCon2Mapper.MapButtons(Minus, JoyConLayout.SingleLeft));
    }

    [Fact]
    public void Sideways_right_rotates_face_buttons()
    {
        Assert.Equal(Buttons.X, JoyCon2Mapper.MapButtons(Y, JoyConLayout.SingleRight));
        Assert.Equal(Buttons.A, JoyCon2Mapper.MapButtons(X, JoyConLayout.SingleRight));
        Assert.Equal(Buttons.Y, JoyCon2Mapper.MapButtons(B, JoyConLayout.SingleRight));
        Assert.Equal(Buttons.B, JoyCon2Mapper.MapButtons(A, JoyConLayout.SingleRight));
        Assert.Equal(Buttons.L | Buttons.R, JoyCon2Mapper.MapButtons(SlR | SrR, JoyConLayout.SingleRight));
        Assert.Equal(Buttons.LStick | Buttons.Home, JoyCon2Mapper.MapButtons(RStick | Home, JoyConLayout.SingleRight));
    }

    [Fact]
    public void Sideways_stick_is_rotated_into_the_left_stick()
    {
        // Left Joy-Con: stick up (+Y) points left, stick right (+X) points up.
        var l = JoyCon2Mapper.Map(In(0, lx: 2048 + 500, ly: 2048 + 300), null, Center, Center, -1, false);
        Assert.Equal((2048 - 300, 2048 + 500), (l.LX, l.LY));
        Assert.True(l.SingleStick);

        // Right Joy-Con: stick up points right, stick right points down.
        var r = JoyCon2Mapper.Map(null, In(0, rx: 2048 + 500, ry: 2048 + 300), Center, (2000, 2100), -1, false);
        Assert.Equal((2048 + 300, 2 * 2000 - (2048 + 500)), (r.LX, r.LY)); // mirrored around the calibrated center
        Assert.Equal((2048, 2048), (r.RX, r.RY));
    }

    [Fact]
    public void Sideways_calibration_follows_the_rotation()
    {
        AxisRange[] left = [new(2000, 1300, 1500), new(2100, 1400, 1600)];

        var c = JoyCon2Mapper.Calibration(JoyConLayout.SingleLeft, left, null);

        Assert.Equal(new AxisRange(2100, 1600, 1400), c.Left[0]); // mirrored Y: travel swapped
        Assert.Equal(new AxisRange(2000, 1300, 1500), c.Left[1]);
        Assert.Equal(JoyCon2Mapper.DefaultRange, c.Right);
    }

    [Fact]
    public void Combined_calibration_keeps_each_stick()
    {
        AxisRange[] left = [new(2000, 1300, 1500), new(2100, 1400, 1600)];
        AxisRange[] right = [new(1990, 1200, 1250), new(2010, 1210, 1260)];

        var c = JoyCon2Mapper.Calibration(JoyConLayout.Combined, left, right);

        Assert.Same(left, c.Left);
        Assert.Same(right, c.Right);
        Assert.Equal("Joy-Con", c.Source);
    }
}
