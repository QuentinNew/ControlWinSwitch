using ControlWin.Actions;
using ControlWin.App;
using ControlWin.Config;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>
/// The menu editor (mouse and keyboard only): the ring (radial) or the list on the left, the selected node's properties
/// on the right. Works on a draft (<see cref="MenuEditorModel"/>); Save hands it back, Cancel drops it.
/// </summary>
internal sealed unsafe class MenuEditorWindow : NativeWindow
{
    public const string ClassName = "ControlWin.MenuEditor";

    // Layout in DIPs.
    private const int WidthDip = 1180, HeightDip = 640, HeaderDip = 56, FooterDip = 56;
    private const int RingCenterXDip = 280, RingCenterYDip = 336, RingRadiusDip = 188, NodeDip = 36;
    private const int ColAX = 596, ColAW = 288, ColBX = 904, ColBW = 256;

    private static readonly uint Panel = Rgb(0x2A, 0x2A, 0x30);
    private static readonly uint PanelLight = Rgb(0x36, 0x36, 0x3E);
    private static readonly uint Rim = Rgb(0x4A, 0x4A, 0x54);
    private static readonly uint FieldFill = Rgb(0x1A, 0x1A, 0x1E);
    private static readonly uint Danger = Rgb(0xC0, 0x39, 0x2B);
    private static readonly uint Warn = Rgb(0xE8, 0x9B, 0x2E);

    private static readonly string[] Glyphs =
    [
        "E70F", "E710", "E711", "E712", "E713", "E715", "E716", "E717", "E71B", "E72B", "E73E", "E74E", "E767", "E768", "E769",
        "E774", "E77B", "E7E8", "E7F4", "E7F6", "E7FC", "E80F", "E81D", "E8A5", "E8B7", "E8D6", "E946", "E962", "E706", "E708",
        "E72C", "E72E", "E734", "E753", "E7C3", "E7C9", "E7EF", "E8AD", "E8F1", "EA8A",
    ];

    private enum Field { None, Name, ImagePath, Hex, Target, Args }

    private sealed class Hit
    {
        public RECT R;
        public Action<int, int>? Down, Drag, Up;
        public bool Hand = true;
        public bool IBeam;
        public string? Tag;
    }

    private readonly record struct TextOp(nint Font, uint Color, string Text, RECT Rc, uint Format);

    private MenuEditorModel _model = new(MenuLayouts.Radial, []);
    private List<MenuNodeConfig> _otherTree = [];   // the other layout's saved tree: what "Copy from" copies
    private List<MenuNodeConfig>? _pendingCopy;     // converted copy waiting for the user's yes
    private int _pendingDropped;
    private int _listScroll;                        // list view: first visible row
    private RECT _work;
    private uint _dpi = 96;

    private readonly List<Hit> _hits = [];
    private readonly List<TextOp> _texts = [];
    private Hit? _active;

    // Drag of a node.
    private MenuNodeConfig? _dragNode;
    private bool _dragging;
    private int _dragStartX, _dragStartY, _dragX, _dragY;
    private MenuNodeConfig? _lastClickNode;
    private long _lastClickTick;

    // Text editing.
    private Field _field;
    private readonly TextEditor _edit = new(260);

    private bool _steamOpen;      // the recently played Steam games list is open
    private List<SteamGame> _steamGames = [];
    private bool _pipetting;     // the eyedropper button is held
    private int _addSlot = -1;    // empty slot whose "add" list is open
    private string? _error;
    private float _hue = 210f, _sat = 0.8f, _val = 0.9f;
    private nint _previousForeground;

    // Painting state.
    private CircleCanvas? _canvas;
    private void* _bits;
    private int _w, _h;

    public MenuEditorWindow()
    {
        Create(ClassName, WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
    }

    /// <summary>Save pressed with a valid draft: the layout that was edited and its new root nodes.</summary>
    public event Action<string, List<MenuNodeConfig>>? Saved;

    public event Action? Cancelled;

    public bool IsShown => IsWindowVisible(Handle) != 0;

    /// <param name="layout">The layout to edit: <see cref="MenuLayouts.Radial"/> or <see cref="MenuLayouts.List"/>.</param>
    public void Show(string layout, IEnumerable<MenuNodeConfig> radialTree, IEnumerable<MenuNodeConfig> listTree)
    {
        Init(layout, radialTree, listTree);
        _addSlot = -1;
        _error = null;
        _dragNode = null;
        RadialIcons.Clear();
        Gdi.GetMonitorUnderCursor(out _work, out _dpi);

        // Shrink the whole editor on small screens instead of clipping it.
        double scale = _dpi / 96.0;
        double fit = Math.Min(_work.Width * 0.96 / (WidthDip * scale), _work.Height * 0.94 / (HeightDip * scale));
        if (fit < 1) _dpi = (uint)Math.Max(48, _dpi * fit);

        int width = Px(WidthDip), height = Px(HeightDip);
        int x = _work.Left + (_work.Width - width) / 2;
        int y = _work.Top + (_work.Height - height) / 2;
        SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW);

        _previousForeground = GetForegroundWindow();
        keybd_event(VK_MENU, 0, 0, 0); // lets this process take the foreground
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        SetForegroundWindow(Handle);
        SetFocus(Handle);
        InvalidateRect(Handle, null, 0);
    }

    private void Init(string layout, IEnumerable<MenuNodeConfig> radialTree, IEnumerable<MenuNodeConfig> listTree)
    {
        bool list = layout == MenuLayouts.List;
        _model = new MenuEditorModel(list ? MenuLayouts.List : MenuLayouts.Radial, list ? listTree : radialTree);
        _otherTree = (list ? radialTree : listTree).Select(n => n.Clone()).ToList();
        _pendingCopy = null;
        _listScroll = 0;
        _field = Field.None;
    }

    public void Hide()
    {
        ReleaseCapture();
        ShowWindow(Handle, SW_HIDE);
        if (_previousForeground != 0 && _previousForeground != Handle) SetForegroundWindow(_previousForeground);
        _previousForeground = 0;
        RadialIcons.Clear();
        K32EmptyWorkingSet(GetCurrentProcess());
    }

    private int Px(int dip) => (int)Math.Round(dip * _dpi / 96.0);

    private void Redraw() => InvalidateRect(Handle, null, 0);

    // ---- test hooks (no window is shown) ------------------------------------------------------

    internal MenuEditorModel DebugModel => _model;

    internal string DebugBuffer => _edit.Text;

    internal bool DebugEditing => _field != Field.None;

    internal void DebugInit(string layout, IEnumerable<MenuNodeConfig> radialTree, IEnumerable<MenuNodeConfig> listTree) =>
        Init(layout, radialTree, listTree);

    internal bool DebugCopyPending => _pendingCopy is not null;

    internal int DebugCopyDropped => _pendingDropped;

