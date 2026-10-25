using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>
/// Brightness of the built-in screen, through the display driver's LCD brightness controls (what the keyboard
/// brightness keys change). External monitors are not handled. Open while the slider is shown, then Close.
/// </summary>
internal sealed unsafe class Brightness : IDisposable
{
    private const uint IoctlSupported = 0x230494, IoctlQuery = 0x230498, IoctlSet = 0x23049C;
    private const byte PolicyBoth = 3;

    private nint _lcd;
    private byte[] _levels = [];

    /// <summary>Null when the PC has no screen with adjustable brightness.</summary>
    public static Brightness? Open()
    {
        nint lcd = CreateFileW(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (lcd == -1) return null;

        var levels = stackalloc byte[256];
        uint count = 0;
        if (DeviceIoControl(lcd, IoctlSupported, null, 0, levels, 256, &count, 0) == 0 || count == 0)
        {
            CloseHandle(lcd);
            return null;
        }
        return new Brightness { _lcd = lcd, _levels = new Span<byte>(levels, (int)count).ToArray() };
    }

    /// <summary>Current brightness in percent (the one for the power source in use), or -1 when it can't be read.</summary>
    public int Get()
    {
        if (!Query(out byte ac, out byte dc)) return -1;
        return OnBattery() ? dc : ac;
    }

    /// <summary>
    /// Changes the screen right away. The driver ignores a change of only one of the AC / DC values, so both are
    /// set; <see cref="Save"/> then records it in the power plan, or Windows puts its own value back later.
    /// </summary>
    public bool Set(int percent)
    {
        byte level = Nearest(_levels, percent);
        var value = stackalloc byte[3] { PolicyBoth, level, level };
        uint returned;
        return DeviceIoControl(_lcd, IoctlSet, value, 3, null, 0, &returned, 0) != 0;
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
        if (_lcd == 0) return;
        CloseHandle(_lcd);
        _lcd = 0;
    }
}
