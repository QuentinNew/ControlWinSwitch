using ControlWin.Input;
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

    private uint _serial = (uint)Environment.ProcessId << 8; // unique per process and plug-in: a lingering target can never collide

    private nint _bus = INVALID_HANDLE_VALUE;
    private bool _plugged;
    private XusbReport _last;
    private bool _hasLast;

    public bool IsAttached => _plugged;

    /// <summary>Plugs the pad in. Returns false (and logs why) when ViGEmBus is missing or refuses.</summary>
    public bool Attach()
    {
        if (_plugged) return true;
        try
        {
            if (_bus == INVALID_HANDLE_VALUE && !OpenBus()) return false;

            // VIGEM_CHECK_VERSION { Size, Version = 1 }
            uint* check = stackalloc uint[2] { 8, 1 };
            uint returned;
            if (DeviceIoControl(_bus, IoctlCheckVersion, check, 8, null, 0, &returned, 0) == 0)
            {
                Log.Error($"ViGEmBus version check failed (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
                CloseBus();
                return false;
            }

            // VIGEM_PLUGIN_TARGET { Size = 16, SerialNo, TargetType = X360Wired (0), VendorId = 0, ProductId = 0 }
            uint* plug = stackalloc uint[4] { 16, ++_serial, 0, 0 };
            if (DeviceIoControl(_bus, IoctlPlugin, plug, 16, null, 0, &returned, 0) == 0)
            {
                Log.Error($"ViGEmBus refused the virtual pad (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
                CloseBus();
                return false;
            }

            // VIGEM_WAIT_DEVICE_READY { Size = 8, SerialNo }: blocks until the new pad finished enumerating.
            uint* wait = stackalloc uint[2] { 8, _serial };
            if (DeviceIoControl(_bus, IoctlWaitReady, wait, 8, null, 0, &returned, 0) == 0)
                Log.Error($"Virtual pad wait-ready failed (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");

            _plugged = true;
            _hasLast = false;
            Log.Info("Virtual Xbox 360 pad plugged in");
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
        uint returned;
        if (DeviceIoControl(_bus, IoctlXusbSubmit, buffer, 20, null, 0, &returned, 0) == 0)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
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
        if (_plugged && _bus != INVALID_HANDLE_VALUE)
        {
            uint* unplug = stackalloc uint[2] { 8, _serial };
            uint returned;
            DeviceIoControl(_bus, IoctlUnplug, unplug, 8, null, 0, &returned, 0);
            Log.Info("Virtual Xbox 360 pad removed");
        }
        _plugged = false;
        _hasLast = false;
        CloseBus();
    }

    public void Dispose() => Detach();

    private bool OpenBus()
    {
        var guid = BusInterface;
        uint length = 0;
        if (CM_Get_Device_Interface_List_SizeW(&length, &guid, null, 0) != 0 || length <= 1)
        {
            Log.Error("ViGEmBus not found: virtual pad unavailable (install ViGEmBus)");
            return false;
        }

        var buffer = new char[length];
        string path;
        fixed (char* p = buffer)
        {
            if (CM_Get_Device_Interface_ListW(&guid, null, p, length, 0) != 0)
            {
                Log.Error("ViGEmBus interface list failed");
                return false;
            }
            path = new string(p);
        }

        _bus = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
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
