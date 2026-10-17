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

    [Fact]
    public void RumbleMotors_full_strength_is_the_buzz_pattern()
    {
        var r = ProCommands.RumbleMotors(3, 255, 255, 100, 49);

        Assert.Equal(0x10, r[0]);
        Assert.Equal(0x03, r[1]);
        Assert.Equal(new byte[] { 0x28, 0x88, 0x60, 0x61 }, r[2..6]);
        Assert.Equal(r[2..6], r[6..10]);
    }

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(255, 255, 0)]
    public void RumbleMotors_without_strength_is_neutral(byte large, byte small, int percent)
    {
        var r = ProCommands.RumbleMotors(0, large, small, percent, 49);

        Assert.Equal(new byte[] { 0x00, 0x01, 0x40, 0x40 }, r[2..6]);
    }

    [Fact]
    public void RumbleMotors_scales_with_the_percent_and_each_motor_drives_one_band()
    {
        var half = ProCommands.RumbleMotors(0, 255, 255, 50, 49);
        var full = ProCommands.RumbleMotors(0, 255, 255, 100, 49);
        Assert.True(half[3] < full[3]); // high band amplitude
        Assert.True(half[5] < full[5]); // low band amplitude

        var lowOnly = ProCommands.RumbleMotors(0, 255, 0, 100, 49);
        Assert.Equal(0x01, lowOnly[3]);
        Assert.Equal(0x61, lowOnly[5]);
        var highOnly = ProCommands.RumbleMotors(0, 0, 255, 100, 49);
        Assert.Equal(0x88, highOnly[3]);
        Assert.Equal(0x40, highOnly[5]);
    }

    [Fact]
    public void SetPlayerLights_lights_the_led_of_the_slot()
    {
        Assert.Equal(new byte[] { 1, 2, 4, 8 }, Enumerable.Range(0, 4).Select(ProCommands.PlayerLightMask));

        var r = ProCommands.SetPlayerLights(0, ProCommands.PlayerLightMask(1), 49);
        Assert.Equal(0x30, r[10]);
        Assert.Equal(0x02, r[11]);
    }

    [Fact]
    public void EnableVibration_sends_subcommand_0x48()
    {
        var r = ProCommands.EnableVibration(1, 49);

        Assert.Equal(0x01, r[0]);
        Assert.Equal(0x48, r[10]);
        Assert.Equal(0x01, r[11]);
    }
}
