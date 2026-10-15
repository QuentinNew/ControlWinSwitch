using System.Globalization;
using ControlWin.Config;

namespace ControlWin.App;

/// <summary>Turns the configured radial tree into the menu pages the radial menu shows.</summary>
internal static class RadialTree
{
    /// <param name="padDetail">Status text of the Virtual pad node.</param>
    /// <param name="headsetDetail">Status text of the Headset node.</param>
    public static MenuPage ToPage(string title, IReadOnlyList<RadialNodeConfig> nodes,
        Func<string?>? padDetail = null, Func<string?>? headsetDetail = null)
    {
        var entries = new List<MenuEntry>(nodes.Count);
        foreach (var node in nodes)
            if (ToEntry(node, padDetail, headsetDetail) is { } entry) entries.Add(entry);
        return new MenuPage(title, entries, slotted: true);
    }

    private static MenuEntry? ToEntry(RadialNodeConfig node, Func<string?>? padDetail, Func<string?>? headsetDetail)
    {
        string Label(string fallback) => node.Name ?? fallback;
        char Glyph(char fallback) => GlyphOf(node.Icon) ?? fallback;
        uint? color = RadialMenuRepair.ParseColor(node.Color);
        // App shortcuts show the program's own icon unless told otherwise.
        var icon = node.Icon ?? (node.Type == RadialNodeTypes.Shortcut ? new RadialIconConfig { Kind = "auto" } : null);

        switch (node.Type)
        {
            case RadialNodeTypes.Close:
                return new MenuEntry
                {
                    Label = Label("Close menu"), Glyph = Glyph(MenuPages.Close), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.CloseMenu),
                };
            case RadialNodeTypes.Quit:
                return new MenuEntry
                {
                    Label = Label("Quit ControlWin"), Glyph = Glyph(MenuPages.Quit), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.Quit), ConfirmPrompt = "Quit ControlWin?",
                };
            case RadialNodeTypes.Sleep:
                return new MenuEntry
                {
                    Label = Label("Sleep"), Glyph = Glyph(MenuPages.Sleep), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.Sleep), ConfirmPrompt = "Put the PC to sleep?",
                };
            case RadialNodeTypes.Restart:
                return new MenuEntry
                {
                    Label = Label("Restart"), Glyph = Glyph(MenuPages.Restart), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.Restart), ConfirmPrompt = "Restart the PC?",
                };
            case RadialNodeTypes.Shutdown:
                return new MenuEntry
                {
                    Label = Label("Shut down"), Glyph = Glyph(MenuPages.Power), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.Shutdown), ConfirmPrompt = "Shut down the PC?",
                };
            case RadialNodeTypes.Settings:
                return new MenuEntry
                {
                    Label = Label("Settings"), Glyph = Glyph(MenuPages.Gear), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.OpenSettings),
                };
            case RadialNodeTypes.Headset:
                return new MenuEntry
                {
                    Label = Label("Headset"), Glyph = Glyph(MenuPages.Headphone), Slot = node.Slot, Color = color, Icon = icon,
                    Detail = headsetDetail, Command = new(CommandKind.ToggleHeadset),
                };
            case RadialNodeTypes.MouseMode:
                return new MenuEntry
                {
                    Label = Label("Mouse mode"), Glyph = Glyph(MenuPages.Mouse), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.MouseMode),
                };
            case RadialNodeTypes.VirtualPad:
                return new MenuEntry
                {
                    Label = Label("Virtual pad"), Glyph = Glyph(MenuPages.Game), Slot = node.Slot, Color = color, Icon = icon,
                    Detail = padDetail, Command = new(CommandKind.ToggleVirtualPad),
                };
            case RadialNodeTypes.Shortcut:
                return new MenuEntry
                {
                    Label = Label(node.Target ?? "Shortcut"), Glyph = Glyph(MenuPages.Play), Slot = node.Slot, Color = color, Icon = icon,
                    Command = new(CommandKind.Launch),
                    Launch = new ShortcutConfig
                    {
                        Name = Label(node.Target ?? "Shortcut"), Target = node.Target ?? "", Args = node.Args, WorkingDir = node.WorkingDir, RunAsAdmin = node.RunAsAdmin,
                    },
                };
            case RadialNodeTypes.Folder:
            {
                string name = Label("Folder");
                var children = node.Children ?? [];
                return new MenuEntry
                {
                    Label = name, Glyph = Glyph(MenuPages.Folder), Slot = node.Slot, Color = color, Icon = icon,
                    Submenu = () => ToPage(name, children, padDetail, headsetDetail),
                };
            }
            default:
                return null;
        }
    }

    /// <summary>The icon a node of this type has when none is configured.</summary>
    internal static char DefaultGlyph(string type) => type switch
    {
        RadialNodeTypes.Close => MenuPages.Close,
        RadialNodeTypes.Quit => MenuPages.Quit,
        RadialNodeTypes.Sleep => MenuPages.Sleep,
        RadialNodeTypes.Restart => MenuPages.Restart,
        RadialNodeTypes.Shutdown => MenuPages.Power,
        RadialNodeTypes.Settings => MenuPages.Gear,
        RadialNodeTypes.Headset => MenuPages.Headphone,
        RadialNodeTypes.MouseMode => MenuPages.Mouse,
        RadialNodeTypes.VirtualPad => MenuPages.Game,
        RadialNodeTypes.Folder => MenuPages.Folder,
        _ => MenuPages.Play,
    };

    /// <summary>The name a node of this type has when none is configured.</summary>
    internal static string DefaultLabel(string type) => type switch
    {
        RadialNodeTypes.Close => "Close menu",
        RadialNodeTypes.Quit => "Quit ControlWin",
        RadialNodeTypes.Sleep => "Sleep",
        RadialNodeTypes.Restart => "Restart",
        RadialNodeTypes.Shutdown => "Shut down",
        RadialNodeTypes.Settings => "Settings",
        RadialNodeTypes.Headset => "Headset",
        RadialNodeTypes.MouseMode => "Mouse mode",
        RadialNodeTypes.VirtualPad => "Virtual pad",
        RadialNodeTypes.Folder => "Folder",
        _ => "App shortcut",
    };

    /// <summary>The code point of a "glyph" icon, or null (not a glyph icon, or not a valid hex code point).</summary>
    internal static char? GlyphOf(RadialIconConfig? icon)
    {
        if (icon is null || !string.Equals(icon.Kind, "glyph", StringComparison.OrdinalIgnoreCase)) return null;
        if (icon.Glyph is { Length: > 0 } text
            && int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)
            && code is > 0x20 and <= 0xFFFF)
            return (char)code;
        return null;
    }
}
