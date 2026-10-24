using System.Runtime.InteropServices;
using ControlWinSwitch.Input;
using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>What the virtual pad presents itself as.</summary>
internal enum PadKind { Xbox, Ps4 }

/// <summary>
/// A virtual Xbox 360 or DualShock 4 pad on the ViGEmBus driver. Talks to the bus driver directly with IOCTLs
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
        IoctlCheckVersion = Ioctl(0x803), IoctlWaitReady = Ioctl(0x804), IoctlXusbSubmit = Ioctl(0xA02), IoctlDs4Submit = Ioctl(0xA03);

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER, 0xA01, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA): completes when a game
    // changes the pad's vibration. XUSB_REQUEST_NOTIFICATION { Size = 12, SerialNo, LargeMotor, SmallMotor, LedNumber }.
    private static readonly uint IoctlXusbRequestNotification = (FileDeviceBusExtender << 16) | (3u << 14) | (0xA01u << 2);

    // CTL_CODE(..., 0xA04, METHOD_BUFFERED, FILE_WRITE_DATA): the DualShock 4 counterpart.
    // DS4_REQUEST_NOTIFICATION { Size = 16, SerialNo, SmallMotor, LargeMotor, R, G, B }.
    private static readonly uint IoctlDs4RequestNotification = Ioctl(0xA04);

    private const uint TargetXbox360Wired = 0, TargetDualShock4Wired = 2;
    private const int NotifyBufferSize = 16;

    private static uint s_nextSerial = (uint)Environment.ProcessId << 8; // unique per process and plug-in: a lingering target can never collide
    private uint _serial;

    private nint _bus = INVALID_HANDLE_VALUE;
    private bool _plugged;
    private XusbReport _last;
    private Ds4Report _lastDs4;
    private bool _hasLast;
    private PadKind _plugKind;

    // Rumble feedback: a pending overlapped request on the bus handle that plugged the pad, completed by the driver.
    // The bus handle is overlapped, so the synchronous calls go through Control().
    private nint _controlEvent;
    private nint _notifyEvent;
    private OVERLAPPED* _notifyOverlapped;
    private byte* _notifyBuffer;
    private bool _notifyPending;

    public bool IsAttached => _plugged;

    /// <summary>What the pad presents itself as the next time it is plugged in; changing it needs a re-plug.</summary>
    public PadKind Kind { get; set; } = PadKind.Xbox;

    /// <summary>What the plugged-in pad presents itself as.</summary>
    public PadKind PluggedKind => _plugKind;

    private string KindName => _plugKind == PadKind.Ps4 ? "DualShock 4" : "Xbox 360";

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

            // VIGEM_PLUGIN_TARGET { Size = 16, SerialNo, TargetType = X360Wired (0) / DualShock4Wired (2), VendorId = 0, ProductId = 0 }
            _plugKind = Kind;
            uint* plug = stackalloc uint[4] { 16, _serial = ++s_nextSerial, _plugKind == PadKind.Ps4 ? TargetDualShock4Wired : TargetXbox360Wired, 0 };
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
            Log.Info($"Virtual {KindName} pad plugged in");
            StartNotify();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Virtual pad attach failed", ex);
            return false;
        }
    }

    /// <summary>Sends an Xbox report; identical consecutive reports are skipped.</summary>
    public void Submit(in XusbReport report)
    {
        if (!_plugged || _plugKind != PadKind.Xbox) return;
        if (_hasLast && _last.Equals(report)) return;

        // XUSB_SUBMIT_REPORT { Size = 20, SerialNo, XUSB_REPORT }
        byte* buffer = stackalloc byte[20];
        *(uint*)buffer = 20;
        *(uint*)(buffer + 4) = _serial;
        *(XusbReport*)(buffer + 8) = report;
        if (!Send(IoctlXusbSubmit, buffer)) return;
        _last = report;
        _hasLast = true;
    }

    /// <summary>Sends a DualShock 4 report; identical consecutive reports are skipped.</summary>
    public void Submit(in Ds4Report report)
    {
        if (!_plugged || _plugKind != PadKind.Ps4) return;
        if (_hasLast && _lastDs4.Equals(report)) return;

        // DS4_SUBMIT_REPORT { Size = 20, SerialNo, DS4_REPORT (9 bytes, padded) }
        byte* buffer = stackalloc byte[20];
        new Span<byte>(buffer, 20).Clear();
        *(uint*)buffer = 20;
        *(uint*)(buffer + 4) = _serial;
        *(Ds4Report*)(buffer + 8) = report;
        if (!Send(IoctlDs4Submit, buffer)) return;
        _lastDs4 = report;
        _hasLast = true;
    }

    /// <summary>Neutral pad of whichever kind is plugged in.</summary>
    public void SubmitNeutral()
    {
        if (_plugKind == PadKind.Ps4) Submit(Ds4Mapper.Neutral);
        else Submit(default(XusbReport));
    }

    private bool Send(uint code, byte* buffer)
    {
        int error = Control(code, buffer, 20);
        if (error == 0) return true;
        if (error == 170) return false; // ERROR_BUSY: not ready yet, the next report retries
        Log.Error($"Virtual pad submit failed (error {error}), unplugging");
        Detach();
        return false;
    }

    public void Detach()
    {
        StopNotify();
        if (_plugged && _bus != INVALID_HANDLE_VALUE)
        {
            uint* unplug = stackalloc uint[2] { 8, _serial };
            Control(IoctlUnplug, unplug, 8);
            Log.Info($"Virtual {KindName} pad removed");
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
            _notifyBuffer = (byte*)NativeMemory.AllocZeroed(NotifyBufferSize);
        }
        RequestNotification();
    }

    private void RequestNotification()
    {
        *_notifyOverlapped = default;
        _notifyOverlapped->hEvent = _notifyEvent;
        bool ps4 = _plugKind == PadKind.Ps4;
        uint size = ps4 ? 16u : 12u;
        new Span<byte>(_notifyBuffer, NotifyBufferSize).Clear();
        *(uint*)_notifyBuffer = size;
        *(uint*)(_notifyBuffer + 4) = _serial;
        uint returned;
        if (DeviceIoControl(_bus, ps4 ? IoctlDs4RequestNotification : IoctlXusbRequestNotification, _notifyBuffer, size, _notifyBuffer, size, &returned, (nint)_notifyOverlapped) == 0)
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
        // Xbox: LargeMotor, SmallMotor. DualShock 4: SmallMotor, LargeMotor.
        byte large = _plugKind == PadKind.Ps4 ? _notifyBuffer[9] : _notifyBuffer[8];
        byte small = _plugKind == PadKind.Ps4 ? _notifyBuffer[8] : _notifyBuffer[9];
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
