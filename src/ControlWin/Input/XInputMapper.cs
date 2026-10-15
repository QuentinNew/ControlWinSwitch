using System.Runtime.InteropServices;

namespace ControlWin.Input;

/// <summary>XUSB_REPORT as the ViGEm bus expects it (12 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct XusbReport : IEquatable<XusbReport>
{
    public ushort Buttons;
    public byte LeftTrigger, RightTrigger;
    public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;

    public readonly bool Equals(XusbReport o) =>
        Buttons == o.Buttons && LeftTrigger == o.LeftTrigger && RightTrigger == o.RightTrigger
        && ThumbLX == o.ThumbLX && ThumbLY == o.ThumbLY && ThumbRX == o.ThumbRX && ThumbRY == o.ThumbRY;

    public override readonly bool Equals(object? obj) => obj is XusbReport r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(Buttons, LeftTrigger, RightTrigger, ThumbLX, ThumbLY, ThumbRX, ThumbRY);
}

/// <summary>Pure mapping from the Pro Controller state to an Xbox 360 report.</summary>
internal static class XInputMapper
{
    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
        Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
        LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    /// <summary>
    /// Face buttons are mapped by position (Nintendo B = bottom = Xbox A). The swaps exchange them back
    /// to label-based mapping. Capture is never mapped. <paramref name="left"/>/<paramref name="right"/>
    /// use screen convention (+Y = down) and are flipped here.
    /// </summary>
    public static XusbReport Map(Buttons held, Stick left, Stick right, bool swapAB = false, bool swapXY = false)
    {
        ushort b = 0;
        ushort bottom = A, rightFace = B, leftFace = X, top = Y;
        if (swapAB) (bottom, rightFace) = (rightFace, bottom);
        if (swapXY) (leftFace, top) = (top, leftFace);

        if ((held & Buttons.B) != 0) b |= bottom;
        if ((held & Buttons.A) != 0) b |= rightFace;
        if ((held & Buttons.Y) != 0) b |= leftFace;
        if ((held & Buttons.X) != 0) b |= top;
        if ((held & Buttons.Up) != 0) b |= DpadUp;
        if ((held & Buttons.Down) != 0) b |= DpadDown;
        if ((held & Buttons.Left) != 0) b |= DpadLeft;
        if ((held & Buttons.Right) != 0) b |= DpadRight;
        if ((held & Buttons.Plus) != 0) b |= Start;
        if ((held & Buttons.Minus) != 0) b |= Back;
        if ((held & Buttons.Home) != 0) b |= Guide;
        if ((held & Buttons.LStick) != 0) b |= LeftThumb;
        if ((held & Buttons.RStick) != 0) b |= RightThumb;
        if ((held & Buttons.L) != 0) b |= LeftShoulder;
        if ((held & Buttons.R) != 0) b |= RightShoulder;

        return new XusbReport
        {
            Buttons = b,
            LeftTrigger = (held & Buttons.ZL) != 0 ? (byte)255 : (byte)0,
            RightTrigger = (held & Buttons.ZR) != 0 ? (byte)255 : (byte)0,
            ThumbLX = Axis(left.X),
            ThumbLY = Axis(-left.Y),
            ThumbRX = Axis(right.X),
            ThumbRY = Axis(-right.Y),
        };
    }

    public static short Axis(float v)
    {
        float s = MathF.Round(Math.Clamp(v, -1f, 1f) * 32767f);
        return (short)s;
    }
}
