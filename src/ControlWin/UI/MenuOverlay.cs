using ControlWin.Actions;
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
    private const int CornerDip = 8; // matches the Windows 11 rounded window corners
    private const int CloseDip = 48, ControllersDip = 44, ControllerPicDip = 28;
    private const int SideDip = 12, GlyphXDip = 24, GlyphWidthDip = 32, LabelXDip = 72, DetailWidthDip = 150;

    private MenuModel? _model;
    private int _lastMouseX = -1, _lastMouseY = -1;

    /// <summary>Mouse: row index hovered / clicked, "back" (right click), confirmation answered, wheel step (-1 / +1).</summary>
    public event Action<int>? RowHovered, RowClicked, Scrolled;
    public event Action? BackRequested, CloseRequested;
    public event Action<bool>? ConfirmAnswered;
    private RECT _work;
    private uint _dpi = 96;
    private int _top; // first visible row when the page is longer than MaxRows
    private MenuPage? _topPage;
    private IReadOnlyList<SlotTile> _controllers = [];

    public MenuOverlay()
    {
        // Layered: position, size and pixels change together in one UpdateLayeredWindow, so a page change
        // never shows the previous page moved or cut to the new size (a plain window repaints after the move).
        Create(ClassName, WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
    }

    public bool IsShown => IsWindowVisible(Handle) != 0;

    public void Show(MenuModel model, IReadOnlyList<SlotTile>? controllers = null)
    {
        _model = model;
        _controllers = controllers ?? [];
        _lastMouseX = _lastMouseY = -1;
        _top = 0;
        _topPage = null;
        RadialIcons.Clear(); // pictures are read again each time the menu opens
        Gdi.GetMonitorUnderCursor(out _work, out _dpi);
        Render();
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        // Re-assert topmost: at logon the shell can push a window created early below the others.
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Call after the model changed.</summary>
    public void Refresh()
    {
        if (_model is null) return;
        Render();
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

        if (IsCloseButton(pt.X, pt.Y)) return true;
        if (_model.IsConfirming) return pt.Y >= Px(HeaderDip); // body answers the question
        int index = RowAt(pt.Y);
        return index >= 0 && _model.Page.Entries[index].Enabled;
    }

    private void OnMouseMove(int x, int y)
    {
        // The menu appearing, or resizing to another page, under a resting cursor sends a mouse move:
        // only react to real movement, compared in screen coordinates since the window itself moves.
        POINT screen;
        GetCursorPos(&screen);
        bool first = _lastMouseX < 0;
        bool moved = screen.X != _lastMouseX || screen.Y != _lastMouseY;
        _lastMouseX = screen.X;
        _lastMouseY = screen.Y;
        if (first || !moved) return;

        int index = RowAt(y);
        if (index >= 0) RowHovered?.Invoke(index);
    }

    private bool IsCloseButton(int x, int y) => y < Px(HeaderDip) && x >= Px(WidthDip) - Px(CloseDip);

    private void OnClick(int x, int y)
    {
        if (_model is null) return;
        if (IsCloseButton(x, y))
        {
            CloseRequested?.Invoke();
            return;
        }
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

    /// <summary>Height of the controller strip between the rows and the footer (0 = none): main page only.</summary>
    private int StripHeight() => _controllers.Count > 0 && _model is { IsConfirming: false, Depth: 1 } ? Px(ControllersDip) : 0;

    /// <summary>Draws the current page off-screen, then moves, resizes and repaints the window in one step.</summary>
    private void Render()
    {
        int width = Px(WidthDip);
        int height = Px(HeaderDip + VisibleRows() * RowDip + FooterDip) + StripHeight();

        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
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

        Paint(dc, width, height);
        GdiFlush();
        RoundCorners((uint*)bits, width, height, Px(CornerDip));

        var dst = new POINT { X = _work.Left + (_work.Width - width) / 2, Y = _work.Top + (_work.Height - height) / 2 };
        var size = new SIZE { cx = width, cy = height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, 0, &dst, &size, dc, &src, 0, &blend, ULW_ALPHA);

        SelectObject(dc, oldBitmap);
        DeleteObject(dib);
        DeleteDC(dc);
    }

    /// <summary>
    /// The menu is opaque (GDI leaves the alpha byte unset): makes every pixel opaque, except outside the
    /// antialiased rounded corners, which fade out (premultiplied, as UpdateLayeredWindow expects).
    /// </summary>
    private static void RoundCorners(uint* pixels, int width, int height, int radius)
    {
        for (int i = 0, n = width * height; i < n; i++) pixels[i] |= 0xFF000000;

        for (int y = 0; y < radius; y++)
            for (int x = 0; x < radius; x++)
            {
                // Coverage of this corner pixel by the quarter circle centered at (radius, radius).
                double dx = radius - (x + 0.5), dy = radius - (y + 0.5);
                double coverage = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                if (coverage >= 1) continue;
                Fade(ref pixels[y * width + x], coverage);
                Fade(ref pixels[y * width + width - 1 - x], coverage);
                Fade(ref pixels[(height - 1 - y) * width + x], coverage);
                Fade(ref pixels[(height - 1 - y) * width + width - 1 - x], coverage);
            }

        static void Fade(ref uint pixel, double coverage)
        {
            uint a = (uint)Math.Round(coverage * 255);
            uint b = (pixel & 0xFF) * a / 255, g = ((pixel >> 8) & 0xFF) * a / 255, r = ((pixel >> 16) & 0xFF) * a / 255;
            pixel = (a << 24) | (r << 16) | (g << 8) | b;
        }
    }

    private void Paint(nint dc, int width, int height)
    {
        var fonts = Gdi.GetFonts(_dpi);

        Gdi.Fill(dc, Gdi.BackgroundBrush, 0, 0, width, height);
        if (_model is not { } model) return;

        int headerH = Px(HeaderDip), rowH = Px(RowDip), footerH = Px(FooterDip);
        int rows = VisibleRows();
        int bodyBottom = headerH + rows * rowH;
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;

        if (model.IsConfirming)
        {
            var entry = model.CurrentEntry;
            Gdi.Text(dc, fonts.Title, Gdi.TextColor, entry?.Label ?? "", Px(24), 0, width - Px(CloseDip), headerH, DT_LEFT | oneLine | DT_END_ELLIPSIS);
            Gdi.Text(dc, fonts.GlyphSmall, Gdi.Dim, "", width - Px(CloseDip), 0, width, headerH, DT_CENTER | oneLine);
            Gdi.Text(dc, fonts.Title, Gdi.TextColor, model.ConfirmPrompt ?? "", Px(24), headerH, width - Px(24), bodyBottom, DT_CENTER | oneLine | DT_END_ELLIPSIS);
            Gdi.Text(dc, fonts.Footer, Gdi.Dim, "Yes (A / left click)     No (B / right click)", 0, bodyBottom, width, bodyBottom + footerH, DT_CENTER | oneLine);
            return;
        }

        Gdi.Text(dc, fonts.Title, Gdi.TextColor, model.Page.Title, Px(24), 0, width - Px(CloseDip), headerH, DT_LEFT | oneLine | DT_END_ELLIPSIS);
        Gdi.Text(dc, fonts.GlyphSmall, Gdi.Dim, "", width - Px(CloseDip), 0, width, headerH, DT_CENTER | oneLine);

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

            // A node's own color only shows while it is selected; every other row uses the default colors.
            uint? own = selected ? entry.Color : null;
            if (selected)
            {
                nint brush = own is { } picked ? CreateSolidBrush(picked) : Gdi.SelectionBrush;
                Gdi.RoundedFill(dc, brush, Px(SideDip), y + Px(4), width - Px(SideDip), y + rowH - Px(4), Px(20));
                if (own is not null) DeleteObject(brush);
            }

            uint color = entry.Enabled ? (own is { } lit ? RadialOverlay.GlyphColor(lit, true) : Gdi.TextColor) : Gdi.Dim;
            nint image = RadialIcons.Get(entry, out _);
            if (image != 0)
            {
                int size = Px(GlyphWidthDip);
                ControllerPictures.DrawRounded(dc, image, Px(GlyphXDip), y + (rowH - size) / 2, size, Px(8));
            }
            else
            {
                Gdi.Text(dc, fonts.Glyph, color, entry.Glyph.ToString(), Px(GlyphXDip), y, Px(GlyphXDip + GlyphWidthDip), y + rowH, DT_CENTER | oneLine);
            }

            if (entry.Submenu is not null)
            {
                Gdi.Text(dc, fonts.GlyphSmall, color, "", right - Px(16), y, right, y + rowH, DT_CENTER | oneLine);
                right -= Px(28);
            }

            string? detail = entry.Detail?.Invoke();
            int labelRight = right;
            if (!string.IsNullOrEmpty(detail))
            {
                uint detailColor = own is not null ? color : selected ? Gdi.DimOnSelection : Gdi.Dim;
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

        int stripH = StripHeight();
        if (stripH > 0)
        {
            int picR = Px(ControllerPicDip) / 2, stripW = ControllerTile.StripLayout(_dpi, _controllers, picR, out var xs);
            int left = (width - stripW) / 2, cy = bodyBottom + stripH / 2;
            for (int i = 0; i < xs.Length; i++)
            {
                var c = _controllers[i];
                int x = left + xs[i], top = cy - picR;
                if (c.Image != 0)
                    ControllerPictures.DrawRounded(dc, c.Image, x, top, picR * 2, picR * 2);
                else
                {
                    Gdi.RoundedFill(dc, Gdi.OnBrush, x, top, x + picR * 2, top + picR * 2, picR * 2);
                    Gdi.Text(dc, fonts.GlyphSmall, Gdi.TextColor, ControllerTile.GamepadGlyph, x, top, x + picR * 2, top + picR * 2, DT_CENTER | oneLine);
                }
                ControllerTile.PaintBars(dc, _dpi, c, x + picR * 2 + Px(ControllerTile.PicGapDip), cy);
            }
        }

        Gdi.Text(dc, fonts.Footer, Gdi.Dim, "A / click  Select     B / right click  Back     Capture  Close", 0, bodyBottom + stripH, width, bodyBottom + stripH + footerH, DT_CENTER | oneLine);
    }
}
