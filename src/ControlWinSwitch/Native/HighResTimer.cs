using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Native;

/// <summary>
/// High-resolution periodic waitable timer. Its handle joins the message loop's wait set, so ticks stay precise
/// without raising the system timer resolution, and nothing fires while it is cancelled.
/// </summary>
internal sealed unsafe class HighResTimer : IDisposable
{
    private nint _handle;
    private bool _armed;

    public HighResTimer()
    {
        _handle = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_handle == 0) Log.Error($"CreateWaitableTimerEx failed ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
    }

    public nint Handle => _handle;

    public void Start(int periodMs)
    {
        if (_handle == 0 || _armed) return;
        long due = -(long)periodMs * 10_000; // relative, 100 ns units
        _armed = SetWaitableTimer(_handle, &due, periodMs, 0, 0, 0) != 0;
    }

    public void Stop()
    {
        if (_handle == 0 || !_armed) return;
        CancelWaitableTimer(_handle);
        _armed = false;
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Stop();
        CloseHandle(_handle);
        _handle = 0;
    }
}
