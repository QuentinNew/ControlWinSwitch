using ControlWin.Config;

namespace ControlWin.App;

/// <summary>
/// The menu editor's draft: a private copy of one tree (radial or list) plus "which folder am I in" and "which node
/// is selected". Radial nodes sit in ring slots; list nodes keep their <see cref="MenuNodeConfig.Slot"/> equal to their
/// index. Pure (no Win32); the window only draws it and forwards the mouse.
/// </summary>
internal sealed class MenuEditorModel
{
    private List<MenuNodeConfig> _root;
    private readonly List<MenuNodeConfig> _path = [];

    public MenuEditorModel(string layout, IEnumerable<MenuNodeConfig> tree)
    {
        Layout = layout;
        _root = tree.Select(n => n.Clone()).ToList();
        if (IsList) Renumber();
    }

    public string Layout { get; }

    public bool IsList => Layout == MenuLayouts.List;

    /// <summary>The folder being edited, or null on the root.</summary>
    public MenuNodeConfig? Folder => _path.Count > 0 ? _path[^1] : null;

    /// <summary>The open folders, from the root's down to the one being edited.</summary>
    public IReadOnlyList<MenuNodeConfig> Path => _path;

    public MenuNodeConfig? Selected { get; private set; }

    public IReadOnlyList<MenuNodeConfig> Root => _root;

    /// <summary>Nodes of the ring / list being edited.</summary>
    public List<MenuNodeConfig> Nodes => Folder is { } folder ? (folder.Children ??= []) : _root;

    /// <summary>Radial: slots of the ring being edited. List: unlimited.</summary>
    public int Capacity => MenuDefaults.Capacity(Layout, Folder is not null);

    public MenuNodeConfig? At(int slot) => Nodes.Find(n => n.Slot == slot);

    public void Select(MenuNodeConfig? node) => Selected = node;

    /// <summary>Goes into a folder of the ring / list being edited.</summary>
    public bool Enter(MenuNodeConfig folder)
    {
        if (folder.Type != MenuNodeTypes.Folder || !Nodes.Contains(folder)) return false;
        _path.Add(folder);
        Selected = null;
        return true;
    }

    /// <summary>Goes up one folder and selects the folder it left.</summary>
    public void Up()
    {
        if (_path.Count == 0) return;
        Selected = _path[^1];
        _path.RemoveAt(_path.Count - 1);
    }

    /// <summary>Radial: moves a node of the current ring to a slot; an occupied slot swaps places.</summary>
    public bool Move(MenuNodeConfig node, int slot)
    {
        if (IsList || slot < 0 || slot >= Capacity || !Nodes.Contains(node) || node.Slot == slot) return false;
        var other = At(slot);
        if (other is not null) other.Slot = node.Slot;
        node.Slot = slot;
        return true;
    }

    /// <summary>List: moves a node of the current list so that it ends up at <paramref name="index"/>.</summary>
    public bool MoveTo(MenuNodeConfig node, int index)
    {
        var nodes = Nodes;
        int from = nodes.IndexOf(node);
        if (!IsList || from < 0) return false;
        index = Math.Clamp(index, 0, nodes.Count - 1);
        if (index == from) return false;
        nodes.RemoveAt(from);
        nodes.Insert(index, node);
        Renumber();
        return true;
    }

    /// <summary>List: moves a node of the current list into another folder of that list (at the end of it).</summary>
    public bool MoveInto(MenuNodeConfig node, MenuNodeConfig folder)
    {
        var nodes = Nodes;
        if (!IsList || ReferenceEquals(node, folder) || folder.Type != MenuNodeTypes.Folder
            || !nodes.Contains(node) || !nodes.Contains(folder)) return false;
        nodes.Remove(node);
        (folder.Children ??= []).Add(node);
        Renumber();
        return true;
    }

    public static bool CanHold(string type) => MenuNodeTypes.All.Contains(type);

