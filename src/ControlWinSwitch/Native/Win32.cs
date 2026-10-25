using System.Runtime.InteropServices;

namespace ControlWinSwitch.Native;

// Hand-written, blittable Win32 interop (AOT friendly). Only what the app uses.

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PAINTSTRUCT
{
    public nint hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore, fIncUpdate;
    public fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE { public int cx, cy; }

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GdiplusStartupInput
{
    public uint GdiplusVersion;
    public nint DebugEventCallback;
    public int SuppressBackgroundThread, SuppressExternalCodecs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor, rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MONITORINFOEXW
{
    public MONITORINFO Info;
    public fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PHYSICAL_MONITOR
{
    public nint hPhysicalMonitor;
    public fixed char szPhysicalMonitorDescription[128];
}

[StructLayout(LayoutKind.Sequential)]
internal struct OVERLAPPED
{
    public nuint Internal, InternalHigh;
    public uint Offset, OffsetHigh;
    public nint hEvent;
}

/// <summary>DEV_BROADCAST_DEVICEINTERFACE_W header; the NUL-terminated name follows at offset 28.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DEV_BROADCAST_DEVICEINTERFACE_W
{
    public uint dbcc_size, dbcc_devicetype, dbcc_reserved;
    public Guid dbcc_classguid;
    public char dbcc_name;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int dx, dy;
    public uint mouseData, dwFlags, time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct INPUT
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public MOUSEINPUT mi;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct STARTUPINFOW
{
    public uint cb;
    public char* lpReserved, lpDesktop, lpTitle;
    public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public ushort wShowWindow, cbReserved2;
    public nint lpReserved2, hStdInput, hStdOutput, hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public nint hProcess, hThread;
    public uint dwProcessId, dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SHELLEXECUTEINFOW
{
    public uint cbSize, fMask;
    public nint hwnd;
    public char* lpVerb, lpFile, lpParameters, lpDirectory;
    public int nShow;
    public nint hInstApp, lpIDList;
    public char* lpClass;
    public nint hkeyClass;
    public uint dwHotKey;
    public nint hIconOrMonitor, hProcess;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID, uFlags, uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public uint cbSize, dwFlags;
    public nint hwndTrack;
    public uint dwHoverTime;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OPENFILENAMEW
{
    public uint lStructSize;
    public nint hwndOwner, hInstance;
    public char* lpstrFilter, lpstrCustomFilter;
    public uint nMaxCustFilter, nFilterIndex;
    public char* lpstrFile;
    public uint nMaxFile;
    public char* lpstrFileTitle;
    public uint nMaxFileTitle;
    public char* lpstrInitialDir, lpstrTitle;
    public uint Flags;
    public ushort nFileOffset, nFileExtension;
    public char* lpstrDefExt;
    public nint lCustData, lpfnHook;
    public char* lpTemplateName;
    public nint pvReserved;
    public uint dwReserved, FlagsEx;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SHFILEINFOW
{
    public nint hIcon;
    public int iIcon;
    public uint dwAttributes;
    public fixed char szDisplayName[260];
    public fixed char szTypeName[80];
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth, biHeight;
    public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage;
    public int biXPelsPerMeter, biYPelsPerMeter;
    public uint biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID { public uint LowPart; public int HighPart; }

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
    public uint BatteryLifeTime, BatteryFullLifeTime;
}

internal static unsafe partial class Win32
{
    // ---- Messages / constants -------------------------------------------------------------
    public const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_QUIT = 0x0012,
        WM_ERASEBKGND = 0x0014, WM_POWERBROADCAST = 0x0218, WM_TIMER = 0x0113, WM_COMMAND = 0x0111,
        WM_DEVICECHANGE = 0x0219, WM_NULL = 0x0000, WM_DPICHANGED = 0x02E0,
        WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, WM_APP = 0x8000;

    public const uint WM_CHAR = 0x0102, WM_KILLFOCUS = 0x0008;
    public const int GWL_EXSTYLE = -20;
    public const byte VK_MENU = 0x12;
    public const uint KEYEVENTF_KEYUP = 0x2;
    public const nint MA_ACTIVATE = 1;
    public const uint WM_MOUSEACTIVATE = 0x0021, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_MOUSEWHEEL = 0x020A;
    public const nint MA_NOACTIVATE = 3;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly nint HWND_TOPMOST = -1;

    public const uint PM_REMOVE = 1, QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x4;
    public const uint WAIT_OBJECT_0 = 0, INFINITE = 0xFFFFFFFF;

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;

    public const uint PBT_APMRESUMEAUTOMATIC = 0x12, PBT_APMSUSPEND = 0x4;

    public const uint DT_LEFT = 0, DT_CENTER = 1, DT_RIGHT = 2, DT_VCENTER = 4, DT_WORDBREAK = 0x10,
        DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
    public const int TRANSPARENT = 1;
    public const uint SRCCOPY = 0x00CC0020;
    public const int NULL_PEN = 8;
    public const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
    public const uint ANTIALIASED_QUALITY = 4, CLEARTYPE_QUALITY = 5;
    public const uint DEFAULT_CHARSET = 1;
    public const int DEFAULT_GUI_FONT = 17;

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    public const uint MF_STRING = 0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    public const uint NIN_KEYSELECT = 0x401;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;

    public const uint MSGFLT_ALLOW = 1;

    public const uint STARTF_USESHOWWINDOW = 1;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    public const uint SEE_MASK_FLAG_NO_UI = 0x400;

    public const uint SYNCHRONIZE = 0x00100000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x8, TOKEN_DUPLICATE = 0x2, TOKEN_ASSIGN_PRIMARY = 0x1,
        TOKEN_ADJUST_DEFAULT = 0x80, TOKEN_ADJUST_SESSIONID = 0x100, TOKEN_ADJUST_PRIVILEGES = 0x20;
    public const int SecurityImpersonation = 2, TokenPrimary = 1, TokenElevation = 20;
    public const uint SE_PRIVILEGE_ENABLED = 2;

    public const uint EWX_SHUTDOWN = 0x1, EWX_REBOOT = 0x2, EWX_POWEROFF = 0x8, EWX_FORCEIFHUNG = 0x10,
        EWX_HYBRID_SHUTDOWN = 0x00400000;
    public const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;
    public const int ERROR_ALREADY_EXISTS = 183, ERROR_ACCESS_DENIED = 5;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int MDT_EFFECTIVE_DPI = 0;

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---- user32 ---------------------------------------------------------------------------
    [LibraryImport("user32.dll", SetLastError = true)] public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] public static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll")] public static partial int InvalidateRect(nint hwnd, RECT* rect, int erase);
    [LibraryImport("user32.dll")] public static partial int UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] public static partial int TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] public static partial int PostMessageW(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint wakeMask, uint flags);
    [LibraryImport("user32.dll")] public static partial uint SendInput(uint count, INPUT* inputs, int size);
    [LibraryImport("user32.dll")] public static partial int GetCursorPos(POINT* pt);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport("user32.dll")] public static partial int GetMonitorInfoW(nint monitor, MONITORINFO* info);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] public static partial int GetMonitorInfoExW(nint monitor, MONITORINFOEXW* info);
    [LibraryImport("user32.dll")] public static partial int GetDisplayConfigBufferSizes(uint flags, uint* paths, uint* modes);
    [LibraryImport("user32.dll")] public static partial int QueryDisplayConfig(uint flags, uint* paths, void* pathArray, uint* modes, void* modeArray, nint topology);
    [LibraryImport("user32.dll")] public static partial int DisplayConfigGetDeviceInfo(void* packet);
    public const uint QDC_ONLY_ACTIVE_PATHS = 2;
    [LibraryImport("dxva2.dll")] public static partial int GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, uint* count);
    [LibraryImport("dxva2.dll")] public static partial int GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, PHYSICAL_MONITOR* monitors);
    [LibraryImport("dxva2.dll")] public static partial int DestroyPhysicalMonitor(nint monitor);
    [LibraryImport("dxva2.dll")] public static partial int GetMonitorBrightness(nint monitor, uint* min, uint* current, uint* max);
    [LibraryImport("dxva2.dll")] public static partial int SetMonitorBrightness(nint monitor, uint value);
    [LibraryImport("user32.dll")] public static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] public static partial int EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] public static partial int GetClientRect(nint hwnd, RECT* rect);
    [LibraryImport("user32.dll")] public static partial int FillRect(nint hdc, RECT* rect, nint brush);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(nint hdc, string text, int count, RECT* rect, uint format);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] public static partial int KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32.dll")] public static partial int ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint changeInfo);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowW(string? className, string? windowName);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, uint* pid);
    [LibraryImport("user32.dll")] public static partial nint GetShellWindow();
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int AppendMenuW(nint menu, uint flags, nuint id, string? item);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [LibraryImport("user32.dll")] public static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] public static partial int SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint hwnd, uint flags);
    public const uint GA_ROOT = 2;
    [LibraryImport("user32.dll")] public static partial int GetClassNameW(nint hwnd, char* name, int max);
    [LibraryImport("user32.dll")] public static partial int GetWindowTextW(nint hwnd, char* text, int max);
    [LibraryImport("user32.dll")] public static partial nint LoadCursorW(nint instance, nint name);
    public const nint IDC_ARROW = 32512, IDC_HAND = 32649, IDC_IBEAM = 32513;
    public const uint WM_SETCURSOR = 0x0020;
    public const int HTCLIENT = 1;
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] public static partial int ScreenToClient(nint hwnd, POINT* pt);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32.dll")] public static partial void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [LibraryImport("user32.dll")] public static partial int DestroyIcon(nint icon);
    public const int LIM_SMALL = 0;
    [LibraryImport("comctl32.dll")] public static partial int LoadIconMetric(nint hInstance, nint name, int size, nint* icon);
    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint hdc);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial int OpenClipboard(nint owner);
    [LibraryImport("user32.dll")] public static partial int CloseClipboard();
    [LibraryImport("user32.dll")] public static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll")] public static partial int EmptyClipboard();
    [LibraryImport("user32.dll")] public static partial nint SetClipboardData(uint format, nint handle);
    public const uint CF_UNICODETEXT = 13;
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int virtualKey);
    [LibraryImport("user32.dll")] public static partial uint GetDoubleClickTime();
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    [LibraryImport("user32.dll", SetLastError = true)] public static partial int RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [LibraryImport("user32.dll")] public static partial int UnregisterHotKey(nint hwnd, int id);
    public const uint WM_HOTKEY = 0x0312, MOD_NOREPEAT = 0x4000;
    [LibraryImport("kernel32.dll")] public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalFree(nint handle);
    public const uint GMEM_MOVEABLE = 0x0002;
    [LibraryImport("kernel32.dll")] public static partial void* GlobalLock(nint handle);
    [LibraryImport("kernel32.dll")] public static partial int GlobalUnlock(nint handle);
    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")] public static partial int GetOpenFileNameW(OPENFILENAMEW* ofn);
    public const uint OFN_FILEMUSTEXIST = 0x1000, OFN_PATHMUSTEXIST = 0x800, OFN_EXPLORER = 0x80000, OFN_NOCHANGEDIR = 0x8;

    // ---- gdi32 ----------------------------------------------------------------------------
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] public static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial int Polygon(nint hdc, POINT* points, int count);
    [LibraryImport("gdi32.dll")] public static partial int DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial int GdiFlush();
    [LibraryImport("gdi32.dll")] public static partial uint GetPixel(nint hdc, int x, int y);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetTextExtentPoint32W(nint hdc, string text, int count, SIZE* size);
    [LibraryImport("gdi32.dll")] public static partial int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] public static partial nint GetStockObject(int obj);
    [LibraryImport("gdi32.dll")] public static partial int RoundRect(nint hdc, int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic,
        uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    // ---- gdiplus (flat API: antialiased circles with alpha for the radial menu) ----------------
    [LibraryImport("gdiplus.dll")] public static partial int GdiplusStartup(nuint* token, GdiplusStartupInput* input, nint output);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreateFromHDC(nint hdc, nint* graphics);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDeleteGraphics(nint graphics);
    [LibraryImport("gdiplus.dll")] public static partial int GdipSetSmoothingMode(nint graphics, int mode);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreateSolidFill(uint argb, nint* brush);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDeleteBrush(nint brush);
    [LibraryImport("gdiplus.dll")] public static partial int GdipFillEllipseI(nint graphics, nint brush, int x, int y, int w, int h);
    [LibraryImport("gdiplus.dll")] public static partial int GdipFillRectangleI(nint graphics, nint brush, int x, int y, int w, int h);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, byte* scan0, nint* bitmap);
    [LibraryImport("gdiplus.dll")] public static partial int GdipGetImageGraphicsContext(nint image, nint* graphics);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDisposeImage(nint image);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCloneImage(nint image, nint* clone);
    [LibraryImport("gdiplus.dll")] public static partial int GdipImageRotateFlip(nint image, int rotateFlipType);
    public const int Rotate90FlipNone = 1, Rotate270FlipNone = 3; // clockwise / counterclockwise quarter turn
    public const int PixelFormat32bppPARGB = 0x000E200B;
    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int GdipCreateBitmapFromFile(string file, nint* bitmap);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreateBitmapFromHICON(nint icon, nint* bitmap);
    [LibraryImport("gdiplus.dll")] public static partial int GdipGetImageWidth(nint image, uint* width);
    [LibraryImport("gdiplus.dll")] public static partial int GdipGetImageHeight(nint image, uint* height);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDrawImageRectI(nint graphics, nint image, int x, int y, int w, int h);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreatePath(int fillMode, nint* path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDeletePath(nint path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipAddPathEllipseI(nint path, int x, int y, int w, int h);
    [LibraryImport("gdiplus.dll")] public static partial int GdipSetClipPath(nint graphics, nint path, int combineMode);
    [LibraryImport("gdiplus.dll")] public static partial int GdipResetClip(nint graphics);
    [LibraryImport("gdiplus.dll")] public static partial int GdipSetInterpolationMode(nint graphics, int mode);
    public const int InterpolationModeHighQualityBicubic = 7;
    [LibraryImport("gdiplus.dll")] public static partial int GdipDrawImageRectRectI(nint graphics, nint image, int dx, int dy, int dw, int dh,
        int sx, int sy, int sw, int sh, int srcUnit, nint attributes, nint callback, nint callbackData);
    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipSaveImageToFile(nint image, string file, Guid* encoder, nint parameters);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreateBitmapFromHBITMAP(nint hbitmap, nint palette, nint* bitmap);
    public const uint CAPTUREBLT = 0x40000000, CF_DIB = 8, DIB_RGB_COLORS = 0, BI_RGB = 0;
    [LibraryImport("gdiplus.dll")] public static partial int GdipSetPixelOffsetMode(nint graphics, int mode);
    [LibraryImport("gdiplus.dll")] public static partial int GdipAddPathArcI(nint path, int x, int y, int w, int h, float startAngle, float sweepAngle);
    [LibraryImport("gdiplus.dll")] public static partial int GdipClosePathFigure(nint path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipFillPath(nint graphics, nint brush, nint path);
    [LibraryImport("user32.dll")] public static partial int TrackMouseEvent(TRACKMOUSEEVENT* e);
    public const uint WM_MOUSELEAVE = 0x02A3, TME_LEAVE = 0x2;
    public const int PixelFormat32bppARGB = 0x0026200A, UnitPixel = 2, PixelOffsetModeHalf = 4;
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nuint SHGetFileInfoW(string path, uint attributes, SHFILEINFOW* info, uint size, uint flags);
    public const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint SearchPathW(string? path, string file, string? extension, uint bufferLength, char* buffer, nint filePart);
    [LibraryImport("user32.dll")]
    public static partial int UpdateLayeredWindow(nint hwnd, nint dstDc, POINT* dst, SIZE* size, nint srcDc, POINT* src, uint key, BLENDFUNCTION* blend, uint flags);
    public const uint ULW_ALPHA = 0x2;
    public const int SmoothingModeAntiAlias = 4;

    // ---- kernel32 -------------------------------------------------------------------------
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandleW(string? name);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateMutexW(nint attributes, int initialOwner, string name);
    [LibraryImport("kernel32.dll")] public static partial int CloseHandle(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateWaitableTimerExW(nint attributes, char* name, uint flags, uint access);
    [LibraryImport("kernel32.dll")] public static partial int SetWaitableTimer(nint timer, long* dueTime, int period, nint completion, nint arg, int resume);
    [LibraryImport("kernel32.dll")] public static partial int CancelWaitableTimer(nint timer);
    [LibraryImport("kernel32.dll")] public static partial nint OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32.dll")] public static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll")] public static partial int K32EmptyWorkingSet(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int ReadFile(nint file, void* buffer, uint bytes, uint* read, nint overlapped);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int WriteFile(nint file, void* buffer, uint bytes, uint* written, nint overlapped);
    [LibraryImport("kernel32.dll")] public static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll")] public static partial int GetSystemPowerStatus(SYSTEM_POWER_STATUS* status);
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3,
        FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_OPERATION_ABORTED = 995, ERROR_IO_INCOMPLETE = 996, ERROR_IO_PENDING = 997, ERROR_TIMEOUT = 1460;
    public const nuint DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const uint DBT_DEVTYP_DEVICEINTERFACE = 5, DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial nint CreateEventW(nint attributes, int manualReset, int initialState, char* name);
    [LibraryImport("kernel32.dll")] public static partial int SetEvent(nint handle);
    [LibraryImport("kernel32.dll")] public static partial int ResetEvent(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial int GetOverlappedResult(nint file, OVERLAPPED* overlapped, uint* transferred, int wait);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial int CancelIoEx(nint file, OVERLAPPED* overlapped);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial nint RegisterDeviceNotificationW(nint recipient, void* filter, uint flags);
    [LibraryImport("user32.dll")] public static partial int UnregisterDeviceNotification(nint handle);
    public static readonly nint INVALID_HANDLE_VALUE = -1;
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int DeviceIoControl(nint device, uint code, void* input, uint inputSize, void* output, uint outputSize, uint* returned, nint overlapped);

    [LibraryImport("hid.dll")] public static partial byte HidD_GetPreparsedData(nint file, nint* preparsed);
    [LibraryImport("hid.dll")] public static partial byte HidD_FreePreparsedData(nint preparsed);
    /// <summary>caps points to a 64-byte HIDP_CAPS (Usage@0, UsagePage@2, InputReportByteLength@4).</summary>
    /// <summary>reportType 0 = input. caps: array of 72-byte HIDP_BUTTON_CAPS; length in/out = element count.</summary>
    [LibraryImport("hid.dll")] public static partial int HidP_GetButtonCaps(int reportType, byte* caps, ushort* length, nint preparsed);
    [LibraryImport("hid.dll")] public static partial int HidP_GetCaps(nint preparsed, byte* caps);

    // ---- cfgmgr32 -------------------------------------------------------------------------
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_Interface_List_SizeW(uint* length, Guid* interfaceClass, string? deviceId, uint flags);
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_Interface_PropertyW(string interfacePath, Guid* propertyKey, uint* propertyType, byte* buffer, uint* size, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint QueryDosDeviceW(string deviceName, char* target, uint max);
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_Interface_ListW(Guid* interfaceClass, string? deviceId, char* buffer, uint length, uint flags);

    // ---- advapi32 -------------------------------------------------------------------------
    [LibraryImport("advapi32.dll", SetLastError = true)] public static partial int OpenProcessToken(nint process, uint access, nint* token);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int DuplicateTokenEx(nint token, uint access, nint attributes, int impersonationLevel, int tokenType, nint* newToken);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int CreateProcessWithTokenW(nint token, uint logonFlags, char* appName, char* commandLine, uint creationFlags,
        nint environment, char* currentDirectory, STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInfo);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int LookupPrivilegeValueW(string? system, string name, LUID* luid);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int AdjustTokenPrivileges(nint token, int disableAll, TOKEN_PRIVILEGES* state, uint length, nint previous, nint returnLength);
    [LibraryImport("advapi32.dll")] public static partial int GetTokenInformation(nint token, int infoClass, void* info, uint length, uint* returnLength);

    /// <summary>info: 560-byte BLUETOOTH_DEVICE_INFO (dwSize@0, Address@8, fConnected@20). Returns 0 on success.</summary>
    [LibraryImport("bthprops.cpl")] public static partial uint BluetoothGetDeviceInfo(nint radio, byte* info);

    // Bluetooth discovery / pairing (bthprops.cpl). Structs are passed as raw buffers.
    [LibraryImport("bthprops.cpl")] public static partial nint BluetoothFindFirstRadio(uint* findParams, nint* radio);
    [LibraryImport("bthprops.cpl")] public static partial int BluetoothFindNextRadio(nint find, nint* radio);
    [LibraryImport("bthprops.cpl")] public static partial int BluetoothFindRadioClose(nint find);
    [LibraryImport("bthprops.cpl")] public static partial nint BluetoothFindFirstDevice(byte* search, byte* info);
    [LibraryImport("bthprops.cpl")] public static partial int BluetoothFindNextDevice(nint find, byte* info);
    [LibraryImport("bthprops.cpl")] public static partial int BluetoothFindDeviceClose(nint find);
    [LibraryImport("bthprops.cpl")] public static partial uint BluetoothAuthenticateDeviceEx(nint owner, nint radio, byte* info, nint oob, uint requirements);
    /// <summary>address: 64-bit Bluetooth address. Removes the remembered pairing. Returns 0 on success.</summary>
    [LibraryImport("bthprops.cpl")] public static partial uint BluetoothRemoveDevice(ulong* address);
    /// <summary>callback: BOOL (nint param, BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS*). Returns 0 on success.</summary>
    [LibraryImport("bthprops.cpl")] public static partial uint BluetoothRegisterForAuthenticationEx(byte* info, nint* registration, delegate* unmanaged<nint, byte*, int> callback, nint param);
    [LibraryImport("bthprops.cpl")] public static partial int BluetoothUnregisterAuthentication(nint registration);
    /// <summary>response: 48-byte BLUETOOTH_AUTHENTICATE_RESPONSE. Returns 0 on success.</summary>
    [LibraryImport("bthprops.cpl")] public static partial uint BluetoothSendAuthenticationResponseEx(nint radio, byte* response);
    [LibraryImport("bthprops.cpl")] public static partial uint BluetoothSetServiceState(nint radio, byte* info, Guid* service, uint flags);

    // ---- misc -----------------------------------------------------------------------------
    [LibraryImport("powrprof.dll")] public static partial uint PowerGetActiveScheme(nint root, Guid** scheme);
    [LibraryImport("powrprof.dll")] public static partial uint PowerSetActiveScheme(nint root, Guid* scheme);
    [LibraryImport("powrprof.dll")] public static partial uint PowerWriteACValueIndex(nint root, Guid* scheme, Guid* subgroup, Guid* setting, uint value);
    [LibraryImport("powrprof.dll")] public static partial uint PowerWriteDCValueIndex(nint root, Guid* scheme, Guid* subgroup, Guid* setting, uint value);
    [LibraryImport("kernel32.dll")] public static partial nint LocalFree(void* memory);
    [LibraryImport("powrprof.dll")] public static partial byte SetSuspendState(byte hibernate, byte force, byte wakeupEventsDisabled);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial int ExitWindowsEx(uint flags, uint reason);
    [LibraryImport("shell32.dll")] public static partial int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
    [LibraryImport("shell32.dll", SetLastError = true)] public static partial int ShellExecuteExW(SHELLEXECUTEINFOW* info);
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint monitor, int type, uint* dpiX, uint* dpiY);
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint hwnd, int attr, void* value, uint size);
    [LibraryImport("dwmapi.dll")] public static partial int DwmFlush();
    [LibraryImport("shell32.dll")] public static partial int SHGetKnownFolderPath(Guid* folderId, uint flags, nint token, char** path);
    public const uint KF_FLAG_DONT_VERIFY = 0x4000;
    [LibraryImport("shell32.dll")] public static partial int SHCreateItemFromParsingName(char* path, nint bindContext, Guid* iid, void** item);

    // ---- ole32 ----------------------------------------------------------------------------
    [LibraryImport("ole32.dll")] public static partial int CoInitializeEx(nint reserved, uint coInit);
    [LibraryImport("ole32.dll")] public static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] public static partial int CoCreateInstance(Guid* clsid, nint outer, uint clsContext, Guid* iid, void** obj);
    [LibraryImport("ole32.dll")] public static partial void CoTaskMemFree(void* ptr);
    [LibraryImport("ole32.dll")] public static partial int PropVariantClear(void* pv);

    public static bool IsElevated()
    {
        nint token;
        if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) == 0) return false;
        try
        {
            uint elevated = 0, len;
            return GetTokenInformation(token, TokenElevation, &elevated, 4, &len) != 0 && elevated != 0;
        }
        finally { CloseHandle(token); }
    }
}
