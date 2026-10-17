using System.Runtime.InteropServices;
using ControlWin.Input;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.Actions;

/// <summary>
/// A virtual Xbox 360 pad on the ViGEmBus driver. Talks to the bus driver directly with IOCTLs
/// (no ViGEmClient.dll to ship). Closing the bus handle makes the driver unplug the pad, so a crash
/// never leaves a ghost controller behind. UI thread only.
/// </summary>
internal sealed unsafe class VirtualPad : IDisposable
{
    // GUID_DEVINTERFACE_BUSENUM_VIGEM
    private static readonly Guid BusInterface = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");

    private const uint FileDeviceBusExtender = 0x2A;
    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER, function, METHOD_BUFFERED, FILE_WRITE_DATA)
    private static uint Ioctl(uint function) => (FileDeviceBusExtender << 16) | (2u << 14) | (function << 2);

    private static readonly uint IoctlPlugin = Ioctl(0x801), IoctlUnplug = Ioctl(0x802),
        IoctlCheckVersion = Ioctl(0x803), IoctlWaitReady = Ioctl(0x804), IoctlXusbSubmit = Ioctl(0xA02);

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER, 0xA01, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA): completes when a game
    // changes the pad's vibration. XUSB_REQUEST_NOTIFICATION { Size = 12, SerialNo, LargeMotor, SmallMotor, LedNumber }.
    private static readonly uint IoctlXusbRequestNotification = (FileDeviceBusExtender << 16) | (3u << 14) | (0xA01u << 2);

    private static uint s_nextSerial = (uint)Environment.ProcessId << 8; // unique per process and plug-in: a lingering target can never collide
    private uint _serial;

    private nint _bus = INVALID_HANDLE_VALUE;
    private bool _plugged;
    private XusbReport _last;
    private bool _hasLast;

    // Rumble feedback: a pending overlapped request on the bus handle that plugged the pad, completed by the driver.
    // The bus handle is overlapped, so the synchronous calls go through Control().
    private nint _controlEvent;
    private nint _notifyEvent;
    private OVERLAPPED* _notifyOverlapped;
    private byte* _notifyBuffer;
    private bool _notifyPending;

    public bool IsAttached => _plugged;

    /// <summary>Signaled when a game changed the vibration; 0 when nothing is pending. Call <see cref="OnNotifySignaled"/>.</summary>
    public nint NotifyHandle => _notifyPending ? _notifyEvent : 0;

    /// <summary>A game set the pad's motors (large, small: 0..255). (0, 0) is also raised when the pad goes away.</summary>
    public event Action<byte, byte>? Feedback;

    /// <summary>Plugs the pad in. Returns false (and logs why) when ViGEmBus is missing or refuses.</summary>
    public bool Attach()
    {
        if (_plugged) return true;
        try
        {
            if (_bus == INVALID_HANDLE_VALUE && !OpenBus()) return false;

            // VIGEM_CHECK_VERSION { Size, Version = 1 }
            uint* check = stackalloc uint[2] { 8, 1 };
            if (Control(IoctlCheckVersion, check, 8) != 0)
            {
                Log.Error($"ViGEmBus version check failed (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
                CloseBus();
                return false;
            }

            // VIGEM_PLUGIN_TARGET { Size = 16, SerialNo, TargetType = X360Wired (0), VendorId = 0, ProductId = 0 }
            uint* plug = stackalloc uint[4] { 16, _serial = ++s_nextSerial, 0, 0 };
            if (Control(IoctlPlugin, plug, 16) != 0)
            {
                Log.Error($"ViGEmBus refused the virtual pad (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
                CloseBus();
                return false;
            }

            // VIGEM_WAIT_DEVICE_READY { Size = 8, SerialNo }: blocks until the new pad finished enumerating.
            uint* wait = stackalloc uint[2] { 8, _serial };
            if (Control(IoctlWaitReady, wait, 8) != 0)
                Log.Error($"Virtual pad wait-ready failed (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");

            _plugged = true;
            _hasLast = false;
            Log.Info("Virtual Xbox 360 pad plugged in");
            StartNotify();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Virtual pad attach failed", ex);
            return false;
        }
    }

    /// <summary>Sends a report; identical consecutive reports are skipped.</summary>
    public void Submit(in XusbReport report)
    {
        if (!_plugged) return;
        if (_hasLast && _last.Equals(report)) return;

        // XUSB_SUBMIT_REPORT { Size = 20, SerialNo, XUSB_REPORT }
        byte* buffer = stackalloc byte[20];
        *(uint*)buffer = 20;
        *(uint*)(buffer + 4) = _serial;
        *(XusbReport*)(buffer + 8) = report;
        int error = Control(IoctlXusbSubmit, buffer, 20);
        if (error != 0)
        {
            if (error == 170) return; // ERROR_BUSY: not ready yet, the next report retries
            Log.Error($"Virtual pad submit failed (error {error}), unplugging");
            Detach();
            return;
        }
        _last = report;
        _hasLast = true;
    }

    public void Detach()
    {
        StopNotify();
        if (_plugged && _bus != INVALID_HANDLE_VALUE)
        {
            uint* unplug = stackalloc uint[2] { 8, _serial };
            Control(IoctlUnplug, unplug, 8);
            Log.Info("Virtual Xbox 360 pad removed");
        }
        _plugged = false;
        _hasLast = false;
        CloseBus();
    }

    public void Dispose()
    {
        Detach();
        if (_notifyEvent != 0) CloseHandle(_notifyEvent);
        if (_controlEvent != 0) CloseHandle(_controlEvent);
        _notifyEvent = _controlEvent = 0;
        if (_notifyOverlapped != null) NativeMemory.Free(_notifyOverlapped);
        if (_notifyBuffer != null) NativeMemory.Free(_notifyBuffer);
        _notifyOverlapped = null;
        _notifyBuffer = null;
    }

    /// <summary>DeviceIoControl on the overlapped bus handle, waited for. Returns 0, or the Win32 error.</summary>
    private int Control(uint code, void* input, uint size)
    {
        if (_controlEvent == 0) _controlEvent = CreateEventW(0, 1, 0, null);
        OVERLAPPED overlapped = default;
        overlapped.hEvent = _controlEvent;
        uint returned;
        if (DeviceIoControl(_bus, code, input, size, null, 0, &returned, (nint)(&overlapped)) != 0) return 0;
        int error = Marshal.GetLastPInvokeError();
        if (error != ERROR_IO_PENDING) return error;
        if (GetOverlappedResult(_bus, &overlapped, &returned, 1) != 0) return 0;
        return Marshal.GetLastPInvokeError();
    }

    private void StartNotify()
    {
        if (_notifyPending) return;
        if (_notifyEvent == 0)
        {
            _notifyEvent = CreateEventW(0, 1, 0, null); // manual reset: the next request resets it
            _notifyOverlapped = (OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(OVERLAPPED));
            _notifyBuffer = (byte*)NativeMemory.AllocZeroed(12);
        }
        RequestNotification();
    }

    private void RequestNotification()
    {
        *_notifyOverlapped = default;
        _notifyOverlapped->hEvent = _notifyEvent;
        new Span<byte>(_notifyBuffer, 12).Clear();
        *(uint*)_notifyBuffer = 12;
        *(uint*)(_notifyBuffer + 4) = _serial;
        uint returned;
        if (DeviceIoControl(_bus, IoctlXusbRequestNotification, _notifyBuffer, 12, _notifyBuffer, 12, &returned, (nint)_notifyOverlapped) == 0)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (error != ERROR_IO_PENDING)
            {
                Log.Error($"Rumble feedback request failed (error {error})");
                StopNotify();
                return;
            }
        }
        _notifyPending = true; // completed immediately or still pending: the event gets signaled either way
    }

    /// <summary>Call when <see cref="NotifyHandle"/> is signaled.</summary>
    public void OnNotifySignaled()
    {
        if (!_notifyPending) return;
        uint transferred;
        if (GetOverlappedResult(_bus, _notifyOverlapped, &transferred, 0) == 0)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (error == ERROR_IO_INCOMPLETE) return;
            Log.Error($"Rumble feedback read failed (error {error})");
            StopNotify();
            return;
        }
        _notifyPending = false;
        byte large = _notifyBuffer[8], small = _notifyBuffer[9];
        Feedback?.Invoke(large, small);
        if (_plugged) RequestNotification();
    }

    private void StopNotify()
    {
        if (!_notifyPending) return;
        uint n;
        CancelIoEx(_bus, _notifyOverlapped);
        GetOverlappedResult(_bus, _notifyOverlapped, &n, 1);
        _notifyPending = false;
        Feedback?.Invoke(0, 0);
    }

    private static string? FindBusPath()
    {
        var guid = BusInterface;
        uint length = 0;
        if (CM_Get_Device_Interface_List_SizeW(&length, &guid, null, 0) != 0 || length <= 1)
        {
            Log.Error("ViGEmBus not found: virtual pad unavailable (install ViGEmBus)");
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            if (CM_Get_Device_Interface_ListW(&guid, null, p, length, 0) != 0)
            {
                Log.Error("ViGEmBus interface list failed");
                return null;
            }
            return new string(p);
        }
    }

    private bool OpenBus()
    {
        var path = FindBusPath();
        if (path is null) return false;

        _bus = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        if (_bus == INVALID_HANDLE_VALUE)
        {
            Log.Error($"Cannot open ViGEmBus (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
            return false;
        }
        return true;
    }

    private void CloseBus()
    {
        if (_bus != INVALID_HANDLE_VALUE) CloseHandle(_bus);
        _bus = INVALID_HANDLE_VALUE;
    }
}