    /// <summary>Builds the hit list the way a paint does, into a throw-away bitmap.</summary>
    internal void DebugRender()
    {
        _w = Px(WidthDip);
        _h = Px(HeightDip);
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        var dc = CreateCompatibleDC(0);
        var dib = CreateDIBSection(dc, &info, 0, &bits, 0, 0);
        var old = SelectObject(dc, dib);
        SetBkMode(dc, TRANSPARENT);
        _bits = bits;
        using (_canvas = new CircleCanvas(bits, _w, _h)) Scene(dc);
        _canvas = null;
        SelectObject(dc, old);
        DeleteObject(dib);
        DeleteDC(dc);
    }

    /// <summary>A left click (down and up) at a fraction of the width of the hit tagged <paramref name="tag"/>.</summary>
    internal bool DebugClick(string tag, double fraction = 0.5)
    {
        var hit = _hits.FindLast(h => h.Tag == tag);
        if (hit is null) return false;
        int x = hit.R.Left + (int)((hit.R.Right - hit.R.Left) * fraction), y = (hit.R.Top + hit.R.Bottom) / 2;
        OnDown(x, y);
        var active = _active;
        _active = null;
        active?.Up?.Invoke(x, y);
        return true;
    }

    internal void DebugChar(char c) => OnChar(c);

    internal bool DebugKey(int virtualKey) => OnKey(virtualKey);

    internal int DebugCaret => _edit.Caret;

    internal string DebugSelection => _edit.Selected;

    /// <summary>A key press with Ctrl and/or Shift held, as <see cref="GetKeyState"/> would report them.</summary>
    internal bool DebugKey(int virtualKey, bool ctrl, bool shift) => OnKey(virtualKey, ctrl, shift);

    // ---- messages ----------------------------------------------------------------------------

