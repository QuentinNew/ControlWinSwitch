using ControlWin.Actions;
using ControlWin.App;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>What a controller tile shows: the controller's name, battery (0..4, -1 unknown) and its picture (a GDI+ image, or 0).</summary>
internal readonly record struct SlotTile(bool Connected, string Name, int Battery, bool Charging, nint Image)
{
    /// <summary>"Full", "Low, charging", ... ("Unknown" until the controller reported it).</summary>
    public string BatteryText => (Battery switch
    {
        4 => "Full",
        3 => "Medium",
        2 => "Low",
        1 => "Critical",
        0 => "Empty",
        _ => "Unknown",
    }) + (Charging && Battery >= 0 ? ", charging" : "");
}

/// <summary>
/// The settings window: tab strip + rows. Topmost and never activating, like the menu. Painted from
/// <see cref="SettingsModel"/>; driven by the controller (via the model) or directly by the mouse.
/// </summary>
internal sealed unsafe class SettingsWindow : NativeWindow
{
    public const string ClassName = "ControlWin.Settings";

    // Layout in DIPs.
    private const int WidthDip = 760, HeaderDip = 60, TabsDip = 52, RowDip = 56, Rows = 6, FooterDip = 44;
    private const int SideDip = 16, ValueWidthDip = 300, ArrowDip = 40, CloseDip = 48, CrossBadgeDip = 24;
    // Connected-controller strip (Controller tab): tiles with the controller's name under each.
    private const int StripDip = RowDip * 2, TileDip = ControllerTile.SizeDip, TileGapDip = 40, NameHeightDip = ControllerTile.NameHeightDip;
    private static readonly string PencilGlyph = ((char)0xE70F).ToString(); // Segoe icon "Edit"

    private const string ChevronLeft = "", ChevronRight = "", Close = "";

    private SettingsModel? _model;
    private const int Slots = 4;
    private RECT _work;
    private uint _dpi = 96;
    private int _lastMouseX = -1, _lastMouseY = -1;
    private bool _editing;
    private string _buffer = "";
    private Action<string>? _commit;
    private nint _previousForeground;

    public SettingsWindow()
    {
        Create(ClassName, WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
        int noFade = 1; // no fade in / out: going from the menu to the settings must not dip in between
        DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, &noFade, sizeof(int));
    }

    /// <summary>The user closed the window with the mouse (close button or right click).</summary>
    public event Action? CloseRequested;

    /// <summary>The mouse changed something (the controller path refreshes by itself).</summary>
    public event Action? Changed;

    /// <summary>What the tile of a slot shows (a default tile = nothing connected).</summary>
    public Func<int, SlotTile>? Tile { get; set; }

    private bool Connected(int slot) => Tile?.Invoke(slot).Connected == true;

    /// <summary>The user clicked the pencil on a controller's picture / the cross that removes it.</summary>
    public event Action<int>? PictureEditRequested;
    public event Action<int>? PictureRemoveRequested;

    private enum StripPart { None, Picture, Cross, Name }

    private int _hoverSlot = -1;     // the controller whose picture the mouse is over (pencil / cross shown)
    private bool _hoverCross;
    private bool _trackingLeave;

    public bool IsShown => IsWindowVisible(Handle) != 0;

    public void Show(SettingsModel model)
    {
        _model = model;
        _lastMouseX = _lastMouseY = -1;
        Gdi.GetMonitorUnderCursor(out _work, out _dpi);

        int width = Px(WidthDip), height = Px(TotalHeightDip);
        int x = _work.Left + (_work.Width - width) / 2;
        int y = _work.Top + (_work.Height - height) / 2;
        SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(Handle, null, 0);
        UpdateWindow(Handle); // paint now: the caller may hide another window right after, and an unpainted frame would flash
    }

    public void Refresh()
    {
        if (_model is null) return;
        InvalidateRect(Handle, null, 0);
    }

