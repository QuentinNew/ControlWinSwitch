using ControlWinSwitch.App;
using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.UI;

/// <summary>
/// The radial menu: a topmost, never-activating, background-less popup (per-pixel alpha, so edges are smooth).
/// The root ring is centered on the monitor and never moves. A submenu opens a smaller ring centered on the
/// node it came from, which stays at the center as a hub; the other nodes of the parent ring are hidden.
/// It paints what <see cref="RadialModel"/> says; the stick, D-pad and buttons come from the controller, or
/// from the mouse (hover highlights, left click runs, right click or the hub goes back).
/// </summary>
internal sealed unsafe class RadialOverlay : NativeWindow
{
    public const string ClassName = "ControlWinSwitch.Radial";

    // Layout in DIPs.
    private const int RingDip = 150, RingStepDip = 15, MinRingDip = 135;
    private const int NodeDip = 34, SelectedNodeDip = 40, PillHeightDip = 34, BarDip = 28;

    private static readonly uint NodeColor = Rgb(0x2A, 0x2A, 0x30);   // same as the list's panels
    private static readonly uint RimColor = Rgb(0x4A, 0x4A, 0x54);
    private static readonly uint SelectedColor = Gdi.Selection;       // same as the list's selection

    private RadialModel? _model;
    private IReadOnlyList<SlotTile> _controllers = [];
    private RECT _work;
    private uint _dpi = 96;
    private (int X, int Y) _center;   // root ring center, screen coordinates
    private (int X, int Y) _origin;   // screen position of the window's top-left pixel
    private (int X, int Y)? _lastMouse; // screen position of the last mouse move, null until the first one

    /// <summary>Mouse: node hovered / clicked (index in the current ring), back (right click or the hub).</summary>
    public event Action<int>? NodeHovered, NodeClicked;
    public event Action? BackRequested;

    public RadialOverlay()
    {
        Create(ClassName, WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        int noFade = 1; // no fade in / out: going from a menu to the settings must not dip in between
        DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, &noFade, sizeof(int));
    }

    public bool IsShown => IsWindowVisible(Handle) != 0;

