using ControlWin.App;

namespace ControlWin.Tests;

public class MenuModelTests
{
    private static MenuEntry Entry(string label, bool enabled = true, CommandKind kind = CommandKind.None,
        Func<MenuPage>? submenu = null, string? confirm = null) =>
        new()
        {
            Label = label,
            Enabled = enabled,
            Command = new MenuCommand(kind),
            Submenu = submenu,
            ConfirmPrompt = confirm,
        };

    private static MenuModel Open(params MenuEntry[] entries)
    {
        var model = new MenuModel();
        model.Reset(new MenuPage("Root", entries));
        return model;
    }

    [Fact]
    public void Selection_starts_on_the_first_enabled_entry()
    {
        var m = Open(Entry("a", enabled: false), Entry("b"), Entry("c"));
        Assert.Equal(1, m.Selected);
    }

    [Fact]
    public void Move_wraps_in_both_directions()
    {
        var m = Open(Entry("a"), Entry("b"), Entry("c"));

        m.Move(-1);
        Assert.Equal(2, m.Selected);

        m.Move(1);
        Assert.Equal(0, m.Selected);
    }

    [Fact]
    public void Move_skips_disabled_entries()
    {
        var m = Open(Entry("a"), Entry("b", enabled: false), Entry("c"), Entry("d", enabled: false));

        m.Move(1);
        Assert.Equal(2, m.Selected);

        m.Move(1); // d is disabled, wraps to a
        Assert.Equal(0, m.Selected);

        m.Move(-1);
        Assert.Equal(2, m.Selected);
    }

    [Fact]
    public void Move_with_only_disabled_entries_does_not_loop_forever()
    {
        var m = Open(Entry("a", enabled: false), Entry("b", enabled: false));
        m.Move(1);
        Assert.Equal(0, m.Selected);
    }

    [Fact]
    public void Accept_returns_the_command_of_a_plain_entry()
    {
        var m = Open(Entry("mouse", kind: CommandKind.MouseMode));
        Assert.Equal(new MenuCommand(CommandKind.MouseMode), m.Accept());
    }

    [Fact]
    public void Accept_on_a_disabled_or_commandless_entry_does_nothing()
    {
        Assert.Null(Open(Entry("off", enabled: false, kind: CommandKind.Quit)).Accept());
        Assert.Null(Open(Entry("none")).Accept());
    }

    [Fact]
    public void Submenu_opens_on_accept_and_back_returns_with_the_selection_restored()
    {
        var m = Open(
            Entry("first"),
            Entry("power", submenu: () => new MenuPage("Power", [Entry("sleep"), Entry("restart")])));
        m.Move(1);

        Assert.Null(m.Accept());
        Assert.Equal("Power", m.Page.Title);
        Assert.Equal(2, m.Depth);
        Assert.Equal(0, m.Selected);

        m.Move(1);
        Assert.Equal("restart", m.CurrentEntry!.Label);

        Assert.True(m.Back());
        Assert.Equal("Root", m.Page.Title);
        Assert.Equal(1, m.Selected);
    }

    [Fact]
    public void Submenu_is_rebuilt_every_time_it_is_opened()
    {
        int builds = 0;
        var m = Open(Entry("sub", submenu: () => { builds++; return new MenuPage("Sub", [Entry("x")]); }));

        m.Accept();
        m.Back();
        m.Accept();

        Assert.Equal(2, builds);
    }

    [Fact]
    public void Back_at_the_root_asks_to_close()
    {
        var m = Open(Entry("a"));
        Assert.False(m.Back());
    }

    [Fact]
    public void Confirmation_A_runs_the_command()
    {
        var m = Open(Entry("shutdown", kind: CommandKind.Shutdown, confirm: "Shut down the PC?"));

        Assert.Null(m.Accept());
        Assert.True(m.IsConfirming);
        Assert.Equal("Shut down the PC?", m.ConfirmPrompt);

        Assert.Equal(new MenuCommand(CommandKind.Shutdown), m.Accept());
        Assert.False(m.IsConfirming);
    }

    [Fact]
    public void Confirmation_B_cancels_and_stays_on_the_page()
    {
        var m = Open(Entry("shutdown", kind: CommandKind.Shutdown, confirm: "Shut down the PC?"));
        m.Accept();

        Assert.True(m.Back());
        Assert.False(m.IsConfirming);
        Assert.Null(m.ConfirmPrompt);
        Assert.Equal("shutdown", m.CurrentEntry!.Label);
    }

    [Fact]
    public void Move_is_ignored_while_confirming()
    {
        var m = Open(Entry("a", kind: CommandKind.Quit, confirm: "Sure?"), Entry("b"));
        m.Accept();

        m.Move(1);
        Assert.Equal(0, m.Selected);
    }

    [Fact]
    public void Reset_discards_previous_state()
    {
        var m = Open(Entry("a", confirm: "Sure?"), Entry("b"));
        m.Accept();

        m.Reset(new MenuPage("Again", [Entry("x")]));

        Assert.False(m.IsConfirming);
        Assert.Equal(1, m.Depth);
        Assert.Equal("Again", m.Page.Title);
    }

    [Fact]
    public void Root_menu_has_the_planned_entries()
    {
        var m = new MenuModel();
        m.Reset(MenuPages.Root());

        var labels = m.Page.Entries.Select(e => e.Label).ToArray();
        Assert.Equal(["Mouse mode", "Shortcuts", "Virtual pad", "Headset", "Power", "Quit ControlWin"], labels);

        // Quit must ask for confirmation, and Power opens a page of three confirmed actions.
        var quit = m.Page.Entries[^1];
        Assert.Equal(CommandKind.Quit, quit.Command.Kind);
        Assert.NotNull(quit.ConfirmPrompt);

        var power = m.Page.Entries.Single(e => e.Label == "Power").Submenu!();
        Assert.Equal(3, power.Entries.Count);
        Assert.All(power.Entries, e => Assert.NotNull(e.ConfirmPrompt));
    }
}
