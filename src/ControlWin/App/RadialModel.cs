using ControlWin.Input;

namespace ControlWin.App;

internal enum RadialNodeKind { Entry, Back, More }

internal sealed class RadialNode
{
    public required RadialNodeKind Kind { get; init; }
    public required string Label { get; init; }
    public char Glyph { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Radians, 0 = up, clockwise.</summary>
    public float Angle { get; init; }

    public MenuEntry? Entry { get; init; }

    /// <summary>"More..." nodes: index in the page of the first entry shown on the next ring.</summary>
    public int NextStart { get; init; }
}

internal sealed class RadialRing(MenuPage page, IReadOnlyList<RadialNode> nodes, float? parentAngle = null)
{
    public MenuPage Page { get; } = page;
    public IReadOnlyList<RadialNode> Nodes { get; } = nodes;

    /// <summary>Angle, on the parent ring, of the node this ring was opened from (null on the root).</summary>
    public float? ParentAngle { get; } = parentAngle;
}

/// <summary>
/// State of the radial menu: a stack of rings, the highlighted node and the "select twice to confirm" step.
/// Pure (no Win32), driven by <c>AppController</c> and painted by <c>RadialOverlay</c>.
/// </summary>
internal sealed class RadialModel
{
    /// <summary>Nodes per ring, the back node included.</summary>
    public const int MaxNodes = 8;

    private const char BackGlyph = '', MoreGlyph = '';

    private readonly List<RadialRing> _rings = [];
    private int _armed = -1;

    public IReadOnlyList<RadialRing> Rings => _rings;

    public RadialRing Ring => _rings[^1];

    public int Depth => _rings.Count;

    /// <summary>Highlighted node of the current ring, or -1.</summary>
    public int Selected { get; private set; } = -1;

    public RadialNode? CurrentNode => Selected >= 0 && Selected < Ring.Nodes.Count ? Ring.Nodes[Selected] : null;

    /// <summary>True while the highlighted node waits for its second selection.</summary>
    public bool IsConfirming => _armed >= 0 && _armed == Selected;

    /// <summary>The node waiting for its confirmation, or -1.</summary>
    public int Armed => _armed;

    public void Reset(MenuPage root)
    {
        _rings.Clear();
        _rings.Add(BuildRing(root, 0, null));
        Selected = -1;
        _armed = -1;
    }

    /// <summary>Highlights a node (-1 clears). Disabled nodes cannot be highlighted. Returns true when it changed.</summary>
    public bool Select(int index)
    {
        if (index >= Ring.Nodes.Count) index = -1;
        if (index >= 0 && !Ring.Nodes[index].Enabled) index = -1;
        if (index == Selected) return false;

        Selected = index;
        if (index >= 0 && _armed >= 0 && index != _armed) _armed = -1; // another node: the confirmation is dropped
        return true;
    }

    /// <summary>Steps the highlight around the ring (+1 clockwise), skipping disabled nodes.</summary>
    public void Step(int delta)
    {
        int count = Ring.Nodes.Count;
        if (count == 0 || delta == 0) return;

        int step = Math.Sign(delta);
        int index = Selected >= 0 ? Selected : (step > 0 ? -1 : 0);
        for (int i = 0; i < count; i++)
        {
            index = ((index + step) % count + count) % count;
            if (Ring.Nodes[index].Enabled)
            {
                Select(index);
                return;
            }
        }
    }

    /// <summary>
    /// Runs the highlighted node. Opens a ring, goes back or arms a confirmation and returns null, or returns
    /// the command to run.
    /// </summary>
    public (MenuEntry Entry, MenuCommand Command)? Activate()
    {
        var node = CurrentNode;
        if (node is null) return null;

        switch (node.Kind)
        {
            case RadialNodeKind.Back:
                Back();
                return null;

            case RadialNodeKind.More:
                Push(BuildRing(Ring.Page, node.NextStart, node.Angle));
                return null;
        }

        var entry = node.Entry!;
        if (entry.Submenu is { } build)
        {
            Push(BuildRing(build(), 0, node.Angle));
            return null;
        }

        if (entry.ConfirmPrompt is not null && _armed != Selected)
        {
            _armed = Selected;
            return null;
        }

        _armed = -1;
        return entry.Command.Kind == CommandKind.None ? null : (entry, entry.Command);
    }

