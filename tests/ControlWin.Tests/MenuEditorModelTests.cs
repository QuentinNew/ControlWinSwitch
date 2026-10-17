using ControlWin.App;
using ControlWin.Config;

namespace ControlWin.Tests;

public class MenuEditorModelTests
{
    private static MenuEditorModel Defaults() => new(MenuLayouts.Radial, MenuDefaults.Create(MenuLayouts.Radial));

    [Fact]
    public void The_draft_is_a_copy_of_the_tree()
    {
        var tree = MenuDefaults.Create(MenuLayouts.Radial);
        var m = new MenuEditorModel(MenuLayouts.Radial, tree);

        m.At(0)!.Name = "Changed";

        Assert.Null(tree[0].Name);
    }

    [Fact]
    public void Moving_to_an_empty_slot_moves_the_node()
    {
        var m = new MenuEditorModel(MenuLayouts.Radial, [new() { Type = "settings", Slot = 0 }]);
        var node = m.At(0)!;

        Assert.True(m.Move(node, 5));

        Assert.Equal(5, node.Slot);
        Assert.Null(m.At(0));
    }

    [Fact]
    public void Moving_onto_an_occupied_slot_swaps()
    {
        var m = new MenuEditorModel(MenuLayouts.Radial, [new() { Type = "settings", Slot = 0 }, new() { Type = "quit", Slot = 3 }]);
        var a = m.At(0)!;
        var b = m.At(3)!;

        m.Move(a, 3);

        Assert.Equal(3, a.Slot);
        Assert.Equal(0, b.Slot);
    }

    [Fact]
    public void Add_fills_an_empty_slot_and_selects_the_new_node()
    {
        var m = new MenuEditorModel(MenuLayouts.Radial, [new() { Type = "settings", Slot = 0 }]);

        var node = m.Add(MenuNodeTypes.Quit, 4);

        Assert.NotNull(node);
        Assert.Same(node, m.Selected);
        Assert.Null(m.Add(MenuNodeTypes.Quit, 4)); // occupied
        Assert.Null(m.Add(MenuNodeTypes.Quit, 8)); // out of range
    }

    [Fact]
    public void Folders_hold_seven_slots_and_no_folders()
    {
        var m = Defaults();
        m.Enter(m.At(5)!); // Power

        Assert.Equal(7, m.Capacity);
        Assert.Null(m.Add(MenuNodeTypes.Folder, 4));
        Assert.Null(m.Add(MenuNodeTypes.Quit, 7));
        Assert.NotNull(m.Add(MenuNodeTypes.Quit, 4));
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

        m.Add(MenuNodeTypes.Settings, 0);
        Assert.Null(m.Validate());
    }

    [Fact]
    public void Settings_in_a_folder_satisfies_the_rule()
    {
        var folder = new MenuNodeConfig { Type = "folder", Slot = 0, Children = [new() { Type = "settings", Slot = 0 }] };
        var m = new MenuEditorModel(MenuLayouts.Radial, [folder]);

        Assert.Null(m.Validate());
    }

    [Fact]
    public void A_shortcut_without_a_target_blocks_saving()
    {
        var m = new MenuEditorModel(MenuLayouts.Radial, [new() { Type = "settings", Slot = 0 }]);
        m.Add(MenuNodeTypes.Shortcut, 1);

        Assert.Contains("target", m.Validate());

        m.At(1)!.Target = "notepad.exe";
        Assert.Null(m.Validate());
    }

