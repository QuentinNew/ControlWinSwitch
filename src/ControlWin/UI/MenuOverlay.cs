using ControlWin.App;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>
/// The menu: a topmost, never-activating popup centered on the monitor under the cursor.
/// It paints what <see cref="MenuModel"/> says; navigation comes from the controller, or from the mouse
/// (hover selects, left click accepts, right click goes back, wheel moves).
/// </summary>
internal sealed unsafe class MenuOverlay : NativeWindow
{
    public const string ClassName = "ControlWin.Menu";

    // Layout in DIPs.
    private const int WidthDip = 520, HeaderDip = 64, RowDip = 60, FooterDip = 48, MaxRows = 7, ConfirmRows = 2;
    private const int SideDip = 12, GlyphXDip = 24, GlyphWidthDip = 32, LabelXDip = 72, DetailWidthDip = 150;

    private MenuModel? _model;
    private int _lastMouseX = -1, _lastMouseY = -1;

    /// <summary>Mouse: row index hovered / clicked, "back" (right click), confirmation answered, wheel step (-1 / +1).</summary>
    public event Action<int>? RowHovered, RowClicked, Scrolled;
    public event Action? BackRequested;
    public event Action<bool>? ConfirmAnswered;
    private RECT _work;
    private uint _dpi = 96;
    private int _top; // first visible row when the page is longer than MaxRows
    private MenuPage? _topPage;

