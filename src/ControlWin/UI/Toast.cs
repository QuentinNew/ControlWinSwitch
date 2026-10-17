using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>A short message at the bottom center of the screen. Never takes focus, hides itself.</summary>
internal sealed unsafe class Toast : NativeWindow
{
    public const string ClassName = "ControlWin.Toast";

    private const nuint HideTimerId = 1;
    private const uint DurationMs = 1600;
    private const int HeightDip = 64, BottomMarginDip = 48, RightPadDip = 20;

    private char _glyph;
    private string _text = "";
    private uint _dpi = 96;

    public Toast()
    {
        Create(ClassName, WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
    }

    /// <summary>Shows <paramref name="text"/> with a glyph; a toast already on screen is replaced and its timer restarted.</summary>
    public void Show(char glyph, string text)
    {
        _glyph = glyph;
        _text = text;
        Gdi.GetMonitorUnderCursor(out var work, out _dpi);

        int width = Px(TextLeftDip() + RightPadDip) + MeasureText(text, Gdi.GetFonts(_dpi).Label);
        width = Math.Min(width, work.Width - Px(32));
        int height = Px(HeightDip);
        int x = work.Left + (work.Width - width) / 2;
        int y = work.Bottom - height - Px(BottomMarginDip);
        SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(Handle, null, 0);
        SetTimer(Handle, HideTimerId, DurationMs, 0);
    }

    public void Hide()
    {
        KillTimer(Handle, HideTimerId);
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
            case WM_TIMER when wParam == HideTimerId:
                Hide();
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private int Px(int dip) => (int)Math.Round(dip * _dpi / 96.0);

    private int TextLeftDip() => _glyph != '\0' ? 60 : 20;

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

    private void Paint()
    {
        using var paint = new BufferedPaint(Handle);
        var dc = paint.Dc;
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;

        Gdi.Fill(dc, Gdi.BackgroundBrush, 0, 0, paint.Width, paint.Height);
        if (_glyph != '\0')
            Gdi.Text(dc, fonts.Glyph, Gdi.TextColor, _glyph.ToString(), Px(16), 0, Px(16 + 32), paint.Height, DT_CENTER | oneLine);
        Gdi.Text(dc, fonts.Label, Gdi.TextColor, _text, Px(TextLeftDip()), 0, paint.Width - Px(RightPadDip), paint.Height, DT_LEFT | oneLine | DT_END_ELLIPSIS);
    }
}
