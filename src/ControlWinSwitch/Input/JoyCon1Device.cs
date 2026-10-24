namespace ControlWinSwitch.Input;

/// <summary>
/// An original Joy-Con (L or R), paired in Windows like a Pro Controller and read through the same
/// <see cref="HidControllerReader"/> (it speaks the same HID protocol, as BetterJoy uses it). Its reports are turned into
/// the Joy-Con 2 shape so it goes into a <see cref="JoyConSlot"/>: alone held sideways, or paired with a Joy-Con of the
/// other side. <see cref="Ready"/> is raised once its MAC address, its stick calibration and its first full report are
/// in. UI thread only.
/// </summary>
internal sealed class JoyCon1Device : IJoyCon
{
    private readonly HidControllerReader _reader;
    private bool _ready, _gone;
    private int _battery = -1;
    private bool _charging;

    public JoyCon1Device(HidControllerReader reader)
    {
        _reader = reader;
        Side = reader.Kind == HidKind.JoyConLeft ? JoyConSide.Left : JoyConSide.Right;
        HidPath = reader.Path;
        Mac = "";
        reader.JoyConInput += OnInput;
        reader.ControllerRemoved += OnReaderRemoved;
    }

    public JoyConSide Side { get; }
    public string Mac { get; private set; }
    public string Name => Side == JoyConSide.Left ? "Joy-Con (L)" : "Joy-Con (R)";
    public AxisRange[]? Calibration => _reader.JoyConCalibration;
    public bool SideButtonsHeld { get; private set; }
    public string? HidPath { get; }

    /// <summary>Set up: it may go into a slot (whoever handles it owns it, or calls <see cref="Disconnect"/>).</summary>
    public event Action<JoyCon1Device>? Ready;

    public event Action<IJoyCon, JoyCon2Input>? Input;
    public event Action<IJoyCon>? Removed;

    private void OnInput(JoyCon2Input input, int battery, bool charging)
    {
        (_battery, _charging) = (battery, charging);
        if (_ready)
        {
            Input?.Invoke(this, input);
            return;
        }
        if (!_reader.SetUpDone) return;
        _ready = true;
        Mac = _reader.Id ?? HidPath ?? "";
        SideButtonsHeld = (input.Buttons & JoyCon2Buttons.SideButtons) != 0;
        Log.Info($"{Name} {Mac} connected (calibration {(Calibration is null ? "missing" : "read")}{(SideButtonsHeld ? ", sideways" : "")}, battery {battery})");
        Ready?.Invoke(this);
    }

    private void OnReaderRemoved()
    {
        if (_gone) return;
        Detach();
        if (_ready) Removed?.Invoke(this);
    }

    public (int Level, bool Charging) Battery(in JoyCon2Input input) => (_battery, _charging);

    public void SetLeds(byte mask)
    {
        if (!_gone) _reader.LightMask = mask;
    }

    public bool SetRumbleMotors(byte large, byte small, int percent) => !_gone && _reader.SetRumbleMotors(large, small, percent);

    /// <summary>Stops reading it (Windows keeps it connected; the reader may open it again later).</summary>
    public void Disconnect()
    {
        if (_gone) return;
        Detach();
        _reader.Close();
        if (_ready) Removed?.Invoke(this);
    }

    private void Detach()
    {
        _gone = true;
        _reader.JoyConInput -= OnInput;
        _reader.ControllerRemoved -= OnReaderRemoved;
    }

    /// <summary>
    /// Decodes a full input report (0x30, 0x31, or a 0x21 subcommand reply) of an original Joy-Con. Its button bytes
    /// use the very bits of the Joy-Con 2 mask (right, shared, left), and its sticks the same 12-bit packing; a left
    /// Joy-Con fills the left half only, a right one the right half. Battery as in <see cref="ProReportParser"/>.
    /// </summary>
    public static bool TryParseInput(ReadOnlySpan<byte> r, out JoyCon2Input input, out int battery, out bool charging)
    {
        input = default;
        battery = -1;
        charging = false;
        if (r.Length < 12 || r[0] is not (0x30 or 0x31 or 0x21)) return false;
        uint buttons = r[3] | ((uint)(r[4] & 0x3F) << 8) | ((uint)r[5] << 16); // shared 0x40 / 0x80: unused / charging grip
        input = new JoyCon2Input(buttons,
            r[6] | ((r[7] & 0x0F) << 8), (r[7] >> 4) | (r[8] << 4),
            r[9] | ((r[10] & 0x0F) << 8), (r[10] >> 4) | (r[11] << 4), 0, 0);
        battery = (r[2] >> 5) & 0x07;
        charging = (r[2] & 0x10) != 0;
        return true;
    }
}
