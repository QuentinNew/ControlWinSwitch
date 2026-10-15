using ControlWin.App;
using ControlWin.Config;

namespace ControlWin.Tests;

public class RadialTreeTests
{
    private static AppConfig Parse(string json) => ConfigStore.Parse(json);

    private static RadialNodeConfig Node(string type, int slot = -1, string? name = null) =>
        new() { Type = type, Slot = slot, Name = name };

    [Fact]
    public void A_config_without_a_radial_tree_gets_todays_menu_in_todays_order()
    {
        var cfg = Parse("{}");
        var root = cfg.RadialMenu!;

        Assert.Equal(
            [RadialNodeTypes.MouseMode, RadialNodeTypes.Folder, RadialNodeTypes.VirtualPad, RadialNodeTypes.Headset,
             RadialNodeTypes.Settings, RadialNodeTypes.Folder, RadialNodeTypes.Quit, RadialNodeTypes.Close],
            root.Select(n => n.Type));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], root.Select(n => n.Slot));

        var power = root[5];
        Assert.Equal("Power", power.Name);
        Assert.Equal([RadialNodeTypes.Sleep, RadialNodeTypes.Restart, RadialNodeTypes.Shutdown], power.Children!.Select(n => n.Type));
    }

    [Fact]
    public void Existing_shortcuts_seed_the_shortcuts_folder()
    {
        var cfg = Parse("""{"shortcuts":[{"name":"A","target":"a.exe"},{"name":"B","target":"b.exe"}]}""");

        var folder = cfg.RadialMenu![1];
        Assert.Equal(["A", "B"], folder.Children!.Select(n => n.Name));
        Assert.Equal("a.exe", folder.Children![0].Target);
    }

    [Fact]
    public void Shortcuts_that_do_not_fit_in_one_folder_take_the_close_slot()
    {
        var shortcuts = string.Join(",", Enumerable.Range(0, 9).Select(i => $$"""{"name":"s{{i}}","target":"t{{i}}"}"""));
        var cfg = Parse($$"""{"shortcuts":[{{shortcuts}}]}""");

        var root = cfg.RadialMenu!;
        Assert.DoesNotContain(root, n => n.Type == RadialNodeTypes.Close);
        Assert.Equal(7, root[1].Children!.Count);
        Assert.Equal("Shortcuts 2", root[7].Name);
        Assert.Equal(2, root[7].Children!.Count);
    }

    [Fact]
    public void Colliding_slots_move_to_the_next_free_one_and_extras_are_dropped()
    {
        var tree = new List<RadialNodeConfig> { Node("settings", 0), Node("quit", 0), Node("close", 0) };
        var notes = new List<string>();

        var fixedTree = RadialMenuRepair.Run(tree, [], notes);

        Assert.Equal([0, 1, 2], fixedTree.Select(n => n.Slot));
        Assert.Equal(["settings", "quit", "close"], fixedTree.Select(n => n.Type));
    }

    [Fact]
    public void More_than_eight_root_nodes_drops_the_extras()
    {
        var tree = Enumerable.Range(0, 10).Select(i => Node("quit", i)).Append(Node("settings", 9)).ToList();
        var notes = new List<string>();

        var fixedTree = RadialMenuRepair.Run(tree, [], notes);

        Assert.Equal(8, fixedTree.Count);
        Assert.Contains(fixedTree, n => n.Type == RadialNodeTypes.Settings); // the Settings rule wins over the drop
        Assert.NotEmpty(notes);
    }

    [Fact]
    public void A_folder_inside_a_folder_is_dropped()
    {
        var inner = new RadialNodeConfig { Type = "folder", Slot = 0, Children = [Node("quit", 0)] };
        var outer = new RadialNodeConfig { Type = "folder", Slot = 0, Children = [inner, Node("sleep", 1)] };
        var tree = new List<RadialNodeConfig> { outer, Node("settings", 1) };

        var fixedTree = RadialMenuRepair.Run(tree, [], []);

        Assert.Equal(["sleep"], fixedTree[0].Children!.Select(n => n.Type));
    }

    [Fact]
    public void Unknown_types_and_targetless_shortcuts_are_dropped()
    {
        var tree = new List<RadialNodeConfig> { Node("banana", 0), Node("shortcut", 1), Node("settings", 2) };

        var fixedTree = RadialMenuRepair.Run(tree, [], []);

        Assert.Equal(["settings"], fixedTree.Select(n => n.Type));
    }

    [Fact]
    public void A_tree_without_settings_gets_one_in_the_first_free_root_slot()
    {
        var tree = new List<RadialNodeConfig> { Node("quit", 0), Node("close", 1) };

        var fixedTree = RadialMenuRepair.Run(tree, [], []);

        var settings = Assert.Single(fixedTree, n => n.Type == RadialNodeTypes.Settings);
        Assert.Equal(2, settings.Slot);
    }

    [Fact]
    public void Settings_inside_a_folder_is_enough()
    {
        var folder = new RadialNodeConfig { Type = "folder", Slot = 0, Children = [Node("settings", 0)] };

        var fixedTree = RadialMenuRepair.Run([folder], [], []);

        Assert.Single(fixedTree);
    }

    [Fact]
    public void Full_tree_without_settings_replaces_the_last_slot()
    {
        var tree = Enumerable.Range(0, 8).Select(i => Node("quit", i)).ToList();

        var fixedTree = RadialMenuRepair.Run(tree, [], []);

        Assert.Equal(8, fixedTree.Count);
        Assert.Equal(RadialNodeTypes.Settings, fixedTree[7].Type);
    }

    [Fact]
    public void Invalid_colors_are_ignored_and_valid_ones_kept()
    {
        var tree = new List<RadialNodeConfig>
        {
            new() { Type = "settings", Slot = 0, Color = "#3F8EE8" },
            new() { Type = "quit", Slot = 1, Color = "red" },
        };

        var fixedTree = RadialMenuRepair.Run(tree, [], []);

        Assert.Equal("#3F8EE8", fixedTree[0].Color);
        Assert.Null(fixedTree[1].Color);
    }

    [Fact]
    public void The_tree_round_trips_through_the_config_file()
    {
        var cfg = Parse("{}");
        cfg.RadialMenu![0].Name = "Pointer";

        var again = ConfigStore.Parse(ConfigStore.Serialize(cfg));

        Assert.Equal("Pointer", again.RadialMenu![0].Name);
        Assert.Equal(cfg.RadialMenu.Count, again.RadialMenu.Count);
        Assert.Equal(3, again.RadialMenu[5].Children!.Count);
    }

    [Fact]
    public void Slotted_root_keeps_nodes_at_their_slot_even_with_gaps()
    {
        var page = RadialTree.ToPage("Root", [Node("settings", 0), Node("quit", 6)]);

        var ring = RadialModel.BuildRing(page, 0, null);

        Assert.Equal(2, ring.Nodes.Count);
        Assert.Equal(0f, ring.Nodes[0].Angle, 3);
        Assert.Equal(MathF.PI * 1.5f, ring.Nodes[1].Angle, 3); // slot 6 = 270 degrees
    }

    [Fact]
    public void Folder_slots_run_clockwise_right_after_the_back_node()
    {
        var folder = new RadialNodeConfig { Type = "folder", Slot = 1, Name = "F", Children = [Node("quit", 0), Node("sleep", 2)] };
        var root = RadialTree.ToPage("Root", [folder]);
        var m = new RadialModel();
        m.Reset(root);
        m.Select(0);
        m.Activate(); // opens the folder from its node at slot 1 (45 degrees)

        var nodes = m.Ring.Nodes;
        float back = nodes[0].Angle;
        Assert.Equal(RadialNodeKind.Back, nodes[0].Kind);
        Assert.Equal(0f, RadialGeometry.Distance(back, MathF.PI / 4 + MathF.PI), 3);
        Assert.Equal(0f, RadialGeometry.Distance(nodes[1].Angle, back + MathF.Tau / 8), 3);      // slot 0
        Assert.Equal(0f, RadialGeometry.Distance(nodes[2].Angle, back + 3 * MathF.Tau / 8), 3);  // slot 2
    }

    [Fact]
    public void Names_override_labels_and_glyphs_come_from_icons()
    {
        var node = new RadialNodeConfig { Type = "settings", Slot = 0, Name = "Config", Icon = new() { Glyph = "E7E8" } };

        var entry = RadialTree.ToPage("Root", [node]).Entries[0];

        Assert.Equal("Config", entry.Label);
        Assert.Equal('', entry.Glyph);
        Assert.Equal(CommandKind.OpenSettings, entry.Command.Kind);
    }

    [Fact]
    public void Shortcut_nodes_carry_their_own_launch_info()
    {
        var node = new RadialNodeConfig { Type = "shortcut", Slot = 0, Name = "Game", Target = "g.exe", Args = "-x" };

        var entry = RadialTree.ToPage("Root", [node]).Entries[0];

        Assert.Equal(CommandKind.Launch, entry.Command.Kind);
        Assert.Equal("g.exe", entry.Launch!.Target);
        Assert.Equal("-x", entry.Launch.Args);
    }
}
