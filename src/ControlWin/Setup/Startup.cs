using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace ControlWin.Setup;

/// <summary>
/// "Launch on startup". Preferred: a scheduled task that starts ControlWin at logon with the highest privileges
/// (so it can control elevated windows, with no UAC prompt). Needs ControlWin to be elevated to create. Otherwise:
/// the per-user Run key, which starts it without administrator rights.
/// </summary>
internal static class Startup
{
    public const string TaskName = "ControlWin";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public enum Mode { Off, Task, RunKey }

    private static Mode? s_cached;

    /// <summary>Where ControlWin is registered to start from (read once, then kept up to date by <see cref="Set"/>).</summary>
    public static Mode Current => s_cached ??= Detect();

    public static bool IsEnabled => Current != Mode.Off;

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

        error = "Cannot register ControlWin to start with Windows";
        return Remember(Detect());
    }

    private static Mode Remember(Mode mode)
    {
        s_cached = mode;
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
        string xmlPath = Path.Combine(Path.GetTempPath(), $"controlwin-task-{Environment.ProcessId}.xml");
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
                <Description>Starts ControlWin when you sign in.</Description>
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
