using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>The four modes of Win+P.</summary>
internal enum ScreenMode { PcOnly, Duplicate, Extend, SecondOnly }

/// <summary>Config values and names of the <see cref="ScreenMode"/>s. Pure, so it can be tested.</summary>
internal static class ScreenModes
{
    public static readonly ScreenMode[] All = [ScreenMode.PcOnly, ScreenMode.Duplicate, ScreenMode.Extend, ScreenMode.SecondOnly];

    /// <summary>The mode written in the config; null, empty or unknown text = Extend.</summary>
    public static ScreenMode Parse(string? text) => All.FirstOrDefault(m => Key(m) == text, ScreenMode.Extend);

    /// <summary>What goes in the config. Extend is the default, so it is stored as null.</summary>
    public static string? ToConfig(ScreenMode mode) => mode == ScreenMode.Extend ? null : Key(mode);

    public static string Label(ScreenMode mode) => mode switch
    {
        ScreenMode.PcOnly => "PC screen only",
        ScreenMode.Duplicate => "Duplicate",
        ScreenMode.SecondOnly => "Second screen only",
        _ => "Extend",
    };

    private static string Key(ScreenMode mode) => mode switch
    {
        ScreenMode.PcOnly => "pcOnly",
        ScreenMode.Duplicate => "duplicate",
        ScreenMode.SecondOnly => "secondOnly",
        _ => "extend",
    };
}

/// <summary>Switches the display topology like Win+P does.</summary>
internal static class DisplayTopology
{
    public static uint Flags(ScreenMode mode) => SDC_APPLY | mode switch
    {
        ScreenMode.PcOnly => SDC_TOPOLOGY_INTERNAL,
        ScreenMode.Duplicate => SDC_TOPOLOGY_CLONE,
        ScreenMode.SecondOnly => SDC_TOPOLOGY_EXTERNAL,
        _ => SDC_TOPOLOGY_EXTEND,
    };

    /// <summary>Returns an error message, or null on success.</summary>
    public static string? Apply(ScreenMode mode)
    {
        int result = SetDisplayConfig(0, 0, 0, 0, Flags(mode));
        return result == 0 ? null : $"SetDisplayConfig returned {result}";
    }
}
