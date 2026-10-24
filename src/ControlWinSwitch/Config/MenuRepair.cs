using System.Globalization;

namespace ControlWinSwitch.Config;

/// <summary>The trees a fresh config starts with: today's menus, in today's order.</summary>
internal static class MenuDefaults
{
    public const int RootSlots = 8, FolderSlots = 7;

    /// <summary>Slots of the ring of a root (<paramref name="inFolder"/> false) or of a folder; list menus have no limit.</summary>
    public static int Capacity(string layout, bool inFolder) =>
        layout == MenuLayouts.Radial ? (inFolder ? FolderSlots : RootSlots) : int.MaxValue;

    public static List<MenuNodeConfig> Create(string layout) =>
        layout == MenuLayouts.Radial ? CreateRadial() : CreateList();

    private static MenuNodeConfig Action(string type, int slot, string? color = null) =>
        new() { Type = type, Slot = slot, Color = color };

    private static MenuNodeConfig Folder(string name, int slot, string glyph, List<MenuNodeConfig> children, string? color = null) => new()
    {
        Type = MenuNodeTypes.Folder,
        Slot = slot,
        Name = name,
        Color = color,
        Icon = new MenuIconConfig { Glyph = glyph },
        Children = children,
    };

    private static List<MenuNodeConfig> Launchers() =>
    [
        new() { Type = MenuNodeTypes.Shortcut, Slot = 0, Name = "Steam Big Picture", Target = "steam://open/bigpicture" },
        new() { Type = MenuNodeTypes.Shortcut, Slot = 1, Name = "Steam", Target = "steam://open/main" },
    ];

    private static List<MenuNodeConfig> PowerNodes() =>
    [
        Action(MenuNodeTypes.Sleep, 0, "#C7652B"),
        Action(MenuNodeTypes.Restart, 1, "#2B9DC7"),
        Action(MenuNodeTypes.Shutdown, 2, "#713333"),
    ];

    private static List<MenuNodeConfig> CreateRadial() =>
    [
        Action(MenuNodeTypes.MouseMode, 0),
        Folder("Shortcuts", 1, "E768", Launchers()),
        Action(MenuNodeTypes.VirtualPad, 2),
        Action(MenuNodeTypes.Headset, 3),
        Action(MenuNodeTypes.Settings, 4, "#2A6573"),
        Folder("Power", 5, "E7E8", PowerNodes(), "#7D4444"),
        Action(MenuNodeTypes.Quit, 6, "#713333"),
        Action(MenuNodeTypes.Close, 7, "#0C0B0B"),
    ];

    private static List<MenuNodeConfig> CreateList() =>
    [
        Action(MenuNodeTypes.MouseMode, 0),
        Folder("Shortcuts", 1, "E768", Launchers()),
        Action(MenuNodeTypes.VirtualPad, 2),
        Action(MenuNodeTypes.Headset, 3),
        Action(MenuNodeTypes.Settings, 4, "#2A6573"),
        Folder("Power", 5, "E7E8", PowerNodes(), "#7D4444"),
        Action(MenuNodeTypes.Quit, 6, "#713333"),
    ];
}

/// <summary>
/// Makes a hand-edited menu tree valid without ever failing the load: unknown or incomplete nodes are dropped,
/// radial slots are made unique and in range (list order is the slot order, renumbered from 0), folders nest no deeper
/// than <see cref="MaxDepth"/>, and the tree always has a Settings node.
/// </summary>
internal static class MenuRepair
{
    /// <summary>Folders inside folders: deeper ones are dropped (also stops a runaway hand-written tree).</summary>
    public const int MaxDepth = 8;

    public static List<MenuNodeConfig> Run(List<MenuNodeConfig>? tree, string layout, List<string> notes, string label = "menu")
    {
        if (tree is null) return MenuDefaults.Create(layout);

        var root = Fix(tree, layout, depth: 0, $"{label} root", notes);
        if (!ContainsSettings(root)) AddSettings(root, layout, label, notes);
        return root;
    }

