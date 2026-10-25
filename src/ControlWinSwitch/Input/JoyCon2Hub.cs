using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Input;

/// <summary>
/// Finds Joy-Con 2s over Bluetooth LE. They are never paired in Windows: a Joy-Con paired with this PC advertises our
/// adapter's address once a button wakes it, and is connected right away; one in sync mode advertises no address and is
/// claimed (paired) only while <see cref="StartPairing"/> is open. The scan only runs while the app looks for them (see
/// <see cref="SetScanning"/>): it wakes the app for every Bluetooth LE device nearby.
/// </summary>
/// <remarks>
/// WinRT calls back on the thread pool: everything meant for the app is queued and run by <see cref="OnSignaled"/> on
/// the UI thread, after <see cref="EventHandle"/> (one of the handles the message loop waits on) fires.
/// </remarks>
internal sealed class JoyCon2Hub : IDisposable
{
    private readonly nint _event;
    private readonly Lock _lock = new();
    private List<Action> _queue = [], _draining = [];
    private readonly HashSet<ulong> _busy = []; // connecting or connected
    private readonly List<JoyCon2Device> _devices = [];

    private BluetoothLEAdvertisementWatcher? _watcher;
    private ulong _hostAddress;
    private bool _starting, _wantScan, _disposed;
    private long _pairUntil;

