using System.Diagnostics;

namespace ControlWin.App;

/// <summary>Helpers for the "disable the virtual pad while these processes run" list.</summary>
internal static class ProcessList
{
    /// <summary>"steam.exe" and "Steam" both become "steam".</summary>
    public static string Normalize(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    /// <summary>Comma-separated text from the settings window to a clean list.</summary>
    public static List<string> Parse(string text)
    {
        var result = new List<string>();
        foreach (var part in text.Split(',', ';'))
        {
            var name = part.Trim();
            if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
        }
        return result;
    }

    public static string Format(IReadOnlyList<string> names) => string.Join(", ", names);

    /// <summary>Name shown in toasts and menus: "steam.exe" -> "Steam".</summary>
    public static string DisplayName(string name)
    {
        var n = Normalize(name);
        return n.Length == 0 ? name : char.ToUpperInvariant(n[0]) + n[1..];
    }

    /// <summary>
    /// Finds a running process whose exe name is in <paramref name="names"/>. Returns its id (0 when none)
    /// and the list entry that matched.
    /// </summary>
    public static int FindRunning(IReadOnlyList<string> names, out string matched)
    {
        matched = "";
        foreach (var name in names)
        {
            var key = Normalize(name);
            if (key.Length == 0) continue;

            Process[] processes;
            try { processes = Process.GetProcessesByName(key); }
            catch { continue; }

            try
            {
                if (processes.Length > 0)
                {
                    matched = name;
                    return processes[0].Id;
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        return 0;
    }
}

/// <summary>
/// Decides when ControlWin itself turns the virtual pad off / on because a watched process runs.
/// Pure state machine: the pad is only re-enabled when this gate was the one that disabled it, and a
/// manual toggle always wins.
/// </summary>
internal sealed class PadGate
{
    public enum Change { None, Disable, Enable }

    /// <summary>The pad is currently off because of a watched process.</summary>
    public bool HeldOff { get; private set; }

    /// <summary>Start-up / config reload: returns whether the pad should be enabled.</summary>
    public bool Apply(bool configEnabled, bool running)
    {
        HeldOff = configEnabled && running;
        return configEnabled && !HeldOff;
    }

    /// <summary>A watched process started or stopped.</summary>
    public Change OnRunningChanged(bool running, bool padEnabled)
    {
        if (running)
        {
            if (!padEnabled) return Change.None; // already off: not ours to restore later
            HeldOff = true;
            return Change.Disable;
        }

        if (!HeldOff) return Change.None;
        HeldOff = false;
        return Change.Enable;
    }

    /// <summary>The user toggled the pad by hand: forget that we disabled it.</summary>
    public void ManualOverride() => HeldOff = false;
}
