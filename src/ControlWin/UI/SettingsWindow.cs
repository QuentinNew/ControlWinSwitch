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
    private const int SideDip = 16, ValueWidthDip = 300, ArrowDip = 40, CloseDip = 48, CrossDip = 44;
    // Connected-controller strip (Controller tab): tiles with the controller's name under each.
    private const int StripDip = RowDip * 2, TileDip = 64, TileGapDip = 40, NameHeightDip = 28;
    private const string GamepadGlyph = "", NoneGlyph = "";

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
    }

    /// <summary>The user closed the window with the mouse (close button or right click).</summary>
    public event Action? CloseRequested;

    /// <summary>The mouse changed something (the controller path refreshes by itself).</summary>
    public event Action? Changed;

    /// <summary>What the tile of a slot shows (a default tile = nothing connected).</summary>
    public Func<int, SlotTile>? Tile { get; set; }

    private bool Connected(int slot) => Tile?.Invoke(slot).Connected == true;

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

    /// <summary>Connected slot whose tile or name is at the point, or -1.</summary>
    private int SlotAt(int x, int y)
    {
        if (_model?.Tab.ControllerStrip != true || y < StripTop || y >= RowsTop) return -1;
        var slots = ConnectedSlots();
        if (slots.Length == 0) return -1;
        int tile = Px(TileDip), pitch = tile + Px(TileGapDip);
        int rel = x - TilesLeftFor(slots.Length);
        if (rel < 0) return -1;
        int index = rel / pitch;
        return index < slots.Length && rel % pitch < tile ? slots[index] : -1;
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
        if (y < RowsTop) return SlotAt(x, y) is var slot and >= 0 && Connected(slot);

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
        // The window appearing under a resting cursor sends a mouse move: only react to real movement.
        bool first = _lastMouseX < 0;
        bool moved = x != _lastMouseX || y != _lastMouseY;
        _lastMouseX = x;
        _lastMouseY = y;
        if (first || !moved) return;

        int row = RowAt(y);
        if (row >= 0 && _model.Select(row)) Refresh();
        if (row >= 0 && row < _model.Tab.Items.Count && _model.Tab.Items[row] is { ShowsClear: true } item)
        {
            bool overCross = x >= Px(WidthDip) - Px(SideDip) - Px(CrossDip);
            if (item.ClearFocused != overCross)
            {
                item.ClearFocused = overCross;
                Refresh();
            }
        }
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
            int slot = SlotAt(x, y);
            if (slot < 0) return;
            if (!_model.InSubPage || Connected(slot)) _model.Tab.Items[0].Slot = slot; // a page only edits connected controllers
            if (!_model.InSubPage) _model.Select(0);
            if (Connected(slot)) _model.Tab.Items[0].Activate?.Invoke(); // on the Controller tab: opens the controller's page
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
            case SettingKind.Action when item.ShowsClear && x >= width - Px(SideDip) - Px(CrossDip):
                item.Clear!();
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
    private void PaintTile(nint dc, FontSet fonts, SlotTile info, int l, int t, bool focused)
    {
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        bool on = info.Connected;
        int tile = Px(TileDip), nameH = Px(NameHeightDip);
        if (focused)
            Gdi.RoundedFill(dc, Gdi.SelectionBrush, l - Px(3), t - Px(3), l + tile + Px(3), t + tile + Px(3), Px(18));
        Gdi.RoundedFill(dc, on && info.Image == 0 ? Gdi.OnBrush : Gdi.PanelBrush, l, t, l + tile, t + tile, Px(14));
        if (on && info.Image != 0)
            ControllerPictures.DrawRounded(dc, info.Image, l, t, tile, Px(14));
        else
            Gdi.Text(dc, fonts.Glyph, on ? Gdi.TextColor : Gdi.Dim, on ? GamepadGlyph : NoneGlyph, l, t, l + tile, t + tile, DT_CENTER | oneLine);
        if (!on) return;
        PaintBattery(dc, info, l + tile, t + tile);
        int reach = Px(TileGapDip) / 2 - 1; // the name gets the tile's whole pitch, never more
        Gdi.Text(dc, fonts.Detail, Gdi.TextColor, info.Name, l - reach, t + tile + Px(6),
            l + tile + reach, t + tile + Px(6) + nameH, DT_CENTER | oneLine | DT_END_ELLIPSIS);
    }

    private static nint s_redBrush, s_amberBrush;

    /// <summary>A small battery badge on the bottom-right corner of a tile: four bars, a plus while charging.</summary>
    private void PaintBattery(nint dc, SlotTile info, int right, int bottom)
    {
        if (info.Battery < 0) return;
        int w = Px(38), h = Px(18);
        int l = right - w + Px(6), t = bottom - h + Px(4);
        Gdi.RoundedFill(dc, Gdi.BackgroundBrush, l, t, l + w, t + h, Px(8));

        s_redBrush = s_redBrush != 0 ? s_redBrush : CreateSolidBrush(Rgb(0xD6, 0x3B, 0x3B));
        s_amberBrush = s_amberBrush != 0 ? s_amberBrush : CreateSolidBrush(Rgb(0xE0, 0xA1, 0x2B));
        nint color = info.Battery <= 1 ? s_redBrush : info.Battery == 2 ? s_amberBrush : Gdi.OnBrush;

        int barW = Px(5), gap = Px(2), barH = Px(10), x = l + Px(6), y = t + (h - barH) / 2;
        int filled = Math.Max(info.Battery, info.Battery == 0 ? 1 : 0); // an empty battery still shows one (red) bar
        for (int i = 0; i < 4; i++)
        {
            int bx = x + i * (barW + gap);
            Gdi.Fill(dc, i < filled ? color : Gdi.DimBrush, bx, y, bx + barW, y + barH);
        }
        if (info.Charging)
        {
            int cx = x + 4 * (barW + gap) + Px(2), cy = t + h / 2, arm = Px(3), thick = Px(1);
            Gdi.Fill(dc, Gdi.TextBrush, cx - arm, cy - thick, cx + arm + thick, cy + thick + 1);
            Gdi.Fill(dc, Gdi.TextBrush, cx - thick, cy - arm, cx + thick + 1, cy + arm + thick);
        }
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
                    (stripSelected || model.InSubPage) && slots[j] == strip0.Slot);
            if (slots.Length == 0)
                Gdi.Text(dc, fonts.Label, Gdi.Dim, "No controller connected", 0, rowsTop, width, rowsTop + Px(StripDip), DT_CENTER | oneLine);
            rowsTop += Px(StripDip);
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
                {
                    int right = valueRight + Px(8);
                    if (item.ShowsClear)
                    {
                        // The cross at the end of the row clears the value; a square marks it when it has the focus.
                        int cl = right - Px(CrossDip), box = Px(38), bx = cl + (Px(CrossDip) - box) / 2, by = y + (rowH - box) / 2;
                        if (item.ClearFocused)
                        {
                            Gdi.RoundedFill(dc, Gdi.TextBrush, bx, by, bx + box, by + box, Px(12));
                            Gdi.RoundedFill(dc, Gdi.BackgroundBrush, bx + Px(2), by + Px(2), bx + box - Px(2), by + box - Px(2), Px(10));
                        }
                        Gdi.Text(dc, fonts.GlyphSmall, Gdi.TextColor, Close, cl, y, right, y + rowH, DT_CENTER | oneLine);
                        right = cl - Px(8);
                    }
                    Gdi.Text(dc, fonts.Detail, selected ? Gdi.DimOnSelection : Gdi.Dim, value, valueLeft, y, right, y + rowH, DT_RIGHT | oneLine | DT_END_ELLIPSIS);
                    break;
                }
            }
        }

        // Footer
        int footerTop = headerH + tabsH + Rows * rowH;
        if (_editing)
        {
            Gdi.Text(dc, fonts.Footer, Gdi.Dim, "Type the name     Enter / A  OK     Esc / B  Cancel",
                0, footerTop, width, footerTop + Px(FooterDip), DT_CENTER | oneLine);
            return;
        }
        string hint = model.InSubPage
            ? "Up / Down  Select     Left / Right  Reach the cross     A  OK     B  Back"
            : "L / R  Tab     Up / Down  Select     Left / Right  Change     A  Toggle     B  Close";
        Gdi.Text(dc, fonts.Footer, Gdi.Dim, hint,
            0, footerTop, width, footerTop + Px(FooterDip), DT_CENTER | oneLine);
    }
}
