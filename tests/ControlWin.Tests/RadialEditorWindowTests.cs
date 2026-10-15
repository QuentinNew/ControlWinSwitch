using ControlWin.Config;
using ControlWin.UI;

namespace ControlWin.Tests;

[Collection("native windows")]
public class RadialEditorWindowTests
{
    private static RadialEditorWindow Open(RadialNodeConfig shortcut)
    {
        var folder = new RadialNodeConfig { Type = "folder", Slot = 0, Name = "Apps", Children = [shortcut] };
        var settings = new RadialNodeConfig { Type = "settings", Slot = 1 };
        var window = new RadialEditorWindow();
        window.DebugInit([folder, settings], []);
        var model = window.DebugModel;
        model.Enter(model.Root[0]);
        model.Select(model.Nodes[0]);
        window.DebugRender();
        return window;
    }

    [Fact]
    public void A_shortcut_target_that_is_already_set_can_be_edited()
    {
        var window = Open(new RadialNodeConfig { Type = "shortcut", Slot = 0, Target = "notepad.exe" });

        Assert.True(window.DebugClick("Target"));
        Assert.True(window.DebugEditing);
        Assert.Equal("notepad.exe", window.DebugBuffer);

        window.DebugChar('c'); // the value is selected: typing replaces it
        window.DebugChar('m');
        window.DebugChar('d');
        window.DebugChar('\r');

        Assert.Equal("cmd", window.DebugModel.Nodes[0].Target);
    }

    [Fact]
    public void A_target_can_be_replaced_after_clearing_it()
    {
        var window = Open(new RadialNodeConfig { Type = "shortcut", Slot = 0, Target = "steam://open/main" });

        window.DebugClick("Target");
        for (int i = 0; i < 40; i++) window.DebugChar('\b');
        foreach (char c in "calc.exe") window.DebugChar(c);
        window.DebugChar('\r');

        Assert.Equal("calc.exe", window.DebugModel.Nodes[0].Target);
    }
}

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

    [Fact]
    public void Typing_after_the_first_key_appends()
    {
        var window = Open("a");
        window.DebugClick("Target");

        foreach (char c in "bcd") window.DebugChar(c);
        window.DebugChar('\b');
        window.DebugChar('\r');

        Assert.Equal("bc", window.DebugModel.Nodes[0].Target);
    }

    [Fact]
    public void Ctrl_A_selects_everything_again()
    {
        var window = Open("");
        window.DebugClick("Target");
        foreach (char c in "abc") window.DebugChar(c);

        window.DebugChar((char)1);
        window.DebugChar('z');
        window.DebugChar('\r');

        Assert.Equal("z", window.DebugModel.Nodes[0].Target);
    }

    [Fact]
    public void A_very_long_value_can_still_be_edited()
    {
        var window = Open("steam://rungameid/" + new string('1', 200));
        window.DebugClick("Target");

        window.DebugChar('x');
        window.DebugChar('y');
        window.DebugChar('\r');

        Assert.Equal("xy", window.DebugModel.Nodes[0].Target);
    }
}