    public unsafe JoyCon2Hub()
    {
        _event = CreateEventW(0, 0, 0, null); // auto reset
        if (_event == 0) throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()})");
    }

    /// <summary>Signaled when work for the UI thread is queued. Always a valid handle.</summary>
    public nint EventHandle => _event;

    /// <summary>A Joy-Con is connected and set up (UI thread). Whoever handles it owns it, or calls <see cref="JoyCon2Device.Disconnect"/>.</summary>
    public event Action<JoyCon2Device>? DeviceReady;

    /// <summary>A Joy-Con in sync mode was paired with this PC (UI thread).</summary>
    public event Action<JoyCon2Device>? DevicePaired;

    /// <summary>Queues <paramref name="action"/> for the UI thread (any thread).</summary>
    public void Post(Action action)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _queue.Add(action);
        }
        SetEvent(_event);
    }

    /// <summary>Call when <see cref="EventHandle"/> is signaled: runs the queued work.</summary>
    public void OnSignaled()
    {
        lock (_lock) (_queue, _draining) = (_draining, _queue);
        foreach (var action in _draining)
        {
            try { action(); }
            catch (Exception ex) { ErrorBoundary.OnUiError("Joy-Con 2", ex); }
        }
        _draining.Clear();
    }

    /// <summary>
    /// Scans while asked to (the Controller tab of the settings is shown and a slot can take a Joy-Con), and during the
    /// pairing window (UI thread). The adapter is looked up on the first start.
    /// </summary>
    public void SetScanning(bool scan)
    {
        if (_disposed) return;
        _wantScan = scan;
        if (_hostAddress == 0)
        {
            if (scan && !_starting) _ = FindAdapterAsync();
            return;
        }
        ApplyScanning();
    }

    private void ApplyScanning()
    {
        bool scan = _wantScan || PairingOpen;
        if (scan && _watcher is null)
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += OnAdvertisement;
            _watcher.Stopped += OnWatcherStopped;
            try
            {
                _watcher.Start();
                Log.Info("Joy-Con 2: scanning");
            }
            catch (Exception ex)
            {
                Log.Error("Joy-Con 2: scanning failed", ex);
                DropWatcher();
            }
        }
        else if (!scan && _watcher is not null)
        {
            DropWatcher();
            Log.Info("Joy-Con 2: scanning stopped");
        }
    }

    private void DropWatcher()
    {
        if (_watcher is null) return;
        _watcher.Received -= OnAdvertisement;
        _watcher.Stopped -= OnWatcherStopped;
        try { _watcher.Stop(); } catch { /* already stopped */ }
        _watcher = null;
    }

    private async Task FindAdapterAsync()
    {
        _starting = true;
        ulong address = 0;
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) Log.Info("Joy-Con 2: no Bluetooth adapter");
            else if (!adapter.IsLowEnergySupported) Log.Info("Joy-Con 2: the Bluetooth adapter has no Low Energy support");
            else address = adapter.BluetoothAddress;
        }
        catch (Exception ex)
        {
            Log.Error("Joy-Con 2: Bluetooth adapter", ex);
        }
        Post(() =>
        {
            _starting = false;
            if (address == 0 || _disposed) return;
            _hostAddress = address;
            ApplyScanning();
        });
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error == BluetoothError.Success) return;
        Log.Error($"Joy-Con 2: scanning stopped ({args.Error})");
        // Radio off or busy: try again a little later.
        Post(() => { if (_watcher == sender) DropWatcher(); });
        _ = Task.Delay(5000).ContinueWith(_ => Post(() => { if (!_disposed) ApplyScanning(); }));
    }

    // ---- pairing ---------------------------------------------------------------------------------

    private bool PairingOpen => Environment.TickCount64 < Volatile.Read(ref _pairUntil);

    /// <summary>For <paramref name="milliseconds"/>, a Joy-Con 2 in sync mode is claimed (UI thread).</summary>
    public void StartPairing(int milliseconds)
    {
        Volatile.Write(ref _pairUntil, Environment.TickCount64 + milliseconds);
        if (_hostAddress == 0) SetScanning(_wantScan);
        else ApplyScanning();
        _ = Task.Delay(milliseconds + 100).ContinueWith(_ => Post(() => { if (!_disposed) ApplyScanning(); }));
    }

    /// <summary>Ends the pairing window early (UI thread).</summary>
    public void StopPairing()
    {
        Volatile.Write(ref _pairUntil, 0);
        if (_hostAddress != 0) ApplyScanning();
    }

    // ---- connecting (thread pool) ----------------------------------------------------------------

    private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var data in args.Advertisement.ManufacturerData)
        {
            if (data.CompanyId != JoyCon2Protocol.NintendoCompanyId) continue;
            var bytes = new byte[data.Data.Length];
            using (var reader = DataReader.FromBuffer(data.Data)) reader.ReadBytes(bytes);
            if (!JoyCon2Protocol.TryParseAdvertisement(bytes, out var side, out var reconnect)) continue;

            ulong host = _hostAddress;
            bool pair = reconnect == 0;
            if (pair ? !PairingOpen : reconnect != host) return; // in sync mode outside pairing, or another host's
            ulong address = args.BluetoothAddress;
            lock (_lock)
                if (_disposed || !_busy.Add(address)) return;
            Log.Info($"Joy-Con 2 {(side == JoyConSide.Left ? "(L)" : "(R)")} {JoyCon2Protocol.FormatAddress(address)} found{(pair ? " in sync mode" : "")}");
            _ = ConnectAsync(address, side, pair, host);
            return;
        }
    }

    private async Task ConnectAsync(ulong address, JoyConSide side, bool pair, ulong host)
    {
        var device = await JoyCon2Device.ConnectAsync(this, address, side, pair, host);
        if (device is null)
        {
            Release(address);
            return;
        }
        Post(() =>
        {
            if (_disposed)
            {
                device.Disconnect();
                return;
            }
            _devices.Add(device);
            device.Removed += _ => _devices.Remove(device);
            if (pair) DevicePaired?.Invoke(device);
            DeviceReady?.Invoke(device);
        });
    }

    /// <summary>The device is gone: its address may connect again.</summary>
    internal void Release(ulong address)
    {
        lock (_lock) _busy.Remove(address);
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock) _disposed = true;
        DropWatcher();
        foreach (var device in _devices.ToArray()) device.Disconnect();
        // The event stays open: a worker thread may still signal it until the process exits.
    }
}
