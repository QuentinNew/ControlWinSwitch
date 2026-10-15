using ControlWin.Input;

namespace ControlWin.Tests;

public class ProCommandsTests
{
    [Fact]
    public void SetInputMode_has_report_id_subcommand_and_mode()
    {
        var r = ProCommands.SetInputMode(packetCounter: 5, mode: 0x30, reportLength: 49);

        Assert.Equal(49, r.Length);
        Assert.Equal(0x01, r[0]);   // output report 0x01
        Assert.Equal(0x05, r[1]);   // packet counter
        Assert.Equal(0x03, r[10]);  // subcommand: set input report mode
        Assert.Equal(0x30, r[11]);  // full mode
    }

    [Fact]
    public void SetInputMode_uses_neutral_rumble_and_zero_padding()
    {
        var r = ProCommands.SetInputMode(0, 0x30, 49);

        Assert.Equal(new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }, r[2..10]);
        Assert.All(r[12..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void SetInputMode_packet_counter_wraps_to_a_nibble()
    {
        Assert.Equal(0x01, ProCommands.SetInputMode(0x11, 0x30, 49)[1]);
    }

    [Fact]
    public void SetInputMode_never_returns_a_buffer_shorter_than_the_command()
    {
        Assert.Equal(12, ProCommands.SetInputMode(0, 0x30, 0).Length);
    }
}
