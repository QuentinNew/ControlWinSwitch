using ControlWinSwitch.Input;

namespace ControlWinSwitch.Tests;

public class ProReportParserTests
{
    /// <summary>0x3F report: id, b1, b2, hat, then LX LY RX RY as uint16 LE.</summary>
    private static byte[] Simple(byte b1 = 0, byte b2 = 0, byte hat = 8,
        ushort lx = 0x8000, ushort ly = 0x8000, ushort rx = 0x8000, ushort ry = 0x8000) =>
    [
        0x3F, b1, b2, hat,
        (byte)lx, (byte)(lx >> 8), (byte)ly, (byte)(ly >> 8),
        (byte)rx, (byte)(rx >> 8), (byte)ry, (byte)(ry >> 8),
    ];

    /// <summary>0x30 report: id, timer, battery, right, shared, left, then two packed 12-bit sticks.</summary>
    private static byte[] Full(byte right = 0, byte shared = 0, byte left = 0,
        int lx = 2048, int ly = 2048, int rx = 2048, int ry = 2048) =>
    [
        0x30, 0, 0, right, shared, left,
        (byte)lx, (byte)(((lx >> 8) & 0xF) | ((ly & 0xF) << 4)), (byte)(ly >> 4),
        (byte)rx, (byte)(((rx >> 8) & 0xF) | ((ry & 0xF) << 4)), (byte)(ry >> 4),
    ];

    private static ProReport Parse(byte[] bytes)
    {
        Assert.True(ProReportParser.TryParse(bytes, out var report));
        return report;
    }

    [Fact]
    public void Simple_A_button()
    {
        var r = Parse(Simple(b1: 0x02));
        Assert.Equal(ReportFormat.Simple, r.Format);
        Assert.Equal(Buttons.A, r.Buttons);
    }

    [Fact]
    public void Simple_Capture_button()
    {
        Assert.Equal(Buttons.Capture, Parse(Simple(b2: 0x20)).Buttons);
    }

    [Fact]
    public void Simple_multiple_buttons()
    {
        var r = Parse(Simple(b1: 0x01 | 0x80, b2: 0x02));
        Assert.Equal(Buttons.B | Buttons.ZR | Buttons.Plus, r.Buttons);
    }

    [Theory]
    [InlineData(0, (uint)(Buttons.Up))]
    [InlineData(1, (uint)(Buttons.Up | Buttons.Right))]
    [InlineData(2, (uint)(Buttons.Right))]
    [InlineData(3, (uint)(Buttons.Down | Buttons.Right))]
    [InlineData(4, (uint)(Buttons.Down))]
    [InlineData(5, (uint)(Buttons.Down | Buttons.Left))]
    [InlineData(6, (uint)(Buttons.Left))]
    [InlineData(7, (uint)(Buttons.Up | Buttons.Left))]
    [InlineData(8, (uint)(Buttons.None))]
    public void Simple_hat(byte hat, uint expected)
    {
        Assert.Equal((Buttons)expected, Parse(Simple(hat: hat)).Buttons);
    }

    [Fact]
    public void Simple_stick_extremes()
    {
        var r = Parse(Simple(lx: 0x0000, ly: 0xFFFF, rx: 0x1234, ry: 0x8000));
        Assert.Equal(0, r.LX);
        Assert.Equal(65535, r.LY);
        Assert.Equal(0x1234, r.RX);
        Assert.Equal(0x8000, r.RY);
    }

    [Fact]
    public void Full_A_and_Capture()
    {
        var r = Parse(Full(right: 0x08, shared: 0x20));
        Assert.Equal(ReportFormat.Full, r.Format);
        Assert.Equal(Buttons.A | Buttons.Capture, r.Buttons);
    }

    [Fact]
    public void Full_dpad_and_shoulders()
    {
        var r = Parse(Full(left: 0x02 | 0x40 | 0x80));
        Assert.Equal(Buttons.Up | Buttons.L | Buttons.ZL, r.Buttons);
    }

    [Fact]
    public void Full_0x31_is_accepted()
    {
        var bytes = Full(right: 0x04);
        bytes[0] = 0x31;
        Assert.Equal(Buttons.B, Parse(bytes).Buttons);
    }

    [Fact]
    public void Full_stick_12bit_unpacking()
    {
        var r = Parse(Full(lx: 0xFFF, ly: 0xABC, rx: 0, ry: 0x123));
        Assert.Equal(0xFFF, r.LX);
        Assert.Equal(0xABC, r.LY);
        Assert.Equal(0, r.RX);
        Assert.Equal(0x123, r.RY);
    }

    [Fact]
    public void Too_short_reports_are_rejected()
    {
        Assert.False(ProReportParser.TryParse(Simple()[..11], out _));
        Assert.False(ProReportParser.TryParse(Full()[..11], out _));
        Assert.False(ProReportParser.TryParse([], out _));
    }

    [Fact]
    public void Unknown_report_id_is_rejected()
    {
        var bytes = Simple();
        bytes[0] = 0x99;
        Assert.False(ProReportParser.TryParse(bytes, out _));
    }
}
