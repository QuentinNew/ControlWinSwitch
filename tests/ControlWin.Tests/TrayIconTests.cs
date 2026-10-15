using ControlWin.UI;

namespace ControlWin.Tests;

public class TrayIconTests
{
    [Fact]
    public void Tooltip_reflects_controller_state()
    {
        Assert.Equal("ControlWin - controller connected", TrayIcon.Tooltip(true));
        Assert.Equal("ControlWin - controller not found", TrayIcon.Tooltip(false));
    }

    [Fact]
    public void Tooltip_fits_the_128_char_tray_buffer()
    {
        Assert.True(TrayIcon.Tooltip(false).Length < 128);
    }
}
