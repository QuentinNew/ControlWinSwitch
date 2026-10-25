namespace ControlWinSwitch.Input;

/// <summary>
/// A controller slot driven by Joy-Cons (Joy-Con 2s or original ones, mixed or not): a left and a right one together (a
/// Pro Controller), or one alone held sideways. When half of a pair goes, the other carries on alone in the same slot; a
/// Joy-Con of the other side may join a lone one again, unless that one was connected with SL / SR held (sideways on
/// purpose). UI thread only.
/// </summary>
internal sealed class JoyConSlot : IControllerSource
{
    private readonly int _slot;
    private IJoyCon? _left, _right;
    private JoyCon2Input? _leftInput, _rightInput;
    private bool _sidewaysByChoice;

    public JoyConSlot(int slot) => _slot = slot;

    /// <summary>Raised for every report of either Joy-Con, merged into one Pro Controller shaped report.</summary>
    public event Action<ProReport>? Report;

    /// <summary>Raised whenever the layout changes: the stick ranges of the new layout.</summary>
    public event Action<StickCalibrations>? CalibrationRead;

    /// <summary>Raised when the last Joy-Con of the slot is gone.</summary>
    public event Action? ControllerRemoved;

    /// <summary>Raised when one Joy-Con of a pair is gone (the other carries on alone).</summary>
    public event Action? PartnerLeft;

    public bool IsOpen => _left is not null || _right is not null;

    /// <summary>The Joy-Cons in the slot (left first).</summary>
    public IEnumerable<IJoyCon> Devices => new[] { _left, _right }.OfType<IJoyCon>();

    public JoyConLayout Layout => JoyCon2Mapper.LayoutOf(_left is not null, _right is not null);

    /// <summary>A lone Joy-Con of the other side that this one may join.</summary>
    public bool CanTake(JoyConSide side) => IsOpen && !_sidewaysByChoice && (side == JoyConSide.Left ? _left is null : _right is null);

    /// <summary>The left Joy-Con names a pair; a lone one names itself.</summary>
    public string? Id => (_left ?? _right)?.Mac;

    public string? PartnerId => Layout == JoyConLayout.Combined ? _right!.Mac : null;

    public string? FallbackId => null;

    public bool HasMac => true;

    /// <summary>Original Joy-Cons are HID devices games could read; Joy-Con 2s are not.</summary>
    public IReadOnlyList<string> HidPaths { get; private set; } = [];

    public (string Id, bool RightField)? StickProfile(bool right) => Layout switch
    {
        _ when !IsOpen => null,
        JoyConLayout.Combined => right ? (_right!.Mac, true) : (_left!.Mac, false),
        JoyConLayout.SingleLeft => right ? null : (_left!.Mac, false),
        _ => right ? null : (_right!.Mac, true),
    };

    public void Add(IJoyCon device)
    {
        if (!IsOpen) _sidewaysByChoice = device.SideButtonsHeld;
        if (device.Side == JoyConSide.Left) _left = device;
        else _right = device;
        device.Input += OnInput;
        device.Removed += OnRemoved;
        LayoutChanged();
    }

    private void OnRemoved(IJoyCon device)
    {
        device.Input -= OnInput;
        device.Removed -= OnRemoved;
        if (device == _left) (_left, _leftInput) = (null, null);
        else if (device == _right) (_right, _rightInput) = (null, null);
        else return;

        if (IsOpen)
        {
            LayoutChanged();
            PartnerLeft?.Invoke();
        }
        else
        {
            HidPaths = [];
            _sidewaysByChoice = false;
            ControllerRemoved?.Invoke();
        }
    }

    /// <summary>The player LEDs show the slot (read from the other end on a lone right Joy-Con), and the stick ranges follow the layout.</summary>
    private void LayoutChanged()
    {
        var layout = Layout;
        HidPaths = Devices.Select(d => d.HidPath).OfType<string>().ToArray();
        Log.Info($"Controller {_slot + 1}: {(layout == JoyConLayout.Combined ? $"{_left!.Name} + {_right!.Name}" : (_left ?? _right)!.Name + " alone")}");
        byte mask = ProCommands.PlayerLightMask(_slot);
        _left?.SetLeds(mask);
        _right?.SetLeds(layout == JoyConLayout.SingleRight ? JoyCon2Protocol.ReverseLeds(mask) : mask);
        CalibrationRead?.Invoke(JoyCon2Mapper.Calibration(layout, _left?.Calibration, _right?.Calibration));
    }

    private void OnInput(IJoyCon device, JoyCon2Input input)
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
        TakeEmptier(_left, left, ref battery, ref charging);
        TakeEmptier(_right, right, ref battery, ref charging);

        Report?.Invoke(JoyCon2Mapper.Map(left, right, lc, rc, battery, charging));
    }

    private static void TakeEmptier(IJoyCon? device, JoyCon2Input? half, ref int battery, ref bool charging)
    {
        if (device is null || half is not { } h) return;
        var (level, charge) = device.Battery(h);
        if (level < 0 || (battery >= 0 && level >= battery)) return;
        battery = level;
        charging = charge;
    }

    private static (int X, int Y) Center(IJoyCon? d) =>
        d?.Calibration is { } c ? (c[0].Center, c[1].Center) : (JoyCon2Mapper.DefaultCenter, JoyCon2Mapper.DefaultCenter);

    private static JoyCon2Input Neutral((int X, int Y) c) => new(0, c.X, c.Y, c.X, c.Y, 0, 0);

    public bool SetRumble(bool on) => SetRumbleMotors(on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0, 100);

    public bool SetRumbleMotors(byte large, byte small, int percent)
    {
        if (!IsOpen) return false;
        bool sent = false;
        if (_left is not null) sent |= _left.SetRumbleMotors(large, small, percent);
        if (_right is not null) sent |= _right.SetRumbleMotors(large, small, percent);
        return sent;
    }

    /// <summary>Drops both Joy-Cons (shutdown).</summary>
    public void DisconnectAll()
    {
        _left?.Disconnect();
        _right?.Disconnect();
    }
}
