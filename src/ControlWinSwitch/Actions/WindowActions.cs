using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>Closes the window the user is working in. The menus never take the focus, so it is still the foreground one.</summary>
internal static unsafe class WindowActions
{
    // The desktop and the taskbar: closing them asks to shut down the PC or does nothing useful.
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>Asks the foreground window to close, as its own close button would. Returns null, or why nothing was closed.</summary>
    public static string? CloseForeground(out string title)
    {
        title = "";
        nint hwnd = GetForegroundWindow();
        if (hwnd != 0) hwnd = GetAncestor(hwnd, GA_ROOT);
        if (hwnd == 0 || hwnd == GetShellWindow()) return "No window to close";

        uint pid;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid == Environment.ProcessId) return "No window to close";

        char* buffer = stackalloc char[256];
        int length = GetClassNameW(hwnd, buffer, 256);
        if (ShellClasses.Contains(new string(buffer, 0, length))) return "No window to close";

        length = GetWindowTextW(hwnd, buffer, 256);
        title = length > 0 ? new string(buffer, 0, length) : "window";

        if (PostMessageW(hwnd, WM_CLOSE, 0, 0) == 0) return $"Could not close {title}";
        return null;
    }
}
