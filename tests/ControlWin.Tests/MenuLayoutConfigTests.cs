using ControlWin.Config;

namespace ControlWin.Tests;

public class MenuLayoutConfigTests
{
    [Fact]
    public void Layout_reads_radial_in_any_case_and_falls_back_to_list_for_junk()
    {
        Assert.Equal(MenuLayouts.Radial, ConfigStore.Parse("""{"menuLayout":"Radial"}""").MenuLayout);
        Assert.Equal(MenuLayouts.List, ConfigStore.Parse("""{"menuLayout":"pie"}""").MenuLayout);
    }

    [Fact]
    public void Release_time_is_clamped()
    {
        Assert.Equal(500, ConfigStore.Parse("""{"radialReleaseMs":99999}""").RadialReleaseMs);
        Assert.Equal(20, ConfigStore.Parse("""{"radialReleaseMs":0}""").RadialReleaseMs);
    }
}
