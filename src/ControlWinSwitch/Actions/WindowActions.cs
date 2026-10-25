using System.Diagnostics;
using System.Runtime.InteropServices;
using ControlWinSwitch.App;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>What a Close window node closes.</summary>
internal enum CloseTarget { Current, ByName }

/// <summary>Config values and names of the <see cref="CloseTarget"/>s. Pure, so it can be tested.</summary>
internal static class CloseTargets
{
    public static readonly CloseTarget[] All = [CloseTarget.Current, CloseTarget.ByName];

    /// <summary>The target written in the config; null, empty or unknown text = the current window.</summary>
    public static CloseTarget Parse(string? text) => text == "byName" ? CloseTarget.ByName : CloseTarget.Current;

    /// <summary>What goes in the config. The current window is the default, so it is stored as null.</summary>
    public static string? ToConfig(CloseTarget target) => target == CloseTarget.ByName ? "byName" : null;

    public static string Label(CloseTarget target) => target == CloseTarget.ByName ? "By name" : "Current window";
}

/// <summary>A running program with a window the user can see.</summary>
internal sealed record OpenWindow(string Process, string Title);

/// <summary>Closes the window the user is working in, or an app by its process name. The menus never take the focus, so the window is still the foreground one.</summary>
internal static unsafe class WindowActions
{
    // The desktop and the taskbar: closing them asks to shut down the PC or does nothing useful.
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private static List<nint> s_found = [];

    /// <summary>The question asked before a Close window node runs.</summary>
    public static string ConfirmPrompt(CloseTarget target, string processName, bool forceQuit)
    {
        if (target == CloseTarget.Current) return "Close the current window?";
        string name = ProcessList.Normalize(processName).Length == 0 ? "the app" : ProcessList.DisplayName(processName);
        return forceQuit ? $"Force quit {name}?" : $"Close {name}?";
    }

    /// <summary>A window that is an app's main window: visible, not owned, not a tool window, not hidden by the shell, with a title.</summary>
    public static bool IsAppWindow(bool visible, bool owned, bool toolWindow, bool cloaked, string title) =>
        visible && !owned && !toolWindow && !cloaked && title.Length > 0;

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

    /// <summary>
    /// Closes the app whose process is called <paramref name="processName"/>: its windows are asked to close, or with
    /// <paramref name="forceQuit"/> its processes are ended at once. Returns null, or why nothing happened;
    /// <paramref name="message"/> is what to tell the user on success.
    /// </summary>
    public static string? CloseByName(string processName, bool forceQuit, out string message)
    {
        message = "";
        string key = ProcessList.Normalize(processName);
        if (key.Length == 0) return "No app name set";
        string display = ProcessList.DisplayName(processName);

        Process[] processes;
        try { processes = Process.GetProcessesByName(key); }
        catch (Exception ex) { return $"Cannot list the processes ({ex.Message})"; }

        try
        {
            if (processes.Length == 0) return $"{display} is not running";

            if (forceQuit)
            {
                int ended = 0;
                foreach (var process in processes)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try { process.Kill(); ended++; }
                    catch (Exception ex) { Log.Info($"Force quit {display} ({process.Id}): {ex.Message}"); }
                }
                if (ended == 0) return $"Could not force quit {display}";
                message = $"{display} force-closed";
                return null;
            }

            var pids = processes.Select(p => (uint)p.Id).ToHashSet();
            var windows = TopLevelWindows().Where(w => pids.Contains(w.Pid) && w.Pid != Environment.ProcessId).ToList();
            if (windows.Count == 0) return $"{display} has no window to close";
            foreach (var window in windows) PostMessageW(window.Hwnd, WM_CLOSE, 0, 0);
            message = windows.Count == 1 ? $"Closing {display}" : $"Closing {display} ({windows.Count} windows)";
            return null;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>The running programs that have a window on screen, one entry each, by name. Takes ControlWinSwitch itself out.</summary>
    public static List<OpenWindow> OpenWindows()
    {
        var result = new List<OpenWindow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in TopLevelWindows())
        {
            if (window.Pid == Environment.ProcessId) continue;
            try
            {
                using var process = Process.GetProcessById((int)window.Pid);
                if (seen.Add(process.ProcessName)) result.Add(new OpenWindow(process.ProcessName, window.Title));
            }
            catch
            {
                // The process ended meanwhile, or is not readable.
            }
        }
        return result.OrderBy(w => w.Process, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private readonly record struct TopWindow(nint Hwnd, uint Pid, string Title);

    private static List<TopWindow> TopLevelWindows()
    {
        s_found = [];
        EnumWindows(&Collect, 0);
        var windows = new List<TopWindow>();
        char* buffer = stackalloc char[256];
        foreach (nint hwnd in s_found)
        {
            int length = GetClassNameW(hwnd, buffer, 256);
            if (ShellClasses.Contains(new string(buffer, 0, length))) continue;

            length = GetWindowTextW(hwnd, buffer, 256);
            string title = new(buffer, 0, Math.Max(0, length));

            int cloaked = 0;
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int));
            bool toolWindow = (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & (nint)WS_EX_TOOLWINDOW) != 0;
            if (!IsAppWindow(IsWindowVisible(hwnd) != 0, GetWindow(hwnd, GW_OWNER) != 0, toolWindow, cloaked != 0, title)) continue;

            uint pid;
            GetWindowThreadProcessId(hwnd, &pid);
            windows.Add(new TopWindow(hwnd, pid, title));
        }
        s_found = [];
        return windows;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        s_found.Add(hwnd);
        return 1;
    }
}
