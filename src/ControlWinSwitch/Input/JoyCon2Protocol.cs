using System.Buffers.Binary;

namespace ControlWinSwitch.Input;

internal enum JoyConSide : byte { Left, Right }

/// <summary>One input notification of a Joy-Con 2, undecoded apart from the fields the app uses.</summary>
/// <param name="Buttons">The 32-bit button mask (<see cref="JoyCon2Buttons"/>).</param>
/// <param name="BatteryMv">Battery voltage in millivolts (0 when not reported).</param>
/// <param name="BatteryCurrent">Battery current, signed (raw units; positive is assumed to mean charging).</param>
internal readonly record struct JoyCon2Input(uint Buttons, int LX, int LY, int RX, int RY, int BatteryMv, int BatteryCurrent);

/// <summary>Bits of the Joy-Con 2 button mask (left and right Joy-Cons use disjoint bits, so a pair's masks just OR).</summary>
internal static class JoyCon2Buttons
{
    public const uint Y = 0x00000001, X = 0x00000002, B = 0x00000004, A = 0x00000008;
    public const uint SrR = 0x00000010, SlR = 0x00000020, R = 0x00000040, ZR = 0x00000080;
    public const uint Minus = 0x00000100, Plus = 0x00000200, RStick = 0x00000400, LStick = 0x00000800;
    public const uint Home = 0x00001000, Capture = 0x00002000, C = 0x00004000;
    public const uint Down = 0x00010000, Up = 0x00020000, Right = 0x00040000, Left = 0x00080000;
    public const uint SrL = 0x00100000, SlL = 0x00200000, L = 0x00400000, ZL = 0x00800000;

    public const uint SideButtons = SrR | SlR | SrL | SlL;
}

/// <summary>
/// The Bluetooth LE protocol of the Switch 2 Joy-Cons (after github.com/Nadeflore/switch2-controllers): advertisement,
/// input notification, commands and the rumble frame. Pure functions only; the GATT transport is <see cref="JoyCon2Device"/>.
/// </summary>
internal static class JoyCon2Protocol
{
    public const ushort NintendoCompanyId = 0x0553;
    public const ushort NintendoVid = 0x057E;
    public const ushort JoyCon2RightPid = 0x2066, JoyCon2LeftPid = 0x2067;

    public static readonly Guid InputReportUuid = new("ab7de9be-89fe-49ad-828f-118f09df7fd2");
    public static readonly Guid CommandWriteUuid = new("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");
    public static readonly Guid CommandResponseUuid = new("c765a961-d9d8-4d36-a20a-5315b111836a");
    public static readonly Guid VibrationLeftUuid = new("289326cb-a471-485d-a8f4-240c14f18241");
    public static readonly Guid VibrationRightUuid = new("fa19b0fb-cd1f-46a7-84a1-bbb09e00c149");

    // ---- advertisement ---------------------------------------------------------------------------

