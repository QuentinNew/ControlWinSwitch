using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace ControlWinSwitch.Setup;

/// <summary>
/// "Launch on startup". Preferred: a scheduled task that starts ControlWinSwitch at logon with the highest privileges
/// (so it can control elevated windows, with no UAC prompt). Needs ControlWinSwitch to be elevated to create. Otherwise:
/// the per-user Run key, which starts it without administrator rights.
/// </summary>
internal static class Startup
{
    public const string TaskName = "ControlWinSwitch";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public enum Mode { Off, Task, RunKey }

    private const int Unknown = -1;
    private static int s_cached = Unknown; // a Mode once known; read and written from the UI and worker threads
    private static volatile bool s_busy;   // a change runs on its worker thread
    private static volatile bool s_requested;

    /// <summary>
    /// Where ControlWinSwitch is registered to start from (read once, then kept up to date by <see cref="Set"/>). Ask early
    /// with <see cref="DetectInBackground"/>: reading it runs schtasks.exe.
    /// </summary>
    public static Mode Current
    {
        get
        {
            int cached = Volatile.Read(ref s_cached);
            if (cached != Unknown) return (Mode)cached;
            var mode = Detect();
            Interlocked.CompareExchange(ref s_cached, (int)mode, Unknown);
            return mode;
        }
    }

    /// <summary>On while it is on, or while turning it on (the change runs in the background).</summary>
    public static bool IsEnabled => s_busy ? s_requested : Current != Mode.Off;

    /// <summary>The last on / off asked for with <see cref="SetInBackground"/>.</summary>
    public static bool LastRequested => s_requested;

    /// <summary>Finds out the current mode on a worker thread, so the Settings never wait for schtasks.exe.</summary>
    public static void DetectInBackground() =>
        ErrorBoundary.StartWorker("startup-detect", () =>
        {
            var mode = Detect();
            Interlocked.CompareExchange(ref s_cached, (int)mode, Unknown); // a change made meanwhile wins
        });

    /// <summary>
    /// <see cref="Set"/> on a worker thread (schtasks.exe can take seconds); the outcome is posted to
    /// <paramref name="hwnd"/> as <see cref="App.AppMessages.StartupDone"/>. False when a change is still running.
    /// </summary>
    public static bool SetInBackground(bool enabled, string exePath, nint hwnd)
    {
        if (s_busy) return false;
        s_requested = enabled;
        s_busy = true;
        ErrorBoundary.StartWorker("startup", () =>
        {
            var mode = Set(enabled, exePath, out var error);
            s_busy = false;
            Native.Win32.PostMessageW(hwnd, App.AppMessages.StartupDone, (nuint)(int)mode, error is null ? 0 : 1);
        }, onFailure: () =>
        {
            s_busy = false;
            Native.Win32.PostMessageW(hwnd, App.AppMessages.StartupDone, (nuint)Math.Max(0, Volatile.Read(ref s_cached)), 1);
        });
        return true;
    }

    /// <summary>Turns startup on or off. Returns the resulting mode; <paramref name="error"/> is set when it could not be applied.</summary>
    public static Mode Set(bool enabled, string exePath, out string? error)
    {
        error = null;
        if (!enabled)
        {
            DeleteTask();
            SetRunKey(null);
            return Remember(Detect());
        }

        // Replace whatever was there, so a moved exe is picked up.
        DeleteTask();
        SetRunKey(null);

        if (TryCreateTask(exePath))
            return Remember(Mode.Task);

        if (SetRunKey(exePath))
            return Remember(Mode.RunKey);

        error = "Cannot register ControlWinSwitch to start with Windows";
        return Remember(Detect());
    }

    private static Mode Remember(Mode mode)
    {
        Volatile.Write(ref s_cached, (int)mode);
        return mode;
    }

    private static Mode Detect()
    {
        if (RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0) return Mode.Task;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(TaskName) is string) return Mode.RunKey;
        }
        catch
        {
            // Unreadable: treat as off.
        }
        return Mode.Off;
    }

    private static bool TryCreateTask(string exePath)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"controlwinswitch-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exePath, $"{Environment.UserDomainName}\\{Environment.UserName}"),
                System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out _) == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Startup task creation failed", ex);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static void DeleteTask() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out _);

    private static bool SetRunKey(string? exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (exePath is null) key.DeleteValue(TaskName, throwOnMissingValue: false);
            else key.SetValue(TaskName, $"\"{exePath}\"");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Startup Run key failed", ex);
            return false;
        }
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        output = "";
        try
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info);
            if (process is null) return -1;
            output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                return -1;
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("schtasks failed", ex);
            return -1;
        }
    }

    /// <summary>The task definition: at logon of this user, highest privileges, never stopped, even on battery.</summary>
    internal static string BuildTaskXml(string exePath, string user)
    {
        string exe = SecurityElement.Escape(exePath);
        string account = SecurityElement.Escape(user);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts ControlWinSwitch when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{account}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{account}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
