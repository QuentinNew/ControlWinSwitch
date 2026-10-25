using System.Runtime.InteropServices;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Native;

/// <summary>
/// Eyedropper for the whole screen: a low-level mouse hook that is installed only while picking. Moving the mouse reports
/// the color under it, a left click picks it and a right click cancels; both clicks are swallowed (the app under the
/// cursor never sees them). The callbacks run on the thread that called <see cref="Start"/>, which needs a message loop.
/// </summary>
internal static unsafe class ScreenPicker
{
    /// <summary>The color under the cursor is read at most this often while the mouse moves.</summary>
    public const int SampleIntervalMs = 30;

    private static nint s_hook;
    private static Action<uint>? s_onMove, s_onPick;
    private static Action? s_onCancel;
    private static bool s_ignoreLeftUp;      // the release of the click that started the picking
    private static uint s_swallowUp;         // WM_LBUTTONUP / WM_RBUTTONUP of the click that ended it; the hook goes with it
    private static long s_lastSample;

    public static bool IsActive => s_hook != 0;

    /// <summary>"#RRGGBB" of a GDI COLORREF (0x00BBGGRR).</summary>
    public static string ToHex(uint colorref) => $"#{colorref & 0xFF:X2}{(colorref >> 8) & 0xFF:X2}{(colorref >> 16) & 0xFF:X2}";

    public static bool IsDue(long now, long last) => now - last >= SampleIntervalMs;

    /// <summary>
    /// Starts picking. Call it from the mouse-down of the button that starts it: that click's release is let through.
    /// Returns false when the hook cannot be installed.
    /// </summary>
    public static bool Start(Action<uint> onMove, Action<uint> onPick, Action onCancel)
    {
        Stop();
        s_onMove = onMove;
        s_onPick = onPick;
        s_onCancel = onCancel;
        s_ignoreLeftUp = true;
        s_swallowUp = 0;
        s_lastSample = 0;
        s_hook = SetWindowsHookExW(WH_MOUSE_LL, &HookProc, GetModuleHandleW(null), 0);
        if (s_hook != 0) return true;
        Log.Error($"Screen picker: cannot hook the mouse ({Marshal.GetLastPInvokeError()})");
        return false;
    }

    /// <summary>Ends picking without a callback (nothing happens when it is not active).</summary>
    public static void Stop()
    {
        if (s_hook != 0) UnhookWindowsHookEx(s_hook);
        s_hook = 0;
        s_onMove = s_onPick = null;
        s_onCancel = null;
    }

    private static uint? ColorAt(POINT pt)
    {
        var screen = GetDC(0);
        uint colorref = GetPixel(screen, pt.X, pt.Y);
        ReleaseDC(0, screen);
        return colorref == 0xFFFFFFFF ? null : colorref; // CLR_INVALID: outside every monitor
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int code, nuint wParam, nint lParam)
    {
        if (code < 0 || s_hook == 0) return CallNextHookEx(0, code, wParam, lParam);

        try
        {
            uint msg = (uint)wParam;
            var info = (MSLLHOOKSTRUCT*)lParam;

            if (s_swallowUp != 0)
            {
                // The click is over once its button is released: then the hook goes away.
                if (msg == s_swallowUp)
                {
                    Stop();
                    return 1;
                }
                return CallNextHookEx(0, code, wParam, lParam);
            }

            switch (msg)
            {
                case WM_MOUSEMOVE:
                    long now = Environment.TickCount64;
                    if (IsDue(now, s_lastSample) && ColorAt(info->Pt) is { } moved)
                    {
                        s_lastSample = now;
                        s_onMove?.Invoke(moved);
                    }
                    break;

                case WM_LBUTTONUP when s_ignoreLeftUp:
                    s_ignoreLeftUp = false;
                    break;

                case WM_LBUTTONDOWN:
                    s_swallowUp = WM_LBUTTONUP;
                    if (ColorAt(info->Pt) is { } picked) s_onPick?.Invoke(picked);
                    else s_onCancel?.Invoke();
                    return 1;

                case WM_RBUTTONDOWN:
                    s_swallowUp = WM_RBUTTONUP;
                    s_onCancel?.Invoke();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Screen picker hook failed", ex);
            Stop();
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }
}
