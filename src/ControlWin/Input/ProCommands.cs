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

    public const byte SubcommandEnableVibration = 0x48;
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
    public static byte[] Rumble(byte packetCounter, bool on, int reportLength)
    {
        var report = new byte[Math.Max(reportLength, 10)];
        report[0] = ReportRumbleOnly;
        report[1] = (byte)(packetCounter & 0x0F);
        byte[] motor = on ? [0x28, 0x88, 0x60, 0x61] : [0x00, 0x01, 0x40, 0x40];
        motor.CopyTo(report, 2);
        motor.CopyTo(report, 6);
        return report;
    }
}
