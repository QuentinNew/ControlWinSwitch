using ControlWin.UI;

namespace ControlWin.Tests;

public class TrayIconTests
{
    [Fact]
    public void Tooltip_fits_the_128_char_tray_buffer()
    {
        Assert.True(TrayIcon.Tooltip(0).Length < 128);
    }
}
