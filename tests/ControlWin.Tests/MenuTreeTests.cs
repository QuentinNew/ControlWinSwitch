using ControlWin.App;
using ControlWin.Config;

namespace ControlWin.Tests;

public class MenuTreeTests
{
    private static AppConfig Parse(string json) => ConfigStore.Parse(json);

    private static MenuNodeConfig Node(string type, int slot = -1, string? name = null) =>
        new() { Type = type, Slot = slot, Name = name };

    [Fact]
    public void A_config_without_a_radial_tree_gets_todays_menu_in_todays_order()
    {
        var cfg = Parse("{}");
        var root = cfg.RadialMenu!;

        Assert.Equal(
            [MenuNodeTypes.MouseMode, MenuNodeTypes.Folder, MenuNodeTypes.VirtualPad, MenuNodeTypes.Headset,
             MenuNodeTypes.Settings, MenuNodeTypes.Folder, MenuNodeTypes.Quit, MenuNodeTypes.Close],
            root.Select(n => n.Type));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], root.Select(n => n.Slot));

        var power = root[5];
        Assert.Equal("Power", power.Name);
        Assert.Equal([MenuNodeTypes.Sleep, MenuNodeTypes.Restart, MenuNodeTypes.Shutdown], power.Children!.Select(n => n.Type));
    }

    [Fact]
    public void The_default_trees_seed_the_two_steam_shortcuts()
    {
        var cfg = Parse("{}");

        foreach (var root in new[] { cfg.RadialMenu!, cfg.ListMenu! })
        {
            var folder = root[1];
            Assert.Equal(["Steam Big Picture", "Steam"], folder.Children!.Select(n => n.Name));
            Assert.Equal("steam://open/bigpicture", folder.Children![0].Target);
        }
    }

    [Fact]
    public void A_config_without_a_list_tree_gets_todays_list()
    {
        var root = Parse("{}").ListMenu!;

        Assert.Equal(
            [MenuNodeTypes.MouseMode, MenuNodeTypes.Folder, MenuNodeTypes.VirtualPad, MenuNodeTypes.Headset,
             MenuNodeTypes.Settings, MenuNodeTypes.Folder, MenuNodeTypes.Quit],
            root.Select(n => n.Type));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], root.Select(n => n.Slot));
        Assert.Equal(["Mouse mode", "Shortcuts", "Virtual pad", "Headset", "Settings", "Power", "Quit ControlWin"],
            MenuTree.ToPage("ControlWin", root, slotted: false).Entries.Select(e => e.Label));
    }

    [Fact]
    public void A_list_has_no_slot_limit_and_keeps_its_order_renumbered()
    {
        var tree = Enumerable.Range(0, 12).Select(i => Node("quit", 100 - i)).Append(Node("settings", -1)).ToList();

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.List, []);

        Assert.Equal(13, fixedTree.Count);
        Assert.Equal(Enumerable.Range(0, 13), fixedTree.Select(n => n.Slot));
        Assert.Equal(MenuNodeTypes.Settings, fixedTree[^1].Type);
    }

    [Fact]
    public void A_list_without_settings_gets_one_at_the_end()
    {
        var fixedTree = MenuRepair.Run([Node("quit", 0)], MenuLayouts.List, []);

        Assert.Equal([MenuNodeTypes.Quit, MenuNodeTypes.Settings], fixedTree.Select(n => n.Type));
    }

    [Fact]
    public void Every_node_type_has_a_kind_and_the_list_page_is_not_slotted()
    {
        foreach (var type in MenuNodeTypes.All) Assert.NotNull(NodeKinds.Find(type));
        Assert.Equal(MenuNodeTypes.All.Length, NodeKinds.All.Count);

        var page = MenuTree.ToPage("L", [Node("quit", 0)], slotted: false);
        Assert.False(page.Slotted);
        Assert.Equal(-1, page.Entries[0].Slot);
    }

    [Fact]
    public void Status_nodes_read_their_status()
    {
        var page = MenuTree.ToPage("L", [Node("virtualPad", 0), Node("headset", 1)], new NodeStatuses(() => "On", () => "Off"), slotted: false);

        Assert.Equal("On", page.Entries[0].Detail!());
        Assert.True(page.Entries[0].Active!());
        Assert.False(page.Entries[1].Active!());
    }

    [Fact]
    public void Colliding_slots_move_to_the_next_free_one_and_extras_are_dropped()
    {
        var tree = new List<MenuNodeConfig> { Node("settings", 0), Node("quit", 0), Node("close", 0) };
        var notes = new List<string>();

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, notes);

        Assert.Equal([0, 1, 2], fixedTree.Select(n => n.Slot));
        Assert.Equal(["settings", "quit", "close"], fixedTree.Select(n => n.Type));
    }

    [Fact]
    public void More_than_eight_root_nodes_drops_the_extras()
    {
        var tree = Enumerable.Range(0, 10).Select(i => Node("quit", i)).Append(Node("settings", 9)).ToList();
        var notes = new List<string>();

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, notes);

        Assert.Equal(8, fixedTree.Count);
        Assert.Contains(fixedTree, n => n.Type == MenuNodeTypes.Settings); // the Settings rule wins over the drop
        Assert.NotEmpty(notes);
    }

    [Fact]
    public void A_folder_inside_a_folder_is_dropped()
    {
        var inner = new MenuNodeConfig { Type = "folder", Slot = 0, Children = [Node("quit", 0)] };
        var outer = new MenuNodeConfig { Type = "folder", Slot = 0, Children = [inner, Node("sleep", 1)] };
        var tree = new List<MenuNodeConfig> { outer, Node("settings", 1) };

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, []);

        Assert.Equal(["sleep"], fixedTree[0].Children!.Select(n => n.Type));
    }

    [Fact]
    public void Unknown_types_and_targetless_shortcuts_are_dropped()
    {
        var tree = new List<MenuNodeConfig> { Node("banana", 0), Node("shortcut", 1), Node("settings", 2) };

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, []);

        Assert.Equal(["settings"], fixedTree.Select(n => n.Type));
    }

    [Fact]
    public void A_tree_without_settings_gets_one_in_the_first_free_root_slot()
    {
        var tree = new List<MenuNodeConfig> { Node("quit", 0), Node("close", 1) };

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, []);

        var settings = Assert.Single(fixedTree, n => n.Type == MenuNodeTypes.Settings);
        Assert.Equal(2, settings.Slot);
    }

    [Fact]
    public void Settings_inside_a_folder_is_enough()
    {
        var folder = new MenuNodeConfig { Type = "folder", Slot = 0, Children = [Node("settings", 0)] };

        var fixedTree = MenuRepair.Run([folder], MenuLayouts.Radial, []);

        Assert.Single(fixedTree);
    }

    [Fact]
    public void Full_tree_without_settings_replaces_the_last_slot()
    {
        var tree = Enumerable.Range(0, 8).Select(i => Node("quit", i)).ToList();

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, []);

        Assert.Equal(8, fixedTree.Count);
        Assert.Equal(MenuNodeTypes.Settings, fixedTree[7].Type);
    }

    [Fact]
    public void Invalid_colors_are_ignored_and_valid_ones_kept()
    {
        var tree = new List<MenuNodeConfig>
        {
            new() { Type = "settings", Slot = 0, Color = "#3F8EE8" },
            new() { Type = "quit", Slot = 1, Color = "red" },
        };

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, []);

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
        var page = MenuTree.ToPage("Root", [Node("settings", 0), Node("quit", 6)]);

        var ring = RadialModel.BuildRing(page, 0, null);

        Assert.Equal(2, ring.Nodes.Count);
        Assert.Equal(0f, ring.Nodes[0].Angle, 3);
        Assert.Equal(MathF.PI * 1.5f, ring.Nodes[1].Angle, 3); // slot 6 = 270 degrees
    }

    [Fact]
    public void Folder_slots_run_clockwise_right_after_the_back_node()
    {
        var folder = new MenuNodeConfig { Type = "folder", Slot = 1, Name = "F", Children = [Node("quit", 0), Node("sleep", 2)] };
        var root = MenuTree.ToPage("Root", [folder]);
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
        var node = new MenuNodeConfig { Type = "settings", Slot = 0, Name = "Config", Icon = new() { Glyph = "E7E8" } };

        var entry = MenuTree.ToPage("Root", [node]).Entries[0];

        Assert.Equal("Config", entry.Label);
        Assert.Equal('', entry.Glyph);
        Assert.Equal(CommandKind.OpenSettings, entry.Command.Kind);
    }

    [Fact]
    public void Shortcut_nodes_carry_their_own_launch_info()
    {
        var node = new MenuNodeConfig { Type = "shortcut", Slot = 0, Name = "Game", Target = "g.exe", Args = "-x" };

        var entry = MenuTree.ToPage("Root", [node]).Entries[0];

        Assert.Equal(CommandKind.Launch, entry.Command.Kind);
        Assert.Equal("g.exe", entry.Launch!.Target);
        Assert.Equal("-x", entry.Launch.Args);
    }
}

