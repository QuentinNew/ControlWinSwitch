using System.Globalization;
using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;

namespace ControlWinSwitch.App;

/// <summary>Where a node's live status text comes from.</summary>
internal enum NodeStatus { None, VirtualPad, Headset }

/// <summary>What a node type is: its defaults and what it does. The one place to touch to add a node type.</summary>
internal sealed record NodeKind(string Type, string Label, char Glyph, CommandKind Command = CommandKind.None,
    string? ConfirmPrompt = null, NodeStatus Status = NodeStatus.None, string? ActiveWhen = null);

internal static class NodeKinds
{
    /// <summary>In the order of the editor's "Add a node" list.</summary>
    public static readonly IReadOnlyList<NodeKind> All =
    [
        new(MenuNodeTypes.Shortcut, "App shortcut", MenuPages.Play, CommandKind.Launch),
        new(MenuNodeTypes.Folder, "Folder", MenuPages.Folder),
        new(MenuNodeTypes.Settings, "Settings", MenuPages.Gear, CommandKind.OpenSettings),
        new(MenuNodeTypes.MouseMode, "Mouse mode", MenuPages.Mouse, CommandKind.MouseMode),
        new(MenuNodeTypes.VirtualPad, "Virtual pad", MenuPages.Game, CommandKind.ToggleVirtualPad, Status: NodeStatus.VirtualPad, ActiveWhen: "On"),
        new(MenuNodeTypes.Headset, "Headset", MenuPages.Headphone, CommandKind.ToggleHeadset, Status: NodeStatus.Headset, ActiveWhen: "Connected"),
        new(MenuNodeTypes.AddController, "Add controller", MenuPages.Add, CommandKind.AddController),
        new(MenuNodeTypes.CloseWindow, "Close window", MenuPages.CloseWindow, CommandKind.CloseWindow, "Close the current window?"),
        new(MenuNodeTypes.Screenshot, "Screenshot", MenuPages.Camera, CommandKind.Screenshot),
        new(MenuNodeTypes.Volume, "Volume", MenuPages.Volume, CommandKind.Volume),
        new(MenuNodeTypes.Brightness, "Brightness", MenuPages.Brightness, CommandKind.Brightness),
        new(MenuNodeTypes.ChangeScreen, "Change screen", MenuPages.ChangeScreen, CommandKind.ChangeScreen),
        new(MenuNodeTypes.Power, "Power", MenuPages.Power, CommandKind.Power),
        new(MenuNodeTypes.Quit, "Quit ControlWinSwitch", MenuPages.Quit, CommandKind.Quit, "Quit ControlWinSwitch?"),
        new(MenuNodeTypes.Close, "Close menu", MenuPages.Close, CommandKind.CloseMenu),
    ];

    public static NodeKind? Find(string type) => All.FirstOrDefault(k => k.Type == type);
}

/// <summary>Status text of the nodes that show one (Virtual pad, Headset).</summary>
internal sealed record NodeStatuses(Func<string?>? VirtualPad = null, Func<string?>? Headset = null)
{
    public Func<string?>? For(NodeStatus status) => status switch
    {
        NodeStatus.VirtualPad => VirtualPad,
        NodeStatus.Headset => Headset,
        _ => null,
    };
}

/// <summary>Turns a configured menu tree (radial or list) into the menu pages the menus show.</summary>
internal static class MenuTree
{
    /// <param name="slotted">Radial: entries are placed by their slot. List: they keep the tree's order.</param>
    public static MenuPage ToPage(string title, IReadOnlyList<MenuNodeConfig> nodes, NodeStatuses? statuses = null, bool slotted = true)
    {
        statuses ??= new();
        var entries = new List<MenuEntry>(nodes.Count);
        foreach (var node in nodes)
            if (ToEntry(node, statuses, slotted) is { } entry) entries.Add(entry);
        return new MenuPage(title, entries, slotted);
    }

    private static MenuEntry? ToEntry(MenuNodeConfig node, NodeStatuses statuses, bool slotted)
    {
        var kind = NodeKinds.Find(node.Type);
        if (kind is null) return null;

        bool isShortcut = node.Type == MenuNodeTypes.Shortcut;
        string label = node.Name ?? (isShortcut ? node.Target ?? "Shortcut" : DefaultLabel(node));
        char glyph = GlyphOf(node.Icon) ?? DefaultGlyph(node);
        uint? color = MenuRepair.ParseColor(node.Color);
        // App shortcuts show the program's own icon unless told otherwise.
        var icon = node.Icon ?? (isShortcut ? new MenuIconConfig { Kind = "auto" } : null);
        int slot = slotted ? node.Slot : -1;

        if (node.Type == MenuNodeTypes.Folder)
        {
            var children = node.Children ?? [];
            return new MenuEntry
            {
                Label = label, Glyph = glyph, Slot = slot, Color = color, Icon = icon,
                Submenu = () => ToPage(label, children, statuses, slotted),
            };
        }

        var detail = statuses.For(kind.Status);
        string? activeWhen = kind.ActiveWhen;
        return new MenuEntry
        {
            Label = label, Glyph = glyph, Slot = slot, Color = color, Icon = icon,
            Command = new(kind.Command),
            ConfirmPrompt = node.Type == MenuNodeTypes.Power ? PowerActions.ConfirmPrompt(PowerActions.Parse(node.PowerMode)) : kind.ConfirmPrompt,
            Detail = detail,
            Active = activeWhen is null ? null : () => detail?.Invoke() == activeWhen,
            Launch = isShortcut
                ? new ShortcutConfig { Name = label, Target = node.Target ?? "", Args = node.Args, WorkingDir = node.WorkingDir, RunAsAdmin = node.RunAsAdmin }
                : null,
            SaveFolder = node.SaveFolder,
            CopyToClipboard = node.CopyToClipboard == true,
            ScreenMode = ScreenModes.Parse(node.ScreenMode),
            PowerMode = PowerActions.Parse(node.PowerMode),
        };
    }

    /// <summary>The icon a node has when none is configured: Power nodes have one per action.</summary>
    internal static char DefaultGlyph(MenuNodeConfig node) => node.Type == MenuNodeTypes.Power
        ? PowerActions.Parse(node.PowerMode) switch
        {
            PowerAction.Restart => MenuPages.Restart,
            PowerAction.Shutdown => MenuPages.Power,
            _ => MenuPages.Sleep,
        }
        : NodeKinds.Find(node.Type)?.Glyph ?? MenuPages.Play;

    /// <summary>The name a node has when none is configured: Change screen nodes are named after their mode.</summary>
    internal static string DefaultLabel(MenuNodeConfig node) => node.Type switch
    {
        MenuNodeTypes.ChangeScreen => ScreenModes.Label(ScreenModes.Parse(node.ScreenMode)),
        MenuNodeTypes.Power => PowerActions.Label(PowerActions.Parse(node.PowerMode)),
        _ => NodeKinds.Find(node.Type)?.Label ?? "App shortcut",
    };

    /// <summary>The code point of a "glyph" icon, or null (not a glyph icon, or not a valid hex code point).</summary>
    internal static char? GlyphOf(MenuIconConfig? icon)
    {
        if (icon is null || !string.Equals(icon.Kind, "glyph", StringComparison.OrdinalIgnoreCase)) return null;
        if (icon.Glyph is { Length: > 0 } text
            && int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)
            && code is > 0x20 and <= 0xFFFF)
            return (char)code;
        return null;
    }
}
