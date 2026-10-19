using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ControlWin.Input;

/// <summary>
/// One connected Joy-Con 2 (Bluetooth LE GATT). Set up by <see cref="ConnectAsync"/> on a worker thread: commands go out
/// on the command characteristic and their replies come back as notifications. Once set up, its input notifications and
/// its disconnection reach the UI thread through the hub's queue (<see cref="Input"/>, <see cref="Removed"/>), and the
/// UI thread drives LEDs and rumble; every GATT call itself runs on the thread pool.
/// </summary>
internal sealed class JoyCon2Device
{
    private const int CommandTimeoutMs = 1500, FirstInputTimeoutMs = 2000;
    private const long RumbleResendMs = 20, RumbleHoldMs = 1000;

    private readonly JoyCon2Hub _hub;
    private readonly BluetoothLEDevice _device;
    private GattCharacteristic _command = null!, _commandReply = null!, _input = null!, _vibration = null!;
    private IReadOnlyList<GattDeviceService> _services = [];
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private TaskCompletionSource<byte[]>? _reply;
    private TaskCompletionSource<JoyCon2Input>? _firstInput;
    private volatile bool _ready, _gone;

    private JoyCon2Device(JoyCon2Hub hub, BluetoothLEDevice device, ulong address, JoyConSide side)
    {
        _hub = hub;
        _device = device;
        Address = address;
        Side = side;
        Mac = JoyCon2Protocol.FormatAddress(address);
    }

    public ulong Address { get; }
    public JoyConSide Side { get; }

    /// <summary>"AA:BB:CC:DD:EE:FF": the id its profile and picture are stored under.</summary>
    public string Mac { get; }

    /// <summary>The stick's ranges from the Joy-Con's memory (null when it holds none).</summary>
    public AxisRange[]? Calibration { get; private set; }

    /// <summary>A side button (SL / SR) was held in the first report: the user wants it alone, sideways.</summary>
    public bool SideButtonsHeld { get; private set; }

    public string Name => Side == JoyConSide.Left ? "Joy-Con 2 (L)" : "Joy-Con 2 (R)";

    /// <summary>Raised on the UI thread for every input notification.</summary>
    public event Action<JoyCon2Device, JoyCon2Input>? Input;

    /// <summary>Raised once on the UI thread when the connection is lost (or <see cref="Disconnect"/> was called).</summary>
    public event Action<JoyCon2Device>? Removed;

    /// <summary>
    /// Connects, reads the calibration, pairs it when <paramref name="pair"/> (writes our address so it reconnects to
    /// this PC), enables input and waits for its first report. Null when any step fails (logged).
    /// </summary>
    public static async Task<JoyCon2Device?> ConnectAsync(JoyCon2Hub hub, ulong address, JoyConSide side, bool pair, ulong hostAddress)
    {
        string mac = JoyCon2Protocol.FormatAddress(address);
        BluetoothLEDevice? ble = null;
        try
        {
            ble = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (ble is null)
            {
                Log.Error($"Joy-Con 2 {mac}: not reachable");
                return null;
            }
            var device = new JoyCon2Device(hub, ble, address, side);
            if (await device.SetUpAsync(pair, hostAddress)) return device;
            device.Close();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Joy-Con 2 {mac}: connection failed", ex);
            ble?.Dispose();
            return null;
        }
    }

    private async Task<bool> SetUpAsync(bool pair, ulong hostAddress)
    {
        // Shorter connection interval: lower input latency (Windows 11 only).
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            _device.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);

