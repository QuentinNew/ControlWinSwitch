namespace ControlWinSwitch.Input;

/// <summary>One axis as stored in the controller's flash: the raw center and the travel on each side of it.</summary>
internal readonly record struct AxisRange(int Center, int Below, int Above);

/// <summary>The calibration of both sticks, and where it came from ("Factory", "User", "Factory + user").</summary>
internal sealed record StickCalibrations(AxisRange[] Left, AxisRange[] Right, string Source);

/// <summary>Decodes the stick calibration blocks read from the Pro Controller's SPI flash (12-bit values packed in 3 bytes per pair).</summary>
internal static class StickCalibrationParser
{
    public const int UserAddress = 0x8010, UserLength = 22;
    public const int FactoryAddress = 0x603D, FactoryLength = 18;

    /// <summary>Marks a user calibration as present (stored before each stick's 9 bytes).</summary>
    private const byte MagicA = 0xB2, MagicB = 0xA1;

    /// <summary>Left stick, 9 bytes: max above center, center, min below center (X and Y each).</summary>
    public static AxisRange[]? ParseLeft(ReadOnlySpan<byte> d)
    {
        if (d.Length < 9) return null;
        var (ax, ay) = Pair(d[0..3]);
        var (cx, cy) = Pair(d[3..6]);
        var (bx, by) = Pair(d[6..9]);
        return Build(cx, cy, bx, by, ax, ay);
    }

    /// <summary>Right stick, 9 bytes: center, min below center, max above center.</summary>
    public static AxisRange[]? ParseRight(ReadOnlySpan<byte> d)
    {
        if (d.Length < 9) return null;
        var (cx, cy) = Pair(d[0..3]);
        var (bx, by) = Pair(d[3..6]);
        var (ax, ay) = Pair(d[6..9]);
        return Build(cx, cy, bx, by, ax, ay);
    }

    /// <summary>The 22 bytes at <see cref="UserAddress"/>: each stick's block is used only when its magic is present.</summary>
    public static (AxisRange[]? Left, AxisRange[]? Right) ParseUser(ReadOnlySpan<byte> d)
    {
        if (d.Length < UserLength) return (null, null);
        var left = d[0] == MagicA && d[1] == MagicB ? ParseLeft(d[2..11]) : null;
        var right = d[11] == MagicA && d[12] == MagicB ? ParseRight(d[13..22]) : null;
        return (left, right);
    }

    /// <summary>The 18 bytes at <see cref="FactoryAddress"/>: left stick block then right stick block.</summary>
    public static (AxisRange[]? Left, AxisRange[]? Right) ParseFactory(ReadOnlySpan<byte> d) =>
        d.Length < FactoryLength ? (null, null) : (ParseLeft(d[0..9]), ParseRight(d[9..18]));

    /// <summary>Per stick the user calibration when present, otherwise the factory one. Null when neither stick has a usable one.</summary>
    public static StickCalibrations? Combine(ReadOnlySpan<byte> user, ReadOnlySpan<byte> factory)
    {
        var (ul, ur) = ParseUser(user);
        var (fl, fr) = ParseFactory(factory);
        var left = ul ?? fl;
        var right = ur ?? fr;
        if (left is null || right is null) return null;
        bool anyUser = ul is not null || ur is not null, anyFactory = fl is not null && ul is null || fr is not null && ur is null;
        string source = anyUser && anyFactory ? "Factory + user" : anyUser ? "User" : "Factory";
        return new StickCalibrations(left, right, source);
    }

    /// <summary>
    /// One stick of the same blocks, for an original Joy-Con (a left one stores its stick in the left blocks, a right one
    /// in the right blocks): the user calibration when present, otherwise the factory one.
    /// </summary>
    public static AxisRange[]? CombineStick(ReadOnlySpan<byte> user, ReadOnlySpan<byte> factory, bool right)
    {
        var (ul, ur) = ParseUser(user);
        var (fl, fr) = ParseFactory(factory);
        return right ? ur ?? fr : ul ?? fl;
    }

    private static (int X, int Y) Pair(ReadOnlySpan<byte> b) => (b[0] | ((b[1] & 0x0F) << 8), (b[1] >> 4) | (b[2] << 4));

    /// <summary>Null for a block that is blank (flash erased: all 0xFF) or has no travel.</summary>
    private static AxisRange[]? Build(int cx, int cy, int bx, int by, int ax, int ay)
    {
        if (bx <= 0 || by <= 0 || ax <= 0 || ay <= 0) return null;
        if (cx == 0xFFF && cy == 0xFFF && bx == 0xFFF && by == 0xFFF) return null;
        return [new AxisRange(cx, bx, ax), new AxisRange(cy, by, ay)];
    }
}
