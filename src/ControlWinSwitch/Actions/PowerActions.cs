using System.Runtime.InteropServices;
using ControlWinSwitch.Native;
using ControlWinSwitch.App;
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

    public static PowerAction? FromCommand(CommandKind kind) => kind switch
    {
        CommandKind.Sleep => PowerAction.Sleep,
        CommandKind.Restart => PowerAction.Restart,
        CommandKind.Shutdown => PowerAction.Shutdown,
        _ => null,
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
