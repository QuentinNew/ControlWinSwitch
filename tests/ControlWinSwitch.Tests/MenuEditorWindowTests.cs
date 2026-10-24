using ControlWinSwitch.Config;
using ControlWinSwitch.UI;

namespace ControlWinSwitch.Tests;

[Collection("native windows")]
public class RadialEditorTextEditingTests
{
    private static MenuEditorWindow Open(string target)
    {
        var folder = new MenuNodeConfig
        {
            Type = "folder", Slot = 0, Children = [new MenuNodeConfig { Type = "shortcut", Slot = 0, Target = target }],
        };
        var window = new MenuEditorWindow();
        window.DebugInit(MenuLayouts.Radial, [folder, new MenuNodeConfig { Type = "settings", Slot = 1 }], []);
        window.DebugModel.Enter(window.DebugModel.Root[0]);
        window.DebugModel.Select(window.DebugModel.Nodes[0]);
        window.DebugRender();
        return window;
    }

    private static void Type(MenuEditorWindow window, string text)
    {
        foreach (char c in text) window.DebugChar(c);
    }

    private static string Target(MenuEditorWindow window) => window.DebugModel.Nodes[0].Target!;

    /// <summary>Starts editing the target with the caret at the end.</summary>
    private static void Edit(MenuEditorWindow window)
    {
        window.DebugClick("Target");
        window.DebugKey(0x23); // end
    }

    [Fact]
    public void Clicking_a_field_keeps_its_content_and_puts_the_caret_where_clicked()
    {
        var window = Open("abc");

        Assert.True(window.DebugClick("Target", 0.99)); // right of the text

        Assert.True(window.DebugEditing);
        Assert.Equal("abc", window.DebugBuffer);
        Assert.Equal(3, window.DebugCaret);
    }

    [Fact]
    public void The_first_click_can_put_the_caret_before_the_first_character()
    {
        var window = Open("notepad.exe");

        window.DebugClick("Target", 0.0);

        Assert.Equal(0, window.DebugCaret);
    }

    [Fact]
    public void Typing_adds_to_the_existing_content()
    {
        var window = Open("notepad.exe");
        Edit(window);

        Type(window, " -x");
        window.DebugChar('\r');

        Assert.Equal("notepad.exe -x", Target(window));
    }

    [Fact]
    public void Backspace_removes_one_character()
    {
        var window = Open("abc");
        Edit(window);

        window.DebugChar('\b');
        window.DebugChar('\r');

        Assert.Equal("ab", Target(window));
    }

    [Fact]
    public void A_click_inside_the_text_moves_the_caret_there()
    {
        var window = Open("notepad.exe");
        Edit(window);

        window.DebugClick("Target", 0.0); // left edge: before the first character
        Assert.Equal(0, window.DebugCaret);

        Type(window, "my-");
        window.DebugChar('\r');

        Assert.Equal("my-notepad.exe", Target(window));
    }

    [Fact]
    public void Arrow_keys_home_end_and_delete_edit_in_the_middle()
    {
        var window = Open("abcd");
        Edit(window);

        Assert.True(window.DebugKey(0x25)); // left
        Assert.True(window.DebugKey(0x25)); // left: caret between b and c
        Type(window, "X");
        Assert.True(window.DebugKey(0x2E)); // delete: removes c
        Assert.True(window.DebugKey(0x24)); // home
        Type(window, "<");
        Assert.True(window.DebugKey(0x23)); // end
        Type(window, ">");
        window.DebugChar('\r');

        Assert.Equal("<abXd>", Target(window));
    }

    [Fact]
    public void Shift_arrows_select_and_typing_replaces_the_selection()
    {
        var window = Open("notepad.exe");
        Edit(window);

        for (int i = 0; i < 4; i++) window.DebugKey(0x25, ctrl: false, shift: true);
        Assert.Equal(".exe", window.DebugSelection);
        Type(window, ".bat");
        window.DebugChar('\r');

        Assert.Equal("notepad.bat", Target(window));
    }

    [Fact]
    public void Ctrl_a_selects_everything()
    {
        var window = Open("notepad.exe");
        Edit(window);

        window.DebugChar((char)0x01);
        Type(window, "calc");
        window.DebugChar('\r');

        Assert.Equal("calc", Target(window));
    }

