using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Native;

/// <summary>Minimal Win32 window wrapper: registers a class once and routes messages to <see cref="WndProc"/>.</summary>
internal abstract unsafe class NativeWindow
{
    private static readonly Dictionary<nint, NativeWindow> s_windows = new();
    private static readonly HashSet<string> s_classes = new();
    private static NativeWindow? s_creating;

    public nint Handle { get; private set; }

    protected void Create(string className, uint style, uint exStyle)
    {
        var hInstance = GetModuleHandleW(null);
        if (s_classes.Add(className))
        {
            fixed (char* name = className)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &StaticWndProc,
                    hInstance = hInstance,
                    hCursor = LoadCursorW(0, IDC_ARROW),
                    lpszClassName = name,
                };
                if (RegisterClassExW(&wc) == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
            }
        }

        s_creating = this;
        Handle = CreateWindowExW(exStyle, className, className, style, 0, 0, 0, 0, 0, 0, hInstance, 0);
        s_creating = null;
        if (Handle == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
        s_windows[Handle] = this;
    }

    protected virtual nint WndProc(uint msg, nuint wParam, nint lParam) => DefWindowProcW(Handle, msg, wParam, lParam);

    [UnmanagedCallersOnly]
    private static nint StaticWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (!s_windows.TryGetValue(hwnd, out var window))
        {
            // Messages sent during CreateWindowEx, before Handle is known.
            if (s_creating is null) return DefWindowProcW(hwnd, msg, wParam, lParam);
            window = s_creating;
            window.Handle = hwnd;
            s_windows[hwnd] = window;
        }

        try
        {
            var result = window.WndProc(msg, wParam, lParam);
            if (msg == WM_DESTROY) s_windows.Remove(hwnd);
            return result;
        }
        catch (Exception ex)
        {
            // Never let an exception unwind through native code. Same policy as the message loop: log, back to idle.
            ErrorBoundary.OnUiError($"WndProc 0x{msg:X}", ex);
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static int LoWord(nint v) => (short)(v & 0xFFFF);
}