    protected override nint WndProc(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                PaintWindow();
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_SETCURSOR when (lParam & 0xFFFF) == HTCLIENT:
            {
                POINT pt;
                GetCursorPos(&pt);
                ScreenToClient(Handle, &pt);
                var over = HitAt(pt.X, pt.Y);
                SetCursor(LoadCursorW(0, over?.IBeam == true ? IDC_IBEAM : over?.Hand == true ? IDC_HAND : IDC_ARROW));
                return 1;
            }
            case WM_LBUTTONDOWN:
                OnDown(LoWord(lParam), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_MOUSEMOVE:
                if (_active is not null) _active.Drag?.Invoke(LoWord(lParam), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_LBUTTONUP:
            {
                var active = _active;
                _active = null;
                ReleaseCapture();
                active?.Up?.Invoke(LoWord(lParam), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            }
            case WM_MOUSEWHEEL when _model.IsList:
                _listScroll -= Math.Sign((short)((wParam >> 16) & 0xFFFF)) * 2;
                Redraw();
                return 0;
            case WM_CHAR:
                OnChar((char)wParam);
                return 0;
            case 0x0100: // WM_KEYDOWN
                if (OnKey((int)wParam)) return 0;
                break;
            case WM_KILLFOCUS:
                CommitField();
                return 0;
            case WM_CLOSE:
                Cancelled?.Invoke();
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private Hit? HitAt(int x, int y)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var r = _hits[i].R;
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return _hits[i];
        }
        return null;
    }

    private void OnDown(int x, int y)
    {
        var hit = HitAt(x, y);

        // A click that is not on the field being edited ends the edit (the edit keeps what was typed).
        if (_field != Field.None && !(hit?.Tag is { } tag && tag == _field.ToString())) CommitField();

        if (hit is null) return;
        _active = hit;
        SetCapture(Handle);
        hit.Down?.Invoke(x, y);
    }

    private void OnChar(char c)
    {
        if (c == 27) // Esc
        {
            if (_pendingCopy is not null) { _pendingCopy = null; Redraw(); }
            else if (_field != Field.None) { _field = Field.None; Redraw(); }
            else if (_steamOpen) { _steamOpen = false; Redraw(); }
            else if (_addSlot >= 0) { _addSlot = -1; Redraw(); }
            return;
        }
        if (_field == Field.None) return;

        if (c == '\r') CommitField();
        else if (_edit.OnChar(c)) Redraw();
    }

    /// <summary>Arrow keys, Home, End, Delete and Insert while a field is being edited. Returns true when handled.</summary>
    private bool OnKey(int key) => OnKey(key, GetKeyState(VK_CONTROL) < 0, GetKeyState(VK_SHIFT) < 0);

    private bool OnKey(int key, bool ctrl, bool shift)
    {
        if (_field == Field.None || !_edit.OnKey(key, ctrl, shift)) return false;
        Redraw();
        return true;
    }

    // ---- editing actions ---------------------------------------------------------------------

    private void BeginEdit(Field field, string current)
    {
        _field = field;
        _edit.Begin(current); // typing continues the value; a click inside moves the caret
        Redraw();
    }

    private void CommitField()
    {
        if (_field == Field.None) return;
        var field = _field;
        _field = Field.None;

        var node = _model.Selected;
        string value = _edit.Text.Trim();
        if (node is not null)
        {
            switch (field)
            {
                case Field.Name:
                    node.Name = value.Length == 0 ? null : value;
                    break;
                case Field.ImagePath:
                    if (node.Icon is { } icon) icon.Path = value;
                    break;
                case Field.Hex:
                    if (value.Length == 0) node.Color = null;
                    else
                    {
                        if (!value.StartsWith('#')) value = "#" + value;
                        if (MenuRepair.IsColor(value)) SetColor(node, value.ToUpperInvariant());
                    }
                    break;
                case Field.Target:
                    node.Target = value;
                    break;
                case Field.Args:
                    node.Args = value.Length == 0 ? null : value;
                    break;
            }
        }
        Redraw();
    }

    private void SelectNode(MenuNodeConfig? node)
    {
        CommitField();
        _model.Select(node);
        if (node?.Color is { } hex && MenuRepair.ParseColor(hex) is { } colorref)
            (_hue, _sat, _val) = ToHsv((int)(colorref & 0xFF), (int)((colorref >> 8) & 0xFF), (int)((colorref >> 16) & 0xFF));
        Redraw();
    }

    private void SetColor(MenuNodeConfig node, string hex)
    {
        node.Color = hex;
        if (MenuRepair.ParseColor(hex) is { } colorref)
            (_hue, _sat, _val) = ToHsv((int)(colorref & 0xFF), (int)((colorref >> 8) & 0xFF), (int)((colorref >> 16) & 0xFF));
    }

    private void ApplyHsv(MenuNodeConfig node)
    {
        var (r, g, b) = FromHsv(_hue, _sat, _val);
        node.Color = $"#{r:X2}{g:X2}{b:X2}";
        Redraw();
    }

    private string? PickFile(string title, string filter)
    {
        CommitField();
        return FilePicker.Pick(Handle, title, filter);
    }

    private void Save()
    {
        CommitField();
        _error = _model.Validate();
        if (_error is not null)
        {
            Redraw();
            return;
        }
        Saved?.Invoke(_model.Layout, _model.Result());
    }

    // ---- geometry ----------------------------------------------------------------------------

    private (int X, int Y) RingCenter => (Px(RingCenterXDip), Px(RingCenterYDip));

    private float BackAngle => _model.Folder is { } f ? MathF.PI + f.Slot * MathF.Tau / 8 : 0f;

    private float SlotAngle(int slot) =>
        _model.Folder is null ? slot * MathF.Tau / 8 : BackAngle + (slot + 1) * MathF.Tau / 8;

    private (int X, int Y) SlotPosition(float angle)
    {
        var c = RingCenter;
        int r = Px(RingRadiusDip);
        return (c.X + (int)MathF.Round(r * MathF.Sin(angle)), c.Y - (int)MathF.Round(r * MathF.Cos(angle)));
    }

    private int SlotAt(int x, int y)
    {
        int best = -1;
        double bestDistance = Px(NodeDip) * 1.4;
        for (int s = 0; s < _model.Capacity; s++)
        {
            var p = SlotPosition(SlotAngle(s));
            double d = Math.Sqrt((double)(x - p.X) * (x - p.X) + (double)(y - p.Y) * (y - p.Y));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = s;
            }
        }
        return best;
    }

    // ---- painting ----------------------------------------------------------------------------

    private void PaintWindow()
    {
        PAINTSTRUCT ps;
        var target = BeginPaint(Handle, &ps);
        RECT client;
        GetClientRect(Handle, &client);
        _w = Math.Max(1, client.Width);
        _h = Math.Max(1, client.Height);

        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        var dc = CreateCompatibleDC(target);
        var dib = CreateDIBSection(dc, &info, 0, &bits, 0, 0);
        if (dib != 0)
        {
            var oldBitmap = SelectObject(dc, dib);
            SetBkMode(dc, TRANSPARENT);
            _bits = bits;
            try
            {
                using (_canvas = new CircleCanvas(bits, _w, _h)) Scene(dc);
            }
            finally
            {
                _canvas = null;
            }
            BitBlt(target, 0, 0, _w, _h, dc, 0, 0, SRCCOPY);
            SelectObject(dc, oldBitmap);
            DeleteObject(dib);
        }
        DeleteDC(dc);
        EndPaint(Handle, &ps);
    }

    private static int MeasureNow(string text, nint font) => TextEditor.Measure(text, font);

    private void Text(nint font, uint color, string text, int l, int t, int r, int b, uint format) =>
        _texts.Add(new TextOp(font, color, text, new RECT(l, t, r, b), format));

    private void FlushTexts(nint dc)
    {
        GdiFlush();
        foreach (var op in _texts) Gdi.Text(dc, op.Font, op.Color, op.Text, op.Rc.Left, op.Rc.Top, op.Rc.Right, op.Rc.Bottom, op.Format);
        _texts.Clear();
        GdiFlush();
    }

    private void RoundRect(int l, int t, int r, int b, int radius, uint color)
    {
        radius = Math.Min(radius, Math.Min((r - l) / 2, (b - t) / 2));
        _canvas!.Rect(l + radius, t, r - radius, b, color);
        _canvas.Rect(l, t + radius, l + radius, b - radius, color);
        _canvas.Rect(r - radius, t + radius, r, b - radius, color);
        _canvas.Circle(l + radius, t + radius, radius, color);
        _canvas.Circle(r - radius - 1, t + radius, radius, color);
        _canvas.Circle(l + radius, b - radius - 1, radius, color);
        _canvas.Circle(r - radius - 1, b - radius - 1, radius, color);
    }

    private Hit AddHit(int l, int t, int r, int b, Action<int, int>? down = null, bool hand = true)
    {
        var hit = new Hit { R = new RECT(l, t, r, b), Down = down, Hand = hand };
        _hits.Add(hit);
        return hit;
    }

    private void Button(int l, int t, int r, int b, string label, Action onClick, bool primary = false, bool enabled = true,
        bool active = false, uint? fill = null, string? tag = null)
    {
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        var fonts = Gdi.GetFonts(_dpi);
        uint color = fill ?? (primary ? Gdi.Selection : active ? Gdi.Selection : PanelLight);
        RoundRect(l, t, r, b, Px(8), enabled ? color : Panel);
        Text(fonts.Detail, enabled ? Gdi.TextColor : Gdi.Dim, label, l, t, r, b, DT_CENTER | oneLine | DT_END_ELLIPSIS);
        if (enabled) AddHit(l, t, r, b, (_, _) => { onClick(); Redraw(); }).Tag = tag;
    }

    private Hit TextField(Field field, string label, int l, int t, int r, string value, string placeholder = "")
    {
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        var fonts = Gdi.GetFonts(_dpi);
        int top = t + Px(18), bottom = top + Px(34);
        Text(fonts.Footer, Gdi.Dim, label, l, t, r, top, DT_LEFT | oneLine);

        bool editing = _field == field;
        RoundRect(l, top, r, bottom, Px(8), editing ? Gdi.Selection : Rim);
        RoundRect(l + Px(2), top + Px(2), r - Px(2), bottom - Px(2), Px(7), FieldFill);

        int textL = l + Px(10), textR = r - Px(8);
        bool empty = !editing && value.Length == 0;
        if (editing)
        {
            // The value scrolls so that the caret stays visible; text past the right edge is clipped.
            _edit.ScrollToCaret(fonts.Detail, textR - textL - Px(2));
            int lineT = top + Px(8), lineB = bottom - Px(8);
            if (_edit.HasSelection)
                _canvas!.Rect(_edit.XAt(_edit.SelStart, textL, textR, fonts.Detail), lineT, _edit.XAt(_edit.SelEnd, textL, textR, fonts.Detail), lineB, Gdi.Selection);
            int caretX = _edit.XAt(_edit.Caret, textL, textR, fonts.Detail);
            _canvas!.Rect(caretX, lineT, caretX + Math.Max(1, Px(1)), lineB, Gdi.TextColor);
            Text(fonts.Detail, Gdi.TextColor, _edit.Shown, textL, top, textR, bottom, DT_LEFT | oneLine);
        }
        else
        {
            Text(fonts.Detail, empty ? Gdi.Dim : Gdi.TextColor, empty ? placeholder : value, textL, top, textR, bottom,
                DT_LEFT | oneLine | DT_END_ELLIPSIS);
        }

        var detailFont = fonts.Detail;
        var hit = AddHit(l, top, r, bottom, (x, _) =>
        {
            if (_field != field) BeginEdit(field, value);
            _edit.MouseDown(x, textL, detailFont, Px(4));
            Redraw();
        });
        hit.Drag = (x, _) =>
        {
            if (_field != field) return;
            _edit.MouseDrag(x, textL, detailFont);
            Redraw();
        };
        hit.Hand = false;
        hit.IBeam = true;
        hit.Tag = field.ToString();
        return hit;
    }

    private void Scene(nint dc)
    {
        _hits.Clear();
        _texts.Clear();
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int w = _w, h = _h;

        _canvas!.Rect(0, 0, w, h, Gdi.Background);

        // Header: title, breadcrumb, cancel.
        Text(fonts.Title, Gdi.TextColor, _model.IsList ? "List menu editor" : "Radial menu editor", Px(24), 0, Px(560), Px(HeaderDip), DT_LEFT | oneLine);
        Text(fonts.Detail, Gdi.Dim,
            _model.Folder is { } folder ? $"ControlWin  >  {folder.Name ?? "Folder"}" : "ControlWin",
            Px(330), 0, Px(580), Px(HeaderDip), DT_LEFT | oneLine | DT_END_ELLIPSIS);

        if (_model.IsList) DrawList();
        else DrawRing();
        DrawProperties();
        DrawFooter();

        // Texts of the main layer, then the popup on top of it.
        FlushTexts(dc);
        if (_addSlot >= 0)
        {
            DrawAddPopup();
            FlushTexts(dc);
        }
        if (_steamOpen)
        {
            DrawSteamPopup();
            FlushTexts(dc);
        }
        if (_pendingCopy is not null)
        {
            DrawCopyPopup();
            FlushTexts(dc);
        }
    }

    private void DrawRing()
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int radius = Px(NodeDip);
        var center = RingCenter;

        // Faint guide ring.
        _canvas!.Circle(center.X, center.Y, Px(RingRadiusDip) + Px(1), Panel);
        _canvas.Circle(center.X, center.Y, Px(RingRadiusDip) - Px(1), Gdi.Background);

        string hint = _model.Folder is null
            ? "Drag a node to a slot to move it.\nClick + to add a node."
            : "Drag a node to a slot to move it.\nClick the arrow to go back.";
        Text(fonts.Footer, Gdi.Dim, hint, center.X - Px(120), center.Y - Px(30), center.X + Px(120), center.Y + Px(30),
            DT_CENTER | DT_WORDBREAK);

        // Back marker in a folder.
        if (_model.Folder is not null)
        {
            var p = SlotPosition(BackAngle);
            _canvas.Circle(p.X, p.Y, radius, Rim);
            _canvas.Circle(p.X, p.Y, radius - Px(2), Rgb(0x37, 0x47, 0x4F));
            Text(fonts.Glyph, Gdi.TextColor, "", p.X - radius, p.Y - radius, p.X + radius, p.Y + radius, DT_CENTER | oneLine);
            Text(fonts.Footer, Gdi.Dim, "Back", p.X - radius, p.Y + radius + Px(2), p.X + radius, p.Y + radius + Px(22), DT_CENTER | oneLine);
            AddHit(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius, (_, _) => { CommitField(); _model.Up(); Redraw(); });
        }

        var pictures = new List<(MenuNodeConfig Node, int X, int Y)>();
        for (int slot = 0; slot < _model.Capacity; slot++)
        {
            var p = SlotPosition(SlotAngle(slot));
            var node = _model.At(slot);
            bool dropTarget = _dragging && _dragNode is not null && SlotAt(_dragX, _dragY) == slot;

            if (node is null || ReferenceEquals(node, _dragNode) && _dragging)
            {
                // Empty slot: a hollow circle with a plus.
                _canvas.Circle(p.X, p.Y, radius, dropTarget ? Gdi.Selection : Rim);
                _canvas.Circle(p.X, p.Y, radius - Px(2), Gdi.Background);
                Text(fonts.Glyph, dropTarget ? Gdi.TextColor : Gdi.Dim, "", p.X - radius, p.Y - radius, p.X + radius, p.Y + radius, DT_CENTER | oneLine);
                if (node is null)
                {
                    int s = slot;
                    AddHit(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius, (_, _) =>
                    {
                        CommitField();
                        _addSlot = s;
                        Redraw();
                    });
                }
                continue;
            }

            DrawNode(node, p.X, p.Y, radius, ReferenceEquals(node, _model.Selected), dropTarget, pictures);
            var captured = node;
            var hit = AddHit(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius);
            hit.Down = (x, y) => BeginDrag(captured, x, y);
            hit.Drag = ContinueDrag;
            hit.Up = (x, y) =>
            {
                var dragged = _dragNode;
                bool moved = _dragging;
                _dragNode = null;
                _dragging = false;
                if (dragged is null) return;

                if (moved)
                {
                    int target = SlotAt(x, y);
                    if (target >= 0) _model.Move(dragged, target);
                }
                else TryEnterOnDoubleClick(dragged);
                RememberClick(dragged, moved);
                Redraw();
            };
        }

        // The node being dragged follows the cursor, on top of everything.
        if (_dragging && _dragNode is not null)
            DrawNode(_dragNode, _dragX, _dragY, radius, true, false, pictures);
    }

