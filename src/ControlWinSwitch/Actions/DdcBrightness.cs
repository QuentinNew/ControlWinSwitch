using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>
/// Brightness of external monitors over DDC/CI (the monitor's own setting, which it keeps). Each command takes
/// about 50 to 150 ms, so values are sent by a worker thread that always sends the latest one and skips the rest.
/// </summary>
internal sealed unsafe class DdcBrightness : IDisposable
{
    private readonly record struct Monitor(nint Handle, uint Min, uint Max);

    private readonly Monitor[] _monitors;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile int _pending = -1; // the next value to send, -1 = none
    private volatile bool _stop;

    /// <summary>Percent read when opened.</summary>
    public int Initial { get; }

    private DdcBrightness(Monitor[] monitors, int initial)
    {
        _monitors = monitors;
        Initial = initial;
        _thread = new Thread(Run) { IsBackground = true, Name = "ddc brightness" };
        _thread.Start();
    }

    /// <summary>The monitors of <paramref name="hmonitor"/> that answer DDC/CI brightness; null when none does. Blocks ~100 ms.</summary>
    public static DdcBrightness? Open(nint hmonitor)
    {
        uint count;
        if (GetNumberOfPhysicalMonitorsFromHMONITOR(hmonitor, &count) == 0 || count == 0) return null;
        var physical = new PHYSICAL_MONITOR[count];
        fixed (PHYSICAL_MONITOR* p = physical)
            if (GetPhysicalMonitorsFromHMONITOR(hmonitor, count, p) == 0) return null;

        var usable = new List<Monitor>();
        int initial = -1;
        foreach (var monitor in physical)
        {
            uint min, current, max;
            if (GetMonitorBrightness(monitor.hPhysicalMonitor, &min, &current, &max) == 0 || max <= min)
            {
                DestroyPhysicalMonitor(monitor.hPhysicalMonitor); // the built-in screen, or no DDC/CI
                continue;
            }
            usable.Add(new Monitor(monitor.hPhysicalMonitor, min, max));
            if (initial < 0) initial = ToPercent(current, min, max);
        }
        return usable.Count == 0 ? null : new DdcBrightness([.. usable], initial);
    }

    internal static int ToPercent(uint value, uint min, uint max) =>
        max <= min ? 0 : (int)Math.Round((Math.Clamp(value, min, max) - min) * 100.0 / (max - min));

    internal static uint FromPercent(int percent, uint min, uint max) =>
        min + (uint)Math.Round(Math.Clamp(percent, 0, 100) * (max - min) / 100.0);

    /// <summary>Queues <paramref name="percent"/>; returns at once.</summary>
    public void Set(int percent)
    {
        _pending = Math.Clamp(percent, 0, 100);
        _wake.Set();
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            int percent = Interlocked.Exchange(ref _pending, -1);
            if (percent >= 0)
                foreach (var monitor in _monitors)
                    if (SetMonitorBrightness(monitor.Handle, FromPercent(percent, monitor.Min, monitor.Max)) == 0)
                        Log.Info("DDC/CI: the monitor refused the brightness");
            if (_stop && _pending < 0) break; // the last value is sent before leaving
        }
        foreach (var monitor in _monitors) DestroyPhysicalMonitor(monitor.Handle);
        _wake.Dispose();
    }

    /// <summary>The worker sends the last queued value, then closes the monitors and ends.</summary>
    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _wake.Set();
    }
}