    public MenuOverlay()
    {
        Create(ClassName, WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int)); // Windows 11 only, harmless otherwise
    }

    public bool IsShown => IsWindowVisible(Handle) != 0;

    public void Show(MenuModel model)
    {
        _model = model;
        _lastMouseX = _lastMouseY = -1;
        _top = 0;
        _topPage = null;
        Gdi.GetMonitorUnderCursor(out _work, out _dpi);
        Place();
        InvalidateRect(Handle, null, 0);
    }

    /// <summary>Call after the model changed.</summary>
    public void Refresh()
    {
        if (_model is null) return;
        Place();
        InvalidateRect(Handle, null, 0);
    }

    public void Hide()
    {
        ShowWindow(Handle, SW_HIDE);
        K32EmptyWorkingSet(GetCurrentProcess());
    }

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
            case WM_MOUSEWHEEL:
                Scrolled?.Invoke((short)((wParam >> 16) & 0xFFFF) > 0 ? -1 : 1);
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    /// <summary>Row under a client y coordinate, or -1.</summary>
    private int RowAt(int y)
    {
        if (_model is null || _model.IsConfirming) return -1;
        int local = y - Px(HeaderDip);
        if (local < 0) return -1;
        int row = local / Px(RowDip);
        int index = _top + row;
        return row < VisibleRows() && index < _model.Page.Entries.Count ? index : -1;
    }

    private bool IsClickableAtCursor()
    {
        if (_model is null) return false;
        POINT pt;
        GetCursorPos(&pt);
        ScreenToClient(Handle, &pt);

        if (_model.IsConfirming) return pt.Y >= Px(HeaderDip); // body answers the question
        int index = RowAt(pt.Y);
        return index >= 0 && _model.Page.Entries[index].Enabled;
    }

    private void OnMouseMove(int x, int y)
    {
        // The menu appearing under a resting cursor sends a mouse move: only react to real movement.
        bool first = _lastMouseX < 0;
        bool moved = x != _lastMouseX || y != _lastMouseY;
        _lastMouseX = x;
        _lastMouseY = y;
        if (first || !moved) return;

        int index = RowAt(y);
        if (index >= 0) RowHovered?.Invoke(index);
    }

    private void OnClick(int x, int y)
    {
        if (_model is null) return;
        if (_model.IsConfirming)
        {
            if (y >= Px(HeaderDip)) ConfirmAnswered?.Invoke(x < Px(WidthDip) / 2);
            return;
        }
        int index = RowAt(y);
        if (index >= 0) RowClicked?.Invoke(index);
    }

    private int Px(int dip) => (int)Math.Round(dip * _dpi / 96.0);

    private int VisibleRows()
    {
        if (_model is null) return 1;
        return _model.IsConfirming ? ConfirmRows : Math.Clamp(_model.Page.Entries.Count, 1, MaxRows);
    }

    private void Place()
    {
        int width = Px(WidthDip);
        int height = Px(HeaderDip + VisibleRows() * RowDip + FooterDip);
        int x = _work.Left + (_work.Width - width) / 2;
        int y = _work.Top + (_work.Height - height) / 2;
        SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void Paint()
    {
        using var paint = new BufferedPaint(Handle);
        var dc = paint.Dc;
        int width = paint.Width;
        var fonts = Gdi.GetFonts(_dpi);

        Gdi.Fill(dc, Gdi.BackgroundBrush, 0, 0, width, paint.Height);
        if (_model is not { } model) return;

        int headerH = Px(HeaderDip), rowH = Px(RowDip), footerH = Px(FooterDip);
        int rows = VisibleRows();
        int bodyBottom = headerH + rows * rowH;
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;

        if (model.IsConfirming)
        {
            var entry = model.CurrentEntry;
            Gdi.Text(dc, fonts.Title, Gdi.TextColor, entry?.Label ?? "", Px(24), 0, width - Px(24), headerH, DT_LEFT | oneLine | DT_END_ELLIPSIS);
            Gdi.Text(dc, fonts.Title, Gdi.TextColor, model.ConfirmPrompt ?? "", Px(24), headerH, width - Px(24), bodyBottom, DT_CENTER | oneLine | DT_END_ELLIPSIS);
            Gdi.Text(dc, fonts.Footer, Gdi.Dim, "Yes (A / left click)     No (B / right click)", 0, bodyBottom, width, bodyBottom + footerH, DT_CENTER | oneLine);
            return;
        }

        Gdi.Text(dc, fonts.Title, Gdi.TextColor, model.Page.Title, Px(24), 0, width - Px(24), headerH, DT_LEFT | oneLine | DT_END_ELLIPSIS);

        var entries = model.Page.Entries;
        if (!ReferenceEquals(_topPage, model.Page))
        {
            _topPage = model.Page;
            _top = 0;
        }
        if (model.Selected < _top) _top = model.Selected;
        else if (model.Selected >= _top + rows) _top = model.Selected - rows + 1;

        for (int i = 0; i < rows && _top + i < entries.Count; i++)
        {
            int index = _top + i;
            var entry = entries[index];
            bool selected = index == model.Selected;
            int y = headerH + i * rowH;
            int right = width - Px(24);

            if (selected)
                Gdi.RoundedFill(dc, Gdi.SelectionBrush, Px(SideDip), y + Px(4), width - Px(SideDip), y + rowH - Px(4), Px(20));

            uint color = entry.Enabled ? Gdi.TextColor : Gdi.Dim;
            Gdi.Text(dc, fonts.Glyph, color, entry.Glyph.ToString(), Px(GlyphXDip), y, Px(GlyphXDip + GlyphWidthDip), y + rowH, DT_CENTER | oneLine);

            if (entry.Submenu is not null)
            {
                Gdi.Text(dc, fonts.GlyphSmall, color, "", right - Px(16), y, right, y + rowH, DT_CENTER | oneLine);
                right -= Px(28);
            }

            string? detail = entry.Detail?.Invoke();
            int labelRight = right;
            if (!string.IsNullOrEmpty(detail))
            {
                uint detailColor = selected ? Gdi.DimOnSelection : Gdi.Dim;
                Gdi.Text(dc, fonts.Detail, detailColor, detail, right - Px(DetailWidthDip), y, right, y + rowH, DT_RIGHT | oneLine | DT_END_ELLIPSIS);
                labelRight = right - Px(DetailWidthDip) - Px(8);
            }

            Gdi.Text(dc, fonts.Label, color, entry.Label, Px(LabelXDip), y, labelRight, y + rowH, DT_LEFT | oneLine | DT_END_ELLIPSIS);
        }

        if (entries.Count > rows)
        {
            int trackTop = headerH + Px(8), trackHeight = rows * rowH - Px(16);
            int thumbHeight = Math.Max(Px(24), trackHeight * rows / entries.Count);
            int thumbTop = trackTop + (trackHeight - thumbHeight) * _top / (entries.Count - rows);
            Gdi.Fill(dc, Gdi.DimBrush, width - Px(7), thumbTop, width - Px(4), thumbTop + thumbHeight);
        }

        Gdi.Text(dc, fonts.Footer, Gdi.Dim, "A / click  Select     B / right click  Back     Capture  Close", 0, bodyBottom, width, bodyBottom + footerH, DT_CENTER | oneLine);
    }
}