        var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success)
        {
            Log.Error($"{Name} {Mac}: GATT services unavailable ({services.Status})");
            return false;
        }
        _services = services.Services;
        var vibrationUuid = Side == JoyConSide.Left ? JoyCon2Protocol.VibrationLeftUuid : JoyCon2Protocol.VibrationRightUuid;
        GattCharacteristic? command = null, reply = null, input = null, vibration = null;
        foreach (var service in services.Services)
        {
            var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success) continue;
            foreach (var c in result.Characteristics)
            {
                if (c.Uuid == JoyCon2Protocol.CommandWriteUuid) command = c;
                else if (c.Uuid == JoyCon2Protocol.CommandResponseUuid) reply = c;
                else if (c.Uuid == JoyCon2Protocol.InputReportUuid) input = c;
                else if (c.Uuid == vibrationUuid) vibration = c;
            }
        }
        if (command is null || reply is null || input is null || vibration is null)
        {
            Log.Error($"{Name} {Mac}: expected characteristics missing");
            return false;
        }
        (_command, _commandReply, _input, _vibration) = (command, reply, input, vibration);

        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _commandReply.ValueChanged += OnCommandReply;
        if (!await Subscribe(_commandReply)) return false;

        Calibration = await ReadCalibrationAsync();

        if (pair)
        {
            await CommandAsync(JoyCon2Protocol.PairSetAddress(hostAddress));
            await CommandAsync(JoyCon2Protocol.PairLtk1());
            await CommandAsync(JoyCon2Protocol.PairLtk2());
            await CommandAsync(JoyCon2Protocol.PairFinish());
            Log.Info($"{Name} {Mac}: paired with this PC");
        }

        _firstInput = new TaskCompletionSource<JoyCon2Input>(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.ValueChanged += OnInputNotification;
        if (!await Subscribe(_input)) return false;
        var first = await Task.WhenAny(_firstInput.Task, Task.Delay(FirstInputTimeoutMs));
        if (first != _firstInput.Task)
        {
            Log.Error($"{Name} {Mac}: no input report");
            return false;
        }
        var firstInput = _firstInput.Task.Result;
        SideButtonsHeld = (firstInput.Buttons & JoyCon2Buttons.SideButtons) != 0;
        _ready = true;
        Log.Info($"{Name} {Mac} connected (calibration {(Calibration is null ? "missing" : "read")}{(SideButtonsHeld ? ", sideways" : "")}, " +
            $"battery {firstInput.BatteryMv} mV, current {firstInput.BatteryCurrent})");
        return true;
    }

    private async Task<bool> Subscribe(GattCharacteristic c)
    {
        var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status == GattCommunicationStatus.Success) return true;
        Log.Error($"{Name} {Mac}: notifications refused ({status})");
        return false;
    }

    /// <summary>The user calibration when one was saved, otherwise the factory one.</summary>
    private async Task<AxisRange[]?> ReadCalibrationAsync()
    {
        try
        {
            var user = await ReadMemoryAsync(JoyCon2Protocol.CalibrationLength, JoyCon2Protocol.UserCalibrationAddress);
            if (user is not null && !JoyCon2Protocol.IsBlank(user)) return JoyCon2Protocol.ParseCalibration(user);
            var factory = await ReadMemoryAsync(JoyCon2Protocol.CalibrationLength, JoyCon2Protocol.FactoryCalibrationAddress);
            return factory is null ? null : JoyCon2Protocol.ParseCalibration(factory);
        }
        catch (TimeoutException)
        {
            Log.Error($"{Name} {Mac}: stick calibration not answered, using the default range");
            return null;
        }
    }

    private async Task<byte[]?> ReadMemoryAsync(byte length, uint address) =>
        JoyCon2Protocol.ParseMemoryReply(await CommandAsync(JoyCon2Protocol.ReadMemory(length, address)), length, address);

    /// <summary>Sends a command and waits for its reply (one at a time). Throws <see cref="TimeoutException"/> when unanswered.</summary>
    private async Task<byte[]> CommandAsync(byte[] command)
    {
        await _commandLock.WaitAsync();
        try
        {
            var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reply = reply;
            var status = await Write(_command, command);
            if (status != GattCommunicationStatus.Success) throw new TimeoutException($"command 0x{command[0]:X2} not sent ({status})");
            if (await Task.WhenAny(reply.Task, Task.Delay(CommandTimeoutMs)) != reply.Task)
                throw new TimeoutException($"command 0x{command[0]:X2} not answered");
            var data = reply.Task.Result;
            if (!JoyCon2Protocol.IsReplyTo(data, command[0])) throw new TimeoutException($"command 0x{command[0]:X2}: unexpected reply");
            return data;
        }
        finally
        {
            _reply = null;
            _commandLock.Release();
        }
    }

    private static async Task<GattCommunicationStatus> Write(GattCharacteristic c, byte[] data)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        var option = (c.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
            ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        return await c.WriteValueAsync(writer.DetachBuffer(), option);
    }

    private static byte[] Bytes(IBuffer buffer)
    {
        var data = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(data);
        return data;
    }

    // ---- notifications (thread pool) -------------------------------------------------------------

    private void OnCommandReply(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        _reply?.TrySetResult(Bytes(args.CharacteristicValue));

    private void OnInputNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (!JoyCon2Protocol.TryParseInput(Bytes(args.CharacteristicValue), out var input)) return;
        if (!_ready)
        {
            _firstInput?.TrySetResult(input);
            return;
        }
        _hub.Post(() =>
        {
            if (_gone) return;
            ResendRumble();
            Input?.Invoke(this, input);
        });
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (!_ready || sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        Log.Info($"{Name} {Mac} disconnected");
        _hub.Post(Disconnect);
    }

    // ---- UI thread -------------------------------------------------------------------------------

    /// <summary>Lights the player LEDs (one bit per LED). Fire and forget.</summary>
    public void SetLeds(byte mask)
    {
        if (_gone) return;
        _ = Task.Run(async () =>
        {
            try { await CommandAsync(JoyCon2Protocol.SetPlayerLeds(mask)); }
            catch (Exception ex) { Log.Error($"{Name} {Mac}: player LEDs: {ex.Message}"); }
        });
    }

    private int _rumbleLow, _rumbleHigh;
    private long _rumbleSet, _rumbleSent;
    private byte _rumbleCounter;
    private byte[]? _pendingRumble;
    private bool _rumbleBusy;
    private readonly Lock _rumbleLock = new();

    /// <summary>
    /// Rumbles with these amplitudes (0..<see cref="JoyCon2Protocol.MaxAmplitude"/>); (0, 0) stops it. The Joy-Con stops
    /// unless the frame keeps coming, so it is re-sent from the input reports, for as long as the caller keeps setting it.
    /// </summary>
    public bool SetRumble(int low, int high)
    {
        if (_gone) return false;
        _rumbleLow = low;
        _rumbleHigh = high;
        _rumbleSet = Environment.TickCount64;
        SendRumble();
        return true;
    }

    private void ResendRumble()
    {
        if (_rumbleLow == 0 && _rumbleHigh == 0) return;
        long now = Environment.TickCount64;
        if (now - _rumbleSet > RumbleHoldMs) _rumbleLow = _rumbleHigh = 0; // nobody refreshes it any more: stop
        else if (now - _rumbleSent < RumbleResendMs) return;
        SendRumble();
    }

    /// <summary>Queues the current frame; only the latest one waits while a write is in flight.</summary>
    private void SendRumble()
    {
        _rumbleSent = Environment.TickCount64;
        var packet = JoyCon2Protocol.Rumble(_rumbleCounter++, _rumbleLow, _rumbleHigh);
        lock (_rumbleLock)
        {
            _pendingRumble = packet;
            if (_rumbleBusy) return;
            _rumbleBusy = true;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                byte[]? next;
                lock (_rumbleLock)
                {
                    next = _pendingRumble;
                    _pendingRumble = null;
                    if (next is null || _gone)
                    {
                        _rumbleBusy = false;
                        return;
                    }
                }
                try { await Write(_vibration, next); }
                catch (Exception ex) { Log.Error($"{Name} {Mac}: rumble: {ex.Message}"); }
            }
        });
    }

    /// <summary>Drops the connection (also when it was lost). Raises <see cref="Removed"/> once.</summary>
    public void Disconnect()
    {
        if (_gone) return;
        Close();
        Removed?.Invoke(this);
    }

    private void Close()
    {
        _gone = true;
        _ready = false;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        if (_commandReply is not null) _commandReply.ValueChanged -= OnCommandReply;
        if (_input is not null) _input.ValueChanged -= OnInputNotification;
        var device = _device;
        _ = Task.Run(() =>
        {
            try
            {
                // Windows drops the link once nothing holds the device.
                foreach (var service in _services) service.Dispose();
                device.Dispose();
            }
            catch (Exception ex) { Log.Error("Joy-Con 2: closing", ex); }
        });
        _hub.Release(Address);
    }
}
