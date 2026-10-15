using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>Fonts for one DPI. Sizes are given in DIPs (1/96 inch) and scaled with <see cref="Px"/>.</summary>
internal sealed class FontSet
{
    public required uint Dpi { get; init; }
    public nint Title, Label, Detail, Footer, Glyph, GlyphSmall;

    public int Px(int dip) => (int)Math.Round(dip * Dpi / 96.0);

    public void Delete()
    {
        foreach (var font in new[] { Title, Label, Detail, Footer, Glyph, GlyphSmall })
            if (font != 0) DeleteObject(font);
    }
}

/// <summary>Shared GDI resources: palette, fonts cached per DPI, monitor lookup, DPI scaling.</summary>
internal static unsafe class Gdi
{
    public static readonly uint Background = Rgb(0x1F, 0x1F, 0x23);
    public static readonly uint Selection = Rgb(0x2F, 0x5B, 0xD3);
    public static readonly uint TextColor = Rgb(0xFF, 0xFF, 0xFF);
    public static readonly uint Dim = Rgb(0xA0, 0xA0, 0xA8);
    public static readonly uint DimOnSelection = Rgb(0xE0, 0xE6, 0xFA);

    private static nint s_backgroundBrush, s_selectionBrush, s_dimBrush, s_panelBrush, s_onBrush;
    private static FontSet? s_fonts;
    private static string? s_iconFace;

    public static nint BackgroundBrush => s_backgroundBrush != 0 ? s_backgroundBrush : s_backgroundBrush = CreateSolidBrush(Background);
    public static nint SelectionBrush => s_selectionBrush != 0 ? s_selectionBrush : s_selectionBrush = CreateSolidBrush(Selection);
    public static nint DimBrush => s_dimBrush != 0 ? s_dimBrush : s_dimBrush = CreateSolidBrush(Rgb(0x55, 0x55, 0x5C));

    public static nint PanelBrush => s_panelBrush != 0 ? s_panelBrush : s_panelBrush = CreateSolidBrush(Rgb(0x2A, 0x2A, 0x30));
    public static nint OnBrush => s_onBrush != 0 ? s_onBrush : s_onBrush = CreateSolidBrush(Rgb(0x2E, 0x9E, 0x5B));

    public static FontSet GetFonts(uint dpi)
    {
        if (s_fonts is not null && s_fonts.Dpi == dpi) return s_fonts;
        s_fonts?.Delete();

        int Px(int dip) => (int)Math.Round(dip * dpi / 96.0);
        var icons = IconFace();
        s_fonts = new FontSet
        {
            Dpi = dpi,
            Title = Create(Px(26), FW_SEMIBOLD, "Segoe UI Semibold"),
            Label = Create(Px(20), FW_NORMAL, "Segoe UI"),
            Detail = Create(Px(16), FW_NORMAL, "Segoe UI"),
            Footer = Create(Px(15), FW_NORMAL, "Segoe UI"),
            Glyph = Create(Px(22), FW_NORMAL, icons),
            GlyphSmall = Create(Px(14), FW_NORMAL, icons),
        };
        return s_fonts;
    }

    /// <summary>Work area and DPI of the monitor under the cursor (the one the user is looking at).</summary>
    public static void GetMonitorUnderCursor(out RECT work, out uint dpi)
    {
        POINT pt;
        GetCursorPos(&pt);
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);

        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        work = GetMonitorInfoW(monitor, &info) != 0 ? info.rcWork : new RECT(0, 0, 1920, 1080);

        uint x = 96, y = 96;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &x, &y) != 0 || x == 0) x = 96;
        dpi = x;
    }

    public static void Fill(nint dc, nint brush, int l, int t, int r, int b)
    {
        var rc = new RECT(l, t, r, b);
        FillRect(dc, &rc, brush);
    }

    /// <summary>Draws one line of text (the caller sets <c>SetBkMode(TRANSPARENT)</c> once).</summary>
    public static void Text(nint dc, nint font, uint color, string text, int l, int t, int r, int b, uint format)
    {
        SelectObject(dc, font);
        SetTextColor(dc, color);
        var rc = new RECT(l, t, r, b);
        DrawTextW(dc, text, -1, &rc, format | DT_NOPREFIX);
    }

    public static void RoundedFill(nint dc, nint brush, int l, int t, int r, int b, int diameter)
    {
        var oldBrush = SelectObject(dc, brush);
        var oldPen = SelectObject(dc, GetStockObject(NULL_PEN));
        RoundRect(dc, l, t, r, b, diameter, diameter);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBrush);
    }

    private static nint Create(int pixelHeight, int weight, string face, uint quality = CLEARTYPE_QUALITY) =>
        CreateFontW(-pixelHeight, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, quality, 0, face);

    /// <summary>Icon-font face at an exact pixel height, grayscale-antialiased (for bitmaps whose alpha comes from coverage). Caller deletes it.</summary>
    public static nint CreateIconFont(int pixelHeight) => Create(pixelHeight, FW_NORMAL, IconFace(), ANTIALIASED_QUALITY);

    /// <summary>"Segoe Fluent Icons" ships with Windows 11; Windows 10 only has "Segoe MDL2 Assets" (same code points).</summary>
    private static string IconFace()
    {
        if (s_iconFace is not null) return s_iconFace;
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegoeIcons.ttf");
            s_iconFace = File.Exists(file) ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }
        catch
        {
            s_iconFace = "Segoe MDL2 Assets";
        }
        return s_iconFace;
    }
}

/// <summary>WM_PAINT helper: a double-buffered device context that is blitted to the window on dispose.</summary>
internal sealed unsafe class BufferedPaint : IDisposable
{
    private readonly nint _hwnd;
    private PAINTSTRUCT _ps;
    private readonly nint _target, _memory, _bitmap, _oldBitmap, _oldFont;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The off-screen device context to draw on.</summary>
    public nint Dc => _memory;

    public BufferedPaint(nint hwnd)
    {
        _hwnd = hwnd;
        fixed (PAINTSTRUCT* ps = &_ps) _target = BeginPaint(hwnd, ps);

        RECT client;
        GetClientRect(hwnd, &client);
        Width = client.Width;
        Height = client.Height;

        _memory = CreateCompatibleDC(_target);
        _bitmap = CreateCompatibleBitmap(_target, Math.Max(1, Width), Math.Max(1, Height));
        _oldBitmap = SelectObject(_memory, _bitmap);
        _oldFont = SelectObject(_memory, GetStockObject(DEFAULT_GUI_FONT));
        SetBkMode(_memory, TRANSPARENT);
    }

    public void Dispose()
    {
        BitBlt(_target, 0, 0, Width, Height, _memory, 0, 0, SRCCOPY);
        SelectObject(_memory, _oldFont);
        SelectObject(_memory, _oldBitmap);
        DeleteObject(_bitmap);
        DeleteDC(_memory);
        fixed (PAINTSTRUCT* ps = &_ps) EndPaint(_hwnd, ps);
    }
}
