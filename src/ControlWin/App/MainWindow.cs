using ControlWin.Actions;
using ControlWin.Input;
using ControlWin.Native;
using ControlWin.UI;
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

    private readonly ControllerSet _set;
    private readonly TrayIcon _tray;
    private nint _deviceNotification;

    /// <summary>Receives WM_TIMER; set right after construction (the controller needs this window's handle).</summary>
    public AppController? Controller { get; set; }

    public MainWindow(ControllerSet set)
    {
        _set = set;
        Create(ClassName, WS_POPUP, 0);
        RegisterHidNotifications();

        // The app runs elevated; the second instance, --quit and explorer's TaskbarCreated broadcast may not.
        ChangeWindowMessageFilterEx(Handle, AppMessages.ShowMenu, MSGFLT_ALLOW, 0);
        ChangeWindowMessageFilterEx(Handle, AppMessages.Quit, MSGFLT_ALLOW, 0);
        ChangeWindowMessageFilterEx(Handle, AppMessages.TaskbarCreated, MSGFLT_ALLOW, 0);

        _tray = new TrayIcon(Handle);
        _tray.Add(TrayIcon.Tooltip(_set.OpenCount));
    }

    /// <summary>Call after the controller (dis)connected.</summary>
    public bool ControllerConnected(int slot) => _set.Source(slot).IsOpen;

    public bool SetRumble(int slot, bool on) => _set.Source(slot).SetRumble(on);

    public bool SetRumbleMotors(int slot, byte large, byte small, int percent) => _set.Source(slot).SetRumbleMotors(large, small, percent);

    public void UpdateTray() => _tray.SetTooltip(TrayIcon.Tooltip(_set.OpenCount));

    private const int MenuHotkeyId = 1;
    private bool _hotkeyRegistered;

    /// <summary>Registers the global shortcut that toggles the menu (null = none). Returns false when another app owns it.</summary>
    public bool SetMenuHotkey(Config.Hotkey? hotkey)
    {
        if (_hotkeyRegistered) UnregisterHotKey(Handle, MenuHotkeyId);
        _hotkeyRegistered = hotkey is { } h && RegisterHotKey(Handle, MenuHotkeyId, h.Modifiers | MOD_NOREPEAT, h.Key) != 0;
        if (hotkey is not null && !_hotkeyRegistered)
            Log.Error($"RegisterHotKey {hotkey} failed ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
        return hotkey is null || _hotkeyRegistered;
    }

    /// <summary>Removes the tray icon right away (shutdown).</summary>
    public void RemoveTray() => _tray.Dispose();

    private void OnTrayCallback(nint lParam)
    {
        switch ((uint)LoWord(lParam) & 0xFFFF)
        {
            case WM_LBUTTONUP:
            case NIN_KEYSELECT:
                Controller?.ToggleMenu();
                break;

            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
                switch (_tray.ShowMenu(Controller?.PadEnabled ?? false, Controller?.PadNote))
                {
                    case TrayIcon.CmdOpenMenu: Controller?.ShowMenu(); break;
                    case TrayIcon.CmdTogglePad: Controller?.TogglePad(''); break;
                    case TrayIcon.CmdOpenLog: Controller?.OpenLog(); break;
                    case TrayIcon.CmdSettings: Controller?.ShowSettings(); break;
                    case TrayIcon.CmdEditConfig: Controller?.EditConfig(); break;
                    case TrayIcon.CmdQuit: PostMessageW(Handle, WM_CLOSE, 0, 0); break;
                }
                break;
        }
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
        if (msg == AppMessages.ShowMenu)
        {
            Controller?.ShowMenu();
            return 0;
        }
        if (msg == AppMessages.Quit)
        {
            PostMessageW(Handle, WM_CLOSE, 0, 0);
            return 0;
        }
        if (msg == AppMessages.TaskbarCreated)
        {
            _tray.Add(TrayIcon.Tooltip(_set.OpenCount)); // explorer restarted
            return 0;
        }

        switch (msg)
        {
            case TrayIcon.CallbackMessage:
                OnTrayCallback(lParam);
                return 0;

            case WM_QUERYENDSESSION:
                return 1;

            case WM_ENDSESSION:
                if (wParam != 0)
                {
                    Controller?.Shutdown();
                    Controller?.Dispose();
                    _tray.Dispose();
                }
                return 0;

            case WM_DEVICECHANGE:
                OnDeviceChange(wParam, lParam);
                return 1; // TRUE: grant the request

            case WM_POWERBROADCAST:
                if (wParam == PBT_APMSUSPEND || wParam == PBT_APMRESUMEAUTOMATIC) Controller?.OnPowerChange();
                return 1; // BROADCAST_QUERY_DENY is not used

            case WM_TIMER:
                Controller?.OnTimer(wParam);
                return 0;

            case WM_HOTKEY:
                if ((int)wParam == MenuHotkeyId) Controller?.ToggleMenu();
                return 0;

            case AppMessages.HeadsetDone:
                Controller?.OnHeadsetDone((HeadsetState)(int)wParam, lParam != 0);
                return 0;

            case AppMessages.PairDone:
                Controller?.OnPairDone((PairResult)(int)wParam);
                return 0;

            case AppMessages.PowerFailed:
                Controller?.OnPowerFailed();
                return 0;

            case WM_CLOSE:
                Controller?.Shutdown(); // pad removed, menu closed, tray icon deleted
                DestroyWindow(Handle);
                return 0;

            case WM_DESTROY:
                _tray.Dispose();
                SetMenuHotkey(null);
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
            _set.TryOpen();
        }
        else
        {
            var path = new string(&header->dbcc_name);
            _set.OnInterfaceRemoved(path);
        }
        UpdateTray();
    }
}
