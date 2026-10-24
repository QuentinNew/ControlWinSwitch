using ControlWinSwitch.App;

namespace ControlWinSwitch.Tests;

public class PadGateTests
{
    [Fact]
    public void Process_starting_disables_the_pad_and_closing_restores_it()
    {
        var g = new PadGate();
        Assert.Equal(PadGate.Change.Disable, g.OnRunningChanged(running: true, padEnabled: true));
        Assert.True(g.HeldOff);
        Assert.Equal(PadGate.Change.Enable, g.OnRunningChanged(running: false, padEnabled: false));
        Assert.False(g.HeldOff);
    }

    [Fact]
    public void Pad_the_user_had_off_is_not_turned_on_when_the_process_closes()
    {
        var g = new PadGate();
        Assert.Equal(PadGate.Change.None, g.OnRunningChanged(running: true, padEnabled: false));
        Assert.Equal(PadGate.Change.None, g.OnRunningChanged(running: false, padEnabled: false));
    }

    [Fact]
    public void Manual_override_wins_until_the_process_closes()
    {
        var g = new PadGate();
        g.OnRunningChanged(true, true);   // gate disables the pad
        g.ManualOverride();               // the user turns it back on
        Assert.False(g.HeldOff);
        Assert.Equal(PadGate.Change.None, g.OnRunningChanged(running: false, padEnabled: true));
    }

    [Fact]
    public void Startup_with_the_process_running_starts_disabled()
    {
        var g = new PadGate();
        Assert.False(g.Apply(configEnabled: true, running: true));
        Assert.True(g.HeldOff);
        Assert.True(g.Apply(configEnabled: true, running: false));
        Assert.False(g.Apply(configEnabled: false, running: false));
        Assert.False(g.Apply(configEnabled: false, running: true));
        Assert.False(g.HeldOff); // off by choice, not by the gate
    }
}

public class ProcessListTests
{
    [Theory]
    [InlineData("steam.exe", "steam")]
    [InlineData("  Steam.EXE ", "Steam")]
    [InlineData("steam", "steam")]
    public void Normalize_strips_the_extension(string input, string expected) =>
        Assert.Equal(expected, ProcessList.Normalize(input));

    [Fact]
    public void Parse_splits_trims_and_dedupes()
    {
        Assert.Equal(["steam.exe", "Epic.exe"], ProcessList.Parse(" steam.exe, Epic.exe ;; STEAM.EXE ,"));
        Assert.Empty(ProcessList.Parse("  , ; "));
    }

    [Fact]
    public void Display_name_is_capitalised_without_extension()
    {
        Assert.Equal("Steam", ProcessList.DisplayName("steam.exe"));
    }

    [Fact]
    public void FindRunning_ignores_unknown_names_and_finds_the_test_host()
    {
        Assert.Equal(0, ProcessList.FindRunning(["definitely-not-a-process-xyz"], out _));

        string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        int pid = ProcessList.FindRunning(["nope", self + ".exe"], out var matched);
        Assert.NotEqual(0, pid);
        Assert.Equal(self + ".exe", matched);
    }
}