    /// <summary>Shows the menu. <paramref name="controllers"/> are the connected controllers (a bar under the root ring).</summary>
    public void Show(RadialModel model, IReadOnlyList<SlotTile>? controllers = null)
    {
        _model = model;
        _controllers = controllers ?? [];
        _lastMouse = null;
        Gdi.GetMonitorUnderCursor(out _work, out _dpi);
        _center = (_work.Left + _work.Width / 2, _work.Top + _work.Height / 2);

        RadialIcons.Clear(); // pictures are read again each time the menu opens
        Render();
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        // Re-assert topmost: at logon the shell can push a window created early below the others.
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Call after the model changed.</summary>
    public void Refresh() => Render();

    public void Hide()
    {
        ShowWindow(Handle, SW_HIDE);
        K32EmptyWorkingSet(GetCurrentProcess());
    }

    protected override nint WndProc(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_SETCURSOR when (lParam & 0xFFFF) == HTCLIENT:
                SetCursor(LoadCursorW(0, IsClickableAtCursor() ? IDC_HAND : IDC_ARROW));
                return 1;
            case WM_MOUSEMOVE:
                OnMouseMove(LoWord(lParam), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_LBUTTONDOWN:
                OnClick(LoWord(lParam), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_RBUTTONUP:
                BackRequested?.Invoke();
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private int Px(int dip) => (int)Math.Round(dip * _dpi / 96.0);

    private static int RingRadiusDip(int depth) => Math.Max(MinRingDip, RingDip - depth * RingStepDip);

    private (int X, int Y) NodePosition((int X, int Y) center, int depth, float angle)
    {
        int radius = Px(RingRadiusDip(depth));
        return (center.X + (int)MathF.Round(radius * MathF.Sin(angle)), center.Y - (int)MathF.Round(radius * MathF.Cos(angle)));
    }

    /// <summary>Screen center of ring <paramref name="depth"/>: each ring is centered on the node it was opened from.</summary>
    private (int X, int Y) RingCenter(int depth)
    {
        var center = _center;
        if (_model is null) return center;
        for (int d = 1; d <= depth; d++)
            center = NodePosition(center, d - 1, _model.Rings[d].ParentAngle ?? 0f);
        return center;
    }

    /// <summary>Node of the current ring under a client point, or -1.</summary>
    private int NodeAt(int clientX, int clientY)
    {
        if (_model is null) return -1;
        int x = clientX + _origin.X, y = clientY + _origin.Y;
        var ring = _model.Ring;
        int reach = Px(NodeDip) + Px(4);
        int depth = _model.Depth - 1;
        var center = RingCenter(depth);
        for (int i = 0; i < ring.Nodes.Count; i++)
        {
            var (cx, cy) = NodePosition(center, depth, ring.Nodes[i].Angle);
            long dx = x - cx, dy = y - cy;
            if (dx * dx + dy * dy <= (long)reach * reach) return i;
        }
        return -1;
    }

    private bool IsHub(int clientX, int clientY)
    {
        if (_model is null || _model.Depth < 2) return false;
        var c = RingCenter(_model.Depth - 1);
        long dx = clientX + _origin.X - c.X, dy = clientY + _origin.Y - c.Y;
        long reach = Px(NodeDip) + Px(4);
        return dx * dx + dy * dy <= reach * reach;
    }

    private bool IsClickableAtCursor()
    {
        POINT pt;
        GetCursorPos(&pt);
        ScreenToClient(Handle, &pt);
        if (IsHub(pt.X, pt.Y)) return true;
        int index = NodeAt(pt.X, pt.Y);
        return index >= 0 && _model!.Ring.Nodes[index].Enabled;
    }

    private void OnMouseMove(int x, int y)
    {
        // The menu appearing, or the window moving on a redraw, sends a mouse move under a resting cursor: only react
        // to real movement on screen, so a still mouse leaves the highlight to the controller.
        int screenX = x + _origin.X, screenY = y + _origin.Y;
        bool first = _lastMouse is null;
        bool moved = _lastMouse != (screenX, screenY);
        _lastMouse = (screenX, screenY);
        if (first || !moved) return;

        int index = NodeAt(x, y);
        if (index >= 0) NodeHovered?.Invoke(index);
    }

    private void OnClick(int x, int y)
    {
        if (IsHub(x, y))
        {
            BackRequested?.Invoke();
            return;
        }
        int index = NodeAt(x, y);
        if (index >= 0) NodeClicked?.Invoke(index);
    }

    /// <summary>Width in pixels of a line of text in a font.</summary>
    private static int MeasureText(string text, nint font)
    {
        var dc = CreateCompatibleDC(0);
        var old = SelectObject(dc, font);
        var size = new SIZE();
        GetTextExtentPoint32W(dc, text, text.Length, &size);
        SelectObject(dc, old);
        DeleteDC(dc);
        return size.cx;
    }

    /// <summary>Idle shade of a custom color, relative to the highlighted color the user picked.</summary>
    private const float IdleShade = 0.45f;

    /// <summary>Idle fill, idle rim and highlighted fill of a node: its own color, or the theme colors.</summary>
    private static (uint Idle, uint IdleRim, uint Lit) Style(MenuEntry? entry) => Style(entry?.Color);

    internal static (uint Idle, uint IdleRim, uint Lit) Style(uint? color)
    {
        if (color is not { } picked) return (NodeColor, RimColor, SelectedColor);

        uint idle = Rgb((byte)((picked & 0xFF) * IdleShade), (byte)(((picked >> 8) & 0xFF) * IdleShade), (byte)(((picked >> 16) & 0xFF) * IdleShade));
        return (idle, Blend(idle, Rgb(255, 255, 255), 0.18f), picked);
    }

    /// <summary>White icon, except on a highlighted node whose color is so bright that white would vanish.</summary>
    internal static uint GlyphColor(uint lit, bool highlighted)
    {
        if (!highlighted) return Gdi.TextColor;
        double luma = 0.299 * (lit & 0xFF) + 0.587 * ((lit >> 8) & 0xFF) + 0.114 * ((lit >> 16) & 0xFF);
        return luma > 185 ? Rgb(0x1B, 0x1B, 0x1F) : Gdi.TextColor;
    }

    /// <summary>Draws the node's picture (program icon or image file) when it has one. Returns true when it did.</summary>
    private static bool DrawPicture(CircleCanvas canvas, MenuEntry? entry, int x, int y, int radius)
    {
        if (entry is null) return false;
        nint image = RadialIcons.Get(entry, out bool cover);
        if (image == 0) return false;
        canvas.Image(image, x, y, radius, cover);
        return true;
    }

    internal static uint Blend(uint from, uint to, float t)
    {
        static byte Mix(uint a, uint b, float t) => (byte)Math.Round(a + (b - a) * t);
        return Rgb(Mix(from & 0xFF, to & 0xFF, t), Mix((from >> 8) & 0xFF, (to >> 8) & 0xFF, t), Mix((from >> 16) & 0xFF, (to >> 16) & 0xFF, t));
    }

    /// <summary>Draws the menu into a 32-bit premultiplied bitmap and hands it to the window.</summary>
    private void Render()
    {
        if (_model is not { } model || model.Rings.Count == 0) return;

        int top = model.Depth - 1;
        var ring = model.Ring;
        var center = RingCenter(top);
        int pad = Px(SelectedNodeDip) + Px(8);

        // Node positions and the bounds of everything to draw (the window is only as big as that).
        int l = center.X - pad, t = center.Y - pad, r = center.X + pad, b = center.Y + pad;
        var positions = new (int X, int Y)[ring.Nodes.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = NodePosition(center, top, ring.Nodes[i].Angle);
            l = Math.Min(l, positions[i].X - pad);
            t = Math.Min(t, positions[i].Y - pad);
            r = Math.Max(r, positions[i].X + pad);
            b = Math.Max(b, positions[i].Y + pad);
        }

        // The connected controllers: a capsule in the root ring's center; four of them on two rows of two.
        int barH = Px(BarDip), barPad = Px(10), picR = Px(BarDip) / 2 - Px(4), picGap = Px(ControllerTile.PicGapDip);
        bool drawBar = top == 0 && _controllers.Count > 0;
        int perRow = _controllers.Count >= 4 ? (_controllers.Count + 1) / 2 : _controllers.Count;
        var rows = new List<(int First, int[] Xs, int Width)>();
        for (int first = 0; first < _controllers.Count; first += perRow)
        {
            var row = _controllers.Skip(first).Take(perRow).ToList();
            int entriesW = ControllerTile.StripLayout(_dpi, row, picR, out var xs);
            rows.Add((first, xs, barPad * 2 + entriesW));
        }
        int barW = rows.Count == 0 ? 0 : rows.Max(row => row.Width), barTotalH = barH * rows.Count;
        int barL = center.X - barW / 2;
        int barT = center.Y - barTotalH / 2;
        if (drawBar)
        {
            l = Math.Min(l, barL - Px(2));
            r = Math.Max(r, barL + barW + Px(2));
            t = Math.Min(t, barT - Px(2));
            b = Math.Max(b, barT + barTotalH + Px(2));
        }

        // Where a controller's entry starts (x) and its row's center line (y), in screen coordinates.
        (int X, int Cy) EntryAt(int i)
        {
            var row = rows[i / perRow];
            return (center.X - row.Width / 2 + barPad + row.Xs[i - row.First], barT + (i / perRow) * barH + barH / 2);
        }

        // Name of the highlighted node, in a pill right under it.
        string? text = null;
        uint pillColor = Gdi.TextColor;
        int pillL = 0, pillT = 0, pillR = 0, pillB = 0;
        if (model.CurrentNode is { } selectedNode && model.Selected >= 0)
        {
            text = model.IsConfirming ? $"Confirm: {selectedNode.Label}?" : selectedNode.Label;
            if (model.IsConfirming) pillColor = Blend(Style(selectedNode.Entry).Lit, Rgb(255, 255, 255), 0.35f); // the node's color, lightened to stay readable
            if (!model.IsConfirming && selectedNode.Entry?.Detail?.Invoke() is { Length: > 0 } detail) text += " - " + detail;

            var (nx, ny) = positions[model.Selected];
            int half = (MeasureText(text, Gdi.GetFonts(_dpi).Detail) + Px(32)) / 2;
            half = Math.Clamp(half, Px(50), Px(200));
            int height = Px(PillHeightDip);
            pillL = Math.Clamp(nx - half, _work.Left + Px(8), Math.Max(_work.Left + Px(8), _work.Right - Px(8) - half * 2));
            pillR = pillL + half * 2;
            pillT = Math.Min(ny + Px(SelectedNodeDip) + Px(10), _work.Bottom - Px(8) - height);
            pillB = pillT + height;
            l = Math.Min(l, pillL);
            t = Math.Min(t, pillT);
            r = Math.Max(r, pillR);
            b = Math.Max(b, pillB);
        }

        l = Math.Max(l, _work.Left);
        t = Math.Max(t, _work.Top);
        r = Math.Min(r, _work.Right);
        b = Math.Min(b, _work.Bottom);
        int w = r - l, h = b - t;
        if (w <= 0 || h <= 0) return;
        _origin = (l, t);

        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        var dc = CreateCompatibleDC(0);
        var dib = CreateDIBSection(dc, &info, 0, &bits, 0, 0);
        if (dib == 0)
        {
            DeleteDC(dc);
            return;
        }
        var oldBitmap = SelectObject(dc, dib);
        SetBkMode(dc, TRANSPARENT);
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int half2 = Px(SelectedNodeDip);

        RadialNode? hub = null;
        if (top > 0)
        {
            float parentAngle = ring.ParentAngle ?? 0f;
            foreach (var n in model.Rings[top - 1].Nodes)
                if (RadialGeometry.Distance(n.Angle, parentAngle) < 0.001f) hub = n;
        }

        // Shapes and pictures (antialiased, with alpha) first...
        var pictured = new bool[positions.Length];
        bool hubPictured = false;
        using (var canvas = new CircleCanvas(bits, w, h))
        {
            if (drawBar)
            {
                int rad = barH / 2, x0 = barL - l, y0 = barT - t;
                // A capsule, or for two rows a rounded rectangle with the same corner radius.
                void Capsule(int grow, uint color)
                {
                    canvas.Circle(x0 + rad, y0 + rad, rad + grow, color);
                    canvas.Circle(x0 + barW - rad, y0 + rad, rad + grow, color);
                    canvas.Circle(x0 + rad, y0 + barTotalH - rad, rad + grow, color);
                    canvas.Circle(x0 + barW - rad, y0 + barTotalH - rad, rad + grow, color);
                    canvas.Rect(x0 + rad, y0 - grow, x0 + barW - rad, y0 + barTotalH + grow, color);
                    canvas.Rect(x0 - grow, y0 + rad, x0 + barW + grow, y0 + barTotalH - rad, color);
                }
                Capsule(Px(2), RimColor);
                Capsule(0, NodeColor);
                for (int i = 0; i < _controllers.Count; i++)
                {
                    var (ex, ey) = EntryAt(i);
                    int cx = ex - l + picR, cy = ey - t;
                    var c = _controllers[i];
                    foreach (bool second in c.Pair ? (ReadOnlySpan<bool>)[false, true] : [false])
                    {
                        int pcx = second ? ControllerTile.SecondPictureX(_dpi, cx - picR, picR) + picR : cx;
                        if (c.ImageOf(second) is var image and not 0) canvas.Image(image, pcx, cy, picR, cover: false, whole: true);
                        else canvas.Circle(pcx, cy, picR, Rgb(0x2E, 0x9E, 0x5B));
                    }
                }
            }

            if (hub is not null)
            {
                uint lit = Style(hub.Entry).Lit;
                canvas.Circle(center.X - l, center.Y - t, Px(NodeDip) + Px(2), lit);
                canvas.Circle(center.X - l, center.Y - t, Px(NodeDip), lit);
                hubPictured = DrawPicture(canvas, hub.Entry, center.X - l, center.Y - t, Px(NodeDip));
            }

            for (int i = 0; i < positions.Length; i++)
            {
                var entry = ring.Nodes[i].Entry;
                var style = Style(entry);
                bool selected = i == model.Selected;
                bool armed = i == model.Armed;
                int radius = Px(selected ? SelectedNodeDip : NodeDip);
                uint fill = armed || selected ? style.Lit : style.Idle; // a node waiting for confirmation keeps its own color
                uint rim = armed || selected ? fill : style.IdleRim;
                int x = positions[i].X - l, y = positions[i].Y - t;
                canvas.Circle(x, y, radius + Px(2), rim);
                canvas.Circle(x, y, radius, fill);
                if (!armed) pictured[i] = DrawPicture(canvas, entry, x, y, radius);

                // Status dot at the node's bottom right.
                if (!armed && entry?.Active?.Invoke() == true)
                {
                    int off = (int)(radius * 0.72);
                    canvas.Circle(x + off, y + off, Px(9), Rgb(0x1B, 0x1B, 0x1F));
                    canvas.Circle(x + off, y + off, Px(7), Rgb(0x2E, 0xCC, 0x40));
                }
            }

            if (text is not null)
            {
                int rad = (pillB - pillT) / 2;
                canvas.Circle(pillL + rad - l, pillT + rad - t, rad, NodeColor);
                canvas.Circle(pillR - rad - l, pillT + rad - t, rad, NodeColor);
                canvas.Rect(pillL + rad - l, pillT - t, pillR - rad - l, pillB - t, NodeColor);
            }
        }

        // ...then icons and text with GDI.
        if (hub is not null && !hubPictured)
            Gdi.Text(dc, fonts.Glyph, GlyphColor(Style(hub.Entry).Lit, true), hub.Glyph.ToString(),
                center.X - l - half2, center.Y - t - half2, center.X - l + half2, center.Y - t + half2, DT_CENTER | oneLine);

        if (drawBar)
            for (int i = 0; i < _controllers.Count; i++)
            {
                var (ex, ey) = EntryAt(i);
                int x = ex - l, cy = ey - t;
                var c = _controllers[i];
                foreach (bool second in c.Pair ? (ReadOnlySpan<bool>)[false, true] : [false])
                {
                    int px = second ? ControllerTile.SecondPictureX(_dpi, x, picR) : x;
                    if (c.ImageOf(second) == 0)
                        Gdi.Text(dc, fonts.GlyphSmall, Gdi.TextColor, ControllerTile.GamepadGlyph, px, cy - picR, px + picR * 2, cy + picR, DT_CENTER | oneLine);
                }
                ControllerTile.PaintBars(dc, _dpi, c, x + ControllerTile.PicturesWidth(_dpi, c, picR) + picGap, cy);
            }

        for (int i = 0; i < positions.Length; i++)
        {
            if (pictured[i]) continue;
            var node = ring.Nodes[i];
            string glyph = i == model.Armed ? "" : node.Glyph.ToString();
            int x = positions[i].X - l, y = positions[i].Y - t;
            uint color = !node.Enabled ? Gdi.Dim : i == model.Armed ? Gdi.TextColor : GlyphColor(Style(node.Entry).Lit, i == model.Selected);
            Gdi.Text(dc, fonts.Glyph, color, glyph, x - half2, y - half2, x + half2, y + half2, DT_CENTER | oneLine);
        }

        if (text is not null)
            Gdi.Text(dc, fonts.Detail, pillColor, text,
                pillL - l + Px(8), pillT - t, pillR - l - Px(8), pillB - t, DT_CENTER | oneLine | DT_END_ELLIPSIS);

        GdiFlush();

        // GDI text may clear the alpha byte of the pixels it touched (always on a drawn shape): make them opaque.
        uint* pixels = (uint*)bits;
        for (int i = 0, n = w * h; i < n; i++)
            if ((pixels[i] >> 24) == 0 && (pixels[i] & 0x00FFFFFF) != 0) pixels[i] |= 0xFF000000;

        var dst = new POINT { X = l, Y = t };
        var size = new SIZE { cx = w, cy = h };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, 0, &dst, &size, dc, &src, 0, &blend, ULW_ALPHA);

        SelectObject(dc, oldBitmap);
        DeleteObject(dib);
        DeleteDC(dc);
    }
}
