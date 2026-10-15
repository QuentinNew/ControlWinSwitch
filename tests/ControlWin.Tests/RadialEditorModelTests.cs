using ControlWin.App;
using ControlWin.Config;

namespace ControlWin.Tests;

public class RadialEditorModelTests
{
    private static RadialEditorModel Defaults() => new(RadialDefaults.Create([]));

    [Fact]
    public void The_draft_is_a_copy_of_the_tree()
    {
        var tree = RadialDefaults.Create([]);
        var m = new RadialEditorModel(tree);

        m.At(0)!.Name = "Changed";

        Assert.Null(tree[0].Name);
    }

    [Fact]
    public void Moving_to_an_empty_slot_moves_the_node()
    {
        var m = new RadialEditorModel([new() { Type = "settings", Slot = 0 }]);
        var node = m.At(0)!;

        Assert.True(m.Move(node, 5));

        Assert.Equal(5, node.Slot);
        Assert.Null(m.At(0));
    }

    [Fact]
    public void Moving_onto_an_occupied_slot_swaps()
    {
        var m = new RadialEditorModel([new() { Type = "settings", Slot = 0 }, new() { Type = "quit", Slot = 3 }]);
        var a = m.At(0)!;
        var b = m.At(3)!;

        m.Move(a, 3);

        Assert.Equal(3, a.Slot);
        Assert.Equal(0, b.Slot);
    }

    [Fact]
    public void Add_fills_an_empty_slot_and_selects_the_new_node()
    {
        var m = new RadialEditorModel([new() { Type = "settings", Slot = 0 }]);

        var node = m.Add(RadialNodeTypes.Quit, 4);

        Assert.NotNull(node);
        Assert.Same(node, m.Selected);
        Assert.Null(m.Add(RadialNodeTypes.Quit, 4)); // occupied
        Assert.Null(m.Add(RadialNodeTypes.Quit, 8)); // out of range
    }

    [Fact]
    public void Folders_hold_seven_slots_and_no_folders()
    {
        var m = Defaults();
        m.Enter(m.At(5)!); // Power

        Assert.Equal(7, m.Capacity);
        Assert.Null(m.Add(RadialNodeTypes.Folder, 4));
        Assert.Null(m.Add(RadialNodeTypes.Quit, 7));
        Assert.NotNull(m.Add(RadialNodeTypes.Quit, 4));
    }

    [Fact]
    public void Enter_and_up_switch_between_the_root_and_a_folder()
    {
        var m = Defaults();
        var power = m.At(5)!;

        Assert.True(m.Enter(power));
        Assert.Same(power, m.Folder);
        Assert.Equal(3, m.Nodes.Count);

        m.Up();
        Assert.Null(m.Folder);
        Assert.Same(power, m.Selected);
        Assert.False(m.Enter(m.At(0)!)); // not a folder
    }

    [Fact]
    public void Delete_removes_the_node_and_clears_the_selection()
    {
        var m = Defaults();
        var quit = m.At(6)!;
        m.Select(quit);

        m.Delete(quit);

        Assert.Null(m.At(6));
        Assert.Null(m.Selected);
    }

    [Fact]
    public void Everything_can_be_removed_but_saving_needs_a_settings_node()
    {
        var m = Defaults();
        foreach (var n in m.Nodes.ToList()) m.Delete(n);

        Assert.NotNull(m.Validate());

        m.Add(RadialNodeTypes.Settings, 0);
        Assert.Null(m.Validate());
    }

    [Fact]
    public void Settings_in_a_folder_satisfies_the_rule()
    {
        var folder = new RadialNodeConfig { Type = "folder", Slot = 0, Children = [new() { Type = "settings", Slot = 0 }] };
        var m = new RadialEditorModel([folder]);

        Assert.Null(m.Validate());
    }

    [Fact]
    public void A_shortcut_without_a_target_blocks_saving()
    {
        var m = new RadialEditorModel([new() { Type = "settings", Slot = 0 }]);
        m.Add(RadialNodeTypes.Shortcut, 1);

        Assert.Contains("target", m.Validate());

        m.At(1)!.Target = "notepad.exe";
        Assert.Null(m.Validate());
    }

    [Fact]
    public void Reset_restores_the_default_tree()
    {
        var m = Defaults();
        foreach (var n in m.Nodes.ToList()) m.Delete(n);

        m.Reset([]);

        Assert.Equal(8, m.Nodes.Count);
        Assert.Null(m.Folder);
    }

    [Fact]
    public void Result_is_independent_of_later_edits()
    {
        var m = Defaults();
        var result = m.Result();

        m.At(0)!.Name = "Later";

        Assert.Null(result[0].Name);
    }
}
