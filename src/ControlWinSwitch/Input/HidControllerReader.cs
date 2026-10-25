using System.Runtime.InteropServices;
using ControlWinSwitch.Actions;
using ControlWinSwitch.Native;
using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Input;

/// <summary>What a <see cref="HidControllerReader"/> has open.</summary>
internal enum HidKind : byte { Pro, JoyConLeft, JoyConRight }

/// <summary>
/// Reads Switch Pro Controller reports by opening its HID interface directly with an overlapped read. Original Joy-Cons
/// speak the same protocol and are read the same way: their reports go to <see cref="JoyConInput"/> (for a
/// <see cref="JoyCon1Device"/>) instead of <see cref="Report"/>.
/// Raw Input is not used: it is served by the system itself, so HidHide hides the controller from it even
/// for allow-listed applications. A direct open honours HidHide's application allow-list.
/// Besides reading, it sends one standard command (set input report mode 0x30): in the default simple mode
/// this controller does not report Capture (or Plus) at all. BetterJoy and Steam send the same command, so
/// they keep working alongside.
/// </summary>
/// <remarks>
/// Event-driven: <see cref="EventHandle"/> is signaled when a read completes and must be one of the handles the
/// message loop waits on; call <see cref="OnSignaled"/> when it fires. Output works the same way: wait on
/// <see cref="WriteHandle"/> while it is not 0 and call <see cref="OnWriteSignaled"/>. Everything runs on the UI thread.
/// </remarks>
internal sealed unsafe class HidControllerReader : IControllerSource, IDisposable
{
    public const ushort NintendoVid = 0x057E;

    private static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly nint _event;
    private readonly OVERLAPPED* _overlapped = (OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(OVERLAPPED));
    private byte* _buffer;
    private uint _bufferLength;
    private byte[] _managed = new byte[64];
    private nint _file;
    private string? _path;
    private bool _pending;
    private int _lastOpenError;

    // Output: one overlapped write in flight, its completion seen through WriteHandle in the message loop. Subcommands
    // wait their turn in order; rumble only keeps its latest frame (an older one is stale by the time the link is free).
    private enum OutputKind : byte { Command, EnableVibration, Rumble }
    private readonly nint _writeEvent;
    private readonly OVERLAPPED* _writeOverlapped = (OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(OVERLAPPED));
    private byte* _writeBuffer;
    private int _writeCapacity;
    private bool _writePending;
    private bool _writeCancelled;
    private long _writeStarted;
    private OutputKind _writeKind;
    private readonly Queue<(byte[] Report, OutputKind Kind)> _commands = new();
    private byte[]? _rumble;
    private bool _writeFailing;    // the last write failed (only the first failure of a run is logged for rumble)
    private int _commandFailures;  // subcommand writes failed in a row
    private const int MaxQueuedCommands = 16;
    private const long WriteTimeoutMs = 1000;
    private uint _outputLength;
    private bool _canWrite;
    private byte _packetCounter;
    private byte _lightMask; // 0: the LEDs are not set (yet)
    private bool _vibrationEnabled;
    private int _modeAttempts;
    private long _lastModeAttempt;
    private string? _mac, _fallbackId;
    private int _infoAttempts;
    private long _lastInfoAttempt;
    private bool _lightsAcked;
    private int _lightAttempts;
    private long _lastLightAttempt;
    private long _lastLightCheck;
    private const int MaxModeAttempts = 5;
    private const int MaxLightAttempts = 8;
    private const long ModeRetryMs = 1000;
    private const long LightCheckMs = 2000;

