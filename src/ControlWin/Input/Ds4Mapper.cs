using System.Runtime.InteropServices;

namespace ControlWin.Input;

/// <summary>DS4_REPORT as the ViGEm bus expects it (9 bytes). The low nibble of <see cref="Buttons"/> is the d-pad hat.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Ds4Report : IEquatable<Ds4Report>
{
    public byte ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    public ushort Buttons;
    public byte Special, LeftTrigger, RightTrigger;

    public readonly bool Equals(Ds4Report o) =>
        ThumbLX == o.ThumbLX && ThumbLY == o.ThumbLY && ThumbRX == o.ThumbRX && ThumbRY == o.ThumbRY
        && Buttons == o.Buttons && Special == o.Special && LeftTrigger == o.LeftTrigger && RightTrigger == o.RightTrigger;

    public override readonly bool Equals(object? obj) => obj is Ds4Report r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(ThumbLX, ThumbLY, ThumbRX, ThumbRY, Buttons, Special, LeftTrigger, RightTrigger);
}

/// <summary>Pure mapping from the Pro Controller state to a DualShock 4 report.</summary>
internal static class Ds4Mapper
{
    private const ushort Square = 1 << 4, Cross = 1 << 5, Circle = 1 << 6, Triangle = 1 << 7,
        L1 = 1 << 8, R1 = 1 << 9, L2 = 1 << 10, R2 = 1 << 11, Share = 1 << 12, Options = 1 << 13, L3 = 1 << 14, R3 = 1 << 15;
    private const byte PsButton = 1;
    private const ushort HatNone = 8;

    /// <summary>Sticks centered, nothing pressed.</summary>
    public static Ds4Report Neutral => new() { ThumbLX = 0x80, ThumbLY = 0x80, ThumbRX = 0x80, ThumbRY = 0x80, Buttons = HatNone };

    /// <summary>
    /// Same layout rules as <see cref="XInputMapper.Map"/>: face buttons by position (Nintendo B = bottom = Cross), the
    /// swaps exchange them back to label-based mapping. Sticks use screen convention (+Y = down), which is also the DS4's.
    /// </summary>
    public static Ds4Report Map(Buttons held, Stick left, Stick right, bool swapAB = false, bool swapXY = false)
    {
        ushort b = 0;
        ushort bottom = Cross, rightFace = Circle, leftFace = Square, top = Triangle;
        if (swapAB) (bottom, rightFace) = (rightFace, bottom);
        if (swapXY) (leftFace, top) = (top, leftFace);

        if ((held & Input.Buttons.B) != 0) b |= bottom;
        if ((held & Input.Buttons.A) != 0) b |= rightFace;
        if ((held & Input.Buttons.Y) != 0) b |= leftFace;
        if ((held & Input.Buttons.X) != 0) b |= top;
        if ((held & Input.Buttons.L) != 0) b |= L1;
        if ((held & Input.Buttons.R) != 0) b |= R1;
        if ((held & Input.Buttons.ZL) != 0) b |= L2;
        if ((held & Input.Buttons.ZR) != 0) b |= R2;
        if ((held & Input.Buttons.Minus) != 0) b |= Share;
        if ((held & Input.Buttons.Plus) != 0) b |= Options;
        if ((held & Input.Buttons.LStick) != 0) b |= L3;
        if ((held & Input.Buttons.RStick) != 0) b |= R3;
        b |= Hat(held);

        return new Ds4Report
        {
            ThumbLX = Axis(left.X),
            ThumbLY = Axis(left.Y),
            ThumbRX = Axis(right.X),
            ThumbRY = Axis(right.Y),
            Buttons = b,
            Special = (held & Input.Buttons.Home) != 0 ? PsButton : (byte)0,
            LeftTrigger = (held & Input.Buttons.ZL) != 0 ? (byte)255 : (byte)0,
            RightTrigger = (held & Input.Buttons.ZR) != 0 ? (byte)255 : (byte)0,
        };
    }

    /// <summary>The d-pad as a hat value: 0 = north, clockwise to 7 = north-west, 8 = released.</summary>
    private static ushort Hat(Buttons held)
    {
        int x = ((held & Input.Buttons.Right) != 0 ? 1 : 0) - ((held & Input.Buttons.Left) != 0 ? 1 : 0);
        int y = ((held & Input.Buttons.Down) != 0 ? 1 : 0) - ((held & Input.Buttons.Up) != 0 ? 1 : 0);
        return (x, y) switch
        {
            (0, -1) => 0,
            (1, -1) => 1,
            (1, 0) => 2,
            (1, 1) => 3,
            (0, 1) => 4,
            (-1, 1) => 5,
            (-1, 0) => 6,
            (-1, -1) => 7,
            _ => HatNone,
        };
    }

    public static byte Axis(float v) => (byte)Math.Clamp(MathF.Round(127.5f + Math.Clamp(v, -1f, 1f) * 127.5f), 0f, 255f);
}
