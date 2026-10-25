using ControlWinSwitch.Actions;
using ControlWinSwitch.App;
using ControlWinSwitch.Config;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Tests;

public class ScreenModeTests
{
    [Theory]
    [InlineData(null, "Extend")]
    [InlineData("", "Extend")]
    [InlineData("nonsense", "Extend")]
    [InlineData("pcOnly", "PcOnly")]
    [InlineData("duplicate", "Duplicate")]
    [InlineData("extend", "Extend")]
    [InlineData("secondOnly", "SecondOnly")]
    public void Parse_reads_the_config_text_and_defaults_to_extend(string? text, string expected)
    {
        Assert.Equal(expected, ScreenModes.Parse(text).ToString());
    }

    [Fact]
    public void Every_mode_round_trips_and_extend_is_stored_as_null()
    {
        foreach (var mode in ScreenModes.All)
            Assert.Equal(mode, ScreenModes.Parse(ScreenModes.ToConfig(mode)));
        Assert.Null(ScreenModes.ToConfig(ScreenMode.Extend));
    }

    [Fact]
    public void Flags_apply_the_matching_win_p_topology()
    {
        Assert.Equal(SDC_APPLY | SDC_TOPOLOGY_INTERNAL, DisplayTopology.Flags(ScreenMode.PcOnly));
        Assert.Equal(SDC_APPLY | SDC_TOPOLOGY_CLONE, DisplayTopology.Flags(ScreenMode.Duplicate));
        Assert.Equal(SDC_APPLY | SDC_TOPOLOGY_EXTEND, DisplayTopology.Flags(ScreenMode.Extend));
        Assert.Equal(SDC_APPLY | SDC_TOPOLOGY_EXTERNAL, DisplayTopology.Flags(ScreenMode.SecondOnly));
    }

    [Fact]
    public void Change_screen_node_becomes_an_entry_with_its_mode()
    {
        var page = MenuTree.ToPage("Menu",
        [
            new MenuNodeConfig { Type = MenuNodeTypes.ChangeScreen, Slot = 0, ScreenMode = "duplicate" },
            new MenuNodeConfig { Type = MenuNodeTypes.ChangeScreen, Slot = 1 },
        ]);

        Assert.Equal("Duplicate", page.Entries[0].Label);
        Assert.Equal("Extend", page.Entries[1].Label);
        Assert.Equal(CommandKind.ChangeScreen, page.Entries[0].Command.Kind);
        Assert.Equal(ScreenMode.Duplicate, page.Entries[0].ScreenMode);
        Assert.Equal(ScreenMode.Extend, page.Entries[1].ScreenMode);
    }

    [Fact]
    public void Clone_keeps_the_screen_mode()
    {
        var clone = new MenuNodeConfig { Type = MenuNodeTypes.ChangeScreen, ScreenMode = "pcOnly" }.Clone();
        Assert.Equal("pcOnly", clone.ScreenMode);
    }
}