public class RadialAppearanceTests
{
    [Fact]
    public void Colors_parse_to_colorref_order()
    {
        Assert.Equal(0x00E88E3Fu, MenuRepair.ParseColor("#3F8EE8")); // BBGGRR
        Assert.Null(MenuRepair.ParseColor("3F8EE8"));
        Assert.Null(MenuRepair.ParseColor("#12345"));
        Assert.Null(MenuRepair.ParseColor(null));
    }

    [Fact]
    public void Node_color_reaches_the_menu_entry()
    {
        var node = new MenuNodeConfig { Type = "settings", Slot = 0, Color = "#FF0000" };

        var entry = MenuTree.ToPage("Root", [node]).Entries[0];

        Assert.Equal(0x0000FFu, entry.Color);
    }

    [Fact]
    public void App_shortcuts_default_to_the_programs_own_icon()
    {
        var node = new MenuNodeConfig { Type = "shortcut", Slot = 0, Target = "notepad.exe" };

        var entry = MenuTree.ToPage("Root", [node]).Entries[0];

        Assert.Equal("auto", entry.Icon!.Kind);
    }

    [Fact]
    public void Other_nodes_have_no_picture_icon_by_default()
    {
        var entry = MenuTree.ToPage("Root", [new MenuNodeConfig { Type = "quit", Slot = 0 }]).Entries[0];

        Assert.Null(entry.Icon);
    }

