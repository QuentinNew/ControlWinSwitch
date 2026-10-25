using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>
/// Brightness of the screen under the cursor (the one the menu and the slider show on). The built-in screen goes
/// through the display driver's LCD brightness controls, like the keyboard brightness keys; external monitors
/// through DDC/CI. A screen mirrored on both gets both. Open while the slider is shown, then Dispose.
/// </summary>
internal sealed unsafe class Brightness : IDisposable
{
    private const uint IoctlSupported = 0x230494, IoctlQuery = 0x230498, IoctlSet = 0x23049C;
    private const byte PolicyBoth = 3;

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values of a built-in screen: LVDS, embedded DisplayPort, embedded UDI, internal.
    private const uint TechLvds = 6, TechDisplayPortEmbedded = 11, TechUdiEmbedded = 13, TechInternal = 0x80000000;
    private const int PathSize = 72, ModeSize = 64, PathSourceId = 8, PathTargetTech = 36, SourceNameSize = 84;

    private nint _lcd;
    private byte[] _levels = [];
    private DdcBrightness? _ddc;
    private int _ddcValue;

    /// <summary>The built-in screen is changed: its value goes to the power plan too (see <see cref="Save"/>).</summary>
    public bool SavesToPowerPlan => _lcd != 0;

    /// <summary>Null when the screen under the cursor has no adjustable brightness.</summary>
    public static Brightness? Open()
    {
        POINT pt;
        GetCursorPos(&pt);
        nint monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);

        bool? builtIn = ShowsBuiltInScreen(monitor);
        var ddc = DdcBrightness.Open(monitor);
        byte[]? levels = null;
        nint lcd = builtIn == true || (builtIn is null && ddc is null) ? OpenLcd(out levels) : 0;
        if (lcd == 0 && ddc is null) return null;

