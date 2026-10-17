using ControlWin.App;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>
/// The settings window: tab strip + rows. Topmost and never activating, like the menu. Painted from
/// <see cref="SettingsModel"/>; driven by the controller (via the model) or directly by the mouse.
/// </summary>
internal sealed unsafe class SettingsWindow : NativeWindow
{
    public const string ClassName = "ControlWin.Settings";

    // Layout in DIPs.
    private const int WidthDip = 760, HeaderDip = 60, TabsDip = 52, RowDip = 56, Rows = 6, FooterDip = 44;
    private const int SideDip = 16, ValueWidthDip = 300, ArrowDip = 40, CloseDip = 48;
    // Connected-controller strip (Controller tab): tiles with a Rumble button under each.
    private const int StripDip = RowDip * 2, TileDip = 64, TileGapDip = 24, RumbleHeightDip = 28;
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

    /// <summary>Whether the controller in a slot is connected (tile color, Rumble enabled).</summary>
    public Func<int, bool>? SlotConnected { get; set; }

    /// <summary>The user clicked a connected controller's tile or Rumble button.</summary>
    public event Action<int>? RumbleRequested;

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
                CloseRequested?.Invoke();
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

    private int TilesLeft => (Px(WidthDip) - (Slots * Px(TileDip) + (Slots - 1) * Px(TileGapDip))) / 2;

    /// <summary>Slot whose tile or Rumble button is at the point, or -1.</summary>
    private int SlotAt(int x, int y)
    {
        if (StripHeightDip == 0 || y < StripTop || y >= RowsTop) return -1;
        int tile = Px(TileDip), pitch = tile + Px(TileGapDip);
        int rel = x - TilesLeft;
        if (rel < 0) return -1;
        int slot = rel / pitch;
        return slot < Slots && rel % pitch < tile ? slot : -1;
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
        if (y < RowsTop) return SlotAt(x, y) is var slot and >= 0 && SlotConnected?.Invoke(slot) == true;

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
            _model.Select(0);
            _model.Tab.Items[0].Slot = slot;
            if (SlotConnected?.Invoke(slot) == true) RumbleRequested?.Invoke(slot);
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
        Gdi.Text(dc, fonts.Title, Gdi.TextColor, "Settings", Px(24), 0, width, headerH, DT_LEFT | oneLine);
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
            bool stripSelected = model.Selected == 0;
            int focus = model.Tab.Items[0].Slot;
            int tile = Px(TileDip), pitch = tile + Px(TileGapDip), btnH = Px(RumbleHeightDip);
            for (int i = 0; i < Slots; i++)
            {
                bool on = SlotConnected?.Invoke(i) == true;
                int l = TilesLeft + i * pitch, t = rowsTop + Px(8);
                Gdi.RoundedFill(dc, on ? Gdi.OnBrush : Gdi.PanelBrush, l, t, l + tile, t + tile, Px(14));
                Gdi.Text(dc, fonts.Glyph, on ? Gdi.TextColor : Gdi.Dim, on ? GamepadGlyph : NoneGlyph, l, t, l + tile, t + tile, DT_CENTER | oneLine);
                int bt = t + tile + Px(6);
                if (stripSelected && i == focus)
                    Gdi.RoundedFill(dc, Gdi.TextBrush, l - Px(2), bt - Px(2), l + tile + Px(2), bt + btnH + Px(2), Px(12));
                Gdi.RoundedFill(dc, on ? Gdi.SelectionBrush : Gdi.PanelBrush, l, bt, l + tile, bt + btnH, Px(10));
                Gdi.Text(dc, fonts.Detail, on ? Gdi.TextColor : Gdi.Dim, "Rumble", l, bt, l + tile, bt + btnH, DT_CENTER | oneLine);
            }
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
                    Gdi.Text(dc, fonts.Detail, selected ? Gdi.DimOnSelection : Gdi.Dim, value, valueLeft, y, valueRight + Px(8), y + rowH, DT_RIGHT | oneLine | DT_END_ELLIPSIS);
                    break;
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
        Gdi.Text(dc, fonts.Footer, Gdi.Dim, "L / R  Tab     Up / Down  Select     Left / Right  Change     A  Toggle     B  Close",
            0, footerTop, width, footerTop + Px(FooterDip), DT_CENTER | oneLine);
    }
}
