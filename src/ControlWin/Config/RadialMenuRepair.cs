using System.Globalization;

namespace ControlWin.Config;

/// <summary>The radial tree a fresh config starts with: today's menu, in today's order.</summary>
internal static class RadialDefaults
{
    public const int RootSlots = 8, FolderSlots = 7;

    public static List<RadialNodeConfig> Create(IReadOnlyList<ShortcutConfig> shortcuts, List<string>? notes = null)
    {
        static RadialNodeConfig Action(string type, int slot) => new() { Type = type, Slot = slot };

        static RadialNodeConfig Folder(string name, int slot, string? glyph, List<RadialNodeConfig> children) => new()
        {
            Type = RadialNodeTypes.Folder,
            Slot = slot,
            Name = name,
            Icon = glyph is null ? null : new RadialIconConfig { Glyph = glyph },
            Children = children,
        };

        static List<RadialNodeConfig> Launchers(IEnumerable<ShortcutConfig> items) =>
            items.Select((s, i) => new RadialNodeConfig
            {
                Type = RadialNodeTypes.Shortcut, Slot = i, Name = s.Name, Target = s.Target, Args = s.Args, WorkingDir = s.WorkingDir,
            }).ToList();

        var root = new List<RadialNodeConfig>
        {
            Action(RadialNodeTypes.MouseMode, 0),
            Folder("Shortcuts", 1, "E768", Launchers(shortcuts.Take(FolderSlots))),
            Action(RadialNodeTypes.VirtualPad, 2),
            Action(RadialNodeTypes.Headset, 3),
            Action(RadialNodeTypes.Settings, 4),
            Folder("Power", 5, "E7E8",
            [
                Action(RadialNodeTypes.Sleep, 0),
                Action(RadialNodeTypes.Restart, 1),
                Action(RadialNodeTypes.Shutdown, 2),
            ]),
            Action(RadialNodeTypes.Quit, 6),
        };

        if (shortcuts.Count > FolderSlots)
        {
            // The last slot normally holds Close (B does the same): hand it to the shortcuts that did not fit.
            root.Add(Folder("Shortcuts 2", 7, "E768", Launchers(shortcuts.Skip(FolderSlots).Take(FolderSlots))));
            int dropped = shortcuts.Count - 2 * FolderSlots;
            if (dropped > 0) notes?.Add($"{dropped} shortcuts do not fit in the default radial menu");
        }
        else
        {
            root.Add(Action(RadialNodeTypes.Close, 7));
        }
        return root;
    }
}

/// <summary>
/// Makes a hand-edited radial tree valid without ever failing the load: unknown or incomplete nodes are dropped,
/// slots are made unique and in range, folders do not nest, and the tree always has a Settings node.
/// </summary>
internal static class RadialMenuRepair
{
    public static List<RadialNodeConfig> Run(List<RadialNodeConfig>? tree, IReadOnlyList<ShortcutConfig> shortcuts, List<string> notes)
    {
        if (tree is null) return RadialDefaults.Create(shortcuts, notes);

        var root = Fix(tree, RadialDefaults.RootSlots, allowFolders: true, "root", notes);
        if (!ContainsSettings(root)) AddSettings(root, notes);
        return root;
    }

    private static List<RadialNodeConfig> Fix(List<RadialNodeConfig> nodes, int capacity, bool allowFolders, string where, List<string> notes)
    {
        var accepted = new List<RadialNodeConfig>();
        foreach (var node in nodes)
        {
            if (node is null || !RadialNodeTypes.All.Contains(node.Type))
            {
                notes.Add($"{where}: unknown node type '{node?.Type}' dropped");
                continue;
            }
            if (node.Type == RadialNodeTypes.Shortcut && string.IsNullOrWhiteSpace(node.Target))
            {
                notes.Add($"{where}: shortcut without a target dropped");
                continue;
            }
            if (node.Type == RadialNodeTypes.Folder && !allowFolders)
            {
                notes.Add($"{where}: folder inside a folder dropped");
                continue;
            }

            node.Name = string.IsNullOrWhiteSpace(node.Name) ? null : node.Name.Trim();
            if (node.Color is not null && !IsColor(node.Color))
            {
                notes.Add($"{where}: invalid color '{node.Color}' ignored");
                node.Color = null;
            }
            if (node.Type == RadialNodeTypes.Folder)
                node.Children = Fix(node.Children ?? [], RadialDefaults.FolderSlots, allowFolders: false, node.Name ?? "folder", notes);
            else
                node.Children = null;
            accepted.Add(node);
        }

        // Slots: keep valid unique ones, move the rest to the next free slot, drop what does not fit.
        var used = new bool[capacity];
        var pending = new List<RadialNodeConfig>();
        var result = new List<RadialNodeConfig>();
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

    private static bool ContainsSettings(List<RadialNodeConfig> nodes) =>
        nodes.Any(n => n.Type == RadialNodeTypes.Settings || (n.Children is not null && ContainsSettings(n.Children)));

    private static void AddSettings(List<RadialNodeConfig> root, List<string> notes)
    {
        var settings = new RadialNodeConfig { Type = RadialNodeTypes.Settings };

        if (TryPlace(root, RadialDefaults.RootSlots, settings))
        {
            notes.Add("no Settings node: added to the root");
            return;
        }
        foreach (var folder in root.Where(n => n.Type == RadialNodeTypes.Folder))
        {
            if (TryPlace(folder.Children!, RadialDefaults.FolderSlots, settings))
            {
                notes.Add($"no Settings node: added to '{folder.Name ?? "folder"}'");
                return;
            }
        }

        // Everything is full: Settings replaces the node in the last root slot.
        var replaced = root.Find(n => n.Slot == RadialDefaults.RootSlots - 1);
        if (replaced is not null) root.Remove(replaced);
        settings.Slot = RadialDefaults.RootSlots - 1;
        root.Add(settings);
        notes.Add($"no Settings node: replaced '{replaced?.Name ?? replaced?.Type}'");
    }

    private static bool TryPlace(List<RadialNodeConfig> nodes, int capacity, RadialNodeConfig node)
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

    public static bool IsColor(string value) =>
        value.Length == 7 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
}