    private static List<MenuNodeConfig> Fix(List<MenuNodeConfig> nodes, string layout, int depth, string where, List<string> notes)
    {
        bool allowFolders = depth < MaxDepth;
        int capacity = MenuDefaults.Capacity(layout, inFolder: depth > 0);
        var accepted = new List<MenuNodeConfig>();
        foreach (var node in nodes)
        {
            if (node is null || !MenuNodeTypes.All.Contains(node.Type))
            {
                notes.Add($"{where}: unknown node type '{node?.Type}' dropped");
                continue;
            }
            if (node.Type == MenuNodeTypes.Shortcut && string.IsNullOrWhiteSpace(node.Target))
            {
                notes.Add($"{where}: shortcut without a target dropped");
                continue;
            }
            if (node.Type == MenuNodeTypes.Folder && !allowFolders)
            {
                notes.Add($"{where}: folder nested too deep dropped");
                continue;
            }

            node.Name = string.IsNullOrWhiteSpace(node.Name) ? null : node.Name.Trim();
            if (node.Color is not null && !IsColor(node.Color))
            {
                notes.Add($"{where}: invalid color '{node.Color}' ignored");
                node.Color = null;
            }
            if (node.Icon is { } icon)
            {
                string kind = (icon.Kind ?? "").Trim().ToLowerInvariant();
                bool valid = kind switch
                {
                    "glyph" => !string.IsNullOrWhiteSpace(icon.Glyph),
                    "auto" => true,
                    "image" => !string.IsNullOrWhiteSpace(icon.Path),
                    _ => false,
                };
                if (valid) icon.Kind = kind;
                else
                {
                    notes.Add($"{where}: unusable icon ignored");
                    node.Icon = null;
                }
            }
            if (node.Type == MenuNodeTypes.Folder)
                node.Children = Fix(node.Children ?? [], layout, depth + 1, node.Name ?? "folder", notes);
            else
                node.Children = null;
            accepted.Add(node);
        }

        if (layout == MenuLayouts.List)
        {
            // List: the order is the slot order (stable for equal or missing slots), renumbered from 0.
            var ordered = accepted.Select((n, i) => (n, i)).OrderBy(x => x.n.Slot < 0 ? int.MaxValue : x.n.Slot).ThenBy(x => x.i)
                .Select(x => x.n).ToList();
            for (int i = 0; i < ordered.Count; i++) ordered[i].Slot = i;
            return ordered;
        }

        // Radial slots: keep valid unique ones, move the rest to the next free slot, drop what does not fit.
        var used = new bool[capacity];
        var pending = new List<MenuNodeConfig>();
        var result = new List<MenuNodeConfig>();
        foreach (var node in accepted)
        {
            if (node.Slot >= 0 && node.Slot < capacity && !used[node.Slot])
            {
                used[node.Slot] = true;
                result.Add(node);
            }
            else
            {
                pending.Add(node);
            }
        }

        foreach (var node in pending)
        {
            int free = Array.IndexOf(used, false);
            if (free < 0)
            {
                notes.Add($"{where}: no free slot, '{node.Name ?? node.Type}' dropped");
                continue;
            }
            used[free] = true;
            node.Slot = free;
            result.Add(node);
        }

        result.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return result;
    }

    private static bool ContainsSettings(List<MenuNodeConfig> nodes) =>
        nodes.Any(n => n.Type == MenuNodeTypes.Settings || (n.Children is not null && ContainsSettings(n.Children)));

    private static void AddSettings(List<MenuNodeConfig> root, string layout, string label, List<string> notes)
    {
        var settings = new MenuNodeConfig { Type = MenuNodeTypes.Settings };

        if (layout == MenuLayouts.List)
        {
            settings.Slot = root.Count;
            root.Add(settings);
            notes.Add($"{label}: no Settings node, added to the root");
            return;
        }

        if (TryPlace(root, MenuDefaults.RootSlots, settings))
        {
            notes.Add($"{label}: no Settings node, added to the root");
            return;
        }
        foreach (var folder in root.Where(n => n.Type == MenuNodeTypes.Folder))
        {
            if (TryPlace(folder.Children!, MenuDefaults.FolderSlots, settings))
            {
                notes.Add($"{label}: no Settings node, added to '{folder.Name ?? "folder"}'");
                return;
            }
        }

        // Everything is full: Settings replaces the node in the last root slot.
        var replaced = root.Find(n => n.Slot == MenuDefaults.RootSlots - 1);
        if (replaced is not null) root.Remove(replaced);
        settings.Slot = MenuDefaults.RootSlots - 1;
        root.Add(settings);
        notes.Add($"{label}: no Settings node, replaced '{replaced?.Name ?? replaced?.Type}'");
    }

    private static bool TryPlace(List<MenuNodeConfig> nodes, int capacity, MenuNodeConfig node)
    {
        for (int slot = 0; slot < capacity; slot++)
        {
            if (nodes.Any(n => n.Slot == slot)) continue;
            node.Slot = slot;
            nodes.Add(node);
            nodes.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return true;
        }
        return false;
    }

    /// <summary>"#RRGGBB" as a COLORREF (0x00BBGGRR), or null when missing or invalid.</summary>
    public static uint? ParseColor(string? value)
    {
        if (value is null || !IsColor(value)) return null;
        uint rgb = uint.Parse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
    }

    public static bool IsColor(string value) =>
        value.Length == 7 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
}
