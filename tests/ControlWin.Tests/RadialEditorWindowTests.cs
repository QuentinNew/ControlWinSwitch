using ControlWin.Config;
using ControlWin.UI;

namespace ControlWin.Tests;

[Collection("native windows")]
public class RadialEditorTextEditingTests
{
    private static RadialEditorWindow Open(string target)
    {
        var folder = new RadialNodeConfig
        {
            Type = "folder", Slot = 0, Children = [new RadialNodeConfig { Type = "shortcut", Slot = 0, Target = target }],
        };
        var window = new RadialEditorWindow();
        window.DebugInit([folder, new RadialNodeConfig { Type = "settings", Slot = 1 }], []);
        window.DebugModel.Enter(window.DebugModel.Root[0]);
        window.DebugModel.Select(window.DebugModel.Nodes[0]);
        window.DebugRender();
        return window;
    }

    private static void Type(RadialEditorWindow window, string text)
    {
        foreach (char c in text) window.DebugChar(c);
    }

    private static string Target(RadialEditorWindow window) => window.DebugModel.Nodes[0].Target!;

    [Fact]
    public void Clicking_a_field_keeps_its_content_and_puts_the_caret_at_the_end()
    {
        var window = Open("notepad.exe");

        Assert.True(window.DebugClick("Target"));

        Assert.True(window.DebugEditing);
        Assert.Equal("notepad.exe", window.DebugBuffer);
        Assert.Equal(11, window.DebugCaret);
    }

    [Fact]
    public void Typing_adds_to_the_existing_content()
    {
        var window = Open("notepad.exe");
        window.DebugClick("Target");

        Type(window, " -x");
        window.DebugChar('\r');

        Assert.Equal("notepad.exe -x", Target(window));
    }

    [Fact]
    public void Backspace_removes_one_character()
    {
        var window = Open("abc");
        window.DebugClick("Target");

        window.DebugChar('\b');
        window.DebugChar('\r');

        Assert.Equal("ab", Target(window));
    }

    [Fact]
    public void A_click_inside_the_text_moves_the_caret_there()
    {
        var window = Open("notepad.exe");
        window.DebugClick("Target");

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
        window.DebugClick("Target");

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
    public void Keys_are_ignored_when_no_field_is_edited()
    {
        var window = Open("abc");

        Assert.False(window.DebugKey(0x25));
    }

    [Fact]
    public void Esc_throws_the_changes_away()
    {
        var window = Open("abc");
        window.DebugClick("Target");
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
        window.DebugClick("Target");

        Type(window, "xy");
        window.DebugChar('\r');

        Assert.Equal(target + "xy", Target(window));
    }

    [Fact]
    public void A_target_can_be_cleared_and_replaced()
    {
        var window = Open("steam://open/main");
        window.DebugClick("Target");

        for (int i = 0; i < 40; i++) window.DebugChar('\b');
        Type(window, "calc.exe");
        window.DebugChar('\r');

        Assert.Equal("calc.exe", Target(window));
    }
}
