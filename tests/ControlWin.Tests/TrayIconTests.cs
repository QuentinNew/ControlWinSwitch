using ControlWin.UI;

namespace ControlWin.Tests;

public class TrayIconTests
{
    [Fact]
    public void Tooltip_reflects_controller_state()
    {
        Assert.Equal("ControlWin - controller connected", TrayIcon.Tooltip(1));
        Assert.Equal("ControlWin - controller not found", TrayIcon.Tooltip(0));
        Assert.Equal("ControlWin - 3 controllers connected", TrayIcon.Tooltip(3));
    }

    [Fact]
    public void Tooltip_fits_the_128_char_tray_buffer()
    {
        Assert.True(TrayIcon.Tooltip(0).Length < 128);
    }
}
