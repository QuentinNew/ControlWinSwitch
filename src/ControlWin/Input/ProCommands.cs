namespace ControlWin.Input;

/// <summary>Output reports sent to the Switch Pro Controller.</summary>
internal static class ProCommands
{
    public const byte SubcommandSetInputMode = 0x03;
    public const byte InputModeFull = 0x30;

    /// <summary>
    /// Builds output report 0x01 carrying the "set input report mode" subcommand:
    /// [0]=0x01, [1]=packet counter (low nibble), [2..9]=neutral rumble, [10]=subcommand, [11]=mode.
    /// Padded with zeros to <paramref name="reportLength"/> (the descriptor's OutputReportByteLength).
    /// </summary>
    public static byte[] SetInputMode(byte packetCounter, byte mode, int reportLength)
    {
        var report = new byte[Math.Max(reportLength, 12)];
        report[0] = 0x01;
        report[1] = (byte)(packetCounter & 0x0F);
        // Neutral rumble (no vibration) for both motors.
        report[2] = 0x00; report[3] = 0x01; report[4] = 0x40; report[5] = 0x40;
        report[6] = 0x00; report[7] = 0x01; report[8] = 0x40; report[9] = 0x40;
        report[10] = SubcommandSetInputMode;
        report[11] = mode;
        return report;
    }

    public const byte SubcommandRequestDeviceInfo = 0x02;

    /// <summary>Output report 0x01 carrying "request device info": the controller answers with its firmware and MAC address.</summary>
    public static byte[] RequestDeviceInfo(byte packetCounter, int reportLength)
    {
        var report = SetInputMode(packetCounter, 0, reportLength);
        report[10] = SubcommandRequestDeviceInfo;
        report[11] = 0;
        return report;
    }

    /// <summary>
    /// Reads the MAC address from the device-info reply (input report 0x21, ack 0x82 at byte 13, subcommand 0x02 at byte 14, MAC at byte 19..24).
    /// Returned as "AA:BB:CC:DD:EE:FF"; false for every other report.
    /// </summary>
    public static bool TryParseDeviceInfo(ReadOnlySpan<byte> r, out string mac)
    {
        mac = "";
        if (r.Length < 25 || r[0] != 0x21 || r[13] != 0x82 || r[14] != SubcommandRequestDeviceInfo) return false;
        var m = r.Slice(19, 6);
        if (m.IndexOfAnyExcept((byte)0) < 0) return false; // all zero: no address
        mac = string.Join(':', m.ToArray().Select(b => b.ToString("X2")));
        return true;
    }

    public const byte SubcommandSpiRead = 0x10;

    /// <summary>Output report 0x01 carrying "SPI flash read": address (4 bytes, little endian) then the length (max 0x1D).</summary>
    public static byte[] ReadSpi(byte packetCounter, int address, int length, int reportLength)
    {
        var report = SetInputMode(packetCounter, 0, Math.Max(reportLength, 16));
        report[10] = SubcommandSpiRead;
        BitConverter.GetBytes(address).CopyTo(report, 11);
        report[15] = (byte)length;
        return report;
    }

    /// <summary>
    /// Reads an SPI read reply (input report 0x21, ack 0x90 at byte 13, subcommand 0x10 at byte 14, address at 15..18,
    /// length at 19, data from byte 20).
    /// </summary>
    public static bool TryParseSpiRead(ReadOnlySpan<byte> r, out int address, out byte[] data)
    {
        address = 0;
        data = [];
        if (r.Length < 20 || r[0] != 0x21 || r[13] != 0x90 || r[14] != SubcommandSpiRead) return false;
        int length = r[19];
        if (r.Length < 20 + length) return false;
        address = BitConverter.ToInt32(r.Slice(15, 4));
        data = r.Slice(20, length).ToArray();
        return true;
    }

    public const byte SubcommandEnableVibration = 0x48;
    public const byte SubcommandSetPlayerLights = 0x30;

    /// <summary>Output report 0x01 carrying "set player lights": bit n of <paramref name="mask"/> lights LED n + 1 (solid).</summary>
    public static byte[] SetPlayerLights(byte packetCounter, byte mask, int reportLength)
    {
        var report = SetInputMode(packetCounter, 0, reportLength);
        report[10] = SubcommandSetPlayerLights;
        report[11] = (byte)(mask & 0x0F);
        return report;
    }

    public const byte SubcommandGetPlayerLights = 0x31;

    /// <summary>Output report 0x01 carrying "get player lights" (the reply holds the LED mask: solid in the low nibble, flashing in the high one).</summary>
    public static byte[] GetPlayerLights(byte packetCounter, int reportLength)
    {
        var report = SetInputMode(packetCounter, 0, reportLength);
        report[10] = SubcommandGetPlayerLights;
        report[11] = 0;
        return report;
    }

    /// <summary>The reply to "get player lights" (input report 0x21, ack at byte 13, subcommand at 14, the mask at 15).</summary>
    public static bool TryParsePlayerLights(ReadOnlySpan<byte> r, out byte mask)
    {
        mask = 0;
        if (r.Length < 16 || r[0] != 0x21 || (r[13] & 0x80) == 0 || r[14] != SubcommandGetPlayerLights) return false;
        mask = r[15];
        return true;
    }

    /// <summary>LED mask for a zero-based slot: slot 0 lights LED 1, slot 1 LED 2, ...</summary>
    public static byte PlayerLightMask(int slot) => (byte)(1 << Math.Clamp(slot, 0, 3));
    public const byte ReportRumbleOnly = 0x10;

    /// <summary>Output report 0x01 carrying "enable vibration" (the controller ignores rumble until it gets this).</summary>
    public static byte[] EnableVibration(byte packetCounter, int reportLength)
    {
        var report = SetInputMode(packetCounter, 0, reportLength);
        report[10] = SubcommandEnableVibration;
        report[11] = 0x01;
        return report;
    }

    /// <summary>
    /// Builds output report 0x10 (rumble only): [0]=0x10, [1]=packet counter, [2..5]=left motor, [6..9]=right motor.
    /// </summary>
    public static byte[] Rumble(byte packetCounter, bool on, int reportLength) =>
        RumbleMotors(packetCounter, on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0, 100, reportLength);

    /// <summary>
    /// Rumble report from an Xbox pad's two motors (0..255): the large motor drives the low band, the small one the
    /// high band, both scaled by <paramref name="percent"/> (0..100). Full strength is the usual "buzz" pattern;
    /// no strength is the neutral pattern.
    /// </summary>
    public static byte[] RumbleMotors(byte packetCounter, byte large, byte small, int percent, int reportLength)
    {
        double k = Math.Clamp(percent, 0, 100) / 100.0;
        double low = large / 255.0 * k, high = small / 255.0 * k;

        var report = new byte[Math.Max(reportLength, 10)];
        report[0] = ReportRumbleOnly;
        report[1] = (byte)(packetCounter & 0x0F);

        byte hfFreq = 0x00, hfAmp = 0x01, lfFreq = 0x40, lfAmp = 0x40;
        if (high > 0)
        {
            hfFreq = 0x28;
            hfAmp = (byte)((int)(0x88 * high) & ~1);
        }
        if (low > 0)
        {
            lfFreq = 0x60;
            lfAmp = (byte)(0x40 + (int)((0x61 - 0x40) * low));
        }
        byte[] motor = [hfFreq, hfAmp, lfFreq, lfAmp];
        motor.CopyTo(report, 2);
        motor.CopyTo(report, 6);
        return report;
    }
}