    [Fact]
    public void Unusable_icons_are_dropped_and_kinds_are_normalized()
    {
        var tree = new List<MenuNodeConfig>
        {
            new() { Type = "settings", Slot = 0, Icon = new() { Kind = "Image", Path = "C:\a.png" } },
            new() { Type = "quit", Slot = 1, Icon = new() { Kind = "image" } },
            new() { Type = "close", Slot = 2, Icon = new() { Kind = "sparkle" } },
            new() { Type = "sleep", Slot = 3, Icon = new() { Kind = "glyph", Glyph = "E7E8" } },
        };

        var fixedTree = MenuRepair.Run(tree, MenuLayouts.Radial, []);

        Assert.Equal("image", fixedTree[0].Icon!.Kind);
        Assert.Null(fixedTree[1].Icon);
        Assert.Null(fixedTree[2].Icon);
        Assert.Equal("glyph", fixedTree[3].Icon!.Kind);
    }
}

public class RadialAdminTests
{
    [Fact]
    public void Run_as_admin_reaches_the_launch_info_and_round_trips()
    {
        var node = new MenuNodeConfig { Type = "shortcut", Slot = 0, Target = "game.exe", RunAsAdmin = true };

        var entry = MenuTree.ToPage("Root", [node]).Entries[0];
        Assert.True(entry.Launch!.RunAsAdmin);

        var cfg = ConfigStore.Parse("{}");
        cfg.RadialMenu![1].Children![0].RunAsAdmin = true;
        var again = ConfigStore.Parse(ConfigStore.Serialize(cfg));
        Assert.True(again.RadialMenu![1].Children![0].RunAsAdmin);
    }
}
