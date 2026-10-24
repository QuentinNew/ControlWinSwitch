namespace ControlWinSwitch.Input;

[Flags]
internal enum Buttons : uint
{
    None = 0,
    A = 1 << 0, B = 1 << 1, X = 1 << 2, Y = 1 << 3,
    L = 1 << 4, R = 1 << 5, ZL = 1 << 6, ZR = 1 << 7,
    Minus = 1 << 8, Plus = 1 << 9, LStick = 1 << 10, RStick = 1 << 11,
    Home = 1 << 12, Capture = 1 << 13,
    Up = 1 << 14, Down = 1 << 15, Left = 1 << 16, Right = 1 << 17,
}

internal enum ReportFormat : byte
{
    /// <summary>0x3F: default Bluetooth mode, sent only on change, 16-bit sticks (Y grows downward).</summary>
    Simple,
    /// <summary>0x30/0x31: full mode set up by BetterJoy or Steam, 12-bit sticks (Y grows upward).</summary>
    Full,
}

/// <summary>
/// One decoded input report. <paramref name="Battery"/> is 0 (empty) to 4 (full), or -1 when the report format does not
/// carry it (the simple 0x3F reports). <paramref name="SingleStick"/>: a lone Joy-Con, only the left stick exists.
/// </summary>
internal readonly record struct ProReport(ReportFormat Format, Buttons Buttons, int LX, int LY, int RX, int RY,
    int Battery = -1, bool Charging = false, bool SingleStick = false);

/// <summary>Decodes Switch Pro Controller HID input reports (report ID in byte 0).</summary>
internal static class ProReportParser
{
    public static bool TryParse(ReadOnlySpan<byte> r, out ProReport report)
    {
        report = default;
        if (r.Length == 0) return false;

        switch (r[0])
        {
            case 0x3F when r.Length >= 12:
            {
                var b = Buttons.None;
                byte b1 = r[1], b2 = r[2];
                if ((b1 & 0x01) != 0) b |= Buttons.B;
                if ((b1 & 0x02) != 0) b |= Buttons.A;
                if ((b1 & 0x04) != 0) b |= Buttons.Y;
                if ((b1 & 0x08) != 0) b |= Buttons.X;
                if ((b1 & 0x10) != 0) b |= Buttons.L;
                if ((b1 & 0x20) != 0) b |= Buttons.R;
                if ((b1 & 0x40) != 0) b |= Buttons.ZL;
                if ((b1 & 0x80) != 0) b |= Buttons.ZR;
                if ((b2 & 0x01) != 0) b |= Buttons.Minus;
                if ((b2 & 0x02) != 0) b |= Buttons.Plus;
                if ((b2 & 0x04) != 0) b |= Buttons.LStick;
                if ((b2 & 0x08) != 0) b |= Buttons.RStick;
                if ((b2 & 0x10) != 0) b |= Buttons.Home;
                if ((b2 & 0x20) != 0) b |= Buttons.Capture;
                b |= Hat(r[3]);
                report = new ProReport(ReportFormat.Simple, b,
                    U16(r, 4), U16(r, 6), U16(r, 8), U16(r, 10));
                return true;
            }
            case 0x30 or 0x31 or 0x21 when r.Length >= 12: // 0x21 (a subcommand reply) carries the same input data first
            {
                var b = Buttons.None;
                byte right = r[3], shared = r[4], left = r[5];
                if ((right & 0x01) != 0) b |= Buttons.Y;
                if ((right & 0x02) != 0) b |= Buttons.X;
                if ((right & 0x04) != 0) b |= Buttons.B;
                if ((right & 0x08) != 0) b |= Buttons.A;
                if ((right & 0x40) != 0) b |= Buttons.R;
                if ((right & 0x80) != 0) b |= Buttons.ZR;
                if ((shared & 0x01) != 0) b |= Buttons.Minus;
                if ((shared & 0x02) != 0) b |= Buttons.Plus;
                if ((shared & 0x04) != 0) b |= Buttons.RStick;
                if ((shared & 0x08) != 0) b |= Buttons.LStick;
                if ((shared & 0x10) != 0) b |= Buttons.Home;
                if ((shared & 0x20) != 0) b |= Buttons.Capture;
                if ((left & 0x01) != 0) b |= Buttons.Down;
                if ((left & 0x02) != 0) b |= Buttons.Up;
                if ((left & 0x04) != 0) b |= Buttons.Right;
                if ((left & 0x08) != 0) b |= Buttons.Left;
                if ((left & 0x40) != 0) b |= Buttons.L;
                if ((left & 0x80) != 0) b |= Buttons.ZL;
                report = new ProReport(ReportFormat.Full, b,
                    r[6] | ((r[7] & 0x0F) << 8), (r[7] >> 4) | (r[8] << 4),
                    r[9] | ((r[10] & 0x0F) << 8), (r[10] >> 4) | (r[11] << 4),
                    Battery: (r[2] >> 5) & 0x07, Charging: (r[2] & 0x10) != 0); // high nibble: 8 full, 6, 4, 2, 0 empty; +1 while charging
                return true;
            }
            default:
                return false;
        }
    }

    private static int U16(ReadOnlySpan<byte> r, int i) => r[i] | (r[i + 1] << 8);

    private static Buttons Hat(byte hat) => hat switch
    {
        0 => Buttons.Up,
        1 => Buttons.Up | Buttons.Right,
        2 => Buttons.Right,
        3 => Buttons.Down | Buttons.Right,
        4 => Buttons.Down,
        5 => Buttons.Down | Buttons.Left,
        6 => Buttons.Left,
        7 => Buttons.Up | Buttons.Left,
        _ => Buttons.None,
    };
}
