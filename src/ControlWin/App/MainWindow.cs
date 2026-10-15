using ControlWin.Input;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.App;

/// <summary>
/// Hidden top-level window that owns device notifications and (later) tray, timers and app messages.
/// Deliberately not HWND_MESSAGE: message-only windows don't receive broadcasts such as TaskbarCreated.
/// </summary>
internal sealed unsafe class MainWindow : NativeWindow
{
    public const string ClassName = "ControlWin.Main";

    private static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly HidControllerReader _reader;
    private nint _deviceNotification;

    /// <summary>Receives WM_TIMER; set right after construction (the controller needs this window's handle).</summary>
    public AppController? Controller { get; set; }

    public MainWindow(HidControllerReader reader)
    {
        _reader = reader;
        Create(ClassName, WS_POPUP, 0);
        RegisterHidNotifications();
    }

    /// <summary>Tells us when the controller (dis)connects, so nothing has to poll.</summary>
    private void RegisterHidNotifications()
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE_W
        {
            dbcc_size = (uint)sizeof(DEV_BROADCAST_DEVICEINTERFACE_W),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = HidInterface,
        };
        _deviceNotification = RegisterDeviceNotificationW(Handle, &filter, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_deviceNotification == 0)
            Log.Error($"RegisterDeviceNotification failed ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
    }

    protected override nint WndProc(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_DEVICECHANGE:
                OnDeviceChange(wParam, lParam);
                return 1; // TRUE: grant the request

            case WM_TIMER:
                Controller?.OnTimer(wParam);
                return 0;

            case WM_CLOSE:
                DestroyWindow(Handle);
                return 0;

            case WM_DESTROY:
                if (_deviceNotification != 0) UnregisterDeviceNotification(_deviceNotification);
                _deviceNotification = 0;
                PostQuitMessage(0);
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    private void OnDeviceChange(nuint what, nint lParam)
    {
        if (what != DBT_DEVICEARRIVAL && what != DBT_DEVICEREMOVECOMPLETE) return;
        if (lParam == 0) return;

        var header = (DEV_BROADCAST_DEVICEINTERFACE_W*)lParam;
        if (header->dbcc_devicetype != DBT_DEVTYP_DEVICEINTERFACE) return;

        if (what == DBT_DEVICEARRIVAL)
        {
            _reader.TryOpen();
        }
        else
        {
            var path = new string(&header->dbcc_name);
            _reader.OnInterfaceRemoved(path);
        }
    }
}
