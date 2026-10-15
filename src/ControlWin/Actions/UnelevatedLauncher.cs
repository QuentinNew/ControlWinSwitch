using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.Actions;

/// <summary>
/// Starts programs with the normal (non-elevated) user token even though ControlWin runs elevated:
/// the token of the shell (explorer) process is duplicated and used with CreateProcessWithTokenW.
/// </summary>
internal static unsafe class UnelevatedLauncher
{
    /// <summary>Launches <paramref name="target"/> (an .exe, a URI such as steam://..., a .lnk, a document). Returns an error message, or null.</summary>
    public static string? Launch(string target, string? args = null, string? workingDir = null, bool asAdmin = false)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Empty target";

        bool isExe = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !target.Contains("://");
        bool elevated = IsElevated();

        // Run as administrator: ControlWin already is one, so the program inherits that without a UAC prompt.
        // When ControlWin is not elevated this asks Windows for elevation ("runas"), which does prompt.
        if (asAdmin) return ShellExecute(target, args, workingDir, elevated ? "open" : "runas");
        if (!elevated) return ShellExecute(target, args, workingDir, "open");

        string commandLine;
        string? directory = null;
        if (isExe)
        {
            commandLine = $"\"{target}\"" + (string.IsNullOrWhiteSpace(args) ? "" : " " + args);
            directory = !string.IsNullOrWhiteSpace(workingDir) ? workingDir : TryGetDirectory(target);
        }
        else
        {
            // URIs, shortcuts and documents: let explorer (running as the user) resolve them.
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            commandLine = $"\"{explorer}\" \"{target}\"";
        }

        return StartWithShellToken(commandLine, directory);
    }

    private static string? TryGetDirectory(string exe)
    {
        try { return Path.IsPathRooted(exe) ? Path.GetDirectoryName(exe) : null; }
        catch { return null; }
    }

    private static string? StartWithShellToken(string commandLine, string? directory)
    {
        nint shell = GetShellWindow();
        if (shell == 0) return "Explorer is not running";

        uint pid;
        GetWindowThreadProcessId(shell, &pid);
        if (pid == 0) return "Cannot find the shell process";

        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (process == 0) return $"Cannot open the shell process ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})";

        nint token = 0, primary = 0;
        try
        {
            if (OpenProcessToken(process, TOKEN_DUPLICATE, &token) == 0)
                return $"Cannot open the shell token ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})";

            const uint access = TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (DuplicateTokenEx(token, access, 0, SecurityImpersonation, TokenPrimary, &primary) == 0)
                return $"Cannot duplicate the shell token ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})";

            var startup = new STARTUPINFOW
            {
                cb = (uint)sizeof(STARTUPINFOW),
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = SW_SHOWNORMAL,
            };
            PROCESS_INFORMATION info;

            // CreateProcessWithTokenW may modify the command line buffer: it must be writable.
            char[] buffer = new char[commandLine.Length + 1];
            commandLine.CopyTo(0, buffer, 0, commandLine.Length);
            fixed (char* cmd = buffer)
            fixed (char* dir = directory)
            {
                int ok = CreateProcessWithTokenW(primary, 0, null, cmd, CREATE_UNICODE_ENVIRONMENT, 0,
                    directory is null ? null : dir, &startup, &info);
                if (ok == 0)
                    return $"Cannot start the program ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})";
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return null;
        }
        finally
        {
            if (primary != 0) CloseHandle(primary);
            if (token != 0) CloseHandle(token);
            CloseHandle(process);
        }
    }

    private static string? ShellExecute(string target, string? args, string? workingDir, string verbName)
    {
        fixed (char* verb = verbName)
        fixed (char* file = target)
        fixed (char* parameters = args)
        fixed (char* dir = workingDir)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = SEE_MASK_FLAG_NO_UI,
                lpVerb = verb,
                lpFile = file,
                lpParameters = string.IsNullOrEmpty(args) ? null : parameters,
                lpDirectory = string.IsNullOrEmpty(workingDir) ? null : dir,
                nShow = SW_SHOWNORMAL,
            };
            if (ShellExecuteExW(&info) == 0)
                return $"Cannot open {target} ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})";
            return null;
        }
    }
}
