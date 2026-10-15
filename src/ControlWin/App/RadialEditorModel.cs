using ControlWin.Config;

namespace ControlWin.App;

/// <summary>
/// The radial editor's draft: a private copy of the tree plus "which folder am I in" and "which node is selected".
/// Pure (no Win32); the window only draws it and forwards the mouse.
/// </summary>
internal sealed class RadialEditorModel
{
    private List<RadialNodeConfig> _root;
    private RadialNodeConfig? _folder;

    public RadialEditorModel(IEnumerable<RadialNodeConfig> tree)
    {
        _root = tree.Select(n => n.Clone()).ToList();
    }

    /// <summary>The folder being edited, or null on the root ring.</summary>
    public RadialNodeConfig? Folder => _folder;

    public RadialNodeConfig? Selected { get; private set; }

    public IReadOnlyList<RadialNodeConfig> Root => _root;

    /// <summary>Nodes of the ring being edited.</summary>
    public List<RadialNodeConfig> Nodes => _folder is null ? _root : (_folder.Children ??= []);

    public int Capacity => _folder is null ? RadialDefaults.RootSlots : RadialDefaults.FolderSlots;

    public RadialNodeConfig? At(int slot) => Nodes.Find(n => n.Slot == slot);

    public void Select(RadialNodeConfig? node) => Selected = node;

    /// <summary>Goes into a folder of the root ring (folders do not nest).</summary>
    public bool Enter(RadialNodeConfig folder)
    {
        if (_folder is not null || folder.Type != RadialNodeTypes.Folder || !_root.Contains(folder)) return false;
        _folder = folder;
        Selected = null;
        return true;
    }

    public void Up()
    {
        if (_folder is null) return;
        Selected = _folder;
        _folder = null;
    }

    /// <summary>Moves a node of the current ring to a slot; an occupied slot swaps places.</summary>
    public bool Move(RadialNodeConfig node, int slot)
    {
        if (slot < 0 || slot >= Capacity || !Nodes.Contains(node) || node.Slot == slot) return false;
        var other = At(slot);
        if (other is not null) other.Slot = node.Slot;
        node.Slot = slot;
        return true;
    }

    public static bool CanHold(string type, bool inFolder) =>
        RadialNodeTypes.All.Contains(type) && !(inFolder && type == RadialNodeTypes.Folder);

    /// <summary>Adds a node of a type to an empty slot of the current ring and selects it.</summary>
    public RadialNodeConfig? Add(string type, int slot)
    {
        if (slot < 0 || slot >= Capacity || At(slot) is not null || !CanHold(type, _folder is not null)) return null;

        var node = new RadialNodeConfig { Type = type, Slot = slot };
        if (type == RadialNodeTypes.Folder) node.Children = [];
        if (type == RadialNodeTypes.Shortcut)
        {
            node.Name = "New shortcut";
            node.Target = "";
        }
        Nodes.Add(node);
        Selected = node;
        return node;
    }

    public void Delete(RadialNodeConfig node)
    {
        if (!Nodes.Remove(node)) return;
        if (ReferenceEquals(Selected, node)) Selected = null;
    }

    public void Reset(IReadOnlyList<ShortcutConfig> shortcuts)
    {
        _root = RadialDefaults.Create(shortcuts);
        _folder = null;
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
    public List<RadialNodeConfig> Result() => _root.Select(n => n.Clone()).ToList();

    private static bool ContainsSettings(List<RadialNodeConfig> nodes) =>
        nodes.Any(n => n.Type == RadialNodeTypes.Settings || (n.Children is not null && ContainsSettings(n.Children)));

    private static RadialNodeConfig? FindEmptyShortcut(List<RadialNodeConfig> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Type == RadialNodeTypes.Shortcut && string.IsNullOrWhiteSpace(n.Target)) return n;
            if (n.Children is not null && FindEmptyShortcut(n.Children) is { } found) return found;
        }
        return null;
    }
}