        Log.Info($"Brightness: {(lcd != 0 ? "built-in screen" : "")}{(lcd != 0 && ddc is not null ? " + " : "")}{(ddc is not null ? "DDC/CI" : "")}");
        return new Brightness { _lcd = lcd, _levels = lcd != 0 ? levels! : [], _ddc = ddc, _ddcValue = ddc?.Initial ?? 0 };
    }

    private static nint OpenLcd(out byte[]? supported)
    {
        supported = null;
        nint lcd = CreateFileW(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (lcd == -1) return 0;

        var levels = stackalloc byte[256];
        uint count = 0;
        if (DeviceIoControl(lcd, IoctlSupported, null, 0, levels, 256, &count, 0) == 0 || count == 0)
        {
            CloseHandle(lcd);
            return 0;
        }
        supported = new Span<byte>(levels, (int)count).ToArray();
        return lcd;
    }

    /// <summary>Current brightness in percent (the built-in screen's for the power source in use), or -1 when it can't be read.</summary>
    public int Get()
    {
        if (_lcd == 0) return _ddcValue; // DDC/CI reads are slow and only the monitor's own buttons change it
        if (!Query(out byte ac, out byte dc)) return -1;
        return OnBattery() ? dc : ac;
    }

    /// <summary>
    /// Changes the screen. The driver ignores a change of only one of the AC / DC values, so both are set;
    /// <see cref="Save"/> then records it in the power plan, or Windows puts its own value back later.
    /// External monitors are sent the value in the background.
    /// </summary>
    public bool Set(int percent)
    {
        bool ok = true;
        if (_lcd != 0)
        {
            byte level = Nearest(_levels, percent);
            var value = stackalloc byte[3] { PolicyBoth, level, level };
            uint returned;
            ok = DeviceIoControl(_lcd, IoctlSet, value, 3, null, 0, &returned, 0) != 0;
        }
        if (_ddc is not null)
        {
            _ddcValue = Math.Clamp(percent, 0, 100);
            _ddc.Set(_ddcValue);
        }
        return ok;
    }

    private static readonly Guid VideoSubgroup = new("7516B95F-F776-4464-8C53-06167F40CC99"),
        BrightnessSetting = new("ADED5E82-B909-4619-9949-F5D71DAC0BCB");

    /// <summary>
    /// Writes the brightness into the active power plan for the power source in use, like the Windows brightness
    /// slider and keys. Blocks for about 250 ms: call it from a worker thread.
    /// </summary>
    public static bool Save(int percent)
    {
        Guid* scheme = null;
        if (PowerGetActiveScheme(0, &scheme) != 0 || scheme == null) return false;
        try
        {
            var subgroup = VideoSubgroup;
            var setting = BrightnessSetting;
            uint value = (uint)Math.Clamp(percent, 0, 100);
            uint error = OnBattery()
                ? PowerWriteDCValueIndex(0, scheme, &subgroup, &setting, value)
                : PowerWriteACValueIndex(0, scheme, &subgroup, &setting, value);
            return error == 0 && PowerSetActiveScheme(0, scheme) == 0; // applies the plan's new value
        }
        finally { LocalFree(scheme); }
    }

    /// <summary>The supported level closest to <paramref name="percent"/>.</summary>
    internal static byte Nearest(ReadOnlySpan<byte> levels, int percent)
    {
        if (levels.IsEmpty) return (byte)Math.Clamp(percent, 0, 100);
        byte best = levels[0];
        foreach (byte level in levels)
            if (Math.Abs(level - percent) < Math.Abs(best - percent)) best = level;
        return best;
    }

    internal static bool IsBuiltIn(uint technology) =>
        technology is TechLvds or TechDisplayPortEmbedded or TechUdiEmbedded or TechInternal;

    /// <summary>Whether the built-in screen is one of those showing <paramref name="monitor"/>; null when it can't be told.</summary>
    private static bool? ShowsBuiltInScreen(nint monitor)
    {
        var info = new MONITORINFOEXW();
        info.Info.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (GetMonitorInfoExW(monitor, &info) == 0) return null;
        string device = new(info.szDevice);

        uint pathCount, modeCount;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != 0) return null;
        var paths = new byte[pathCount * PathSize];
        var modes = new byte[modeCount * ModeSize];
        fixed (byte* p = paths)
        fixed (byte* m = modes)
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, p, &modeCount, m, 0) != 0) return null;

            bool found = false;
            var name = stackalloc byte[SourceNameSize];
            for (uint i = 0; i < pathCount; i++)
            {
                byte* path = p + i * PathSize;
                // DISPLAYCONFIG_SOURCE_DEVICE_NAME: header { type, size, adapterId, id }, then the GDI name.
                new Span<byte>(name, SourceNameSize).Clear();
                *(uint*)name = 1; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                *(uint*)(name + 4) = SourceNameSize;
                *(long*)(name + 8) = *(long*)path; // source adapter id
                *(uint*)(name + 16) = *(uint*)(path + PathSourceId);
                if (DisplayConfigGetDeviceInfo(name) != 0) continue;
                if (!string.Equals(new string((char*)(name + 20)), device, StringComparison.OrdinalIgnoreCase)) continue;

                found = true;
                if (IsBuiltIn(*(uint*)(path + PathTargetTech))) return true;
            }
            return found ? false : null;
        }
    }

    private bool Query(out byte ac, out byte dc)
    {
        var value = stackalloc byte[3];
        uint returned;
        bool ok = DeviceIoControl(_lcd, IoctlQuery, null, 0, value, 3, &returned, 0) != 0;
        ac = value[1];
        dc = value[2];
        return ok;
    }

    private static bool OnBattery()
    {
        SYSTEM_POWER_STATUS status;
        return GetSystemPowerStatus(&status) != 0 && status.ACLineStatus == 0;
    }

    public void Dispose()
    {
        _ddc?.Dispose(); // sends the last value, then lets the monitors go
        _ddc = null;
        if (_lcd == 0) return;
        CloseHandle(_lcd);
        _lcd = 0;
    }
}