    public HidControllerReader()
    {
        _event = CreateEventW(0, 1, 0, null); // manual reset: ReadFile resets it when a new read starts
        if (_event == 0) throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()})");
        _writeEvent = CreateEventW(0, 1, 0, null);
        if (_writeEvent == 0) throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()})");
    }

    /// <summary>Signaled when the pending read completed. Always a valid handle.</summary>
    public nint EventHandle => _event;

    /// <summary>Signaled when the write in flight completed; 0 when none is. Call <see cref="OnWriteSignaled"/>.</summary>
    public nint WriteHandle => _writePending ? _writeEvent : 0;

    public bool IsOpen => _file != 0;

    /// <summary>What is open (meaningful while <see cref="IsOpen"/>).</summary>
    public HidKind Kind { get; private set; }

    public string Name => Kind switch
    {
        HidKind.JoyConLeft => "Joy-Con (L)",
        HidKind.JoyConRight => "Joy-Con (R)",
        _ => "Pro Controller",
    };

    /// <summary>Raised for every decoded report of a Pro Controller (on the UI thread).</summary>
    public event Action<ProReport>? Report;

    /// <summary>Raised for every full report of a Joy-Con: its input, battery level (0..4) and charging state.</summary>
    public event Action<JoyCon2Input, int, bool>? JoyConInput;

    /// <summary>The Joy-Con's stick calibration, once read (null until then, or when its flash holds none).</summary>
    public AxisRange[]? JoyConCalibration { get; private set; }

    /// <summary>The MAC address and the stick calibration were read (or given up on).</summary>
    public bool SetUpDone => _calStep == CalStep.None
        && (_mac is not null || !_canWrite
            || (_infoAttempts >= MaxModeAttempts && Environment.TickCount64 - _lastInfoAttempt >= ModeRetryMs));

    /// <summary>A Pro Controller now drives <paramref name="slot"/>: its player LED shows it.</summary>
    public void Assign(int slot) => LightMask = ProCommands.PlayerLightMask(slot);

    /// <summary>The player LEDs to keep lit (one bit per LED; 0 leaves them alone).</summary>
    public byte LightMask
    {
        get => _lightMask;
        set
        {
            if (value == _lightMask) return;
            _lightMask = value;
            _lightsAcked = false;
            _lightAttempts = 0;
            SetPlayerLights();
        }
    }

    /// <summary>Raised when the controller goes away (disconnected, or the read failed).</summary>
    public event Action? ControllerRemoved;

    /// <summary>Raised once the MAC address was read: <see cref="Id"/> changed from the temporary id to it.</summary>
    public event Action? Identified;

    /// <summary>Device path of the open controller (null when closed).</summary>
    public string? Path => _path;

    public IReadOnlyList<string> HidPaths { get; private set; } = [];

    public string? PartnerId => null;

    public (string Id, bool RightField)? StickProfile(bool right) => Id is { } id ? (id, right) : null;

    /// <summary>
    /// Stable identity of the open controller: its Bluetooth MAC address once the controller has answered, until then
    /// the Windows device instance id (prefixed "dev:"). Null when closed.
    /// </summary>
    public string? Id => _file == 0 ? null : _mac ?? _fallbackId;

    /// <summary>The temporary id used until the MAC is known (null when closed).</summary>
    public string? FallbackId => _file == 0 ? null : _fallbackId;

    /// <summary>The identity is the MAC address (not the temporary instance-id fallback).</summary>
    public bool HasMac => _mac is not null;

    /// <summary>
    /// Opens a controller that is present, not open yet and not in <paramref name="taken"/> (the paths other readers
    /// hold or that are refused): a Pro Controller only when <paramref name="allowPro"/>. Cheap enough to call on every
    /// HID arrival.
    /// </summary>
    public void TryOpen(IReadOnlyCollection<string> taken, bool allowPro)
    {
        if (_file != 0) return;

        var found = FindControllerPath(taken, allowPro);
        if (found is not { } f) return;
        var (path, kind) = f;

        // Read+write so we can ask for full mode; fall back to read-only if write access is refused.
        bool canWrite = true;
        const uint share = FILE_SHARE_READ | FILE_SHARE_WRITE;
        var file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, share, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        if (file == INVALID_HANDLE_VALUE)
        {
            canWrite = false;
            file = CreateFileW(path, GENERIC_READ, share, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        }
        if (file == INVALID_HANDLE_VALUE)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != _lastOpenError)
            {
                _lastOpenError = error;
                Kind = kind;
                Log.Error(error == ERROR_ACCESS_DENIED
                    ? $"{Name} found but access denied (error 5): add this exe to HidHide's allowed applications"
                    : $"{Name} found but CreateFile failed (error {error})");
            }
            return;
        }

        uint length = 0, outputLength = 0;
        nint preparsed;
        if (HidD_GetPreparsedData(file, &preparsed) != 0)
        {
            var caps = stackalloc byte[64];
            if (HidP_GetCaps(preparsed, caps) >= 0)
            {
                length = *(ushort*)(caps + 4); // InputReportByteLength
                outputLength = *(ushort*)(caps + 6); // OutputReportByteLength
                LogButtonCaps(preparsed, *(ushort*)(caps + 46)); // NumberInputButtonCaps
            }
            HidD_FreePreparsedData(preparsed);
        }
        if (length == 0) length = 64;

        _file = file;
        _path = path;
        HidPaths = [path];
        Kind = kind;
        _lightMask = 0;
        JoyConCalibration = null;
        _lastOpenError = 0;
        _bufferLength = length;
        _buffer = (byte*)NativeMemory.Alloc(length);
        _canWrite = canWrite && outputLength > 0;
        _outputLength = outputLength;
        _modeAttempts = 0;
        _vibrationEnabled = false;
        _lastModeAttempt = 0;
        _mac = null;
        _infoAttempts = 0;
        _lastInfoAttempt = 0;
        _lightsAcked = false;
        _lightAttempts = 0;
        _lastLightAttempt = 0;
        _lastLightCheck = 0;
        _writeFailing = false;
        _commandFailures = 0;
        var instance = HidHide.InstanceId(path);
        _fallbackId = instance is null ? null : "dev:" + instance;
        Log.Info($"{Name} opened (input report length {length}, output {outputLength}, write {(_canWrite ? "ok" : "unavailable")})");
        BeginRead();
        RequestFullMode();
        RequestDeviceInfo();
        RequestCalibration();
    }

    /// <summary>Call when <see cref="EventHandle"/> is signaled.</summary>
    public void OnSignaled()
    {
        if (_file == 0 || !_pending) return;

        uint transferred;
        if (GetOverlappedResult(_file, _overlapped, &transferred, 0) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ERROR_IO_INCOMPLETE) return;
            Fail($"Read failed (error {error})");
            return;
        }
        _pending = false;

        // finally: a handler that throws must not leave the controller without a pending read.
        try { Process(transferred); }
        finally
        {
            // A handler may have closed the device.
            if (_file != 0) BeginRead();
        }
    }

    private void Process(uint transferred)
    {
        CheckWriteTimeout();
        if (transferred > 0)
        {
            if (_managed.Length < transferred) _managed = new byte[transferred];
            new ReadOnlySpan<byte>(_buffer, (int)transferred).CopyTo(_managed);
            var bytes = new ReadOnlyMemory<byte>(_managed, 0, (int)transferred);
            if (IsPlayerLightsAck(bytes.Span)) _lightsAcked = true;
            else if (ProCommands.TryParsePlayerLights(bytes.Span, out var lights) && _lightMask != 0 && lights != _lightMask)
            {
                // Something else (Windows' GameInput service, a game) set the controller's LEDs: light ours again.
                Log.Info($"{Name} player lights 0x{_lightMask:X2} were changed to 0x{lights:X2} by another app: setting them again");
                _lightsAcked = false;
                _lightAttempts = 0;
            }
            if (ProCommands.TryParseDeviceInfo(bytes.Span, out var mac))
            {
                if (_mac == mac) return;
                Log.Info($"{Name} identified ({mac})");
                _mac = mac;
                Identified?.Invoke();
            }
            else if (ProCommands.TryParseSpiRead(bytes.Span, out var address, out var spi))
                OnSpiRead(address, spi);
            else if (ProReportParser.TryParse(bytes.Span, out var report))
            {
                if (_mac is null) RequestDeviceInfo(); // rate limited; the reply may have been lost
                RetryCalibration();
                SetPlayerLights();
                CheckPlayerLights();
                // Still in simple mode (e.g. the first reports after connecting, or the mode got reset): ask again.
                if (report.Format == ReportFormat.Simple) RequestFullMode();
                if (Kind == HidKind.Pro) Report?.Invoke(report);
                else if (JoyCon1Device.TryParseInput(bytes.Span, out var input, out int battery, out bool charging))
                    JoyConInput?.Invoke(input, battery, charging); // a Joy-Con's simple reports are laid out otherwise: ignored
            }
        }
    }

    /// <summary>Call on WM_DEVICECHANGE removal of a HID interface.</summary>
    public void OnInterfaceRemoved(string path)
    {
        if (_file != 0 && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            Fail("Device removed");
    }

    public void Dispose()
    {
        Close();
        CloseHandle(_event);
        CloseHandle(_writeEvent);
        NativeMemory.Free(_overlapped);
        NativeMemory.Free(_writeOverlapped);
        NativeMemory.Free(_writeBuffer);
        _writeBuffer = null;
        _writeCapacity = 0;
    }

    /// <summary>
    /// Sends "set input report mode = 0x30" if the controller is still in simple mode. Rate limited, and gives up
    /// after a few attempts per connection so a device that ignores it can't make us spin.
    /// </summary>
    private void RequestFullMode()
    {
        if (_file == 0 || !_canWrite || _modeAttempts >= MaxModeAttempts) return;
        long now = Environment.TickCount64;
        if (_modeAttempts > 0 && now - _lastModeAttempt < ModeRetryMs) return;
        _lastModeAttempt = now;
        _modeAttempts++;

        Send(ProCommands.SetInputMode(_packetCounter++, ProCommands.InputModeFull, (int)_outputLength));
        Log.Info($"Requested full input mode (attempt {_modeAttempts})");
    }

    /// <summary>Asks the controller for its MAC address. Rate limited and capped, like <see cref="RequestFullMode"/>.</summary>
    private void RequestDeviceInfo()
    {
        if (_file == 0 || !_canWrite || _mac is not null || _infoAttempts >= MaxModeAttempts) return;
        long now = Environment.TickCount64;
        if (_infoAttempts > 0 && now - _lastInfoAttempt < ModeRetryMs) return;
        _lastInfoAttempt = now;
        _infoAttempts++;
        Send(ProCommands.RequestDeviceInfo(_packetCounter++, (int)_outputLength));
    }

    // ---- stick calibration (SPI flash) -----------------------------------------------------------

    private enum CalStep { None, User, Factory }

    private CalStep _calStep;
    private int _calAttempts;
    private long _lastCalRequest;
    private byte[] _userCal = [];

    /// <summary>Raised on the UI thread once the stick calibration was read from the controller's flash.</summary>
    public event Action<StickCalibrations>? CalibrationRead;

    /// <summary>
    /// Reads the stick calibration from the controller's flash (user block, then factory block). The replies come back
    /// through the read loop. Returns false when the controller is absent or read-only.
    /// </summary>
    public bool RequestCalibration()
    {
        if (_file == 0 || !_canWrite) return false;
        _calStep = CalStep.User;
        _calAttempts = 0;
        SendCalibrationRead();
        return true;
    }

    private void SendCalibrationRead()
    {
        _calAttempts++;
        _lastCalRequest = Environment.TickCount64;
        var (address, length) = _calStep == CalStep.User
            ? (StickCalibrationParser.UserAddress, StickCalibrationParser.UserLength)
            : (StickCalibrationParser.FactoryAddress, StickCalibrationParser.FactoryLength);
        Send(ProCommands.ReadSpi(_packetCounter++, address, length, (int)_outputLength));
    }

    /// <summary>Asks again when a reply was lost; gives up after a few attempts (the adaptive range stays).</summary>
    private void RetryCalibration()
    {
        if (_calStep == CalStep.None) return;
        if (Environment.TickCount64 - _lastCalRequest < ModeRetryMs) return;
        if (_calAttempts >= MaxModeAttempts)
        {
            Log.Error("Stick calibration: the controller did not answer, keeping the adaptive range");
            _calStep = CalStep.None;
            return;
        }
        SendCalibrationRead();
    }

    private void OnSpiRead(int address, byte[] data)
    {
        if (_calStep == CalStep.User && address == StickCalibrationParser.UserAddress)
        {
            _userCal = data;
            _calStep = CalStep.Factory;
            _calAttempts = 0;
            SendCalibrationRead();
        }
        else if (_calStep == CalStep.Factory && address == StickCalibrationParser.FactoryAddress)
        {
            _calStep = CalStep.None;
            if (Kind != HidKind.Pro)
            {
                JoyConCalibration = StickCalibrationParser.CombineStick(_userCal, data, right: Kind == HidKind.JoyConRight);
                if (JoyConCalibration is null) Log.Error($"{Name}: the flash holds no usable stick calibration, using the default range");
                return;
            }
            var cal = StickCalibrationParser.Combine(_userCal, data);
            if (cal is null)
            {
                Log.Error("Stick calibration: the flash holds no usable calibration, keeping the adaptive range");
                return;
            }
            Log.Info($"Stick calibration read ({cal.Source})");
            CalibrationRead?.Invoke(cal);
        }
    }

    /// <summary>Starts or stops the rumble motors. Returns false when the controller is absent or read-only.</summary>
    public bool SetRumble(bool on) => SetRumbleMotors(on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0, 100);

    /// <summary>
    /// Rumbles from Xbox motor strengths (0..255) scaled by <paramref name="percent"/>; (0, 0) stops it. Queued: returns
    /// false only when the controller is absent or read-only.
    /// </summary>
    public bool SetRumbleMotors(byte large, byte small, int percent)
    {
        if (_file == 0 || !_canWrite) return false;
        if ((large > 0 || small > 0) && percent > 0 && !_vibrationEnabled)
        {
            // A subcommand: it goes out before the rumble frame queued below.
            Send(ProCommands.EnableVibration(_packetCounter++, (int)_outputLength), OutputKind.EnableVibration);
            _vibrationEnabled = true;
        }
        Send(ProCommands.RumbleMotors(_packetCounter++, large, small, percent, (int)_outputLength), OutputKind.Rumble);
        return true;
    }

    /// <summary>
    /// Lights <see cref="LightMask"/> (for a Pro Controller, the LED of the player number: slot 1 = LED 1, ...), so each
    /// controller shows which pad it drives. Official pads handle one subcommand at a time and drop the ones sent right
    /// behind another, so this repeats (rate limited, capped) from the read loop until the controller acknowledges it.
    /// </summary>
    private void SetPlayerLights()
    {
        if (_file == 0 || !_canWrite || _lightMask == 0 || _lightsAcked || _lightAttempts >= MaxLightAttempts) return;
        long now = Environment.TickCount64;
        if (_lightAttempts > 0 && now - _lastLightAttempt < ModeRetryMs) return;
        _lastLightAttempt = now;
        _lightAttempts++;
        Send(ProCommands.SetPlayerLights(_packetCounter++, _lightMask, (int)_outputLength));
        Log.Info($"{Name} player lights 0x{_lightMask:X2} requested (attempt {_lightAttempts})");
    }

    /// <summary>
    /// Once the LED is set, reads it back every couple of seconds: another app talking to the controller may change it
    /// (it then shows LED 1), and <see cref="OnSignaled"/> sets ours again.
    /// </summary>
    private void CheckPlayerLights()
    {
        if (_file == 0 || !_canWrite || !_lightsAcked) return;
        long now = Environment.TickCount64;
        if (now - _lastLightCheck < LightCheckMs) return;
        _lastLightCheck = now;
        Send(ProCommands.GetPlayerLights(_packetCounter++, (int)_outputLength));
    }

    /// <summary>A subcommand reply (input report 0x21): ack with bit 7 set at byte 13, subcommand id at byte 14.</summary>
    private static bool IsPlayerLightsAck(ReadOnlySpan<byte> r) =>
        r.Length > 14 && r[0] == 0x21 && (r[13] & 0x80) != 0 && r[14] == ProCommands.SubcommandSetPlayerLights;

    // ---- output ------------------------------------------------------------------------------

    /// <summary>
    /// Queues an output report; it goes out as soon as the write before it completed. Never waits: a slow Bluetooth link
    /// must not hold up the reports of the other controllers. The senders retry what matters (rate limited), so a full
    /// queue drops the new subcommand.
    /// </summary>
    private void Send(byte[] report, OutputKind kind = OutputKind.Command)
    {
        if (_file == 0 || !_canWrite) return;
        if (kind == OutputKind.Rumble) _rumble = report;
        else if (_commands.Count < MaxQueuedCommands) _commands.Enqueue((report, kind));
        else return;
        PumpWrites();
    }

    /// <summary>Starts the next write when none is in flight: subcommands first, then the latest rumble frame.</summary>
    private void PumpWrites()
    {
        // A write that fails at once leaves nothing in flight: the next one is tried right away.
        while (!_writePending && _file != 0 && _canWrite)
        {
            if (_commands.TryDequeue(out var next)) StartWrite(next.Report, next.Kind);
            else if (_rumble is { } frame)
            {
                _rumble = null;
                StartWrite(frame, OutputKind.Rumble);
            }
            else return;
        }
    }

    private void StartWrite(byte[] report, OutputKind kind)
    {
        if (_writeCapacity < report.Length)
        {
            NativeMemory.Free(_writeBuffer);
            _writeBuffer = (byte*)NativeMemory.Alloc((nuint)report.Length);
            _writeCapacity = report.Length;
        }
        report.CopyTo(new Span<byte>(_writeBuffer, report.Length));
        *_writeOverlapped = default;
        _writeOverlapped->hEvent = _writeEvent;
        _writeKind = kind;
        uint written;
        if (WriteFile(_file, _writeBuffer, (uint)report.Length, &written, (nint)_writeOverlapped) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ERROR_IO_PENDING)
            {
                OnWriteDone(error);
                return; // the next one goes out on the next Send or completion
            }
        }
        // Completed at once or still pending: either way the event gets signaled.
        _writePending = true;
        _writeCancelled = false;
        _writeStarted = Environment.TickCount64;
    }

    /// <summary>Call when <see cref="WriteHandle"/> is signaled.</summary>
    public void OnWriteSignaled()
    {
        if (_file == 0 || !_writePending) return;
        uint written;
        int error = 0;
        if (GetOverlappedResult(_file, _writeOverlapped, &written, 0) == 0)
        {
            error = Marshal.GetLastPInvokeError();
            if (error == ERROR_IO_INCOMPLETE) return;
        }
        _writePending = false;
        OnWriteDone(error);
        PumpWrites();
    }

    /// <summary>A write still pending after <see cref="WriteTimeoutMs"/> is cancelled: its completion reports it.</summary>
    private void CheckWriteTimeout()
    {
        if (!_writePending || _writeCancelled || Environment.TickCount64 - _writeStarted < WriteTimeoutMs) return;
        _writeCancelled = true;
        CancelIoEx(_file, _writeOverlapped);
    }

    private void OnWriteDone(int error)
    {
        bool rumble = _writeKind == OutputKind.Rumble;
        if (error == 0)
        {
            _writeFailing = false;
            if (!rumble) _commandFailures = 0;
            return;
        }

        if (_writeKind == OutputKind.EnableVibration) _vibrationEnabled = false; // asked again with the next rumble
        if (!rumble || !_writeFailing)
            Log.Error(error == ERROR_OPERATION_ABORTED ? $"{Name}: write timed out" : $"{Name}: write failed (error {error})");
        _writeFailing = true;
        if (rumble || ++_commandFailures < MaxModeAttempts) return;

        // A controller that refuses every subcommand can't make us spin: read-only until it reconnects.
        Log.Error($"{Name}: {_commandFailures} writes failed in a row, no more output to this controller");
        _canWrite = false;
        _commands.Clear();
        _rumble = null;
    }

    /// <summary>Cancels the write in flight and drops what waits (closing the device).</summary>
    private void StopWrites()
    {
        if (_writePending)
        {
            uint n;
            CancelIoEx(_file, _writeOverlapped);
            GetOverlappedResult(_file, _writeOverlapped, &n, 1);
            _writePending = false;
        }
        ResetEvent(_writeEvent); // like the read event: a signaled handle would wake the loop for nothing
        _commands.Clear();
        _rumble = null;
    }

    /// <summary>Logs which button usages the device descriptor declares (HIDP_BUTTON_CAPS is 72 bytes).</summary>
    private static void LogButtonCaps(nint preparsed, int count)
    {
        if (count <= 0 || count > 32) return;
        var caps = stackalloc byte[72 * 32];
        ushort n = (ushort)count;
        if (HidP_GetButtonCaps(0, caps, &n, preparsed) < 0) return;
        for (int i = 0; i < n; i++)
        {
            byte* c = caps + i * 72;
            ushort page = *(ushort*)c;
            byte id = c[2];
            bool range = c[12] != 0;
            ushort min = *(ushort*)(c + 56), max = range ? *(ushort*)(c + 58) : min;
            Log.Info($"HID buttons: report 0x{id:X2}, usage page 0x{page:X}, usages {min}..{max}");
        }
    }

    private void BeginRead()
    {
        *_overlapped = default;
        _overlapped->hEvent = _event;
        uint read;
        if (ReadFile(_file, _buffer, _bufferLength, &read, (nint)_overlapped) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ERROR_IO_PENDING)
            {
                Fail($"Read failed (error {error})");
                return;
            }
        }
        // Completed immediately or still pending: either way the event gets signaled.
        _pending = true;
    }

    private void Fail(string reason)
    {
        Log.Info($"{Name} disconnected: {reason}");
        Close();
        ControllerRemoved?.Invoke();
    }

    /// <summary>Stops reading (without <see cref="ControllerRemoved"/>): the reader is free again.</summary>
    public void Close()
    {
        if (_file == 0) return;
        if (_pending)
        {
            uint n;
            CancelIoEx(_file, _overlapped);
            GetOverlappedResult(_file, _overlapped, &n, 1);
            _pending = false;
        }
        StopWrites();
        CloseHandle(_file);
        _file = 0;
        // The failed read left the (manual reset) event signaled: the message loop would wake on it forever, and since it
        // serves the first signaled handle, the other controllers' reports would never be read.
        ResetEvent(_event);
        _calStep = CalStep.None;
        _path = null;
        HidPaths = [];
        _mac = null;
        _fallbackId = null;
        NativeMemory.Free(_buffer);
        _buffer = null;
    }

    private static readonly (string Pid, HidKind Kind)[] Products =
        [("2009", HidKind.Pro), ("2006", HidKind.JoyConLeft), ("2007", HidKind.JoyConRight)];

    private static (string Path, HidKind Kind)? FindControllerPath(IReadOnlyCollection<string> taken, bool allowPro)
    {
        var guid = HidInterface;
        uint length = 0;
        if (CM_Get_Device_Interface_List_SizeW(&length, &guid, null, 0) != 0 || length <= 1) return null;

        var buffer = new char[length];
        fixed (char* p = buffer)
            if (CM_Get_Device_Interface_ListW(&guid, null, p, length, 0) != 0) return null;

        // USB: ...vid_057e&pid_2009...   Bluetooth: ..._vid&0002057e_pid&2009...
        foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (taken.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var (pid, kind) in Products)
            {
                if (kind == HidKind.Pro && !allowPro) continue;
                if (path.Contains($"vid_057e&pid_{pid}", StringComparison.OrdinalIgnoreCase)
                    || path.Contains($"vid&0002057e_pid&{pid}", StringComparison.OrdinalIgnoreCase))
                    return (path, kind);
            }
        }
        return null;
    }
}