    /// <summary>
    /// Decodes Nintendo's manufacturer data (company id already stripped): a Joy-Con 2 and the address of the host it
    /// wants to reconnect to (0 while it is in sync mode, waiting to be paired).
    /// </summary>
    public static bool TryParseAdvertisement(ReadOnlySpan<byte> data, out JoyConSide side, out ulong reconnectAddress)
    {
        side = default;
        reconnectAddress = 0;
        if (data.Length < 16) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[3..]) != NintendoVid) return false;
        switch (BinaryPrimitives.ReadUInt16LittleEndian(data[5..]))
        {
            case JoyCon2LeftPid: side = JoyConSide.Left; break;
            case JoyCon2RightPid: side = JoyConSide.Right; break;
            default: return false;
        }
        reconnectAddress = ReadUInt48(data[10..16]);
        return true;
    }

    // ---- input -----------------------------------------------------------------------------------

    /// <summary>
    /// Decodes an input notification: buttons at 4, left stick at 10, right stick at 13 (12-bit pairs, Y grows upward),
    /// battery voltage (mV) at 31 and current at 33.
    /// </summary>
    public static bool TryParseInput(ReadOnlySpan<byte> r, out JoyCon2Input input)
    {
        input = default;
        if (r.Length < 35) return false;
        var (lx, ly) = Stick(r[10..13]);
        var (rx, ry) = Stick(r[13..16]);
        input = new JoyCon2Input(BinaryPrimitives.ReadUInt32LittleEndian(r[4..]), lx, ly, rx, ry,
            BinaryPrimitives.ReadUInt16LittleEndian(r[31..]), BinaryPrimitives.ReadInt16LittleEndian(r[33..]));
        return true;
    }

    /// <summary>Two 12-bit values packed in 3 bytes (x low, y high).</summary>
    public static (int X, int Y) Stick(ReadOnlySpan<byte> b) => (b[0] | ((b[1] & 0x0F) << 8), (b[1] >> 4) | (b[2] << 4));

    /// <summary>
    /// Battery level 0 (empty) to 4 (full) from the voltage, -1 when not reported. A full Joy-Con 2 reports about 3.66 V;
    /// the reference app calls below 3.125 V low.
    /// </summary>
    public static int BatteryLevel(int millivolts) => millivolts switch
    {
        <= 0 => -1,
        >= 3550 => 4,
        >= 3400 => 3,
        >= 3250 => 2,
        >= 3125 => 1,
        _ => 0,
    };

    // ---- commands --------------------------------------------------------------------------------

    public const byte CommandMemory = 0x02, SubcommandMemoryRead = 0x04;
    public const byte CommandLeds = 0x09, SubcommandLedsSetPlayer = 0x07;
    public const byte CommandPair = 0x15;
    public const byte SubcommandPairSetAddress = 0x01, SubcommandPairLtk1 = 0x04, SubcommandPairLtk2 = 0x02, SubcommandPairFinish = 0x03;

    /// <summary>A command on the command characteristic: [command, 0x91, 0x01, subcommand, 0, data length, 0, 0, data...].</summary>
    public static byte[] Command(byte command, byte subcommand, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[8 + data.Length];
        buffer[0] = command;
        buffer[1] = 0x91;
        buffer[2] = 0x01;
        buffer[3] = subcommand;
        buffer[5] = (byte)data.Length;
        data.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    /// <summary>The reply to <paramref name="command"/> ([command, 0x01, ...]); its payload starts at byte 8.</summary>
    public static bool IsReplyTo(ReadOnlySpan<byte> reply, byte command) => reply.Length >= 8 && reply[0] == command && reply[1] == 0x01;

    /// <summary>Reads <paramref name="length"/> bytes (at most 0x4F) of the controller's memory.</summary>
    public static byte[] ReadMemory(byte length, uint address)
    {
        Span<byte> data = stackalloc byte[8];
        data[0] = length;
        data[1] = 0x7E;
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], address);
        return Command(CommandMemory, SubcommandMemoryRead, data);
    }

    /// <summary>The data of a memory read reply, when it answers that exact length and address.</summary>
    public static byte[]? ParseMemoryReply(ReadOnlySpan<byte> reply, byte length, uint address)
    {
        if (!IsReplyTo(reply, CommandMemory)) return null;
        var payload = reply[8..];
        if (payload.Length < 8 + length || payload[0] != length || BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) != address) return null;
        return payload.Slice(8, length).ToArray();
    }

    /// <summary>Lights the player LEDs (one bit per LED). Shorter data makes the controller crash, hence 4 bytes.</summary>
    public static byte[] SetPlayerLeds(byte mask) => Command(CommandLeds, SubcommandLedsSetPlayer, [mask, 0, 0, 0]);

    /// <summary>The same LED mask read from the other end (a lone right Joy-Con is held the other way round).</summary>
    public static byte ReverseLeds(byte mask) =>
        (byte)(((mask & 1) << 3) | ((mask & 2) << 1) | ((mask & 4) >> 1) | ((mask & 8) >> 3));

    // ---- stick calibration -----------------------------------------------------------------------

    public const uint UserCalibrationAddress = 0x1FC042, FactoryCalibrationAddress = 0x0130A8;
    public const byte CalibrationLength = 0x0B;

    /// <summary>A user calibration block that was never written (flash erased).</summary>
    public static bool IsBlank(ReadOnlySpan<byte> block) => block.Length < 3 || (block[0] == 0xFF && block[1] == 0xFF && block[2] == 0xFF);

    /// <summary>A Joy-Con's stick calibration block: center, travel above, travel below (X and Y each). Null when unusable.</summary>
    public static AxisRange[]? ParseCalibration(ReadOnlySpan<byte> d)
    {
        if (d.Length < 9 || IsBlank(d)) return null;
        var (cx, cy) = Stick(d[0..3]);
        var (ax, ay) = Stick(d[3..6]);
        var (bx, by) = Stick(d[6..9]);
        if (ax <= 0 || ay <= 0 || bx <= 0 || by <= 0) return null;
        return [new AxisRange(cx, bx, ax), new AxisRange(cy, by, ay)];
    }

    // ---- pairing ---------------------------------------------------------------------------------

    /// <summary>Tells a Joy-Con in sync mode the address of the host to reconnect to (sent twice, as the Switch 2 does).</summary>
    public static byte[] PairSetAddress(ulong hostAddress)
    {
        var data = new byte[14];
        data[1] = 0x02;
        WriteUInt48(data.AsSpan(2), hostAddress);
        WriteUInt48(data.AsSpan(8), hostAddress);
        return Command(CommandPair, SubcommandPairSetAddress, data);
    }

    public static byte[] PairLtk1() => Command(CommandPair, SubcommandPairLtk1,
        [0x00, 0xEA, 0xBD, 0x47, 0x13, 0x89, 0x35, 0x42, 0xC6, 0x79, 0xEE, 0x07, 0xF2, 0x53, 0x2C, 0x6C, 0x31]);

    public static byte[] PairLtk2() => Command(CommandPair, SubcommandPairLtk2,
        [0x00, 0x40, 0xB0, 0x8A, 0x5F, 0xCD, 0x1F, 0x9B, 0x41, 0x12, 0x5C, 0xAC, 0xC6, 0x3F, 0x38, 0xA0, 0x73]);

    public static byte[] PairFinish() => Command(CommandPair, SubcommandPairFinish, [0]);

    // ---- rumble ----------------------------------------------------------------------------------

    /// <summary>Largest amplitude sent (the field holds 10 bits; the reference app never goes past 800).</summary>
    public const int MaxAmplitude = 800;

    private const int LowFrequency = 0x0E1, HighFrequency = 0x1E1;

    /// <summary>
    /// The write on a Joy-Con's vibration characteristic: [0, 0x50 | counter, three 5-byte motor frames]. The first frame
    /// carries the amplitudes (low band, high band: 0..<see cref="MaxAmplitude"/>), the other two are silent.
    /// </summary>
    public static byte[] Rumble(byte counter, int lowAmplitude, int highAmplitude)
    {
        var packet = new byte[17];
        packet[1] = (byte)(0x50 | (counter & 0x0F));
        WriteFrame(packet.AsSpan(2), lowAmplitude, highAmplitude);
        WriteFrame(packet.AsSpan(7), 0, 0);
        WriteFrame(packet.AsSpan(12), 0, 0);
        return packet;
    }

    /// <summary>Xbox motor strengths (0..255) scaled by <paramref name="percent"/> to Joy-Con 2 amplitudes.</summary>
    public static (int Low, int High) Amplitudes(byte large, byte small, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return (large * MaxAmplitude * percent / (255 * 100), small * MaxAmplitude * percent / (255 * 100));
    }

    /// <summary>40 bits, little endian: low band (9-bit frequency, tone bit, 10-bit amplitude), then the high band.</summary>
    private static void WriteFrame(Span<byte> d, int low, int high)
    {
        ulong value = LowFrequency
            | ((ulong)(Math.Clamp(low, 0, 0x3FF)) << 10)
            | ((ulong)HighFrequency << 20)
            | ((ulong)(Math.Clamp(high, 0, 0x3FF)) << 30);
        for (int i = 0; i < 5; i++) d[i] = (byte)(value >> (8 * i));
    }

    // ---- addresses -------------------------------------------------------------------------------

    /// <summary>A Bluetooth address as "AA:BB:CC:DD:EE:FF" (the form Pro Controller ids use).</summary>
    public static string FormatAddress(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((byte)(address >> (8 * (5 - i)))).ToString("X2")));

    private static ulong ReadUInt48(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        for (int i = 5; i >= 0; i--) v = (v << 8) | b[i];
        return v;
    }

    private static void WriteUInt48(Span<byte> d, ulong v)
    {
        for (int i = 0; i < 6; i++) d[i] = (byte)(v >> (8 * i));
    }
}
