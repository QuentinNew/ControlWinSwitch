using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.UI;

/// <summary>Notification-area icon: generated glyph, tooltip, context menu. UI thread only.</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    public const uint CallbackMessage = WM_APP + 1;
    public const int CmdOpenMenu = 1, CmdTogglePad = 2, CmdOpenLog = 3, CmdQuit = 4, CmdSettings = 5, CmdEditConfig = 6;

    private const uint IconId = 1;
    private const int IconSize = 32;

    private readonly nint _hwnd;
    private nint _icon;
    private bool _added;

    public TrayIcon(nint hwnd) => _hwnd = hwnd;

    public static string Tooltip(int controllers) => controllers switch
    {
        0 => "ControlWin - controller not found",
        1 => "ControlWin - controller connected",
        _ => $"ControlWin - {controllers} controllers connected",
    };

    /// <summary>Adds (or re-adds, after an explorer restart) the icon.</summary>
    public void Add(string tooltip)
    {
        if (_icon == 0) _icon = CreateGlyphIcon();
        var data = NewData(tooltip);
        _added = Shell_NotifyIconW(NIM_ADD, &data) != 0;
        if (!_added)
        {
            Log.Error("Tray icon: NIM_ADD failed");
            return;
        }
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    public void SetTooltip(string tooltip)
    {
        if (!_added) return;
        var data = NewData(tooltip);
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    /// <summary>Shows the context menu at the cursor and returns the chosen command (0 = dismissed).</summary>
    public int ShowMenu(bool padEnabled, string? padNote = null)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, CmdOpenMenu, "Open menu");
            AppendMenuW(menu, MF_STRING | (padEnabled ? MF_CHECKED : 0), CmdTogglePad, padNote is null ? "Virtual pad" : $"Virtual pad ({padNote})");
            AppendMenuW(menu, MF_STRING, CmdSettings, "Settings");
            AppendMenuW(menu, MF_STRING, CmdEditConfig, "Edit config file");
            AppendMenuW(menu, MF_STRING, CmdOpenLog, "Open log");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdQuit, "Quit ControlWin");

            POINT pt;
            GetCursorPos(&pt);
            SetForegroundWindow(_hwnd); // required, or the menu never closes when clicking elsewhere
            int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            return command;
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>Removes the icon from the tray; safe to call twice.</summary>
    public void Dispose()
    {
        if (_added)
        {
            var data = NewData("");
            Shell_NotifyIconW(NIM_DELETE, &data);
            _added = false;
        }
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private NOTIFYICONDATAW NewData(string tooltip)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        int n = Math.Min(tooltip.Length, 127);
        for (int i = 0; i < n; i++) data.szTip[i] = tooltip[i];
        return data;
    }

    /// <summary>White controller glyph on a transparent 32x32 icon: draw white-on-black text, alpha = coverage.</summary>
    private static nint CreateGlyphIcon()
    {
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = IconSize, biHeight = -IconSize,
            biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        var dc = CreateCompatibleDC(0);
        var color = CreateDIBSection(dc, &info, 0, &bits, 0, 0);
        if (color == 0)
        {
            DeleteDC(dc);
            return 0;
        }

        var oldBitmap = SelectObject(dc, color);
        var font = Gdi.CreateIconFont(28);
        var oldFont = SelectObject(dc, font);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, Rgb(255, 255, 255));
        var rc = new RECT(0, 0, IconSize, IconSize);
        DrawTextW(dc, "", -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        GdiFlush();

        uint* pixels = (uint*)bits;
        for (int i = 0; i < IconSize * IconSize; i++)
        {
            uint coverage = (pixels[i] >> 8) & 0xFF; // green channel of white-on-black text
            pixels[i] = (coverage << 24) | 0x00FFFFFF;
        }

        SelectObject(dc, oldFont);
        SelectObject(dc, oldBitmap);
        DeleteObject(font);
        DeleteDC(dc);

        var mask = CreateBitmap(IconSize, IconSize, 1, 1, null);
        var icon = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        var handle = CreateIconIndirect(&icon);
        DeleteObject(mask);
        DeleteObject(color);
        return handle;
    }
}