    public void Hide()
    {
        EndEdit(commit: false);
        ShowWindow(Handle, SW_HIDE);
        K32EmptyWorkingSet(GetCurrentProcess());
    }

    public bool IsEditing => _editing;

    /// <summary>
    /// Inline text editing with the physical keyboard. The window is normally never activated (so it doesn't
    /// steal focus from a game); while editing it takes the foreground, and gives it back afterwards.
    /// </summary>
    public void BeginEdit(string current, Action<string> commit)
    {
        if (_editing) return;
        _buffer = current ?? "";
        _commit = commit;
        _editing = true;
        _previousForeground = GetForegroundWindow();

        nint style = GetWindowLongPtrW(Handle, GWL_EXSTYLE);
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, style & ~(nint)WS_EX_NOACTIVATE);

        // Windows only lets the foreground process change the foreground: a synthetic Alt press lifts that restriction.
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        SetForegroundWindow(Handle);
        SetFocus(Handle);
        Refresh();
    }

    public void EndEdit(bool commit)
    {
        if (!_editing) return;
        _editing = false;

        nint style = GetWindowLongPtrW(Handle, GWL_EXSTYLE);
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, style | (nint)WS_EX_NOACTIVATE);
        if (_previousForeground != 0 && _previousForeground != Handle) SetForegroundWindow(_previousForeground);
        _previousForeground = 0;

        var action = commit ? _commit : null;
        _commit = null;
        action?.Invoke(_buffer);
        Refresh();
    }

    private int StripHeightDip => _model?.Tab.ControllerStrip == true ? StripDip : 0;

    private static int TotalHeightDip => HeaderDip + TabsDip + Rows * RowDip + FooterDip;

    private int Px(int dip) => (int)Math.Round(dip * _dpi / 96.0);

    protected override nint WndProc(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_MOUSEACTIVATE:
                return _editing ? MA_ACTIVATE : MA_NOACTIVATE;
            case WM_CHAR when _editing:
                OnChar((char)wParam);
                return 0;
            case WM_KILLFOCUS when _editing:
                EndEdit(commit: false);
                return 0;
            case WM_SETCURSOR when (lParam & 0xFFFF) == HTCLIENT:
                SetCursor(LoadCursorW(0, IsClickableAtCursor() ? IDC_HAND : IDC_ARROW));
                return 1;
            case WM_MOUSEMOVE:
                OnMouseMove(LoWord(lParam), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_MOUSELEAVE:
                _trackingLeave = false;
                if (_hoverSlot >= 0)
                {
                    _hoverSlot = -1;
                    _hoverCross = false;
                    Refresh();
                }
                return 0;
            case WM_LBUTTONDOWN:
                OnClick(LoWord(lParam), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_RBUTTONUP:
                if (_model is { InSubPage: true })
                {
                    _model.Back();
                    Refresh();
                }
                else CloseRequested?.Invoke();
                return 0;
            case WM_MOUSEWHEEL:
                if (_model is not null)
                {
                    int delta = (short)((wParam >> 16) & 0xFFFF);
                    _model.Move(delta > 0 ? -1 : 1);
                    Refresh();
                }
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private int TabsTop => Px(HeaderDip);
    private int StripTop => Px(HeaderDip + TabsDip);
    private int RowsTop => Px(HeaderDip + TabsDip + StripHeightDip);

    /// <summary>The connected slots, in order: the only ones the strip shows.</summary>
    private int[] ConnectedSlots() => Enumerable.Range(0, Slots).Where(Connected).ToArray();

    /// <summary>Left edge of a row of <paramref name="count"/> tiles centered in the window.</summary>
    private int TilesLeftFor(int count) => (Px(WidthDip) - (count * Px(TileDip) + (count - 1) * Px(TileGapDip))) / 2;

    /// <summary>The cross badge sits on the top right corner of a tile, poking out a little.</summary>
    private (int X, int Y, int Size) CrossBadge(int tileLeft, int tileTop) =>
        (tileLeft + Px(TileDip) - Px(CrossBadgeDip) + Px(8), tileTop - Px(8), Px(CrossBadgeDip));

    /// <summary>The connected slot whose tile is at the point (and which part of it), or -1.</summary>
    private int HitAt(int x, int y, out StripPart part)
    {
        part = StripPart.None;
        if (_model?.Tab.ControllerStrip != true || y < StripTop || y >= RowsTop) return -1;
        var slots = ConnectedSlots();
        int tile = Px(TileDip), pitch = tile + Px(TileGapDip), top = StripTop + Px(8), left = TilesLeftFor(slots.Length);
        int nameTop = top + tile + Px(6), nameBottom = nameTop + Px(NameHeightDip), reach = Px(TileGapDip) / 2 - 1;
        for (int j = 0; j < slots.Length; j++)
        {
            int l = left + j * pitch;
            var badge = CrossBadge(l, top);
            if (Tile?.Invoke(slots[j]).Image != 0 && x >= badge.X && x < badge.X + badge.Size && y >= badge.Y && y < badge.Y + badge.Size)
                part = StripPart.Cross;
            else if (x >= l && x < l + tile && y >= top && y < top + tile)
                part = StripPart.Picture;
            else if (x >= l - reach && x < l + tile + reach && y >= nameTop && y < nameBottom)
                part = StripPart.Name;
            if (part != StripPart.None) return slots[j];
        }
        return -1;
    }

    private int RowAt(int y)
    {
        int top = StripTop;
        if (y < top) return -1;
        int rel = y - top, strip = Px(StripHeightDip);
        if (strip == 0) return rel / Px(RowDip);
        return rel < strip ? 0 : 1 + (rel - strip) / Px(RowDip);
    }

    private bool IsClickableAtCursor()
    {
        if (_model is null) return false;
        POINT pt;
        GetCursorPos(&pt);
        ScreenToClient(Handle, &pt);
        int x = pt.X, y = pt.Y;

        if (_editing) return true; // a click confirms
        if (y < TabsTop) return x >= Px(WidthDip) - Px(CloseDip); // close button
        if (y < StripTop) return true;                            // tabs
        if (y < RowsTop) return HitAt(x, y, out var part) >= 0 && part != StripPart.None;

        if (_model.Tab.Calibration is not null) return CalibrationHit(x, y) != CalHit.None;

        int row = RowAt(y);
        return row >= 0 && row < _model.Tab.Items.Count && _model.Tab.Items[row].Selectable;
    }

    private void OnChar(char c)
    {
        switch (c)
        {
            case '\r':
                EndEdit(commit: true);
                break;
            case (char)27:
                EndEdit(commit: false);
                break;
            case '\b':
                if (_buffer.Length > 0) _buffer = _buffer[..^1];
                Refresh();
                break;
            default:
                if (c >= ' ' && _buffer.Length < 60)
                {
                    _buffer += c;
                    Refresh();
                }
                break;
        }
    }

    private void OnMouseMove(int x, int y)
    {
        if (_model is null || _editing) return;
        if (!_trackingLeave)
        {
            var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = Handle };
            _trackingLeave = TrackMouseEvent(&track) != 0;
        }
        UpdateHover(x, y);
        // The window appearing under a resting cursor sends a mouse move: only react to real movement.
        bool first = _lastMouseX < 0;
        bool moved = x != _lastMouseX || y != _lastMouseY;
        _lastMouseX = x;
        _lastMouseY = y;
        if (first || !moved) return;

        int row = RowAt(y);
        if (row >= 0 && _model.Select(row)) Refresh();
    }

    /// <summary>The controller being edited: the strip's focused one.</summary>
    private int SelectedSlot => _model?.Tab.ControllerStrip == true ? _model.Tab.Items[0].Slot : -1;

    /// <summary>
    /// The pencil and the cross only appear on a controller's page, on the controller being edited, when the mouse is
    /// over its picture. The Controller tab never edits pictures.
    /// </summary>
    private void UpdateHover(int x, int y)
    {
        int hit = HitAt(x, y, out var over);
        bool onPicture = _model?.InSubPage == true && over is StripPart.Picture or StripPart.Cross && hit == SelectedSlot;
        int hover = onPicture ? hit : -1;
        bool cross = onPicture && over == StripPart.Cross;
        if (hover == _hoverSlot && cross == _hoverCross) return;
        _hoverSlot = hover;
        _hoverCross = cross;
        Refresh();
    }

    private void OnClick(int x, int y)
    {
        if (_model is null) return;
        if (_editing)
        {
            EndEdit(commit: true);
            return;
        }
        int width = Px(WidthDip);

        if (y < TabsTop)
        {
            if (x >= width - Px(CloseDip)) CloseRequested?.Invoke();
            return;
        }

        if (y >= StripTop && y < RowsTop)
        {
            int slot = HitAt(x, y, out var part);
            if (slot < 0 || part == StripPart.None) return;
            var strip = _model.Tab.Items[0];
            if (!_model.InSubPage)
            {
                // The Controller tab: a tile only opens that controller's page.
                strip.Slot = slot;
                _model.Select(0);
                strip.Activate?.Invoke();
            }
            else if (slot != strip.Slot || part == StripPart.Name)
            {
                strip.Slot = slot; // another controller: edit it instead
                UpdateHover(x, y);
            }
            else if (part == StripPart.Cross)
            {
                PictureRemoveRequested?.Invoke(slot);
            }
            else
            {
                PictureEditRequested?.Invoke(slot);
            }
            Refresh();
            return;
        }

        if (y < StripTop)
        {
            int tab = Math.Clamp(x * _model.Tabs.Count / width, 0, _model.Tabs.Count - 1);
            _model.SetTab(tab);
            Refresh();
            return;
        }

        if (_model.Tab.Calibration is { } calibration)
        {
            OnCalibrationClick(calibration, x, y);
            Refresh();
            return;
        }

        int row = RowAt(y);
        if (row < 0 || row >= _model.Tab.Items.Count) return;
        var item = _model.Tab.Items[row];
        if (!item.Selectable) return;

        _model.Select(row);
        switch (item.Kind)
        {
            case SettingKind.Toggle:
                item.Activate?.Invoke();
                break;
            case SettingKind.Action:
            case SettingKind.Text:
                item.Activate?.Invoke();
                break;
            case SettingKind.Number:
            case SettingKind.Choice:
                // Left half of the value area decrements, right half increments.
                int valueLeft = width - Px(SideDip) - Px(ValueWidthDip);
                if (x >= valueLeft) item.Adjust?.Invoke(x < valueLeft + Px(ValueWidthDip) / 2 ? -1 : 1);
                break;
        }
        Refresh();
        Changed?.Invoke();
    }

    /// <summary>One controller tile: picture or gamepad icon, battery badge, name underneath.</summary>
    private void PaintTile(nint dc, FontSet fonts, SlotTile info, int l, int t, bool focused, bool hovered = false, bool crossHot = false)
    {
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        bool on = info.Connected;
        int tile = Px(TileDip);
        if (focused)
            Gdi.RoundedFill(dc, Gdi.SelectionBrush, l - Px(3), t - Px(3), l + tile + Px(3), t + tile + Px(3), Px(18));
        ControllerTile.PaintFace(dc, fonts, _dpi, info, l, t);
        if (on && hovered)
        {
            // Hovering the picture: a pencil changes it, and (when there is one) a cross on the top right removes it.
            ControllerPictures.DrawDim(dc, l, t, tile, Px(14));
            Gdi.Text(dc, fonts.Glyph, Gdi.TextColor, PencilGlyph, l, t, l + tile, t + tile, DT_CENTER | oneLine);
        }
        if (!on) return;
        ControllerTile.PaintBattery(dc, _dpi, info, l + tile, t + tile);
        if (hovered && info.Image != 0)
        {
            var badge = CrossBadge(l, t);
            Gdi.RoundedFill(dc, crossHot ? ControllerTile.RedHotBrush : ControllerTile.RedBrush, badge.X, badge.Y, badge.X + badge.Size, badge.Y + badge.Size, badge.Size);
            Gdi.Text(dc, BadgeFont(), Gdi.TextColor, Close, badge.X, badge.Y, badge.X + badge.Size, badge.Y + badge.Size, DT_CENTER | oneLine);
        }
        int reach = Px(TileGapDip) / 2 - 1; // the name gets the tile's whole pitch, never more
        ControllerTile.PaintName(dc, fonts, _dpi, info, l, t, reach);
    }

    // ---- deadzone calibration screen -----------------------------------------------------------

    private const int CircleDip = 130, CaptionDip = 34;

    private enum CalHit { None, CircleLeft, CircleRight, LessLeft, MoreLeft, LessRight, MoreRight, Auto, Done }

    /// <summary>Center x of a stick's circle, and its top / radius, for the rows area starting at <paramref name="top"/>.</summary>
    private (int Cx, int Top, int Radius) CircleOf(int stick, int top) =>
        (Px(WidthDip) * (stick == 0 ? 25 : 75) / 100, top + Px(10), Px(CircleDip) / 2);

    private RECT CaptionOf(int stick, int top)
    {
        var (cx, circleTop, r) = CircleOf(stick, top);
        int t = circleTop + 2 * r + Px(10);
        return new RECT(cx - Px(90), t, cx + Px(90), t + Px(CaptionDip));
    }

    private RECT AutoButton(int top) => new(Px(WidthDip) / 2 - Px(80), top + Px(30), Px(WidthDip) / 2 + Px(80), top + Px(78));

    private RECT DoneButton(int top) => new(Px(WidthDip) / 2 - Px(80), top + Px(92), Px(WidthDip) / 2 + Px(80), top + Px(140));

    private static bool In(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private CalHit CalibrationHit(int x, int y)
    {
        int top = RowsTop;
        if (y < top) return CalHit.None;
        for (int stick = 0; stick < 2; stick++)
        {
            var (cx, circleTop, r) = CircleOf(stick, top);
            long dx = x - cx, dy = y - (circleTop + r);
            if (dx * dx + dy * dy <= (long)r * r) return stick == 0 ? CalHit.CircleLeft : CalHit.CircleRight;
            var caption = CaptionOf(stick, top);
            if (In(caption, x, y))
            {
                bool less = x < (caption.Left + caption.Right) / 2;
                return stick == 0 ? (less ? CalHit.LessLeft : CalHit.MoreLeft) : (less ? CalHit.LessRight : CalHit.MoreRight);
            }
        }
        if (In(AutoButton(top), x, y)) return CalHit.Auto;
        if (In(DoneButton(top), x, y)) return CalHit.Done;
        return CalHit.None;
    }

    private void OnCalibrationClick(DeadzoneCalibration cal, int x, int y)
    {
        switch (CalibrationHit(x, y))
        {
            case CalHit.CircleLeft: cal.SelectStick(0); break;
            case CalHit.CircleRight: cal.SelectStick(1); break;
            case CalHit.LessLeft: cal.SelectStick(0); cal.Adjust(-1); break;
            case CalHit.MoreLeft: cal.SelectStick(0); cal.Adjust(1); break;
            case CalHit.LessRight: cal.SelectStick(1); cal.Adjust(-1); break;
            case CalHit.MoreRight: cal.SelectStick(1); cal.Adjust(1); break;
            case CalHit.Auto: cal.StartAuto(); break;
            case CalHit.Done: _model?.Back(); break;
        }
    }

    private void PaintButton(nint dc, FontSet fonts, RECT r, string text)
    {
        Gdi.RoundedFill(dc, Gdi.SelectionBrush, r.Left, r.Top, r.Right, r.Bottom, Px(16));
        Gdi.Text(dc, fonts.Label, Gdi.TextColor, text, r.Left, r.Top, r.Right, r.Bottom, DT_CENTER | DT_SINGLELINE | DT_VCENTER);
    }

    private void Disc(nint dc, nint brush, int cx, int cy, int radius) =>
        Gdi.RoundedFill(dc, brush, cx - radius, cy - radius, cx + radius, cy + radius, radius * 2);

    /// <summary>Two circles (left / right stick): the grey disc is the deadzone, the dot is where the stick is right now.</summary>
    private void PaintCalibration(nint dc, FontSet fonts, DeadzoneCalibration cal, int top)
    {
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        var (left, right) = cal.Sticks;
        string[] names = ["Left stick", "Right stick"];

        for (int stick = 0; stick < 2; stick++)
        {
            bool selected = cal.Selected == stick;
            var (cx, circleTop, r) = CircleOf(stick, top);
            int cy = circleTop + r, ring = Px(3);
            var pos = stick == 0 ? left : right;
            float zone = cal.Get(stick);

            Disc(dc, selected ? Gdi.SelectionBrush : Gdi.DimBrush, cx, cy, r);
            Disc(dc, Gdi.PanelBrush, cx, cy, r - ring);
            int zoneR = (int)Math.Round((r - ring) * zone);
            if (zoneR > 0) Disc(dc, ControllerTile.AmberBrush, cx, cy, zoneR);

            int dot = Px(6), reach = r - ring - dot;
            int px = cx + (int)Math.Round(Math.Clamp(pos.X, -1f, 1f) * reach), py = cy + (int)Math.Round(Math.Clamp(pos.Y, -1f, 1f) * reach);
            Disc(dc, pos.Magnitude > zone ? Gdi.OnBrush : Gdi.TextBrush, px, py, dot);

            var caption = CaptionOf(stick, top);
            if (selected) Gdi.RoundedFill(dc, Gdi.SelectionBrush, caption.Left, caption.Top, caption.Right, caption.Bottom, Px(16));
            Gdi.Text(dc, fonts.GlyphSmall, selected ? Gdi.TextColor : Gdi.Dim, ChevronLeft, caption.Left, caption.Top, caption.Left + Px(32), caption.Bottom, DT_CENTER | oneLine);
            Gdi.Text(dc, fonts.GlyphSmall, selected ? Gdi.TextColor : Gdi.Dim, ChevronRight, caption.Right - Px(32), caption.Top, caption.Right, caption.Bottom, DT_CENTER | oneLine);
            Gdi.Text(dc, fonts.Detail, Gdi.TextColor, $"{names[stick]}  {zone.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}",
                caption.Left + Px(32), caption.Top, caption.Right - Px(32), caption.Bottom, DT_CENTER | oneLine);
        }

        PaintButton(dc, fonts, AutoButton(top), "Auto");
        PaintButton(dc, fonts, DoneButton(top), "Done");
        int width = Px(WidthDip);
        Gdi.Text(dc, fonts.Detail, cal.Auto == AutoDeadzone.Pushed ? Rgb(0xF2, 0x5C, 0x5C) : Gdi.Dim, cal.Status,
            width / 2 - Px(120), top + Px(150), width / 2 + Px(120), top + Px(190), DT_CENTER | DT_WORDBREAK);
    }

    private nint _badgeFont;
    private uint _badgeFontDpi;

    /// <summary>A small icon font for the cross inside the red badge (rebuilt when the DPI changes).</summary>
    private nint BadgeFont()
    {
        if (_badgeFont == 0 || _badgeFontDpi != _dpi)
        {
            if (_badgeFont != 0) DeleteObject(_badgeFont);
            _badgeFont = Gdi.CreateIconFont(Px(10));
            _badgeFontDpi = _dpi;
        }
        return _badgeFont;
    }

    private void Paint()
    {
        using var paint = new BufferedPaint(Handle);
        var dc = paint.Dc;
        int width = paint.Width, height = paint.Height;
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;

        Gdi.Fill(dc, Gdi.BackgroundBrush, 0, 0, width, height);
        if (_model is not { } model) return;

        int headerH = Px(HeaderDip), tabsH = Px(TabsDip), rowH = Px(RowDip), side = Px(SideDip);

        // Header
        Gdi.Text(dc, fonts.Title, Gdi.TextColor, model.SubTitle is { } sub ? "Settings  >  " + sub : "Settings",
            Px(24), 0, width, headerH, DT_LEFT | oneLine);
        Gdi.Text(dc, fonts.GlyphSmall, Gdi.Dim, Close, width - Px(CloseDip), 0, width, headerH, DT_CENTER | oneLine);

        // Tabs
        int tabCount = model.Tabs.Count;
        Gdi.Fill(dc, Gdi.PanelBrush, 0, headerH, width, headerH + tabsH);
        for (int i = 0; i < tabCount; i++)
        {
            int l = width * i / tabCount, r = width * (i + 1) / tabCount;
            bool current = i == model.TabIndex;
            if (current)
                Gdi.RoundedFill(dc, Gdi.SelectionBrush, l + Px(6), headerH + Px(6), r - Px(6), headerH + tabsH - Px(6), Px(16));
            Gdi.Text(dc, fonts.Label, Gdi.TextColor, model.Tabs[i].Title, l, headerH, r, headerH + tabsH, DT_CENTER | oneLine);
        }

        // Connected controllers
        int rowsTop = headerH + tabsH;
        bool strip = model.Tab.ControllerStrip;
        if (strip)
        {
            // Only connected controllers are shown, centered as a group.
            var slots = ConnectedSlots();
            var strip0 = model.Tab.Items[0];
            if (slots.Length > 0 && !slots.Contains(strip0.Slot)) strip0.Slot = slots[0]; // the focus follows a controller that is there
            bool stripSelected = model.Selected == 0;
            int tile = Px(TileDip), pitch = tile + Px(TileGapDip), left = TilesLeftFor(slots.Length);
            for (int j = 0; j < slots.Length; j++)
                PaintTile(dc, fonts, Tile?.Invoke(slots[j]) ?? default, left + j * pitch, rowsTop + Px(8),
                    (stripSelected || model.InSubPage) && slots[j] == strip0.Slot, hovered: model.InSubPage && slots[j] == _hoverSlot && slots[j] == strip0.Slot, crossHot: slots[j] == _hoverSlot && _hoverCross);
            if (slots.Length == 0)
                Gdi.Text(dc, fonts.Label, Gdi.Dim, "No controller connected", 0, rowsTop, width, rowsTop + Px(StripDip), DT_CENTER | oneLine);
            rowsTop += Px(StripDip);
        }

        if (model.Tab.Calibration is { } calibration)
        {
            PaintCalibration(dc, fonts, calibration, rowsTop);
            PaintFooter(dc, fonts, model, headerH + tabsH + Rows * rowH);
            return;
        }

        // Rows
        var items = model.Tab.Items;
        int valueRight = width - side - Px(28); // keeps values clear of the row's rounded edge
        int valueLeft = width - side - Px(ValueWidthDip);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind == SettingKind.Strip) continue;
            int y = rowsTop + (strip ? i - 1 : i) * rowH;
            bool selected = i == model.Selected;
            if (selected)
                Gdi.RoundedFill(dc, Gdi.SelectionBrush, side, y + Px(4), width - side, y + rowH - Px(4), Px(16));

            uint labelColor = item.Kind == SettingKind.Info ? Gdi.Dim : Gdi.TextColor;
            uint valueColor = selected ? Gdi.TextColor : (item.Kind == SettingKind.Info ? Gdi.Dim : Gdi.TextColor);
            int labelRight = valueLeft - Px(8);
            Gdi.Text(dc, fonts.Label, labelColor, item.Label, Px(28), y, labelRight, y + rowH, DT_LEFT | oneLine | DT_END_ELLIPSIS);

            string value = item.Value();
            switch (item.Kind)
            {
                case SettingKind.Number:
                case SettingKind.Choice:
                    Gdi.Text(dc, fonts.GlyphSmall, selected ? Gdi.TextColor : Gdi.Dim, ChevronLeft, valueLeft, y, valueLeft + Px(ArrowDip), y + rowH, DT_CENTER | oneLine);
                    Gdi.Text(dc, fonts.Label, valueColor, value, valueLeft + Px(ArrowDip), y, valueRight - Px(ArrowDip) + Px(8), y + rowH, DT_CENTER | oneLine | DT_END_ELLIPSIS);
                    Gdi.Text(dc, fonts.GlyphSmall, selected ? Gdi.TextColor : Gdi.Dim, ChevronRight, valueRight - Px(ArrowDip) + Px(8), y, valueRight + Px(8), y + rowH, DT_CENTER | oneLine);
                    break;

                case SettingKind.Toggle:
                {
                    bool on = item.IsOn?.Invoke() ?? false;
                    int pillW = Px(64), pillH = Px(30);
                    int pr = valueRight + Px(8), pl = pr - pillW, pt = y + (rowH - pillH) / 2;
                    Gdi.RoundedFill(dc, on ? Gdi.OnBrush : Gdi.DimBrush, pl, pt, pr, pt + pillH, pillH);
                    Gdi.Text(dc, fonts.Detail, Gdi.TextColor, value, pl, pt, pr, pt + pillH, DT_CENTER | oneLine);
                    break;
                }

                case SettingKind.Text when _editing && selected:
                {
                    int bl = valueLeft - Px(40), br = valueRight + Px(8), bh = Px(38), bt = y + (rowH - bh) / 2;
                    Gdi.RoundedFill(dc, Gdi.BackgroundBrush, bl, bt, br, bt + bh, Px(12));
                    Gdi.Text(dc, fonts.Label, Gdi.TextColor, _buffer + "|", bl + Px(12), bt, br - Px(8), bt + bh, DT_LEFT | oneLine);
                    break;
                }

                case SettingKind.Text:
                    Gdi.Text(dc, fonts.Label, Gdi.TextColor, value, valueLeft - Px(40), y, valueRight + Px(8), y + rowH, DT_RIGHT | oneLine | DT_END_ELLIPSIS);
                    break;

                default:
                    Gdi.Text(dc, fonts.Detail, selected ? Gdi.DimOnSelection : Gdi.Dim, value, valueLeft, y, valueRight + Px(8), y + rowH, DT_RIGHT | oneLine | DT_END_ELLIPSIS);
                    break;
            }
        }

        PaintFooter(dc, fonts, model, headerH + tabsH + Rows * rowH);
    }

    private void PaintFooter(nint dc, FontSet fonts, SettingsModel model, int footerTop)
    {
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int width = Px(WidthDip);
        if (_editing)
        {
            Gdi.Text(dc, fonts.Footer, Gdi.Dim, "Type the name     Enter / A  OK     Esc / B  Cancel",
                0, footerTop, width, footerTop + Px(FooterDip), DT_CENTER | oneLine);
            return;
        }
        string hint = model.Tab.Calibration is not null
            ? "Left / Right  Stick     Up / Down  Deadzone     X  Auto     ZL / ZR  Controller     A / B  Back"
            : model.InSubPage
            ? "Up / Down  Select     ZL / ZR  Controller     A  OK     B  Back"
            : "L / R  Tab     Up / Down  Select     Left / Right  Change     A  Toggle     B  Close";
        Gdi.Text(dc, fonts.Footer, Gdi.Dim, hint,
            0, footerTop, width, footerTop + Px(FooterDip), DT_CENTER | oneLine);
    }
}
