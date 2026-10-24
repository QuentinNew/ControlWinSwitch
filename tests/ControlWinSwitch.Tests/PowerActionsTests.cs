using ControlWinSwitch.Actions;
using ControlWinSwitch.App;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Tests;

public class PowerActionsTests
{
    [Fact]
    public void Shutdown_is_a_hybrid_poweroff()
    {
        uint f = PowerActions.ExitFlags(PowerAction.Shutdown);
        Assert.Equal(EWX_SHUTDOWN | EWX_POWEROFF | EWX_HYBRID_SHUTDOWN | EWX_FORCEIFHUNG, f);
    }

    [Fact]
    public void Restart_is_a_reboot()
    {
        Assert.Equal(EWX_REBOOT | EWX_FORCEIFHUNG, PowerActions.ExitFlags(PowerAction.Restart));
    }

    [Fact]
    public void Sleep_does_not_use_ExitWindowsEx()
    {
        Assert.Equal(0u, PowerActions.ExitFlags(PowerAction.Sleep));
    }

    [Theory]
    [InlineData((int)CommandKind.Sleep, (int)PowerAction.Sleep)]
    [InlineData((int)CommandKind.Restart, (int)PowerAction.Restart)]
    [InlineData((int)CommandKind.Shutdown, (int)PowerAction.Shutdown)]
    public void Commands_map_to_actions(int kind, int expected)
    {
        Assert.Equal((PowerAction)expected, PowerActions.FromCommand((CommandKind)kind));
    }

    [Fact]
    public void Other_commands_are_not_power_actions()
    {
        Assert.Null(PowerActions.FromCommand(CommandKind.Quit));
    }
}
