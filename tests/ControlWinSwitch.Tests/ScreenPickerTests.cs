using ControlWinSwitch.Native;

namespace ControlWinSwitch.Tests;

public class ScreenPickerTests
{
    [Theory]
    [InlineData(0x00000000u, "#000000")]
    [InlineData(0x00FFFFFFu, "#FFFFFF")]
    [InlineData(0x00332211u, "#112233")] // COLORREF is 0x00BBGGRR
    [InlineData(0x0000A5FFu, "#FFA500")]
    public void Colorref_becomes_rrggbb(uint colorref, string expected)
    {
        Assert.Equal(expected, ScreenPicker.ToHex(colorref));
    }

    [Fact]
    public void Sampling_is_limited_to_one_read_per_interval()
    {
        Assert.False(ScreenPicker.IsDue(100 + ScreenPicker.SampleIntervalMs - 1, 100));
        Assert.True(ScreenPicker.IsDue(100 + ScreenPicker.SampleIntervalMs, 100));
        Assert.True(ScreenPicker.IsDue(5000, 0));
    }

    [Fact]
    public void Picking_is_off_until_started()
    {
        Assert.False(ScreenPicker.IsActive);
    }
}
