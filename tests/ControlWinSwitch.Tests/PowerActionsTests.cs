using ControlWinSwitch.Actions;
using ControlWinSwitch.App;
using ControlWinSwitch.Config;
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
    [InlineData(null, "Sleep")]
    [InlineData("", "Sleep")]
    [InlineData("nonsense", "Sleep")]
    [InlineData("sleep", "Sleep")]
    [InlineData("restart", "Restart")]
    [InlineData("shutdown", "Shutdown")]
    public void Parse_reads_the_config_text_and_defaults_to_sleep(string? text, string expected)
    {
        Assert.Equal(expected, PowerActions.Parse(text).ToString());
    }

    [Fact]
    public void Every_action_round_trips_and_sleep_is_stored_as_null()
    {
        foreach (var action in PowerActions.All)
            Assert.Equal(action, PowerActions.Parse(PowerActions.ToConfig(action)));
        Assert.Null(PowerActions.ToConfig(PowerAction.Sleep));
    }

    [Fact]
    public void Power_node_is_named_and_confirmed_after_its_action()
    {
        var page = MenuTree.ToPage("Menu",
        [
            new MenuNodeConfig { Type = MenuNodeTypes.Power, Slot = 0, PowerMode = "shutdown" },
            new MenuNodeConfig { Type = MenuNodeTypes.Power, Slot = 1 },
        ]);

        Assert.Equal("Shut down", page.Entries[0].Label);
        Assert.Equal("Shut down the PC?", page.Entries[0].ConfirmPrompt);
        Assert.Equal(PowerAction.Shutdown, page.Entries[0].PowerMode);
        Assert.Equal(CommandKind.Power, page.Entries[0].Command.Kind);
        Assert.Equal("Sleep", page.Entries[1].Label);
        Assert.Equal(PowerAction.Sleep, page.Entries[1].PowerMode);
    }
}
