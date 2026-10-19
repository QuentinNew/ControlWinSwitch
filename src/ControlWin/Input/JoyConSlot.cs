namespace ControlWin.Input;

/// <summary>
/// A controller slot driven by Joy-Con 2s: a left and a right one together (a Pro Controller), or one alone held
/// sideways. When half of a pair goes, the other carries on alone in the same slot; a Joy-Con of the other side may join
/// a lone one again, unless that one was connected with SL / SR held (sideways on purpose). UI thread only.
/// </summary>
internal sealed class JoyConSlot : IControllerSource
{
    private readonly int _slot;
    private JoyCon2Device? _left, _right;
    private JoyCon2Input? _leftInput, _rightInput;
    private bool _sidewaysByChoice;

    public JoyConSlot(int slot) => _slot = slot;

    /// <summary>Raised for every report of either Joy-Con, merged into one Pro Controller shaped report.</summary>
    public event Action<ProReport>? Report;

    /// <summary>Raised whenever the layout changes: the stick ranges of the new layout.</summary>
    public event Action<StickCalibrations>? CalibrationRead;

    /// <summary>Raised when the last Joy-Con of the slot is gone.</summary>
    public event Action? ControllerRemoved;

    public bool IsOpen => _left is not null || _right is not null;

    public JoyConLayout Layout => JoyCon2Mapper.LayoutOf(_left is not null, _right is not null);

    /// <summary>A lone Joy-Con of the other side that this one may join.</summary>
    public bool CanTake(JoyConSide side) => IsOpen && !_sidewaysByChoice && (side == JoyConSide.Left ? _left is null : _right is null);

    /// <summary>The left Joy-Con names a pair; a lone one names itself.</summary>
    public string? Id => (_left ?? _right)?.Mac;

    public string? PartnerId => Layout == JoyConLayout.Combined ? _right!.Mac : null;

    public string? FallbackId => null;

    public bool HasMac => true;

    public string? HidPath => null; // not a HID device: games cannot see it

    public (string Id, bool RightField)? StickProfile(bool right) => Layout switch
    {
        _ when !IsOpen => null,
        JoyConLayout.Combined => right ? (_right!.Mac, true) : (_left!.Mac, false),
        JoyConLayout.SingleLeft => right ? null : (_left!.Mac, false),
        _ => right ? null : (_right!.Mac, true),
    };

    public void Add(JoyCon2Device device)
    {
        if (!IsOpen) _sidewaysByChoice = device.SideButtonsHeld;
        if (device.Side == JoyConSide.Left) _left = device;
        else _right = device;
        device.Input += OnInput;
        device.Removed += OnRemoved;
        LayoutChanged();
    }

    private void OnRemoved(JoyCon2Device device)
    {
        device.Input -= OnInput;
        device.Removed -= OnRemoved;
        if (device == _left) (_left, _leftInput) = (null, null);
        else if (device == _right) (_right, _rightInput) = (null, null);
        else return;

        if (IsOpen) LayoutChanged();
        else
        {
            _sidewaysByChoice = false;
            ControllerRemoved?.Invoke();
        }
    }

    /// <summary>The player LEDs show the slot (read from the other end on a lone right Joy-Con), and the stick ranges follow the layout.</summary>
    private void LayoutChanged()
    {
        var layout = Layout;
        Log.Info($"Controller {_slot + 1}: {(layout == JoyConLayout.Combined ? "Joy-Con 2 pair" : (_left ?? _right)!.Name + " alone")}");
        byte mask = ProCommands.PlayerLightMask(_slot);
        _left?.SetLeds(mask);
        _right?.SetLeds(layout == JoyConLayout.SingleRight ? JoyCon2Protocol.ReverseLeds(mask) : mask);
        CalibrationRead?.Invoke(JoyCon2Mapper.Calibration(layout, _left?.Calibration, _right?.Calibration));
    }

    private void OnInput(JoyCon2Device device, JoyCon2Input input)
    {
        if (device == _left) _leftInput = input;
        else if (device == _right) _rightInput = input;
        else return;

        // A half that has not reported yet is neutral (sticks at its center).
        var (lc, rc) = (Center(_left), Center(_right));
        JoyCon2Input? left = _left is null ? null : _leftInput ?? Neutral(lc);
        JoyCon2Input? right = _right is null ? null : _rightInput ?? Neutral(rc);

        // The battery badge shows the emptier Joy-Con.
        int battery = -1;
        bool charging = false;
        foreach (var half in new[] { left, right })
        {
            if (half is not { } h) continue;
            int level = JoyCon2Protocol.BatteryLevel(h.BatteryMv);
            if (level < 0 || (battery >= 0 && level >= battery)) continue;
            battery = level;
            charging = h.BatteryCurrent > 0;
        }

        Report?.Invoke(JoyCon2Mapper.Map(left, right, lc, rc, battery, charging));
    }

    private static (int X, int Y) Center(JoyCon2Device? d) =>
        d?.Calibration is { } c ? (c[0].Center, c[1].Center) : (JoyCon2Mapper.DefaultCenter, JoyCon2Mapper.DefaultCenter);

    private static JoyCon2Input Neutral((int X, int Y) c) => new(0, c.X, c.Y, c.X, c.Y, 0, 0);

    public bool SetRumble(bool on) => SetRumbleMotors(on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0, 100);

    public bool SetRumbleMotors(byte large, byte small, int percent)
    {
        if (!IsOpen) return false;
        var (low, high) = JoyCon2Protocol.Amplitudes(large, small, percent);
        bool sent = false;
        if (_left is not null) sent |= _left.SetRumble(low, high);
        if (_right is not null) sent |= _right.SetRumble(low, high);
        return sent;
    }

    /// <summary>Drops both Joy-Cons (shutdown).</summary>
    public void DisconnectAll()
    {
        _left?.Disconnect();
        _right?.Disconnect();
    }
}
