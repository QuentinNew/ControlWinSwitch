using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.UI;

/// <summary>
/// The Volume / Brightness slider: a toast-style bar centered on the screen, with the node's glyph, the bar and
/// the value. Never takes focus.
/// </summary>
internal sealed unsafe class SliderOverlay : NativeWindow
{
    public const string ClassName = "ControlWinSwitch.Slider";

    private const int WidthDip = 420, HeightDip = 64, GlyphLeftDip = 16, GlyphDip = 32, BarLeftDip = 64,
        NumberDip = 56, RightPadDip = 16, BarHeightDip = 6, ThumbDip = 16;

    private static nint s_mutedBrush;
    private static nint MutedBrush => s_mutedBrush != 0 ? s_mutedBrush : s_mutedBrush = CreateSolidBrush(Gdi.Dim);

    private char _glyph;
    private int _value;
    private bool _muted;
    private uint _dpi = 96;

    public SliderOverlay()
    {
        Create(ClassName, WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
    }

    public void Show(char glyph, int value, bool muted)
    {
        _glyph = glyph;
        _value = value;
        _muted = muted;
        Gdi.GetMonitorUnderCursor(out var work, out _dpi);

        int width = Math.Min(Px(WidthDip), work.Width - Px(32));
        int height = Px(HeightDip);
        int x = work.Left + (work.Width - width) / 2;
        int y = work.Top + (work.Height - height) / 2;
        SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(Handle, null, 0);
    }

    public void Update(char glyph, int value, bool muted)
    {
        if (glyph == _glyph && value == _value && muted == _muted) return;
        _glyph = glyph;
        _value = value;
        _muted = muted;
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
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private int Px(int dip) => (int)Math.Round(dip * _dpi / 96.0);

    private void Paint()
    {
        using var paint = new BufferedPaint(Handle);
        var dc = paint.Dc;
        var fonts = Gdi.GetFonts(_dpi);
        const uint oneLine = DT_SINGLELINE | DT_VCENTER;
        int w = paint.Width, h = paint.Height;

        Gdi.Fill(dc, Gdi.BackgroundBrush, 0, 0, w, h);
        if (_glyph != '\0')
            Gdi.Text(dc, fonts.Glyph, _muted ? Gdi.Dim : Gdi.TextColor, _glyph.ToString(), Px(GlyphLeftDip), 0, Px(GlyphLeftDip + GlyphDip), h, DT_CENTER | oneLine);

        int left = Px(BarLeftDip), right = w - Px(RightPadDip + NumberDip);
        int barH = Px(BarHeightDip), top = (h - barH) / 2;
        int thumb = Px(ThumbDip);
        left += thumb / 2; // the thumb stays inside the bar's ends
        right -= thumb / 2;
        Gdi.RoundedFill(dc, Gdi.DimBrush, left, top, right, top + barH, barH);
        int filled = left + (right - left) * Math.Clamp(_value, 0, 100) / 100;
        if (filled - left >= barH) Gdi.RoundedFill(dc, _muted ? MutedBrush : Gdi.SelectionBrush, left, top, filled, top + barH, barH);
        int thumbTop = (h - thumb) / 2;
        Gdi.RoundedFill(dc, _muted ? MutedBrush : Gdi.TextBrush, filled - thumb / 2, thumbTop, filled - thumb / 2 + thumb, thumbTop + thumb, thumb);
        right += thumb / 2;

        Gdi.Text(dc, fonts.Label, _muted ? Gdi.Dim : Gdi.TextColor, _value.ToString(), right, 0, w - Px(RightPadDip), h, DT_RIGHT | oneLine);
    }
}