    /// <summary>Drops a pending confirmation, else goes up one ring. Returns false when the menu should close.</summary>
    public bool Back()
    {
        if (_armed >= 0)
        {
            _armed = -1;
            return true;
        }
        if (_rings.Count > 1)
        {
            _rings.RemoveAt(_rings.Count - 1);
            Selected = -1;
            return true;
        }
        return false;
    }

    private void Push(RadialRing ring)
    {
        _rings.Add(ring);
        Selected = -1;
        _armed = -1;
    }

    /// <summary>
    /// Lays the entries of <paramref name="page"/> from <paramref name="start"/> on a ring. A ring that is not the
    /// root gets a back node opposite the node it was opened from; a page that does not fit gets a "More..." node.
    /// </summary>
    internal static RadialRing BuildRing(MenuPage page, int start, float? parentAngle)
    {
        var entries = page.Entries;
        bool hasBack = parentAngle.HasValue;
        if (page.Slotted) return BuildSlotted(page, parentAngle);

        int capacity = MaxNodes - (hasBack ? 1 : 0);
        int remaining = Math.Max(0, entries.Count - start);
        bool more = remaining > capacity;
        int shown = more ? capacity - 1 : remaining;
        int total = shown + (more ? 1 : 0) + (hasBack ? 1 : 0);

        var nodes = new List<RadialNode>(total);
        if (total == 0) return new RadialRing(page, nodes, parentAngle);

        float slice = MathF.Tau / total;
        float first = hasBack ? Normalize(parentAngle!.Value + MathF.PI) : 0f;
        int slot = 0;

        if (hasBack)
            nodes.Add(new RadialNode { Kind = RadialNodeKind.Back, Label = "Back", Glyph = BackGlyph, Angle = first });

        // Back (when present) takes slot 0, then the entries go clockwise.
        slot = hasBack ? 1 : 0;
        for (int i = 0; i < shown; i++, slot++)
        {
            var entry = entries[start + i];
            nodes.Add(new RadialNode
            {
                Kind = RadialNodeKind.Entry,
                Label = entry.Label,
                Glyph = entry.Glyph,
                Enabled = entry.Enabled,
                Angle = Normalize(first + slot * slice),
                Entry = entry,
            });
        }

        if (more)
            nodes.Add(new RadialNode
            {
                Kind = RadialNodeKind.More,
                Label = "More...",
                Glyph = MoreGlyph,
                Angle = Normalize(first + slot * slice),
                NextStart = start + shown,
            });

        return new RadialRing(page, nodes, parentAngle);
    }

    /// <summary>
    /// Fixed positions: the root has 8 slots from the top, clockwise; a folder has a back node opposite the node it
    /// came from and 7 slots going clockwise right after it. Empty slots stay empty.
    /// </summary>
    private static RadialRing BuildSlotted(MenuPage page, float? parentAngle)
    {
        bool hasBack = parentAngle.HasValue;
        float slice = MathF.Tau / MaxNodes;
        float first = hasBack ? Normalize(parentAngle!.Value + MathF.PI) : 0f;

        var nodes = new List<RadialNode>(MaxNodes);
        if (hasBack)
            nodes.Add(new RadialNode { Kind = RadialNodeKind.Back, Label = "Back", Glyph = BackGlyph, Angle = first });

        foreach (var entry in page.Entries.Where(e => e.Slot >= 0).OrderBy(e => e.Slot))
        {
            int position = hasBack ? entry.Slot + 1 : entry.Slot;
            if (position >= MaxNodes) continue;
            nodes.Add(new RadialNode
            {
                Kind = RadialNodeKind.Entry,
                Label = entry.Label,
                Glyph = entry.Glyph,
                Enabled = entry.Enabled,
                Angle = Normalize(first + position * slice),
                Entry = entry,
            });
        }
        return new RadialRing(page, nodes, parentAngle);
    }

