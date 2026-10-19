using static ControlWin.Input.JoyCon2Buttons;

namespace ControlWin.Input;

/// <summary>How the Joy-Cons of a slot are held.</summary>
internal enum JoyConLayout : byte
{
    /// <summary>A left and a right Joy-Con together: a Pro Controller.</summary>
    Combined,
    /// <summary>A lone left Joy-Con held sideways (stick on the left, rail up).</summary>
    SingleLeft,
    /// <summary>A lone right Joy-Con held sideways (stick on the left, rail up).</summary>
    SingleRight,
}

/// <summary>
/// Turns Joy-Con 2 input into the Pro Controller shaped <see cref="ProReport"/> the rest of the app consumes. Held sideways,
/// the face buttons are rotated so A / B / X / Y sit where they do on a Pro Controller, SL / SR become L / R, the shoulder
/// buttons become ZL / ZR and the stick becomes the left stick. C and Capture both open the menu.
/// </summary>
internal static class JoyCon2Mapper
{
    /// <summary>Raw center used until the calibration is known.</summary>
    public const int DefaultCenter = 2048;

    private static readonly (uint From, Buttons To)[] CombinedMap =
    [
        (Y, Buttons.Y), (X, Buttons.X), (B, Buttons.B), (A, Buttons.A),
        (R, Buttons.R), (ZR, Buttons.ZR), (L, Buttons.L), (ZL, Buttons.ZL),
        (Minus, Buttons.Minus), (Plus, Buttons.Plus), (LStick, Buttons.LStick), (RStick, Buttons.RStick),
        (Home, Buttons.Home), (Capture, Buttons.Capture), (C, Buttons.Capture),
        (Up, Buttons.Up), (Down, Buttons.Down), (Left, Buttons.Left), (Right, Buttons.Right),
    ];

    // Turned a quarter counterclockwise: the D-pad becomes the face buttons.
    private static readonly (uint From, Buttons To)[] SingleLeftMap =
    [
        (Left, Buttons.B), (Down, Buttons.A), (Up, Buttons.Y), (Right, Buttons.X),
        (SlL, Buttons.L), (SrL, Buttons.R), (L, Buttons.ZL), (ZL, Buttons.ZR),
        (Minus, Buttons.Plus), (LStick, Buttons.LStick), (Capture, Buttons.Capture),
    ];

    // Turned a quarter clockwise.
    private static readonly (uint From, Buttons To)[] SingleRightMap =
    [
        (Y, Buttons.X), (X, Buttons.A), (B, Buttons.Y), (A, Buttons.B),
        (SlR, Buttons.L), (SrR, Buttons.R), (R, Buttons.ZL), (ZR, Buttons.ZR),
        (Plus, Buttons.Plus), (RStick, Buttons.LStick), (Home, Buttons.Home), (C, Buttons.Capture),
    ];

    public static Buttons MapButtons(uint raw, JoyConLayout layout)
    {
        var map = layout switch
        {
            JoyConLayout.SingleLeft => SingleLeftMap,
            JoyConLayout.SingleRight => SingleRightMap,
            _ => CombinedMap,
        };
        var b = Buttons.None;
        foreach (var (from, to) in map)
            if ((raw & from) != 0) b |= to;
        return b;
    }

    /// <summary>
    /// The report of a slot. <paramref name="left"/> / <paramref name="right"/> are the inputs of its Joy-Cons (null when
    /// absent); the layout follows from which are present. <paramref name="leftCenter"/> / <paramref name="rightCenter"/>
    /// are the raw stick centers (the sideways rotation mirrors one axis around it).
    /// </summary>
    public static ProReport Map(JoyCon2Input? left, JoyCon2Input? right, (int X, int Y) leftCenter, (int X, int Y) rightCenter,
        int battery, bool charging)
    {
        var layout = LayoutOf(left is not null, right is not null);
        const int c = DefaultCenter;
        switch (layout)
        {
            case JoyConLayout.SingleLeft:
            {
                var l = left!.Value;
                // Stick up (toward the rail-less end) points left; stick right points up.
                return new ProReport(ReportFormat.Full, MapButtons(l.Buttons, layout),
                    2 * leftCenter.Y - l.LY, l.LX, c, c, battery, charging, SingleStick: true);
            }
            case JoyConLayout.SingleRight:
            {
                var r = right!.Value;
                return new ProReport(ReportFormat.Full, MapButtons(r.Buttons, layout),
                    r.RY, 2 * rightCenter.X - r.RX, c, c, battery, charging, SingleStick: true);
            }
            default:
            {
                var l = left!.Value;
                var r = right!.Value;
                return new ProReport(ReportFormat.Full, MapButtons(l.Buttons | r.Buttons, layout),
                    l.LX, l.LY, r.RX, r.RY, battery, charging);
            }
        }
    }

    public static JoyConLayout LayoutOf(bool hasLeft, bool hasRight) =>
        hasLeft && hasRight ? JoyConLayout.Combined : hasLeft ? JoyConLayout.SingleLeft : JoyConLayout.SingleRight;

    /// <summary>A stick's range used until (or when) its calibration is unknown.</summary>
    public static readonly AxisRange[] DefaultRange = [new(DefaultCenter, 1400, 1400), new(DefaultCenter, 1400, 1400)];

    /// <summary>
    /// The calibration matching <see cref="Map"/>: each stick's ranges, rotated like the stick when held sideways (the
    /// mirrored axis swaps its travel below and above the center).
    /// </summary>
    public static StickCalibrations Calibration(JoyConLayout layout, AxisRange[]? left, AxisRange[]? right)
    {
        var l = left ?? DefaultRange;
        var r = right ?? DefaultRange;
        string source = left is null && right is null ? "Default" : left is null || right is null ? "Joy-Con + default" : "Joy-Con";
        return layout switch
        {
            JoyConLayout.SingleLeft => new([Mirror(l[1]), l[0]], DefaultRange, left is null ? "Default" : "Joy-Con"),
            JoyConLayout.SingleRight => new([r[1], Mirror(r[0])], DefaultRange, right is null ? "Default" : "Joy-Con"),
            _ => new(l, r, source),
        };
    }

    private static AxisRange Mirror(AxisRange a) => new(a.Center, a.Above, a.Below);
}