    private void DrawNode(MenuNodeConfig node, int x, int y, int radius, bool selected, bool dropTarget,
        List<(MenuNodeConfig, int, int)> pictures)
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        uint? colorref = MenuRepair.ParseColor(node.Color);
        var style = RadialOverlay.Style(colorref);

        _canvas!.Circle(x, y, radius + Px(selected ? 5 : 2), selected ? Gdi.TextColor : (dropTarget ? Gdi.Selection : style.IdleRim));
        _canvas.Circle(x, y, radius, selected ? style.Lit : style.Idle);

        var icon = node.Icon ?? (node.Type == MenuNodeTypes.Shortcut ? new MenuIconConfig { Kind = "auto" } : null);
        nint image = RadialIcons.Get(icon, node.Target, out bool cover);
        if (image != 0)
        {
            _canvas.Image(image, x, y, radius, cover);
        }
        else
        {
            char glyph = MenuTree.GlyphOf(node.Icon) ?? MenuTree.DefaultGlyph(node.Type);
            Text(fonts.Glyph, RadialOverlay.GlyphColor(style.Lit, selected), glyph.ToString(),
                x - radius, y - radius, x + radius, y + radius, DT_CENTER | oneLine);
        }

        string name = node.Name ?? (node.Type == MenuNodeTypes.Shortcut && !string.IsNullOrEmpty(node.Target)
            ? node.Target! : MenuTree.DefaultLabel(node.Type));
        Text(fonts.Footer, Gdi.Dim, name, x - Px(54), y + radius + Px(4), x + Px(54), y + radius + Px(24), DT_CENTER | oneLine | DT_END_ELLIPSIS);
    }

    // ---- shared by the ring and the list ------------------------------------------------------

    private void BeginDrag(MenuNodeConfig node, int x, int y)
    {
        _dragNode = node;
        _dragging = false;
        _dragStartX = _dragX = x;
        _dragStartY = _dragY = y;
        SelectNode(node);
    }

    private void ContinueDrag(int x, int y)
    {
        _dragX = x;
        _dragY = y;
        if (!_dragging && Math.Abs(x - _dragStartX) + Math.Abs(y - _dragStartY) > Px(6)) _dragging = true;
        if (_dragging) Redraw();
    }

    /// <summary>Double click on a folder of the root goes into it.</summary>
    private void TryEnterOnDoubleClick(MenuNodeConfig node)
    {
        if (node.Type != MenuNodeTypes.Folder || !ReferenceEquals(_lastClickNode, node)
            || Environment.TickCount64 - _lastClickTick >= 450) return;
        CommitField();
        if (_model.Enter(node)) _listScroll = 0;
    }

    private void RememberClick(MenuNodeConfig node, bool moved)
    {
        _lastClickNode = moved ? null : node;
        _lastClickTick = Environment.TickCount64;
    }

    // ---- list view ---------------------------------------------------------------------------

    private const int ListLeftDip = 24, ListRightDip = 560, RowDip = 46;

    private int ListTop => Px(HeaderDip + 12) + Px(46);
    private int ListBottom => Px(HeightDip - FooterDip) - Px(8);

    /// <summary>Rows that fit under the list's top bar (the Back row takes one inside a folder).</summary>
    private int VisibleRows => Math.Max(1, (ListBottom - ListTop) / Px(RowDip) - (_model.Folder is null ? 0 : 1));

    private int FirstRowTop => ListTop + (_model.Folder is null ? 0 : Px(RowDip));

    private int ClampScroll()
    {
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _model.Nodes.Count - VisibleRows));
        return _listScroll;
    }

    /// <summary>Where a dragged node would land: the index to insert at, or the root folder to drop it into.</summary>
    private (int Index, MenuNodeConfig? Into) ListDrop(int y)
    {
        var nodes = _model.Nodes;
        double position = (y - FirstRowTop) / (double)Px(RowDip);
        int row = Math.Clamp((int)Math.Floor(position) + _listScroll, 0, nodes.Count);
        if (row < nodes.Count)
        {
            var over = nodes[row];
            double within = position - Math.Floor(position);
            if (_model.Folder is null && over.Type == MenuNodeTypes.Folder && _dragNode is { } dragged
                && dragged.Type != MenuNodeTypes.Folder && within is > 0.25 and < 0.75)
                return (row, over);
            if (within >= 0.5) row++;
        }
        return (Math.Min(row, nodes.Count), null);
    }

    private void DrawList()
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int l = Px(ListLeftDip), r = Px(ListRightDip), rowH = Px(RowDip);
        var nodes = _model.Nodes;
        int visible = VisibleRows;
        int scroll = ClampScroll();

        // Top bar: hint and the Add button.
        int barTop = Px(HeaderDip + 12);
        Text(fonts.Footer, Gdi.Dim, "Drag a row to move it.\nDrop it on a folder to put it inside.", l, barTop, r - Px(140), barTop + Px(40),
            DT_LEFT | DT_WORDBREAK);
        Button(r - Px(130), barTop, r, barTop + Px(36), "+ Add a node", () =>
        {
            CommitField();
            _addSlot = nodes.Count;
        }, primary: true);

        // Back row in a folder.
        if (_model.Folder is not null)
        {
            int t = ListTop;
            RoundRect(l, t + Px(2), r, t + rowH - Px(2), Px(10), Rgb(0x37, 0x47, 0x4F));
            Text(fonts.Glyph, Gdi.TextColor, "", l, t, l + Px(52), t + rowH, DT_CENTER | oneLine);
            Text(fonts.Detail, Gdi.TextColor, "Back", l + Px(56), t, r - Px(12), t + rowH, DT_LEFT | oneLine);
            AddHit(l, t, r, t + rowH, (_, _) => { CommitField(); _model.Up(); _listScroll = 0; Redraw(); });
        }

        (int Index, MenuNodeConfig? Into) drop = _dragging && _dragNode is not null ? ListDrop(_dragY) : (-1, null);
        for (int i = 0; i < visible && scroll + i < nodes.Count; i++)
        {
            var node = nodes[scroll + i];
            int t = FirstRowTop + i * rowH;
            bool hidden = _dragging && ReferenceEquals(node, _dragNode);
            DrawListRow(node, l, t, r, t + rowH, ReferenceEquals(node, _model.Selected), ReferenceEquals(drop.Into, node), hidden);

            var captured = node;
            var hit = AddHit(l, t, r, t + rowH);
            hit.Tag = $"row:{scroll + i}";
            hit.Down = (x, y) => BeginDrag(captured, x, y);
            hit.Drag = ContinueDrag;
            hit.Up = (x, y) =>
            {
                var dragged = _dragNode;
                bool moved = _dragging;
                _dragNode = null;
                _dragging = false;
                if (dragged is null) return;

                if (moved)
                {
                    var (index, into) = ListDrop(y);
                    if (into is not null) _model.MoveInto(dragged, into);
                    else
                    {
                        int from = _model.Nodes.IndexOf(dragged);
                        _model.MoveTo(dragged, index > from ? index - 1 : index);
                    }
                }
                else TryEnterOnDoubleClick(dragged);
                RememberClick(dragged, moved);
                Redraw();
            };
        }

        // Drop indicator between rows.
        if (_dragging && drop.Into is null && drop.Index >= 0)
        {
            int rowsAbove = Math.Clamp(drop.Index - scroll, 0, visible);
            int y = FirstRowTop + rowsAbove * rowH;
            _canvas!.Rect(l, y - Px(1), r, y + Px(2), Gdi.Selection);
        }

        // Scroll thumb.
        if (nodes.Count > visible)
        {
            int trackH = visible * rowH;
            int thumbH = Math.Max(Px(24), trackH * visible / nodes.Count);
            int thumbTop = FirstRowTop + (trackH - thumbH) * scroll / (nodes.Count - visible);
            _canvas!.Rect(r + Px(6), thumbTop, r + Px(9), thumbTop + thumbH, Rim);
        }
        else if (nodes.Count == 0)
        {
            Text(fonts.Detail, Gdi.Dim, "Empty. Add a node.", l, FirstRowTop, r, FirstRowTop + rowH, DT_CENTER | oneLine);
        }

        // The row being dragged follows the cursor, on top of everything.
        if (_dragging && _dragNode is not null)
        {
            int top = Math.Clamp(_dragY - rowH / 2, ListTop, ListBottom - rowH);
            DrawListRow(_dragNode, l, top, r, top + rowH, true, false, false);
        }
    }

    private void DrawListRow(MenuNodeConfig node, int l, int t, int r, int b, bool selected, bool dropInto, bool hidden)
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        var style = RadialOverlay.Style(MenuRepair.ParseColor(node.Color));

        if (hidden)
        {
            RoundRect(l, t + Px(2), r, b - Px(2), Px(10), Rim);
            RoundRect(l + Px(2), t + Px(4), r - Px(2), b - Px(4), Px(9), Gdi.Background);
            return;
        }

        uint fill = dropInto ? Gdi.Selection : selected ? style.Lit : Panel;
        if (selected) RoundRect(l - Px(2), t, r + Px(2), b, Px(11), Gdi.TextColor);
        RoundRect(l, t + Px(2), r, b - Px(2), Px(10), fill);

        int iconR = Px(15), iconX = l + Px(26), iconY = (t + b) / 2;
        var icon = node.Icon ?? (node.Type == MenuNodeTypes.Shortcut ? new MenuIconConfig { Kind = "auto" } : null);
        nint image = RadialIcons.Get(icon, node.Target, out bool cover);
        if (image != 0)
        {
            _canvas!.Image(image, iconX, iconY, iconR, cover);
        }
        else
        {
            char glyph = MenuTree.GlyphOf(node.Icon) ?? MenuTree.DefaultGlyph(node.Type);
            Text(fonts.Glyph, selected ? RadialOverlay.GlyphColor(style.Lit, true) : Gdi.TextColor, glyph.ToString(),
                iconX - iconR, iconY - iconR, iconX + iconR, iconY + iconR, DT_CENTER | oneLine);
        }

        string name = node.Name ?? (node.Type == MenuNodeTypes.Shortcut && !string.IsNullOrEmpty(node.Target)
            ? node.Target! : MenuTree.DefaultLabel(node.Type));
        uint textColor = selected ? RadialOverlay.GlyphColor(style.Lit, true) : Gdi.TextColor;
        int textRight = r - Px(12);
        if (node.Type == MenuNodeTypes.Folder)
        {
            Text(fonts.GlyphSmall, textColor, "", r - Px(32), t, r - Px(12), b, DT_CENTER | oneLine);
            Text(fonts.Footer, selected ? textColor : Gdi.Dim, $"{node.Children?.Count ?? 0}", r - Px(80), t, r - Px(36), b, DT_RIGHT | oneLine);
            textRight = r - Px(84);
        }
        Text(fonts.Detail, textColor, name, l + Px(52), t, textRight, b, DT_LEFT | oneLine | DT_END_ELLIPSIS);
    }

    // ---- copy from the other menu ------------------------------------------------------------

    private void AskCopy()
    {
        CommitField();
        _pendingCopy = MenuEditorModel.Convert(_otherTree, _model.Layout, out _pendingDropped);
    }

    private void DrawCopyPopup()
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        string mine = _model.IsList ? "list" : "radial", theirs = _model.IsList ? "radial" : "list";

        // Everything behind the popup: a click anywhere else cancels.
        AddHit(0, 0, _w, _h, (_, _) =>
        {
            _pendingCopy = null;
            Redraw();
        }, hand: false);

        int width = Px(460), height = Px(_pendingDropped > 0 ? 250 : 210);
        int l = (_w - width) / 2, t = (_h - height) / 2;
        RoundRect(l - Px(2), t - Px(2), l + width + Px(2), t + height + Px(2), Px(14), Rim);
        RoundRect(l, t, l + width, t + height, Px(12), Panel);
        AddHit(l, t, l + width, t + height, null, hand: false);

        Text(fonts.Label, Gdi.TextColor, $"Copy from {theirs} menu?", l, t + Px(12), l + width, t + Px(46), DT_CENTER | oneLine);
        string message = $"This will replace all of your current {mine} menu layout with a copy of your {theirs} menu layout. "
            + "Nothing is saved until you press Save.";
        if (_pendingDropped > 0)
            message += $"\n\n{_pendingDropped} node{(_pendingDropped == 1 ? " does" : "s do")} not fit on the ring and will be dropped.";
        Text(fonts.Detail, _pendingDropped > 0 ? Warn : Gdi.Dim, message, l + Px(24), t + Px(54), l + width - Px(24), t + height - Px(64),
            DT_CENTER | DT_WORDBREAK);

        int by = t + height - Px(52);
        Button(l + Px(24), by, l + width / 2 - Px(6), by + Px(36), "Replace", () =>
        {
            if (_pendingCopy is { } tree) _model.Replace(tree);
            _pendingCopy = null;
            _listScroll = 0;
            _error = null;
        }, fill: Danger, tag: "copy-yes");
        Button(l + width / 2 + Px(6), by, l + width - Px(24), by + Px(36), "Cancel", () => _pendingCopy = null, tag: "copy-no");
    }

    private void DrawProperties()
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int panelL = Px(ColAX - 16), panelT = Px(HeaderDip + 12), panelR = Px(ColBX + ColBW + 12), panelB = Px(HeightDip - FooterDip);
        RoundRect(panelL, panelT, panelR, panelB, Px(14), Panel);

        var node = _model.Selected;
        if (node is null)
        {
            Text(fonts.Label, Gdi.Dim, "Select a node to edit it", panelL, panelT, panelR, panelB, DT_CENTER | oneLine);
            return;
        }

        int ax = Px(ColAX), aw = Px(ColAW), bx = Px(ColBX), bw = Px(ColBW);
        int y = Px(HeaderDip + 24);
        bool isShortcut = node.Type == MenuNodeTypes.Shortcut;
        bool isFolder = node.Type == MenuNodeTypes.Folder;

        // ---- column A: name, icon ----
        Text(fonts.Label, Gdi.TextColor, MenuTree.DefaultLabel(node.Type), ax, y, ax + aw, y + Px(28), DT_LEFT | oneLine);
        y += Px(36);

        TextField(Field.Name, "Name", ax, y, ax + aw, node.Name ?? "", MenuTree.DefaultLabel(node.Type));
        y += Px(62);

        Text(fonts.Footer, Gdi.Dim, "Icon", ax, y, ax + aw, y + Px(18), DT_LEFT | oneLine);
        y += Px(20);
        string kind = node.Icon?.Kind ?? "default";
        int bwid = (aw - Px(12)) / 4;
        void IconButton(int index, string label, string value, bool enabled, Action set)
        {
            int l = ax + index * (bwid + Px(4));
            Button(l, y, l + bwid, y + Px(32), label, set, active: kind == value, enabled: enabled);
        }
        IconButton(0, "Default", "default", true, () => node.Icon = null);
        IconButton(1, "Glyph", "glyph", true, () =>
            node.Icon = new MenuIconConfig { Kind = "glyph", Glyph = ((int)(MenuTree.GlyphOf(node.Icon) ?? MenuTree.DefaultGlyph(node.Type))).ToString("X4") });
        IconButton(2, "Program", "auto", isShortcut, () => node.Icon = new MenuIconConfig { Kind = "auto" });
        IconButton(3, "Image", "image", true, () => node.Icon = new MenuIconConfig { Kind = "image", Path = "" });
        y += Px(42);

        if (kind == "glyph")
        {
            const int cols = 8;
            int cell = (aw - Px(4)) / cols;
            for (int i = 0; i < Glyphs.Length; i++)
            {
                int l = ax + (i % cols) * cell, t = y + (i / cols) * cell;
                bool current = string.Equals(node.Icon?.Glyph, Glyphs[i], StringComparison.OrdinalIgnoreCase);
                RoundRect(l + Px(1), t + Px(1), l + cell - Px(1), t + cell - Px(1), Px(6), current ? Gdi.Selection : PanelLight);
                Text(fonts.Glyph, Gdi.TextColor, ((char)Convert.ToInt32(Glyphs[i], 16)).ToString(), l, t, l + cell, t + cell, DT_CENTER | oneLine);
                string code = Glyphs[i];
                AddHit(l, t, l + cell, t + cell, (_, _) =>
                {
                    node.Icon = new MenuIconConfig { Kind = "glyph", Glyph = code };
                    Redraw();
                });
            }
        }
        else if (kind == "image")
        {
            int browseW = Px(74);
            TextField(Field.ImagePath, "Image file (.png, .ico)", ax, y, ax + aw - browseW - Px(6), node.Icon?.Path ?? "", "Path to a picture");
            Button(ax + aw - browseW, y + Px(18), ax + aw, y + Px(52), "Browse...", () =>
            {
                var file = PickFile("Choose a picture", "Pictures\0*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif\0All files\0*.*\0\0");
                if (file is not null && node.Icon is not null) node.Icon.Path = file;
            });
        }
        else if (kind == "auto")
        {
            Text(fonts.Footer, Gdi.Dim, "Uses the program's own icon.", ax, y, ax + aw, y + Px(24), DT_LEFT | oneLine);
        }

        // ---- column B: color, shortcut fields ----
        int by = Px(HeaderDip + 24);
        Text(fonts.Footer, Gdi.Dim, "Highlight color (idle is a darker shade)", bx, by, bx + bw, by + Px(18), DT_LEFT | oneLine);
        by += Px(22);
        int svW = Px(200), svH = Px(150), hueW = Px(26);
        DrawColorPicker(node, bx, by, svW, svH, hueW);
        by += svH + Px(10);

        int hexW = Px(112), pipW = Px(46), gap = Px(6);
        TextField(Field.Hex, "Hex", bx, by, bx + hexW, node.Color ?? "", "default");
        Button(bx + hexW + gap, by + Px(18), bx + bw - pipW - gap, by + Px(52), "Default", () => node.Color = null);
        DrawPipette(node, bx + bw - pipW, by, bx + bw, by + Px(52));
        by += Px(66);

        if (isFolder && _model.Folder is null)
        {
            Button(bx, by, bx + bw, by + Px(36), "Open folder", () => _model.Enter(node), primary: true);
            by += Px(46);
        }

        if (isShortcut)
        {
            int browseW = Px(74);
            TextField(Field.Target, "Program, file or link", bx, by, bx + bw - browseW - Px(6), node.Target ?? "", "notepad.exe or steam://...");
            Button(bx + bw - browseW, by + Px(18), bx + bw, by + Px(52), "Browse...", () =>
            {
                var file = PickFile("Choose a program or file", "All files\0*.*\0\0");
                if (file is not null) node.Target = file;
            });
            by += Px(62);
            TextField(Field.Args, "Arguments", bx, by, bx + bw, node.Args ?? "");
            by += Px(62);
            Button(bx, by, bx + bw, by + Px(36), "Find a recently played Steam game", OpenSteamPicker);
            by += Px(46);
            bool admin = node.RunAsAdmin == true;
            Button(bx, by, bx + bw, by + Px(36), admin ? "Run as administrator: On" : "Run as administrator: Off",
                () => node.RunAsAdmin = admin ? null : true, active: admin);
        }

        // Delete.
        int footerTop = Px(HeightDip - FooterDip);
        Button(ax, footerTop - Px(52), ax + Px(130), footerTop - Px(16), "Delete node", () => _model.Delete(node), fill: Danger);
    }

    /// <summary>
    /// Eyedropper: press it and drag onto any pixel of the screen (the mouse stays captured while the button is
    /// held); the node's color follows the pixel under the cursor and is kept on release.
    /// </summary>
    private void DrawPipette(MenuNodeConfig node, int l, int t, int r, int b)
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int top = t + Px(18);
        Text(fonts.Footer, Gdi.Dim, "Pick", l, t, r, top, DT_CENTER | oneLine);
        RoundRect(l, top, r, b, Px(8), _pipetting ? Gdi.Selection : PanelLight);
        Text(fonts.Glyph, Gdi.TextColor, "", l, top, r, b, DT_CENTER | oneLine);

        var hit = AddHit(l, top, r, b);
        hit.Down = (_, _) =>
        {
            CommitField();
            _pipetting = true;
            SampleScreen(node);
        };
        hit.Drag = (_, _) => SampleScreen(node);
        hit.Up = (_, _) =>
        {
            _pipetting = false;
            Redraw();
        };
    }

    private void SampleScreen(MenuNodeConfig node)
    {
        POINT pt;
        GetCursorPos(&pt);
        var screen = GetDC(0);
        uint colorref = GetPixel(screen, pt.X, pt.Y);
        ReleaseDC(0, screen);
        if (colorref == 0xFFFFFFFF) return; // CLR_INVALID: outside every monitor

        SetColor(node, $"#{colorref & 0xFF:X2}{(colorref >> 8) & 0xFF:X2}{(colorref >> 16) & 0xFF:X2}");
        Redraw();
    }

    private void DrawColorPicker(MenuNodeConfig node, int l, int t, int w, int h, int hueW)
    {
        var uBits = (uint*)_bits;
        int hueL = l + w + Px(10);

        for (int y = 0; y < h; y++)
        {
            float v = 1f - y / (float)(h - 1);
            float hue = y / (float)(h - 1) * 360f;
            var (hr, hg, hb) = FromHsv(hue, 1f, 1f);
            uint hueColor = 0xFF000000u | (uint)(hr << 16) | (uint)(hg << 8) | (uint)hb;
            int row = t + y;
            if (row < 0 || row >= _h) continue;

            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = FromHsv(_hue, x / (float)(w - 1), v);
                int col = l + x;
                if (col >= 0 && col < _w) uBits[row * _w + col] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
            }
            for (int x = 0; x < hueW; x++)
            {
                int col = hueL + x;
                if (col >= 0 && col < _w) uBits[row * _w + col] = hueColor;
            }
        }

        // Markers.
        int mx = l + (int)(_sat * (w - 1)), my = t + (int)((1f - _val) * (h - 1));
        _canvas!.Circle(mx, my, Px(7), Gdi.TextColor);
        _canvas.Circle(mx, my, Px(5), Rgb(0x10, 0x10, 0x10));
        int hy = t + (int)(_hue / 360f * (h - 1));
        _canvas.Rect(hueL - Px(3), hy - Px(2), hueL + hueW + Px(3), hy + Px(2), Gdi.TextColor);

        var sv = AddHit(l, t, l + w, t + h, hand: true);
        void PickSv(int x, int y)
        {
            _sat = Math.Clamp((x - l) / (float)(w - 1), 0f, 1f);
            _val = 1f - Math.Clamp((y - t) / (float)(h - 1), 0f, 1f);
            ApplyHsv(node);
        }
        sv.Down = PickSv;
        sv.Drag = PickSv;

        var hue2 = AddHit(hueL, t, hueL + hueW, t + h, hand: true);
        void PickHue(int x, int y)
        {
            _hue = Math.Clamp((y - t) / (float)(h - 1), 0f, 1f) * 360f;
            ApplyHsv(node);
        }
        hue2.Down = PickHue;
        hue2.Drag = PickHue;
    }

    private void DrawFooter()
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int top = Px(HeightDip - FooterDip) + Px(10), bottom = Px(HeightDip) - Px(10);
        int right = Px(WidthDip) - Px(24);

        Button(right - Px(110), top, right, bottom, "Save", Save, primary: true);
        Button(right - Px(230), top, right - Px(120), bottom, "Cancel", () => Cancelled?.Invoke());
        Button(Px(24), top, Px(24) + Px(170), bottom, "Reset to default", () =>
        {
            CommitField();
            _model.Reset();
            _error = null;
        });
        Button(Px(204), top, Px(204) + Px(220), bottom, _model.IsList ? "Copy from radial menu" : "Copy from list menu", AskCopy, tag: "copy");

        if (_error is not null)
            Text(fonts.Detail, Warn, _error, Px(444), top, right - Px(246), bottom, DT_LEFT | oneLine | DT_END_ELLIPSIS);
    }

    private void OpenSteamPicker()
    {
        CommitField();
        _steamGames = SteamLibrary.Recent(10);
        _steamOpen = true;
    }

    /// <summary>Points the selected shortcut at a Steam game: its link, its name and its cover.</summary>
    internal void ApplySteamGame(SteamGame game)
    {
        if (_model.Selected is not { } node) return;

        node.Target = game.LaunchTarget;
        node.Name = game.Name;
        if (game.CoverPath is not null) node.Icon = new MenuIconConfig { Kind = "image", Path = game.CoverPath };
    }

    private void DrawSteamPopup()
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;

        AddHit(0, 0, _w, _h, (_, _) =>
        {
            _steamOpen = false;
            Redraw();
        }, hand: false);

        int rows = Math.Max(1, _steamGames.Count);
        int rowH = Px(36), width = Px(420), height = rowH * rows + Px(52);
        var center = RingCenter;
        int l = center.X - width / 2, t = Math.Max(Px(HeaderDip + 8), center.Y - height / 2);

        RoundRect(l - Px(2), t - Px(2), l + width + Px(2), t + height + Px(2), Px(14), Rim);
        RoundRect(l, t, l + width, t + height, Px(12), Panel);
        Text(fonts.Detail, Gdi.Dim, "Recently played on Steam", l, t + Px(6), l + width, t + Px(40), DT_CENTER | oneLine);
        AddHit(l, t, l + width, t + height, null, hand: false);

        if (_steamGames.Count == 0)
        {
            Text(fonts.Detail, Gdi.Dim, "No recently played Steam games found", l, t + Px(44), l + width, t + Px(44) + rowH, DT_CENTER | oneLine);
            return;
        }

        for (int i = 0; i < _steamGames.Count; i++)
        {
            var game = _steamGames[i];
            int rt = t + Px(46) + i * rowH;
            RoundRect(l + Px(8), rt, l + width - Px(8), rt + rowH - Px(4), Px(8), PanelLight);
            Text(fonts.Detail, Gdi.TextColor, game.Name, l + Px(20), rt, l + width - Px(20), rt + rowH - Px(4), DT_LEFT | oneLine | DT_END_ELLIPSIS);
            AddHit(l + Px(8), rt, l + width - Px(8), rt + rowH - Px(4), (_, _) =>
            {
                _steamOpen = false;
                ApplySteamGame(game);
                Redraw();
            });
        }
    }

    private void DrawAddPopup()
    {
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;

        // Everything behind the popup: a click anywhere else closes it.
        AddHit(0, 0, _w, _h, (_, _) =>
        {
            _addSlot = -1;
            Redraw();
        }, hand: false);

        var types = NodeKinds.All.Where(t => MenuEditorModel.CanHold(t.Type, _model.Folder is not null)).ToArray();
        int rowH = Px(34), width = Px(220), height = rowH * types.Length + Px(48);
        var center = RingCenter;
        int l = center.X - width / 2, t = center.Y - height / 2;

        RoundRect(l - Px(2), t - Px(2), l + width + Px(2), t + height + Px(2), Px(14), Rim);
        RoundRect(l, t, l + width, t + height, Px(12), Panel);
        Text(fonts.Detail, Gdi.Dim, "Add a node", l, t + Px(6), l + width, t + Px(38), DT_CENTER | oneLine);
        AddHit(l, t, l + width, t + height, null, hand: false); // swallow clicks on the popup's own background

        for (int i = 0; i < types.Length; i++)
        {
            int rt = t + Px(42) + i * rowH;
            string type = types[i].Type;
            RoundRect(l + Px(8), rt, l + width - Px(8), rt + rowH - Px(4), Px(8), PanelLight);
            Text(fonts.Detail, Gdi.TextColor, types[i].Label, l + Px(8), rt, l + width - Px(8), rt + rowH - Px(4), DT_CENTER | oneLine);
            AddHit(l + Px(8), rt, l + width - Px(8), rt + rowH - Px(4), (_, _) =>
            {
                int slot = _addSlot;
                _addSlot = -1;
                var added = _model.Add(type, slot);
                if (added is not null) SelectNode(added);
                Redraw();
            });
        }
    }

    // ---- color math --------------------------------------------------------------------------

    private static (int R, int G, int B) FromHsv(float hue, float sat, float val)
    {
        hue = (hue % 360f + 360f) % 360f;
        float c = val * sat, x = c * (1f - MathF.Abs(hue / 60f % 2f - 1f)), m = val - c;
        (float r, float g, float b) = hue switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return ((int)MathF.Round((r + m) * 255f), (int)MathF.Round((g + m) * 255f), (int)MathF.Round((b + m) * 255f));
    }

    private static (float H, float S, float V) ToHsv(int r, int g, int b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = MathF.Max(rf, MathF.Max(gf, bf)), min = MathF.Min(rf, MathF.Min(gf, bf)), d = max - min;
        float hue = d == 0f ? 0f
            : max == rf ? 60f * (((gf - bf) / d % 6f + 6f) % 6f)
            : max == gf ? 60f * ((bf - rf) / d + 2f)
            : 60f * ((rf - gf) / d + 4f);
        return (hue, max == 0f ? 0f : d / max, max);
    }
}