    [Fact]
    public void Reset_restores_the_default_tree()
    {
        var m = Defaults();
        foreach (var n in m.Nodes.ToList()) m.Delete(n);

        m.Reset();

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

public class ListEditorModelTests
{
    private static MenuNodeConfig Node(string type, string? name = null) => new() { Type = type, Name = name };

    private static MenuEditorModel List(params MenuNodeConfig[] nodes) => new(MenuLayouts.List, nodes);

    [Fact]
    public void A_list_draft_numbers_its_nodes_by_position_and_has_no_capacity_limit()
    {
        var m = List(Node("settings"), Node("quit"));

        Assert.Equal([0, 1], m.Nodes.Select(n => n.Slot));
        for (int i = 0; i < 20; i++) Assert.NotNull(m.Add(MenuNodeTypes.Sleep, int.MaxValue));
        Assert.Equal(22, m.Nodes.Count);
        Assert.Equal(Enumerable.Range(0, 22), m.Nodes.Select(n => n.Slot));
    }

    [Fact]
    public void Add_inserts_at_the_index_and_selects_the_node()
    {
        var m = List(Node("settings", "a"), Node("quit", "b"));

        var added = m.Add(MenuNodeTypes.Sleep, 1)!;

        Assert.Equal(["a", null, "b"], m.Nodes.Select(n => n.Name));
        Assert.Same(added, m.Selected);
        Assert.Equal(1, added.Slot);
    }

    [Fact]
    public void MoveTo_reorders_and_renumbers()
    {
        var m = List(Node("settings", "a"), Node("quit", "b"), Node("sleep", "c"));

        Assert.True(m.MoveTo(m.Nodes[2], 0));

        Assert.Equal(["c", "a", "b"], m.Nodes.Select(n => n.Name));
        Assert.Equal([0, 1, 2], m.Nodes.Select(n => n.Slot));
        Assert.False(m.MoveTo(m.Nodes[0], 0));
    }

    [Fact]
    public void A_node_can_be_dropped_into_a_folder_but_a_folder_cannot()
    {
        var folder = new MenuNodeConfig { Type = "folder", Name = "F", Children = [] };
        var other = new MenuNodeConfig { Type = "folder", Name = "G", Children = [] };
        var m = List(Node("settings", "a"), folder, other);

        var f = m.Nodes[1];
        Assert.True(m.MoveInto(m.Nodes[0], f));
        Assert.False(m.MoveInto(m.Nodes[1], m.Nodes[0]));

        Assert.Equal(["F", "G"], m.Nodes.Select(n => n.Name));
        Assert.Equal(["a"], f.Children!.Select(n => n.Name));
        Assert.Equal(0, f.Children![0].Slot);
    }

    [Fact]
    public void Radial_only_operations_are_refused_on_a_list()
    {
        var m = List(Node("settings"), Node("quit"));

        Assert.False(m.Move(m.Nodes[0], 5));
        var radial = new MenuEditorModel(MenuLayouts.Radial, [new() { Type = "settings", Slot = 0 }]);
        Assert.False(radial.MoveTo(radial.Nodes[0], 0));
    }

    [Fact]
    public void Delete_renumbers_a_list()
    {
        var m = List(Node("settings"), Node("quit"), Node("sleep"));

        m.Delete(m.Nodes[0]);

        Assert.Equal([0, 1], m.Nodes.Select(n => n.Slot));
    }

    [Fact]
    public void Reset_restores_the_list_default()
    {
        var m = List(Node("settings"));

        m.Reset();

        Assert.Equal(7, m.Nodes.Count);
    }

    [Fact]
    public void Copying_radial_to_list_orders_by_slot_and_drops_nothing()
    {
        var radial = new List<MenuNodeConfig>
        {
            new() { Type = "quit", Slot = 5, Name = "q" },
            new() { Type = "settings", Slot = 1, Name = "s" },
            new() { Type = "folder", Slot = 3, Name = "f", Children = [new() { Type = "sleep", Slot = 4, Name = "z" }, new() { Type = "restart", Slot = 2, Name = "r" }] },
        };

        var list = MenuEditorModel.Convert(radial, MenuLayouts.List, out int dropped);

        Assert.Equal(0, dropped);
        Assert.Equal(["s", "f", "q"], list.Select(n => n.Name));
        Assert.Equal([0, 1, 2], list.Select(n => n.Slot));
        Assert.Equal(["r", "z"], list[1].Children!.Select(n => n.Name));
        Assert.Equal(5, radial[0].Slot); // the source is untouched
    }

    [Fact]
    public void Copying_a_long_list_to_radial_truncates_and_counts_what_was_dropped()
    {
        var folder = new MenuNodeConfig
        {
            Type = "folder", Name = "f",
            Children = Enumerable.Range(0, 9).Select(i => new MenuNodeConfig { Type = "sleep", Name = $"c{i}" }).ToList(),
        };
        var list = new List<MenuNodeConfig> { folder };
        list.AddRange(Enumerable.Range(0, 9).Select(i => new MenuNodeConfig { Type = "quit", Name = $"n{i}" }));
        // 10 root nodes: the folder + 9 others -> 2 root nodes dropped; 9 children -> 2 dropped.

        var radial = MenuEditorModel.Convert(list, MenuLayouts.Radial, out int dropped);

        Assert.Equal(4, dropped);
        Assert.Equal(8, radial.Count);
        Assert.Equal(7, radial[0].Children!.Count);
        Assert.Equal(Enumerable.Range(0, 8), radial.Select(n => n.Slot));
    }

    [Fact]
    public void Dropping_a_folder_counts_its_contents_too()
    {
        var list = Enumerable.Range(0, 8).Select(i => new MenuNodeConfig { Type = "quit" }).ToList();
        list.Add(new MenuNodeConfig { Type = "folder", Children = [new() { Type = "sleep" }, new() { Type = "restart" }] });

        MenuEditorModel.Convert(list, MenuLayouts.Radial, out int dropped);

        Assert.Equal(3, dropped);
    }

    [Fact]
    public void Replace_swaps_the_draft_and_leaves_the_folder()
    {
        var folder = new MenuNodeConfig { Type = "folder", Children = [] };
        var m = List(folder, Node("settings"));
        m.Enter(folder);

        m.Replace([Node("quit")]);

        Assert.Null(m.Folder);
        Assert.Null(m.Selected);
        Assert.Equal(["quit"], m.Nodes.Select(n => n.Type));
    }
}
