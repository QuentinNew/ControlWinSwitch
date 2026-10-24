using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.UI;

/// <summary>Notification-area icon: the app icon, tooltip, context menu. UI thread only.</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    public const uint CallbackMessage = WM_APP + 1;
    public const int CmdOpenMenu = 1, CmdTogglePad = 2, CmdOpenLog = 3, CmdQuit = 4, CmdSettings = 5, CmdEditConfig = 6;

    private const uint IconId = 1;
    /// <summary>Resource id the SDK gives the ApplicationIcon (MAKEINTRESOURCE(32512)).</summary>
    private const nint AppIconResource = 32512;

    private readonly nint _hwnd;
    private nint _icon;
    private bool _added;

    public TrayIcon(nint hwnd) => _hwnd = hwnd;

    public static string Tooltip(int controllers) => controllers switch
    {
        0 => "ControlWinSwitch - controller not found",
        1 => "ControlWinSwitch - controller connected",
        _ => $"ControlWinSwitch - {controllers} controllers connected",
    };

    /// <summary>Adds (or re-adds, after an explorer restart) the icon.</summary>
    public void Add(string tooltip)
    {
        if (_icon == 0) _icon = LoadAppIcon();
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
            AppendMenuW(menu, MF_STRING, CmdQuit, "Quit ControlWinSwitch");

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

    /// <summary>The app icon embedded in the exe (assets\icon.ico), at the small-icon size for the screen's DPI.</summary>
    private static nint LoadAppIcon()
    {
        nint icon;
        int hr = LoadIconMetric(GetModuleHandleW(null), AppIconResource, LIM_SMALL, &icon);
        if (hr >= 0) return icon;
        Log.Error($"Tray icon: LoadIconMetric failed (0x{hr:X8})");
        return 0;
    }
}
