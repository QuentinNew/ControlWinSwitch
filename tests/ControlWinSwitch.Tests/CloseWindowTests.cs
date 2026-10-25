using ControlWinSwitch.Actions;
using ControlWinSwitch.App;
using ControlWinSwitch.Config;

namespace ControlWinSwitch.Tests;

public class CloseWindowTests
{
    private static MenuEntry Entry(MenuNodeConfig node) => MenuTree.ToPage("Menu", [node]).Entries.Single();

    [Theory]
    [InlineData(null, "Current")]
    [InlineData("", "Current")]
    [InlineData("nonsense", "Current")]
    [InlineData("byName", "ByName")]
    public void Parse_reads_the_config_text_and_defaults_to_the_current_window(string? text, string expected)
    {
        Assert.Equal(expected, CloseTargets.Parse(text).ToString());
    }

    [Fact]
    public void Every_target_round_trips_and_the_current_window_is_stored_as_null()
    {
        foreach (var target in CloseTargets.All)
            Assert.Equal(target, CloseTargets.Parse(CloseTargets.ToConfig(target)));
        Assert.Null(CloseTargets.ToConfig(CloseTarget.Current));
    }

    [Theory]
    [InlineData(false, "steam", false, "Close the current window?")]
    [InlineData(true, "steam", false, "Close Steam?")]
    [InlineData(true, "steam.exe", true, "Force quit Steam?")]
    [InlineData(true, "", false, "Close the app?")]
    [InlineData(true, "  .exe", true, "Force quit the app?")]
    public void Prompt_follows_the_settings(bool byName, string name, bool force, string expected)
    {
        Assert.Equal(expected, WindowActions.ConfirmPrompt(byName ? CloseTarget.ByName : CloseTarget.Current, name, force));
    }

    [Theory]
    [InlineData(true, false, false, false, "Steam", true)]
    [InlineData(false, false, false, false, "Steam", false)] // hidden
    [InlineData(true, true, false, false, "Steam", false)]   // owned by another window
    [InlineData(true, false, true, false, "Steam", false)]   // tool window
    [InlineData(true, false, false, true, "Steam", false)]   // cloaked by the shell
    [InlineData(true, false, false, false, "", false)]       // no title
    public void Only_visible_titled_main_windows_are_listed(bool visible, bool owned, bool tool, bool cloaked, string title, bool expected)
    {
        Assert.Equal(expected, WindowActions.IsAppWindow(visible, owned, tool, cloaked, title));
    }

    [Fact]
    public void Node_asks_before_closing_unless_confirmation_is_off()
    {
        Assert.Equal("Close the current window?", Entry(new() { Type = MenuNodeTypes.CloseWindow }).ConfirmPrompt);
        Assert.Null(Entry(new() { Type = MenuNodeTypes.CloseWindow, Confirm = false }).ConfirmPrompt);
        Assert.Equal("Close the current window?", Entry(new() { Type = MenuNodeTypes.CloseWindow, Confirm = true }).ConfirmPrompt);
    }

    [Fact]
    public void Node_by_name_carries_its_settings()
    {
        var entry = Entry(new() { Type = MenuNodeTypes.CloseWindow, CloseTarget = "byName", ProcessName = " Steam ", ForceQuit = true });

        Assert.Equal(CloseTarget.ByName, entry.CloseTarget);
        Assert.Equal("Steam", entry.ProcessName);
        Assert.True(entry.ForceQuit);
        Assert.Equal("Force quit Steam?", entry.ConfirmPrompt);
        Assert.Equal(CommandKind.CloseWindow, entry.Command.Kind);
    }

    [Fact]
    public void Clone_keeps_the_close_settings()
    {
        var clone = new MenuNodeConfig
        {
            Type = MenuNodeTypes.CloseWindow, CloseTarget = "byName", ProcessName = "steam", ForceQuit = true, Confirm = false,
        }.Clone();

        Assert.Equal("byName", clone.CloseTarget);
        Assert.Equal("steam", clone.ProcessName);
        Assert.True(clone.ForceQuit);
        Assert.False(clone.Confirm);
    }

    [Fact]
    public void Closing_by_name_without_a_name_or_a_running_app_explains_why()
    {
        Assert.Equal("No app name set", WindowActions.CloseByName("  ", false, out _));
        Assert.Equal("Controlwinswitchnotrunning is not running",
            WindowActions.CloseByName("controlwinswitchnotrunning", false, out _));
    }
}