    [Fact]
    public void Ctrl_arrows_and_ctrl_backspace_work_on_words()
    {
        var window = Open("C:\\Games\\Steam");
        Edit(window);

        Assert.True(window.DebugKey(0x25, ctrl: true, shift: false));
        Assert.Equal(9, window.DebugCaret);
        window.DebugChar((char)0x7F);
        window.DebugChar('\r');

        Assert.Equal("C:\\Steam", Target(window));
    }

    [Fact]
    public void Ctrl_z_undoes_the_typed_word_and_ctrl_y_redoes_it()
    {
        var window = Open("abc");
        Edit(window);

        Type(window, "def");
        window.DebugChar((char)0x1A);
        Assert.Equal("abc", window.DebugBuffer);
        window.DebugChar((char)0x19);

        Assert.Equal("abcdef", window.DebugBuffer);
    }

    [Fact]
    public void Left_collapses_a_selection_to_its_start()
    {
        var window = Open("abcd");
        Edit(window);

        window.DebugKey(0x25, ctrl: false, shift: true);
        window.DebugKey(0x25, ctrl: false, shift: true);
        window.DebugKey(0x25, ctrl: false, shift: false);

        Assert.Equal(2, window.DebugCaret);
        Assert.Equal("", window.DebugSelection);
    }

    [Fact]
    public void Keys_are_ignored_when_no_field_is_edited()
    {
        var window = Open("abc");

        Assert.False(window.DebugKey(0x25));
    }

    [Fact]
    public void Esc_throws_the_changes_away()
    {
        var window = Open("abc");
        Edit(window);
        Type(window, "zzz");

        window.DebugChar((char)27);

        Assert.False(window.DebugEditing);
        Assert.Equal("abc", Target(window));
    }

    [Fact]
    public void A_very_long_value_can_still_be_edited()
    {
        string target = "steam://rungameid/" + new string('1', 200);
        var window = Open(target);
        Edit(window);

        Type(window, "xy");
        window.DebugChar('\r');

        Assert.Equal(target + "xy", Target(window));
    }

    [Fact]
    public void A_target_can_be_cleared_and_replaced()
    {
        var window = Open("steam://open/main");
        Edit(window);

        for (int i = 0; i < 40; i++) window.DebugChar('\b');
        Type(window, "calc.exe");
        window.DebugChar('\r');

        Assert.Equal("calc.exe", Target(window));
    }
}

[Collection("native windows")]
public class MenuEditorCopyTests
{
    private static MenuEditorWindow Open(string layout)
    {
        var window = new MenuEditorWindow();
        window.DebugInit(layout, MenuDefaults.Create(MenuLayouts.Radial), MenuDefaults.Create(MenuLayouts.List));
        window.DebugRender();
        return window;
    }

    [Fact]
    public void Copy_asks_before_replacing_and_replaces_only_on_yes()
    {
        var window = Open(MenuLayouts.Radial);
        int before = window.DebugModel.Nodes.Count;
        window.DebugModel.Delete(window.DebugModel.Nodes[^1]);

        Assert.True(window.DebugClick("copy"));
        Assert.True(window.DebugCopyPending);
        Assert.Equal(before - 1, window.DebugModel.Nodes.Count); // nothing replaced yet

        window.DebugRender();
        Assert.True(window.DebugClick("copy-yes"));
        Assert.False(window.DebugCopyPending);
        Assert.Equal(7, window.DebugModel.Nodes.Count); // the list default has 7 nodes
    }

    [Fact]
    public void Cancelling_the_copy_keeps_the_draft()
    {
        var window = Open(MenuLayouts.List);
        window.DebugModel.Delete(window.DebugModel.Nodes[^1]);

        window.DebugClick("copy");
        window.DebugRender();
        window.DebugClick("copy-no");

        Assert.False(window.DebugCopyPending);
        Assert.Equal(6, window.DebugModel.Nodes.Count);
    }

    [Fact]
    public void A_list_that_does_not_fit_the_ring_says_how_much_is_dropped()
    {
        var longList = Enumerable.Range(0, 10).Select(i => new MenuNodeConfig { Type = "quit", Slot = i }).ToList();
        var window = new MenuEditorWindow();
        window.DebugInit(MenuLayouts.Radial, MenuDefaults.Create(MenuLayouts.Radial), longList);
        window.DebugRender();

        window.DebugClick("copy");

        Assert.Equal(2, window.DebugCopyDropped);
    }

    [Fact]
    public void The_list_view_builds_a_row_hit_per_node()
    {
        var window = Open(MenuLayouts.List);

        Assert.True(window.DebugClick("row:0"));
        Assert.Same(window.DebugModel.Nodes[0], window.DebugModel.Selected);
    }
}
