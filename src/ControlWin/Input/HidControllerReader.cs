using System.Runtime.InteropServices;
using ControlWin.Native;
using static ControlWin.Native.Win32;

namespace ControlWin.Input;

/// <summary>
/// Reads Switch Pro Controller reports by opening its HID interface directly with an overlapped read.
/// Raw Input is not used: it is served by the system itself, so HidHide hides the controller from it even
/// for allow-listed applications. A direct open honours HidHide's application allow-list.
/// Besides reading, it sends one standard command (set input report mode 0x30): in the default simple mode
/// this controller does not report Capture (or Plus) at all. BetterJoy and Steam send the same command, so
/// they keep working alongside.
/// </summary>
/// <remarks>
/// Event-driven: <see cref="EventHandle"/> is signaled when a read completes and must be one of the handles the
/// message loop waits on; call <see cref="OnSignaled"/> when it fires. Everything runs on the UI thread.
/// </remarks>
internal sealed unsafe class HidControllerReader : IDisposable
{
    public const ushort NintendoVid = 0x057E;
    public const ushort ProControllerPid = 0x2009;

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

    // Output (set full mode)
    private readonly nint _writeEvent;
    private uint _outputLength;
    private bool _canWrite;
    private byte _packetCounter;
    private bool _vibrationEnabled;
    private int _modeAttempts;
    private long _lastModeAttempt;
    private const int MaxModeAttempts = 5;
    private const long ModeRetryMs = 1000;

    public HidControllerReader()
    {
        _event = CreateEventW(0, 1, 0, null); // manual reset: ReadFile resets it when a new read starts
        if (_event == 0) throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()})");
        _writeEvent = CreateEventW(0, 1, 0, null);
        if (_writeEvent == 0) throw new InvalidOperationException($"CreateEvent failed ({Marshal.GetLastPInvokeError()})");
    }

    /// <summary>Signaled when the pending read completed. Always a valid handle.</summary>
    public nint EventHandle => _event;

    public bool IsOpen => _file != 0;

    /// <summary>Raised for every decoded report (on the UI thread).</summary>
    public event Action<ProReport, ReadOnlyMemory<byte>>? Report;

    /// <summary>Raised for reports with an ID the parser doesn't know (diagnostics).</summary>
    public event Action<ReadOnlyMemory<byte>>? UnknownReport;

    /// <summary>Raised when the controller goes away (disconnected, or the read failed).</summary>
    public event Action? ControllerRemoved;

    /// <summary>Opens the controller if it is present and not open yet. Cheap enough to call on every HID arrival.</summary>
    public void TryOpen()
    {
        if (_file != 0) return;

        var path = FindControllerPath();
        if (path is null) return;

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
                Log.Error(error == ERROR_ACCESS_DENIED
                    ? "Pro Controller found but access denied (error 5): add this exe to HidHide's allowed applications"
                    : $"Pro Controller found but CreateFile failed (error {error})");
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
        _lastOpenError = 0;
        _bufferLength = length;
        _buffer = (byte*)NativeMemory.Alloc(length);
        _canWrite = canWrite && outputLength > 0;
        _outputLength = outputLength;
        _modeAttempts = 0;
        _vibrationEnabled = false;
        _lastModeAttempt = 0;
        Log.Info($"Pro Controller opened (input report length {length}, output {outputLength}, write {(_canWrite ? "ok" : "unavailable")})");
        BeginRead();
        RequestFullMode();
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

        if (transferred > 0)
        {
            if (_managed.Length < transferred) _managed = new byte[transferred];
            new ReadOnlySpan<byte>(_buffer, (int)transferred).CopyTo(_managed);
            var bytes = new ReadOnlyMemory<byte>(_managed, 0, (int)transferred);
            if (ProReportParser.TryParse(bytes.Span, out var report))
            {
                // Still in simple mode (e.g. the first reports after connecting, or the mode got reset): ask again.
                if (report.Format == ReportFormat.Simple) RequestFullMode();
                Report?.Invoke(report, bytes);
            }
            else UnknownReport?.Invoke(bytes);
        }

        // A handler may have closed the device.
        if (_file != 0) BeginRead();
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

        var report = ProCommands.SetInputMode(_packetCounter++, ProCommands.InputModeFull, (int)_outputLength);
        if (Write(report))
            Log.Info($"Requested full input mode (attempt {_modeAttempts})");
        else if (_modeAttempts >= MaxModeAttempts)
            _canWrite = false;
    }

    /// <summary>Starts or stops the rumble motors. Returns false when the controller is absent or read-only.</summary>
    public bool SetRumble(bool on) => SetRumbleMotors(on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0, 100);

    /// <summary>Rumbles from Xbox motor strengths (0..255) scaled by <paramref name="percent"/>; (0, 0) stops it.</summary>
    public bool SetRumbleMotors(byte large, byte small, int percent)
    {
        if (_file == 0 || !_canWrite) return false;
        if ((large > 0 || small > 0) && percent > 0 && !_vibrationEnabled)
        {
            if (!Write(ProCommands.EnableVibration(_packetCounter++, (int)_outputLength))) return false;
            _vibrationEnabled = true;
        }
        return Write(ProCommands.RumbleMotors(_packetCounter++, large, small, percent, (int)_outputLength));
    }

    /// <summary>Synchronous (bounded) overlapped write on the device handle.</summary>
    private bool Write(byte[] report)
    {
        var overlapped = (OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(OVERLAPPED));
        var buffer = (byte*)NativeMemory.Alloc((nuint)report.Length);
        try
        {
            report.CopyTo(new Span<byte>(buffer, report.Length));
            overlapped->hEvent = _writeEvent;
            uint written;
            if (WriteFile(_file, buffer, (uint)report.Length, &written, (nint)overlapped) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != ERROR_IO_PENDING)
                {
                    Log.Error($"Set full mode: WriteFile failed (error {error})");
                    return false;
                }
                if (WaitForSingleObject(_writeEvent, 1000) != WAIT_OBJECT_0)
                {
                    CancelIoEx(_file, overlapped);
                    GetOverlappedResult(_file, overlapped, &written, 1);
                    Log.Error("Set full mode: write timed out");
                    return false;
                }
            }
            if (GetOverlappedResult(_file, overlapped, &written, 0) == 0)
            {
                Log.Error($"Set full mode: write failed (error {Marshal.GetLastPInvokeError()})");
                return false;
            }
            return true;
        }
        finally
        {
            NativeMemory.Free(buffer);
            NativeMemory.Free(overlapped);
        }
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
        Log.Info($"Pro Controller disconnected: {reason}");
        Close();
        ControllerRemoved?.Invoke();
    }

    private void Close()
    {
        if (_file == 0) return;
        if (_pending)
        {
            uint n;
            CancelIoEx(_file, _overlapped);
            GetOverlappedResult(_file, _overlapped, &n, 1);
            _pending = false;
        }
        CloseHandle(_file);
        _file = 0;
        _path = null;
        NativeMemory.Free(_buffer);
        _buffer = null;
    }

    private static string? FindControllerPath()
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
            if (path.Contains("vid_057e&pid_2009", StringComparison.OrdinalIgnoreCase)
                || path.Contains("vid&0002057e_pid&2009", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }
}