    private static float Normalize(float angle)
    {
        angle %= MathF.Tau;
        return angle < 0 ? angle + MathF.Tau : angle;
    }
}

internal static class RadialGeometry
{
    /// <summary>Angle of a stick position: 0 = up, clockwise (screen convention, +Y = down).</summary>
    public static float AngleOf(float x, float y)
    {
        float a = MathF.Atan2(x, -y);
        return a < 0 ? a + MathF.Tau : a;
    }

    /// <summary>Smallest distance between two angles, 0..π.</summary>
    public static float Distance(float a, float b)
    {
        float d = MathF.Abs(a - b) % MathF.Tau;
        return d > MathF.PI ? MathF.Tau - d : d;
    }

    /// <summary>Index of the node closest to <paramref name="angle"/>, or -1 for an empty ring.</summary>
    public static int Nearest(RadialRing ring, float angle)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < ring.Nodes.Count; i++)
        {
            float d = Distance(ring.Nodes[i].Angle, angle);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }
}

/// <summary>
/// Turns the left stick into "highlight a node" and "release". A released stick springs back through the
/// center in a few milliseconds; a deliberate return takes much longer and only clears the highlight.
/// </summary>
internal sealed class RadialStick(int releaseMs)
{
    /// <summary>Magnitude to start highlighting, to keep it, and under which the stick counts as centered.</summary>
    public const float Engage = 0.6f, Hold = 0.4f, Center = 0.25f;

    /// <summary>Hysteresis (radians) before the highlight moves to a neighbouring node.</summary>
    private const float SwitchMargin = 0.17f;

    /// <summary>A gap this long between two reports means the stick was resting (reports may only come on change).</summary>
    private const long GapMs = 50;

    private long _pushedAt, _lastAt;
    private bool _hasSample;
    private bool _needCenter = true;

    public int ReleaseMs { get; set; } = releaseMs;

    /// <summary>Highlighted node, or -1.</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>Forgets everything; the stick must come back to the center before it can highlight again.</summary>
    public void Reset()
    {
        Selected = -1;
        _hasSample = false;
        _needCenter = true;
    }

    /// <summary>
    /// Feeds one stick sample. Returns the index of the node that was released (the caller runs it), or -1.
    /// <see cref="Selected"/> is the new highlight.
    /// </summary>
    public int Update(Stick stick, long nowMs, RadialRing ring)
    {
        float magnitude = stick.Magnitude;

        // "Pushed" = the last moment the stick was clearly deflected; the clock for a release starts there.
        if (magnitude >= Engage || !_hasSample || nowMs - _lastAt > GapMs) _pushedAt = nowMs;
        _lastAt = nowMs;
        _hasSample = true;

        if (_needCenter)
        {
            if (magnitude < Center) _needCenter = false;
            Selected = -1;
            return -1;
        }

        if (magnitude >= (Selected >= 0 ? Hold : Engage))
        {
            Highlight(RadialGeometry.AngleOf(stick.X, stick.Y), ring);
            return -1;
        }

        if (magnitude >= Center) return -1; // between: keep what is highlighted

        int released = -1;
        if (Selected >= 0 && nowMs - _pushedAt <= ReleaseMs) released = Selected;
        Selected = -1;
        return released;
    }

    private void Highlight(float angle, RadialRing ring)
    {
        int nearest = RadialGeometry.Nearest(ring, angle);
        if (nearest < 0)
        {
            Selected = -1;
            return;
        }

        if (Selected >= 0 && Selected < ring.Nodes.Count && nearest != Selected
            && RadialGeometry.Distance(ring.Nodes[nearest].Angle, angle) + SwitchMargin
               >= RadialGeometry.Distance(ring.Nodes[Selected].Angle, angle))
            return; // not clearly closer to the neighbour yet

        Selected = ring.Nodes[nearest].Enabled ? nearest : -1;
    }
}
