using System.Runtime.InteropServices;
using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

internal enum PowerAction { Sleep, Restart, Shutdown }

/// <summary>Sleep, restart and shut down. The flag choices are pure so they can be tested without touching the PC.</summary>
internal static unsafe class PowerActions
{
    /// <summary>ExitWindowsEx flags for restart / shut down; 0 for sleep (it uses SetSuspendState).</summary>
    public static uint ExitFlags(PowerAction action) => action switch
    {
        // Same as the Start menu's "Shut down": hybrid shutdown keeps Fast Startup.
        PowerAction.Shutdown => EWX_SHUTDOWN | EWX_POWEROFF | EWX_HYBRID_SHUTDOWN | EWX_FORCEIFHUNG,
        PowerAction.Restart => EWX_REBOOT | EWX_FORCEIFHUNG,
        _ => 0,
    };

    public static readonly PowerAction[] All = [PowerAction.Sleep, PowerAction.Restart, PowerAction.Shutdown];

    /// <summary>The action written in a Power node's config; null, empty or unknown text = Sleep.</summary>
    public static PowerAction Parse(string? text) => All.FirstOrDefault(a => Key(a) == text, PowerAction.Sleep);

    /// <summary>What goes in the config. Sleep is the default, so it is stored as null.</summary>
    public static string? ToConfig(PowerAction action) => action == PowerAction.Sleep ? null : Key(action);

    public static string Label(PowerAction action) => action switch
    {
        PowerAction.Restart => "Restart",
        PowerAction.Shutdown => "Shut down",
        _ => "Sleep",
    };

    public static string ConfirmPrompt(PowerAction action) => action switch
    {
        PowerAction.Restart => "Restart the PC?",
        PowerAction.Shutdown => "Shut down the PC?",
        _ => "Put the PC to sleep?",
    };

    private static string Key(PowerAction action) => action switch
    {
        PowerAction.Restart => "restart",
        PowerAction.Shutdown => "shutdown",
        _ => "sleep",
    };

    /// <summary>
    /// Runs the action. Sleep blocks until the PC wakes, so call this from a worker thread.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? Run(PowerAction action)
    {
        if (action == PowerAction.Sleep)
        {
            // hibernate = false, force = false, wakeupEventsDisabled = false. Returns 0 on failure.
            return SetSuspendState(0, 0, 0) != 0 ? null : "Sleep was refused by Windows";
        }

        var error = EnableShutdownPrivilege();
        if (error is not null) return error;

        if (ExitWindowsEx(ExitFlags(action), SHTDN_REASON_FLAG_PLANNED) == 0)
            return $"ExitWindowsEx failed ({Marshal.GetLastPInvokeError()})";
        return null;
    }

    private static string? EnableShutdownPrivilege()
    {
        nint token = 0;
        if (OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token) == 0)
            return $"Cannot open the process token ({Marshal.GetLastPInvokeError()})";
        try
        {
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Attributes = SE_PRIVILEGE_ENABLED };
            if (LookupPrivilegeValueW(null, "SeShutdownPrivilege", &privileges.Luid) == 0)
                return $"Cannot look up the shutdown privilege ({Marshal.GetLastPInvokeError()})";
            if (AdjustTokenPrivileges(token, 0, &privileges, 0, 0, 0) == 0 || Marshal.GetLastPInvokeError() != 0)
                return $"Cannot enable the shutdown privilege ({Marshal.GetLastPInvokeError()})";
            return null;
        }
        finally { CloseHandle(token); }
    }
}