    /// <summary>
    /// Adds a node of a type to the current ring / list and selects it. Radial: <paramref name="position"/> is an empty
    /// slot. List: it is the index to insert at (anything out of range goes to the nearest end).
    /// </summary>
    public MenuNodeConfig? Add(string type, int position)
    {
        if (!CanHold(type)) return null;
        if (!IsList && (position < 0 || position >= Capacity || At(position) is not null)) return null;

        var node = new MenuNodeConfig { Type = type, Slot = position };
        if (type == MenuNodeTypes.Folder) node.Children = [];
        if (type == MenuNodeTypes.Shortcut)
        {
            node.Name = "New shortcut";
            node.Target = "";
        }
        if (IsList)
        {
            Nodes.Insert(Math.Clamp(position, 0, Nodes.Count), node);
            Renumber();
        }
        else
        {
            Nodes.Add(node);
        }
        Selected = node;
        return node;
    }

    public void Delete(MenuNodeConfig node)
    {
        if (!Nodes.Remove(node)) return;
        if (IsList) Renumber();
        if (ReferenceEquals(Selected, node)) Selected = null;
    }

    public void Reset()
    {
        _root = MenuDefaults.Create(Layout);
        _path.Clear();
        Selected = null;
    }

    /// <summary>
    /// Turns a tree of one layout into a tree of the other: list → radial keeps the first 8 root nodes and the first 7
    /// per folder (at every depth), radial → list orders by slot. <paramref name="dropped"/> counts the nodes (folder contents included)
    /// that did not fit.
    /// </summary>
    public static List<MenuNodeConfig> Convert(IEnumerable<MenuNodeConfig> source, string to, out int dropped)
    {
        dropped = 0;
        var result = Order(source.Select(n => n.Clone()).ToList());
        if (to == MenuLayouts.Radial) result = Fit(result, MenuDefaults.RootSlots, ref dropped);
        Number(result);
        return result;
    }

    /// <summary>Replaces the whole draft by another tree (already converted) and goes back to the root.</summary>
    public void Replace(List<MenuNodeConfig> tree)
    {
        _root = tree;
        _path.Clear();
        Selected = null;
    }

    /// <summary>Why the draft cannot be saved, or null.</summary>
    public string? Validate()
    {
        if (!ContainsSettings(_root)) return "Add a Settings node before saving";
        if (FindEmptyShortcut(_root) is { } missing) return $"'{missing.Name ?? "Shortcut"}' needs a target";
        return null;
    }

    /// <summary>A copy of the draft to hand to the config.</summary>
    public List<MenuNodeConfig> Result() => _root.Select(n => n.Clone()).ToList();

    private void Renumber() => Number(_root);

    /// <summary>Sorts by slot (stable) at every level.</summary>
    private static List<MenuNodeConfig> Order(List<MenuNodeConfig> nodes)
    {
        var ordered = nodes.Select((n, i) => (n, i)).OrderBy(x => x.n.Slot < 0 ? int.MaxValue : x.n.Slot).ThenBy(x => x.i)
            .Select(x => x.n).ToList();
        foreach (var n in ordered)
            if (n.Children is not null) n.Children = Order(n.Children);
        return ordered;
    }

    private static List<MenuNodeConfig> Fit(List<MenuNodeConfig> nodes, int capacity, ref int dropped)
    {
        for (int i = capacity; i < nodes.Count; i++) dropped += 1 + Count(nodes[i].Children);
        var kept = nodes.Take(capacity).ToList();
        foreach (var folder in kept.Where(n => n.Children is not null))
            folder.Children = Fit(folder.Children!, MenuDefaults.FolderSlots, ref dropped);
        return kept;
    }

    private static int Count(List<MenuNodeConfig>? nodes) => nodes is null ? 0 : nodes.Count + nodes.Sum(n => Count(n.Children));

    private static void Number(List<MenuNodeConfig> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            nodes[i].Slot = i;
            if (nodes[i].Children is { } children) Number(children);
        }
    }

    private static bool ContainsSettings(List<MenuNodeConfig> nodes) =>
        nodes.Any(n => n.Type == MenuNodeTypes.Settings || (n.Children is not null && ContainsSettings(n.Children)));

    private static MenuNodeConfig? FindEmptyShortcut(List<MenuNodeConfig> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Type == MenuNodeTypes.Shortcut && string.IsNullOrWhiteSpace(n.Target)) return n;
            if (n.Children is not null && FindEmptyShortcut(n.Children) is { } found) return found;
        }
        return null;
    }
}
