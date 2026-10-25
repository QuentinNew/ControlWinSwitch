using ControlWinSwitch.Actions;
using ControlWinSwitch.Config;
using ControlWinSwitch.UI;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.App;

/// <summary>
/// The Bluetooth headset of the Headset node and Settings tab: its state, read and toggled on a worker thread (both
/// block), the outcome posted back to the UI thread as <see cref="AppMessages.HeadsetDone"/>.
/// </summary>
internal sealed class HeadsetService(ConfigStore store, nint hwnd, Toast toast, Func<bool> shutDown, Action openBluetoothSettings)
{
    private HeadsetState _state;
    private bool _busy;
    private volatile IReadOnlyList<string> _devices = []; // written by the worker thread

    /// <summary>A query or toggle finished: the menu and settings show the new state.</summary>
    public event Action? Changed;

    /// <summary>The paired audio devices, read with the last state query (for the Headset tab's name picker).</summary>
    public IReadOnlyList<string> Devices => _devices;

    /// <summary>The status shown on the Headset tab.</summary>
    public string Status => _busy ? "Working..." : Detail() ?? "Unknown";

    /// <summary>The status shown on the headset's menu node.</summary>
    public string? Detail() => _busy ? "..." : _state switch
    {
        HeadsetState.Connected => "Connected",
        HeadsetState.Disconnected => "Disconnected",
        HeadsetState.NotFound => "Not paired",
        HeadsetState.Failed => "Error",
        HeadsetState.Connecting => "Connecting...",
        _ => null,
    };

    /// <summary>Reads the state again (the menu or settings opened).</summary>
    public void Refresh() => Start(toggle: false);

    /// <summary>Connects or disconnects the headset, with a toast saying which.</summary>
    public void Toggle(char glyph)
    {
        if (_busy) return;
        toast.Show(glyph, _state == HeadsetState.Connected ? "Disconnecting headset..." : "Connecting headset...");
        Start(toggle: true);
    }

    /// <summary>Runs the (blocking) headset query or toggle on a worker thread and posts the outcome back.</summary>
    private void Start(bool toggle)
    {
        if (_busy || shutDown()) return;
        _busy = true;
        string name = store.Current.HeadsetName;
        ErrorBoundary.StartWorker("headset", () =>
        {
            if (!toggle) _devices = BluetoothHeadset.ListDevices();
            var state = toggle ? BluetoothHeadset.Toggle(name) : BluetoothHeadset.GetState(name);
            PostMessageW(hwnd, AppMessages.HeadsetDone, (nuint)(int)state, toggle ? 1 : 0);
        }, onFailure: () => PostMessageW(hwnd, AppMessages.HeadsetDone, (nuint)(int)HeadsetState.Failed, toggle ? 1 : 0));
    }

    /// <summary>AppMessages.HeadsetDone, on the UI thread.</summary>
    public void OnDone(HeadsetState state, bool wasToggle)
    {
        _busy = false;
        if (shutDown()) return;
        _state = state;
        Log.Info($"Headset: {state}");

        if (wasToggle)
        {
            const char glyph = MenuPages.Headphone;
            switch (state)
            {
                case HeadsetState.Connected: toast.Show(glyph, "Headset connected"); break;
                case HeadsetState.Disconnected: toast.Show(glyph, "Headset disconnected"); break;
                case HeadsetState.Connecting: break; // no toast: the menu row shows it
                case HeadsetState.NotFound:
                    toast.Show(glyph, string.IsNullOrWhiteSpace(store.Current.HeadsetName) ? "Set the headset name in Settings" : "Headset not paired");
                    break;
                default:
                    toast.Show(glyph, "Headset failed - opening Bluetooth settings");
                    openBluetoothSettings();
                    break;
            }
        }
        Changed?.Invoke();
    }
}
